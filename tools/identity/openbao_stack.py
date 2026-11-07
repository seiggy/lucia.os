#!/usr/bin/env python3
"""Own the existing Spark vault in a separate Compose project; never initialize or unseal it."""

import argparse
import datetime
import fcntl
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import signal
import ssl
import stat
import subprocess
import tarfile
import time

ROOT = Path(__file__).resolve().parents[2]
import sys
sys.path.insert(0, str(ROOT / "tools" / "domains"))
from activation_worker import parse_json, private_directory, regular, write_file

HOME = Path.home() / ".local/share/lucia"
IDENTITY = HOME / "identity"
PRIVATE = HOME / "openbao-stack"
SHARED = HOME / "host/data/openbao-stack"
PROJECT = "lucia-openbao"
CONTAINER = "lucia-identity-openbao"
IMAGE = "openbao/openbao:2.7.2@sha256:5046911075e137e154ce49ab051502f4ec7cf367d484ae4a4dce665d9b90561f"
UNIT = "lucia-openbao-stack.service"


def now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def command(*args):
    result = subprocess.run(args, capture_output=True, text=True, timeout=120)
    if result.returncode:
        # Never put raw Docker output, environment or credentials into shared state.
        raise RuntimeError(f"{args[0]} operation failed (exit {result.returncode}). Check the private service journal.")
    return result.stdout


def read(path, maximum=16384):
    return parse_json(regular(path, maximum))


def request(value):
    if (not isinstance(value, dict) or set(value) != {"schemaVersion", "generation", "desired"}
            or type(value["schemaVersion"]) is not int or value["schemaVersion"] != 1
            or type(value["generation"]) is not int or value["generation"] < 0
            or value["desired"] not in ("running", "stopped")):
        raise ValueError("OpenBao accepts only a versioned start or stop request.")
    return value


def inspect():
    result = subprocess.run(["docker", "inspect", CONTAINER], capture_output=True, text=True, timeout=30)
    if result.returncode:
        if "No such object" in result.stderr or "no such object" in result.stderr:
            return None
        raise RuntimeError("Docker could not inspect OpenBao.")
    value = json.loads(result.stdout)[0]
    if value["Config"].get("Labels", {}).get("com.docker.compose.project") != PROJECT:
        raise RuntimeError("OpenBao belongs to another Compose project; refusing to adopt or stop it.")
    return value


def compose(network, uid, gid, endpoint):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+", network) or uid <= 0 or gid <= 0:
        raise ValueError("Invalid existing identity network or service user.")
    mounts = [
        ("openbao/data", "/bao/data", False), ("openbao/audit", "/bao/audit", False),
        ("openbao/config", "/bao/config", True), ("certificates", "/certificates", True),
        ("trust", "/trust", True),
    ]
    return {"name": PROJECT, "services": {"openbao": {
        "image": IMAGE, "container_name": CONTAINER, "restart": "unless-stopped",
        "user": f"{uid}:{gid}", "read_only": True, "cap_drop": ["ALL"],
        "security_opt": ["no-new-privileges:true"],
        "entrypoint": ["/bin/sh"], "command": ["/bao/config/start-openbao.sh"],
        "environment": {"BAO_ADDR": "https://127.0.0.1:8200", "BAO_CACERT": "/trust/lucia-root-ca.crt",
                        "BAO_TLS_SERVER_NAME": "identity-gateway"},
        "ports": [f"127.0.0.1:{endpoint}:8200"],
        "volumes": [{"type": "bind", "source": str(IDENTITY / source), "target": target,
                     "read_only": readonly, "bind": {"create_host_path": False}} for source, target, readonly in mounts],
        "networks": {"identity": {"aliases": ["identity-openbao"]}},
        "labels": {"io.lucia.component": "openbao-stack"},
        "healthcheck": {"test": ["CMD", "/bin/sh", "/bao/config/openbao-health.sh"],
                        "interval": "10s", "timeout": "5s", "retries": 3},
    }}, "networks": {"identity": {"external": True, "name": network}}}


def docker_compose(*args):
    command("docker", "compose", "-p", PROJECT, "-f", str(PRIVATE / "compose.json"), *args)


