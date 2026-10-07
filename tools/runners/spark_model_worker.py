#!/usr/bin/env python3
"""Qwen3.8-Flash-Next on TensorFold, beside Lucia on the DGX Spark; never run inside the web container.

install|run, from ~/.local/share/lucia/bootstrap/app/tools/runners/spark_model_worker.py.
The web host writes host/data/spark-model/request.json, with EXACT keys:
schemaVersion (1), generation (integer, raised on every change) and desired
(running|stopped). It unloads Lucia's own models before asking for running.
This worker owns the recipe, pinned to one commit, and its settings, so the web
host can only start or stop it. It binds the model to the Docker bridge the web
host shares with the Spark, never the LAN, since TensorFold has no
authentication; Lucia's /v1 forwards to it. Reports in status.json.
"""

import collections
import datetime
import ipaddress
import json
import os
import pathlib
import re
import shutil
import signal
import subprocess
import sys
import threading
import time
import urllib.parse

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "domains"))
from activation_worker import open_directory, parse_json, private_directory, regular, write_file

UTC = datetime.timezone.utc
RECIPE = "https://github.com/MiaAI-Lab/Qwen3.8-Flash-Next-Single-DGX-Spark-TensorFold.git"
COMMIT = "4c0dea8ebe93a1d445d6a716b5a38995ff6e9362"
CONTAINER = "qwen38-flash-next-tf"
HOST_CONTAINER = "lucia-homelab-host"
PORT = 8888
# Keep PARALLEL and CONTEXT in step with SparkModel.cs.
SETTINGS = {"PARALLEL": "4", "CONTEXT": "200000", "TENSORFOLD_MEMORY_RESERVE_GIB": "4", "PORT": str(PORT)}
FIELDS = {"schemaVersion", "generation", "desired"}
HOME = pathlib.Path.home() / ".local" / "share" / "lucia"
SHARED = HOME / "host" / "data" / "spark-model"
PRIVATE = HOME / "spark-model"
CHECKOUT = PRIVATE / "recipe"
# The Spark's telemetry relay scrapes TensorFold's Prometheus /metrics from the targets listed here (file_sd).
TARGETS = HOME / "host" / "data" / "telemetry" / "spark-model.json"
ANSI = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")


def require(condition):
    if not condition:
        raise ValueError("Invalid Spark model request.")


def now():
    return datetime.datetime.now(UTC)


def request():
    try:
        value = parse_json(regular(SHARED / "request.json", 4096))
    except FileNotFoundError:
        return None
    require(isinstance(value, dict) and set(value) == FIELDS and value["schemaVersion"] == 1)
    require(type(value["generation"]) is int and value["generation"] >= 0)
    require(value["desired"] in ("running", "stopped"))
    return value


def run(*args, timeout=60, cwd=None):
    result = subprocess.run(args, cwd=cwd, capture_output=True, text=True, timeout=timeout)
    if result.returncode:
        raise RuntimeError((result.stderr or result.stdout).strip()[-300:] or f"{args[0]} failed.")
    return result.stdout.strip()


def bridge():
    """The web host's Docker gateway: an address on the Spark that the host container reaches and the LAN doesn't."""
    gateway = run("docker", "inspect", HOST_CONTAINER, "--format", "{{range .NetworkSettings.Networks}}{{.Gateway}} {{end}}").split()
    address = ipaddress.ip_address(gateway[0]) if gateway else None
    if not address or address.version != 4 or not address.is_private:
        raise RuntimeError("Lucia's web host has no Docker bridge address to serve the model on.")
    return str(address)


def checkout():
    if not (CHECKOUT / ".git").is_dir():
        shutil.rmtree(CHECKOUT, ignore_errors=True)
        run("git", "clone", "--quiet", RECIPE, str(CHECKOUT), timeout=600)
    if run("git", "-C", str(CHECKOUT), "rev-parse", "HEAD") != COMMIT:
        run("git", "-C", str(CHECKOUT), "fetch", "--quiet", "origin", timeout=600)
        run("git", "-C", str(CHECKOUT), "checkout", "--quiet", "--force", "--detach", COMMIT)


def running():
    try:
        return run("docker", "inspect", "-f", "{{.State.Running}} {{.State.ExitCode}}", CONTAINER).split()
    except RuntimeError:
        return ["false", "missing"]


