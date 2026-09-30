#!/usr/bin/env python3
"""The Spark's on-demand GitHub Actions runner; never run inside the web container.

install|run, from ~/.local/share/lucia/bootstrap/app/tools/runners/spark_runner_worker.py.
The web host writes the owner's choices to the private 0600
host/data/spark-runner/request.json, with EXACT keys: schemaVersion (1),
generation (integer, raised on every change), desired (running|stopped|removed),
repositories (1-8 owner/repo or organization names), labels (0-8 extra labels),
idleMinutes (5-1440) and token (a GitHub personal access token; empty when removed).
This worker owns the compose file and its pinned images, so the web host can
choose only repositories, labels and the token, never what runs on the Spark.
It applies each generation once, stops the runners after idleMinutes without a
job, and reports in host/data/spark-runner/status.json.
"""

import datetime
import json
import os
import pathlib
import re
import shutil
import signal
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "domains"))
from activation_worker import open_directory, parse_json, private_directory, regular, write_file

UTC = datetime.timezone.utc
PROJECT = "lucia-spark-runner"
PREFIX = "spark"
# Keep in step with GitHubRunnerApp in StackCatalog.cs.
RUNNER = "myoung34/github-runner:2.337.0-ubuntu-noble@sha256:1b947d2475cc6f4c3edf0e91dd879b091857e41a311be917016243479bb34101"
DOCKER = "docker:29.8.1-dind@sha256:3f3c01aaaebf7cce837356b688b7c059a4749f10bd7660dec7c58fc454a283f0"
REPOSITORY = re.compile(r"[A-Za-z0-9][A-Za-z0-9-]{0,38}(?:/[A-Za-z0-9._-]{1,100})?")
LABEL = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,63}")
TOKEN = re.compile(r"[A-Za-z0-9_]{20,128}")
FIELDS = {"schemaVersion", "generation", "desired", "repositories", "labels", "idleMinutes", "token"}
HOME = pathlib.Path.home() / ".local" / "share" / "lucia"
SHARED = HOME / "host" / "data" / "spark-runner"
PRIVATE = HOME / "spark-runner"


def require(condition):
    if not condition:
        raise ValueError("Invalid Spark runner request.")


def now():
    return datetime.datetime.now(UTC)


def request():
    try:
        value = parse_json(regular(SHARED / "request.json", 65536))
    except FileNotFoundError:
        return None
    require(isinstance(value, dict) and set(value) == FIELDS and value["schemaVersion"] == 1)
    require(type(value["generation"]) is int and value["generation"] >= 0)
    require(value["desired"] in ("running", "stopped", "removed"))
    repositories, labels = value["repositories"], value["labels"]
    require(isinstance(repositories, list) and len(repositories) <= 8
            and all(isinstance(item, str) and REPOSITORY.fullmatch(item) and not item.lower().endswith(".git") for item in repositories))
    require(isinstance(labels, list) and len(labels) <= 8
            and all(isinstance(item, str) and LABEL.fullmatch(item) for item in labels))
    require(type(value["idleMinutes"]) is int and 5 <= value["idleMinutes"] <= 1440)
    require(isinstance(value["token"], str))
    if value["desired"] != "removed":
        require(repositories and TOKEN.fullmatch(value["token"]))
    return value


def compose_file(repositories, labels):
    labels = ",".join([PREFIX, *(item for item in labels if item.lower() != PREFIX)])
    text = ("# Written by Lucia's Spark runner service; changes are replaced.\n"
            "services:\n"
            "  # The runners' own Docker, on a socket only the runners share. 500 is the runner image's docker group.\n"
            "  docker:\n"
            f"    image: {DOCKER}\n"
            "    restart: unless-stopped\n"
            "    privileged: true\n"
            '    command: ["dockerd", "--host=unix:///run/dind/docker.sock", "--group=500"]\n'
            "    volumes:\n      - docker:/var/lib/docker\n      - socket:/run/dind\n      - work:/tmp/runner\n")
    taken = set()
    for repository in repositories:
        owner, _, repo = repository.partition("/")
        stem = re.sub(r"[^a-z0-9-]+", "-", (repo or owner).lower()).strip("-") or "runner"
        name, n = stem, 2
        while name in taken:
            name, n = f"{stem}-{n}", n + 1
        taken.add(name)
        scope = (f'      RUNNER_SCOPE: repo\n      REPO_URL: "https://github.com/{repository}"\n' if repo
                 else f'      RUNNER_SCOPE: org\n      ORG_NAME: "{owner}"\n')
        text += (f"  runner-{name}:\n    image: {RUNNER}\n    restart: unless-stopped\n    environment:\n{scope}"
                 "      ACCESS_TOKEN: ${ACCESS_TOKEN}\n"
                 f'      RUNNER_NAME_PREFIX: "{PREFIX}"\n      LABELS: "{labels}"\n'
                 '      EPHEMERAL: "true"\n      RUN_AS_ROOT: "false"\n      UNSET_CONFIG_VARS: "true"\n'
                 "      DOCKER_HOST: unix:///run/dind/docker.sock\n"
                 f"      RUNNER_WORKDIR: /tmp/runner/{name}\n"
                 "    volumes:\n      - socket:/run/dind\n      - work:/tmp/runner\n    depends_on:\n      - docker\n")
    return text + "volumes:\n  docker:\n  socket:\n  work:\n"