def prepare():
    """Explicit recovery of an existing vault only, with a stopped-data copy before first start."""
    settings = read(IDENTITY / "openbao-settings.json")
    if settings.get("bind_address") != "127.0.0.1" or type(settings.get("port")) is not int:
        raise ValueError("Recovery requires the existing loopback TLS listener.")
    for relative in ["openbao/data/raft/raft.db", "openbao/data/vault.db", "openbao/config/server.hcl",
                     "openbao/config/start-openbao.sh", "openbao/config/openbao-health.sh",
                     "certificates/ready.sha256", "trust/lucia-root-ca.crt"]:
        path = IDENTITY / relative
        if relative == "trust/lucia-root-ca.crt":
            metadata = path.lstat()
            if not stat.S_ISREG(metadata.st_mode) or metadata.st_uid != os.getuid() or metadata.st_mode & 0o022:
                raise ValueError("The existing public CA must be an owned regular file, not writable by other users.")
        else:
            regular(path, 1024 * 1024 * 1024)
        if not path.stat().st_size:
            raise ValueError(f"Existing vault recovery file is empty: {relative}.")
    identity = read(IDENTITY / "settings.json")
    gateway = json.loads(command("docker", "inspect", "lucia-identity-gateway"))[0]
    networks = list(gateway["NetworkSettings"]["Networks"])
    if len(networks) != 1:
        raise ValueError("The identity gateway must have one existing network.")
    private_directory(PRIVATE)
    private_directory(SHARED)
    backup = PRIVATE / "pre-recovery.tar.gz"
    receipt = PRIVATE / "recovery-copy.json"
    if backup.exists():
        expected = read(receipt)
        regular(backup, 1024 * 1024 * 1024)
        if expected.get("sha256") != hashlib.sha256(backup.read_bytes()).hexdigest():
            raise RuntimeError("The saved cold recovery copy failed verification; inspect it before restarting.")
    if not backup.exists():
        if inspect() is not None:
            raise RuntimeError("Stop the existing vault before taking its cold recovery copy.")
        with backup.open("xb") as output, tarfile.open(fileobj=output, mode="w:gz") as archive:
            for folder in ["data", "audit", "config"]:
                archive.add(IDENTITY / "openbao" / folder, arcname=folder)
        os.chmod(backup, 0o600)
        digest = hashlib.sha256(backup.read_bytes()).hexdigest()
        with tarfile.open(backup, "r:gz") as archive:
            if not {"data/raft/raft.db", "data/vault.db"} <= set(archive.getnames()):
                raise RuntimeError("The cold recovery copy is incomplete.")
        write_file(receipt, json.dumps({"createdAt": now(), "sha256": digest}) + "\n")
    body = compose(networks[0], identity["uid"], identity["gid"], settings["port"])
    write_file(PRIVATE / "compose.json", json.dumps(body, indent=2) + "\n")
    docker_compose("config", "--quiet")
    write_file(IDENTITY / "openbao/stack.json", json.dumps({"schemaVersion": 1, "project": PROJECT}) + "\n")
    if not (SHARED / "request.json").exists():
        write_file(SHARED / "request.json", json.dumps({"schemaVersion": 1, "generation": 0, "desired": "running"}) + "\n")


def health():
    settings = read(IDENTITY / "openbao-settings.json")
    # The listener's certificate is for identity-gateway, not the loopback address.
    context = ssl.create_default_context(cafile=str(IDENTITY / "trust/lucia-root-ca.crt"))
    import socket
    class LoopbackConnection(http.client.HTTPSConnection):
        def connect(self):
            self.sock = context.wrap_socket(
                socket.create_connection(("127.0.0.1", settings["port"]), timeout=5),
                server_hostname="identity-gateway")
    connection = LoopbackConnection("identity-gateway", settings["port"], timeout=5, context=context)
    try:
        connection.request("GET", "/v1/sys/health")
        response = connection.getresponse()
        value = json.loads(response.read(16384))
        if response.status not in (200, 429, 472, 473, 501, 503) or any(
                type(value.get(field)) is not bool for field in ("initialized", "sealed")):
            raise ValueError("OpenBao returned invalid health metadata.")
        return {"initialized": value.get("initialized"), "sealed": value.get("sealed")}
    finally:
        connection.close()