class Worker:
    def __init__(self):
        self.targets = None
        self.status = {"schemaVersion": 1, "generation": -1, "state": "stopped", "since": None, "message": None,
                       "url": None, "checkedAt": now().isoformat()}
        try:
            previous = parse_json(regular(SHARED / "status.json", 16384))
            if isinstance(previous, dict) and set(previous) == set(self.status) and type(previous["generation"]) is int:
                self.status = previous
        except (FileNotFoundError, ValueError):
            pass
        if self.status["state"] in ("starting", "stopping", "running"):
            self.status["generation"] = -1  # After a restart or reboot; start.sh leaves a running model alone.

    def report(self, **changes):
        self.status.update(changes, checkedAt=now().isoformat())
        write_file(SHARED / "status.json", json.dumps(self.status) + "\n")
        url = self.status["url"] if self.status["state"] == "running" else None
        targets = json.dumps([{"targets": [urllib.parse.urlsplit(url).netloc], "labels": {"model": "Qwen3.8-Flash-Next"}}]
                             if url else []) + "\n"
        if targets != self.targets and TARGETS.parent.is_dir():
            write_file(TARGETS, targets)
            self.targets = targets

    def script(self, name, env, timeout):
        # The first start downloads ~125 GB; its latest line is the progress, and reporting it keeps the heartbeat fresh.
        process = subprocess.Popen(["bash", f"./{name}"], cwd=CHECKOUT, env={**os.environ, **env}, stdin=subprocess.DEVNULL,
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, errors="replace")
        lines = collections.deque(maxlen=20)
        reader = threading.Thread(target=lambda: lines.extend(ANSI.sub("", line).strip() for line in process.stdout), daemon=True)
        reader.start()
        deadline = time.monotonic() + timeout
        while process.poll() is None:
            if time.monotonic() > deadline:
                process.kill()
                raise RuntimeError(f"{name} took too long.")
            shown = [line for line in list(lines) if line]
            self.report(message=shown[-1][:300] if shown else None)
            time.sleep(10)
        reader.join(5)
        shown = [line for line in lines if line]
        if process.returncode:
            errors = [line for line in shown if "ERROR" in line]
            raise RuntimeError((errors or shown or [f"{name} failed."])[-1][:300])

    def apply(self, value):
        self.report(generation=value["generation"], state="starting" if value["desired"] == "running" else "stopping",
                    message=None)
        if value["desired"] == "running":
            checkout()
            host = bridge()
            self.script("start.sh", {**SETTINGS, "HOST": host}, timeout=12 * 3600)
            self.report(state="running", since=now().isoformat(), message=None, url=f"http://{host}:{PORT}/")
        else:
            if (CHECKOUT / "stop.sh").exists():
                self.script("stop.sh", {**SETTINGS, "STOP_TIMEOUT": "60"}, timeout=300)
            self.report(state="stopped", since=now().isoformat(), message=None, url=None)

    def tick(self):
        value = request()
        if value is None:
            self.report()
        elif value["generation"] != self.status["generation"]:
            try:
                self.apply(value)
            except (OSError, RuntimeError, subprocess.SubprocessError) as error:
                self.report(state="failed", message=str(error) or "Qwen3.8-Flash-Next could not change state.", url=None)
                print(json.dumps({"event": "spark-model-failed", "errorType": type(error).__name__}), flush=True)
        elif self.status["state"] == "running" and running()[0] != "true":
            code = running()[1]
            self.report(state="failed", url=None,
                        message=f"Qwen3.8-Flash-Next stopped{'' if code == 'missing' else f' with exit code {code}'}. Start it again.")
        else:
            self.report()


def platform_check():
    if sys.platform != "linux" or os.getuid() == 0:
        raise RuntimeError("Run the Spark model service as the non-root Linux setup user.")
    stable = HOME / "bootstrap" / "app" / "tools" / "runners" / "spark_model_worker.py"
    require(pathlib.Path(__file__).resolve() == stable)
    os.close(open_directory(stable.parent))
    require(shutil.which("docker") and shutil.which("git"))
    return stable


def serve():
    platform_check()
    os.umask(0o077)
    for path in (SHARED, PRIVATE):
        private_directory(path)
    worker = Worker()
    stopping = False

    def stop(*_):
        nonlocal stopping
        stopping = True
    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    while not stopping:
        try:
            worker.tick()
        except (OSError, ValueError, RuntimeError) as error:
            print(json.dumps({"event": "spark-model-check-failed", "errorType": type(error).__name__}), flush=True)
        time.sleep(10)


def install():
    stable = platform_check()
    os.umask(0o077)
    linger = subprocess.run(["loginctl", "show-user", str(os.getuid()), "-p", "Linger", "--value"],
                            check=True, capture_output=True, text=True, timeout=30).stdout.strip()
    if linger != "yes":
        raise RuntimeError("Approve persistent user services before installing the Spark model service.")
    unit = pathlib.Path.home() / ".config" / "systemd" / "user" / "lucia-spark-model.service"
    unit.parent.mkdir(parents=True, exist_ok=True)
    escaped = str(stable).replace("\\", "\\\\").replace('"', '\\"').replace("%", "%%")
    write_file(unit, "[Unit]\nDescription=Lucia Qwen3.8-Flash-Next on TensorFold\n"
               "[Service]\nType=simple\nExecStart=/usr/bin/python3 \"" + escaped + "\" run\n"
               "Restart=on-failure\nRestartSec=5\nUMask=0077\nNoNewPrivileges=yes\n"
               "[Install]\nWantedBy=default.target\n")
    for args in (["daemon-reload"], ["enable", unit.name], ["restart", unit.name]):
        subprocess.run(["systemctl", "--user", *args], check=True, capture_output=True, timeout=30)


if __name__ == "__main__":
    try:
        if sys.argv[1:] == ["install"]:
            install()
        elif sys.argv[1:] == ["run"]:
            serve()
        else:
            raise ValueError("Usage: spark_model_worker.py install|run")
    except Exception:
        raise SystemExit("The Spark model service stopped. Check Docker, git, native paths and approved user linger.")