class Worker:
    def __init__(self):
        self.status = {"schemaVersion": 1, "generation": -1, "state": "removed", "reason": None, "since": None,
                       "lastJobAt": None, "busy": False, "message": None, "checkedAt": now().isoformat()}
        try:
            previous = parse_json(regular(SHARED / "status.json", 16384))
            if isinstance(previous, dict) and set(previous) == set(self.status) and type(previous["generation"]) is int:
                self.status = previous
        except (FileNotFoundError, ValueError):
            pass
        if self.status["state"] in ("starting", "stopping"):
            self.status["generation"] = -1  # Interrupted mid-change; apply it again.

    def report(self, **changes):
        self.status.update(changes, checkedAt=now().isoformat())
        write_file(SHARED / "status.json", json.dumps(self.status) + "\n")

    def compose(self, *args, timeout=60):
        # Long pulls keep the heartbeat fresh so the web host still sees the service.
        process = subprocess.Popen(["docker", "compose", "-p", PROJECT, "--project-directory", str(PRIVATE), *args],
                                   cwd=PRIVATE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        deadline = time.monotonic() + timeout
        while True:
            try:
                output, _ = process.communicate(timeout=10)
                break
            except subprocess.TimeoutExpired:
                if time.monotonic() > deadline:
                    process.kill()
                    process.communicate()
                    raise RuntimeError("Docker took too long.")
                self.report()
        if process.returncode:
            raise RuntimeError(output.strip().splitlines()[-1][:300] if output.strip() else "Docker failed.")
        return output

    def apply(self, value):
        self.report(generation=value["generation"], state="starting" if value["desired"] == "running" else "stopping",
                    busy=False, message=None)
        running = (PRIVATE / "compose.yml").exists()
        if value["desired"] == "running":
            write_file(PRIVATE / ".env", f"ACCESS_TOKEN={value['token']}\n")
            write_file(PRIVATE / "compose.yml", compose_file(value["repositories"], value["labels"]))
            self.compose("up", "-d", "--remove-orphans", "--pull", "missing", timeout=1800)
            self.report(state="running", reason=None, since=now().isoformat())
        elif value["desired"] == "stopped":
            if running:
                self.compose("down", "--remove-orphans", timeout=300)
            self.report(state="stopped", reason="owner", since=now().isoformat())
        else:
            if running:
                self.compose("down", "--remove-orphans", "--volumes", timeout=300)
            for name in ("compose.yml", ".env"):
                (PRIVATE / name).unlink(missing_ok=True)
            self.report(state="removed", reason="owner", since=now().isoformat(), lastJobAt=None)

    def watch(self, value):
        at = now()
        states = self.compose("ps", "-a", "--format", "{{.Service}} {{.State}}").split()
        # A runner that can't register (a wrong token or repository) exits and restarts over and over.
        failing = "restarting" in states[1::2]
        message = "A runner keeps restarting. Check the token and repositories, then save again." if failing else None
        try:
            busy = "Runner.Worker" in self.compose("top")
        except RuntimeError:
            busy = False  # top refuses while any container restarts, as each one does between jobs.
        if busy:
            self.report(busy=True, lastJobAt=at.isoformat(), message=message)
            return
        active = max(datetime.datetime.fromisoformat(item) for item in (self.status["since"], self.status["lastJobAt"]) if item)
        if at - active >= datetime.timedelta(minutes=value["idleMinutes"]):
            self.compose("down", "--remove-orphans", timeout=300)
            self.report(state="stopped", reason="idle", since=at.isoformat(), busy=False, message=None)
        else:
            self.report(busy=False, message=message)

    def tick(self):
        value = request()
        if value is None:
            self.report()
        elif value["generation"] != self.status["generation"]:
            try:
                self.apply(value)
            except (OSError, RuntimeError, subprocess.SubprocessError) as error:
                self.report(state="failed", busy=False, message=str(error) or "The runner could not start.")
                print(json.dumps({"event": "spark-runner-failed", "errorType": type(error).__name__}), flush=True)
        elif self.status["state"] == "running":
            self.watch(value)
        else:
            self.report()


def platform_check():
    if sys.platform != "linux" or os.getuid() == 0:
        raise RuntimeError("Run the Spark runner service as the non-root Linux setup user.")
    stable = HOME / "bootstrap" / "app" / "tools" / "runners" / "spark_runner_worker.py"
    require(pathlib.Path(__file__).resolve() == stable)
    os.close(open_directory(stable.parent))
    require(shutil.which("docker"))
    return stable


def run():
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
            # A malformed request or an unreadable file waits for the next change.
            print(json.dumps({"event": "spark-runner-check-failed", "errorType": type(error).__name__}), flush=True)
        time.sleep(10)


def install():
    stable = platform_check()
    os.umask(0o077)
    linger = subprocess.run(["loginctl", "show-user", str(os.getuid()), "-p", "Linger", "--value"],
                            check=True, capture_output=True, text=True, timeout=30).stdout.strip()
    if linger != "yes":
        raise RuntimeError("Approve persistent user services before installing the Spark runner service.")
    unit = pathlib.Path.home() / ".config" / "systemd" / "user" / "lucia-spark-runner.service"
    unit.parent.mkdir(parents=True, exist_ok=True)
    escaped = str(stable).replace("\\", "\\\\").replace('"', '\\"').replace("%", "%%")
    write_file(unit, "[Unit]\nDescription=Lucia on-demand GitHub Actions runner\n"
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
            run()
        else:
            raise ValueError("Usage: spark_runner_worker.py install|run")
    except Exception:
        raise SystemExit("The Spark runner service stopped. Check Docker, native paths and approved user linger.")