def report(generation, state, message=None, initialized=None, sealed=None):
    backup = None
    path = IDENTITY / "openbao/backup-status.json"
    if path.exists():
        value = read(path)
        backup = {"state": value.get("state"), "lastSuccessAt": value.get("last_success_at")}
    routes = (IDENTITY / "gateway/openbao.yml").read_text()
    host = re.search(r"Host\(`([a-z0-9.-]+)`\)", routes)
    write_file(SHARED / "status.json", json.dumps({
        "schemaVersion": 1, "configured": True, "generation": generation, "state": state, "message": message,
        "initialized": initialized, "sealed": sealed, "checkedAt": now(), "backup": backup, "image": IMAGE,
        "url": "https://" + host[1] if host else None,
    }) + "\n")


def tick():
    desired = request(read(SHARED / "request.json"))
    applied = read(PRIVATE / "applied.json") if (PRIVATE / "applied.json").exists() else None
    current = inspect()
    if applied != desired:
        if desired["desired"] == "running":
            report(desired["generation"], "starting", "Starting the existing vault; manual unseal may be needed.")
            docker_compose("up", "-d", "--no-build", "--pull", "never")
        elif current and current["State"]["Running"]:
            docker_compose("stop")
        write_file(PRIVATE / "applied.json", json.dumps(desired) + "\n")
        current = inspect()
    if not current:
        report(desired["generation"], "missing", "The OpenBao container is missing. Start it to reuse its existing data.")
    elif not current["State"]["Running"]:
        report(desired["generation"], "stopped" if desired["desired"] == "stopped" else "failed",
               "The vault is stopped." if desired["desired"] == "stopped" else "OpenBao exited. Check the private service journal.")
    else:
        value = health()
        state = "uninitialized" if value["initialized"] is False else "sealed" if value["sealed"] else "running"
        report(desired["generation"], state, initialized=value["initialized"], sealed=value["sealed"],
               message="Enter the existing unseal shares privately in OpenBao." if state == "sealed" else
               "Unexpected empty vault. Do not initialize it; check its data mounts." if state == "uninitialized" else None)


def run():
    lock = (PRIVATE / "worker.lock").open("a+b")
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    stopping = False

    def stop(*_):
        nonlocal stopping
        stopping = True

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    while not stopping:
        try:
            tick()
        except (OSError, ValueError, RuntimeError, http.client.HTTPException, subprocess.SubprocessError) as error:
            print(json.dumps({"event": "openbao-stack-failed", "errorType": type(error).__name__}), flush=True)
            # Failed generations are not acknowledged, so transient Docker/boot failures can retry.
            report(-1, "failed", "Lucia could not manage OpenBao. Check lucia-openbao-stack.service on the Spark.")
        time.sleep(10)


def install():
    if command("loginctl", "show-user", str(os.getuid()), "-p", "Linger", "--value").strip() != "yes":
        raise RuntimeError("Persistent user services must already be approved.")
    prepare()
    unit = Path.home() / ".config/systemd/user" / UNIT
    unit.parent.mkdir(parents=True, exist_ok=True)
    path = str(Path(__file__).resolve()).replace("\\", "\\\\").replace('"', '\\"').replace("%", "%%")
    write_file(unit, "[Unit]\nDescription=Lucia persistent OpenBao stack\n"
               "[Service]\nType=simple\nExecStart=/usr/bin/python3 \"" + path + "\" run\n"
               "Restart=on-failure\nRestartSec=10\nUMask=0077\nNoNewPrivileges=yes\n"
               "[Install]\nWantedBy=default.target\n")
    for args in [("daemon-reload",), ("enable", UNIT), ("restart", UNIT)]:
        command("systemctl", "--user", *args)


if __name__ == "__main__":
    os.umask(0o077)
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=["install", "run"])
    action = parser.parse_args().action
    if sys.platform != "linux" or os.getuid() == 0:
        parser.error("Run as the existing Spark setup user on Linux.")
    install() if action == "install" else run()
