#!/usr/bin/env python3
"""Updates the Spark's third-party platform images (Authentik, the gateway, the CA, the directory, the database and the
telemetry relay) when the owner asks; never run inside the web container.

install|run, from ~/.local/share/lucia/bootstrap/app/tools/runners/platform_update_worker.py.
The web host writes host/data/lucia-platform-updates/request.json with EXACT keys: schemaVersion (1), generation (integer,
raised on every request), requestedAt and updates, a list of {service, tag, digest}. The web host has no Docker access and
isn't trusted: this worker accepts only known services, reads each digest from the registry itself, checks the policy class
in platform_images.py, then writes identity/images.override.json and redeploys the identity stack as deploy-agent-jobs.py
does, after pre-flight checks (the NVIDIA device list, free disk, no other deploy or package install, and a database dump
before an Authentik release line). It waits for every service to come back and rolls back on failure. Reports in status.json.
"""

import argparse
import contextlib
import datetime
import fcntl
import gzip
import json
import os
import pathlib
import shutil
import signal
import subprocess
import sys
import threading
import time

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "tools" / "domains"), str(ROOT / "tools" / "identity"), str(pathlib.Path(__file__).resolve().parent)]
from activation_worker import open_directory, parse_json, private_directory, regular, write_file
import platform_images as images

HOME = pathlib.Path.home() / ".local" / "share" / "lucia"
SHARED = HOME / "host" / "data" / "lucia-platform-updates"
PACKAGES = HOME / "host" / "data" / "packages" / "status.json"
STATE = HOME / "identity"
BOOTSTRAP = HOME / "bootstrap"
OVERRIDE = STATE / "images.override.json"
MANIFEST = STATE / "platform-images.json"
BACKUPS = STATE / "backups"
KEEP_BACKUPS = 3
MIN_FREE = 5 * 1024 ** 3
HEALTH_SECONDS = 300
IDENTITY = ("lucia-identity-db", "lucia-identity-ldap", "lucia-identity-ca", "lucia-identity-server", "lucia-identity-worker",
            "lucia-identity-gateway", "lucia-identity-renewer")
TELEMETRY = ("lucia-telemetry-relay", "lucia-telemetry-node-exporter", "lucia-telemetry-gpu-exporter")
STATUS_FIELDS = ("schemaVersion", "generation", "state", "step", "message", "startedAt", "finishedAt", "updates", "rolledBack",
                 "backup", "history", "checkedAt")


def now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


class Refused(Exception):
    """A request the worker won't run, with a message for the owner; nothing has changed."""


class Busy(Exception):
    """Another deploy or package install holds the Spark; nothing has changed."""


def run(*args, timeout=120, stdin=None):
    result = subprocess.run(args, capture_output=True, text=True, timeout=timeout, input=stdin)
    if result.returncode:
        raise RuntimeError((result.stderr or result.stdout).strip()[-300:] or f"{args[0]} failed.")
    return result.stdout.strip()


def short(error):
    """An error's first line for the owner; provision.py has already redacted secrets from command output."""
    text = str(error).strip()
    return text.splitlines()[0][:300] if text else type(error).__name__


def read_json(path, maximum=65536):
    return parse_json(regular(path, maximum))


def provisioner():
    from provision import Provisioner
    p = Provisioner(argparse.Namespace(state=str(STATE), host=None, certificate_mode="private-ca",
                                       auth_port=None, ldap_port=None, ca_port=None))
    p.load_existing()
    return p


@contextlib.contextmanager
def deploy_locks():
    """The locks every identity deploy takes (bootstrap and provision.py), without waiting."""
    with contextlib.ExitStack() as stack:
        for path in (BOOTSTRAP / ".run.lock", STATE / ".provision.lock"):
            if path.is_symlink():
                raise RuntimeError("A deploy lock is a symbolic link.")
            lock = stack.enter_context(path.open("a+b"))
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                raise Busy("Another identity deploy or Lucia update is running on the Spark. Try again when it finishes.") from None
        yield


def package_install_running():
    try:
        status = read_json(PACKAGES, 2 * 1024 * 1024)
    except FileNotFoundError:
        return False
    return isinstance(status, dict) and isinstance(status.get("operation"), dict)


def plan(updates, manifest):
    """Each update checked against the registry and the policy: (service, image entry, tag, digest, kind)."""
    chosen = []
    for update in updates:
        service, tag, digest = update["service"], update["tag"], update["digest"]
        image = manifest[service]
        token = [None]
        served = images.registry_digest(image["registry"], image["repository"], tag, token)
        if served != digest:
            raise Refused(f"The registry now serves a different {image['repository']}:{tag} than Lucia found. Check for updates again.")
        if tag == image["tag"]:
            if digest == image["digest"]:
                raise Refused(f"{service} already runs {tag} at that digest.")
            kind = "rebuild"
        else:
            tags = images.registry_tags(image["registry"], image["repository"], token)
            if tag not in tags or not images.allowed(image["policy"], image["tag"], tag, tags):
                raise Refused(f"{tag} isn't an update Lucia applies to {service} (its policy is {image['policy']}).")
            kind = "release-line" if image["policy"] == "authentik" and images.release_line(image["tag"], tag) else "update"
        chosen.append((service, image, tag, digest, kind))
    if any(kind == "release-line" for *_, kind in chosen) and len(chosen) > 1:
        raise Refused("Update Authentik to a new release line on its own, after the other updates.")
    return chosen


def containers(manifest, services):
    names = {"lucia-" + name for service in services for name in manifest[service]["containers"]}
    return sorted(names)


def dump_database(source, target):
    """pg_dump of Authentik's database before a release line migrates it; the last three are kept."""
    private_directory(BACKUPS)
    name = f"authentik-{source}-{target}.sql.gz"
    final, partial = BACKUPS / name, BACKUPS / (name + ".partial")
    parent = open_directory(BACKUPS)
    try:
        with contextlib.suppress(FileNotFoundError):
            os.unlink(partial.name, dir_fd=parent)
        descriptor = os.open(partial.name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600, dir_fd=parent)
    finally:
        os.close(parent)
    process = subprocess.Popen(["docker", "exec", "lucia-identity-db", "pg_dump", "-U", "postgres", "-d", "authentik"],
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    with os.fdopen(descriptor, "wb") as raw, gzip.GzipFile(fileobj=raw, mode="wb") as output:
        shutil.copyfileobj(process.stdout, output, 1024 * 1024)
    error = process.stderr.read().decode(errors="replace")
    if process.wait(timeout=1800) or partial.stat().st_size < 64:
        partial.unlink(missing_ok=True)
        raise RuntimeError("Authentik's database couldn't be backed up: " + (error.strip()[-200:] or "pg_dump failed."))
    os.replace(partial, final)
    for old in sorted(BACKUPS.glob("authentik-*.sql.gz"), key=lambda item: item.stat().st_mtime)[:-KEEP_BACKUPS]:
        old.unlink(missing_ok=True)
    return final


def restore_database(backup):
    """Puts Authentik's database back as it was before a release line, with Authentik stopped."""
    run("docker", "stop", "lucia-identity-server", "lucia-identity-worker", timeout=180)
    run("docker", "exec", "lucia-identity-db", "psql", "-U", "postgres", "-d", "postgres", "-v", "ON_ERROR_STOP=1",
        "-c", "DROP DATABASE IF EXISTS authentik WITH (FORCE)", "-c", "CREATE DATABASE authentik", timeout=300)
    process = subprocess.Popen(["docker", "exec", "-i", "lucia-identity-db", "psql", "-U", "postgres", "-d", "authentik", "-q",
                                "-v", "ON_ERROR_STOP=1"], stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    with gzip.open(backup, "rb") as source:
        shutil.copyfileobj(source, process.stdin, 1024 * 1024)
    process.stdin.close()
    error = process.stderr.read().decode(errors="replace")
    if process.wait(timeout=1800):
        raise RuntimeError("Authentik's database couldn't be restored from " + backup.name + ": " + error.strip()[-200:])


def free_disk():
    root = run("docker", "info", "--format", "{{.DockerRootDir}}")
    free = shutil.disk_usage(root if root.startswith("/") else "/").free
    if free < MIN_FREE:
        raise Refused(f"The Spark has {free // 1024 ** 3} GB free for images; Lucia needs at least {MIN_FREE // 1024 ** 3} GB. Free some disk first.")


def retry(check, deadline, pause=5):
    while True:
        try:
            return check()
        except Exception:
            if time.monotonic() + pause > deadline:
                raise
            time.sleep(pause)


def healthy(p, manifest, chosen, origin, telemetry):
    """Waits for every service to come back (Authentik needs about 30 s), then checks each one answers."""
    import application
    deadline = time.monotonic() + HEALTH_SECONDS
    for name in IDENTITY + (TELEMETRY if telemetry else ()):
        p.wait_container(name, timeout=max(30, int(deadline - time.monotonic())))
    retry(lambda: p.wait_gateway_tls(timeout=30), deadline)
    retry(p.check_ldap_tls, deadline)
    if origin:
        def ready():
            status = application.application_status(p, origin)
            if not status["ready"]:
                raise RuntimeError("Authentik isn't ready: " + "; ".join(status["reasons"])[:200])
        retry(ready, deadline)
    for service, image, tag, digest, _ in chosen:
        for name in containers(manifest, [service]):
            if name in TELEMETRY and not telemetry:
                continue
            running = run("docker", "inspect", "-f", "{{.Config.Image}}", name)
            if not images.moved(running, digest):
                raise RuntimeError(f"{name} didn't move to {tag}.")


class Worker:
    def __init__(self):
        self.lock = threading.Lock()
        self.job = None
        self.status = {"schemaVersion": 1, "generation": -1, "state": "idle", "step": None, "message": None, "startedAt": None,
                       "finishedAt": None, "updates": [], "rolledBack": False, "backup": None, "history": [], "checkedAt": now()}
        try:
            previous = read_json(SHARED / "status.json", 65536)
            if isinstance(previous, dict) and set(previous) == set(STATUS_FIELDS) and type(previous["generation"]) is int:
                self.status = previous
        except (FileNotFoundError, ValueError):
            pass
        if self.status["state"] == "working":
            self.finish("failed", "Lucia's platform update service restarted during this update. Check Platform images: the "
                        "Spark may still run the previous images, and the update can be tried again.")

    def report(self, **changes):
        with self.lock:
            self.status.update(changes, checkedAt=now())
            write_file(SHARED / "status.json", json.dumps(self.status) + "\n")

    def finish(self, state, message, **changes):
        entry = {"generation": self.status["generation"], "state": state, "message": message, "updates": self.status["updates"],
                 "startedAt": self.status["startedAt"], "finishedAt": now()}
        self.report(state=state, step=None, message=message, finishedAt=entry["finishedAt"],
                    history=[entry, *self.status["history"]][:10], **changes)

    def apply(self, generation, updates):
        self.report(generation=generation, state="working", step="verifying", startedAt=now(), finishedAt=None, rolledBack=False,
                    backup=None, updates=updates, message="Checking each image with its registry.")
        try:
            manifest = images.validate_manifest(read_json(MANIFEST))
            chosen = plan(updates, manifest)
            self.report(step="preflight", message="Checking the Spark is ready.")
            gpu = images.gpu_devices()
            if not gpu["current"]:
                raise Refused(images.STALE_GPU)
            if package_install_running():
                raise Busy("Lucia is installing Spark packages. Try again when they finish.")
            free_disk()
            with deploy_locks():
                self.deploy(manifest, chosen)
        except Busy as error:
            self.finish("busy", str(error))
        except Refused as error:
            self.finish("failed", str(error))
        except Exception as error:
            print(json.dumps({"event": "platform-update-failed", "errorType": type(error).__name__}), flush=True)
            self.finish("failed", "The update stopped before any service restarted: " + short(error))

    def deploy(self, manifest, chosen):
        p = provisioner()
        host = read_json(STATE / "host-settings.json") if (STATE / "host-settings.json").exists() else None
        origin = host["authentication"]["public_origin"] if host else None
        try:
            previous = regular(OVERRIDE, 16384)
        except FileNotFoundError:
            previous = None
        backup = None
        release = next(((image, tag) for _, image, tag, _, kind in chosen if kind == "release-line"), None)
        if release:
            self.report(step="backing-up", message="Backing up Authentik's database before its release line changes.")
            backup = dump_database(release[0]["tag"], release[1])
            self.report(backup=backup.name)
        entries = parse_json(previous).get("images") if previous else {}
        entries = dict(entries) if isinstance(entries, dict) else {}
        for service, image, tag, digest, _ in chosen:
            entries[service] = images.override_entry(image, tag, digest)
        write_file(OVERRIDE, json.dumps({"schemaVersion": 1, "images": entries}, indent=2) + "\n")
        deploying = False
        try:
            self.report(step="deploying", message="Pulling the new images and restarting their services.")
            p.publish()
            deploying = True
            p.deploy()
            self.report(step="health-checking", message="Waiting for every service to come back.")
            healthy(p, manifest, chosen, origin, host is not None)
        except Exception as error:
            reason = short(error)
            self.report(step="rolling-back", message="The update failed; putting the previous images back.")
            try:
                if previous is None:
                    OVERRIDE.unlink(missing_ok=True)
                else:
                    write_file(OVERRIDE, previous)
                if backup and deploying:
                    restore_database(backup)
                # Publishing again also rewrites the manifests with the previous pins.
                p.publish()
                if deploying:
                    p.deploy()
                    healthy(p, manifest, [], origin, host is not None)
            except Exception as again:
                print(json.dumps({"event": "platform-rollback-failed", "errorType": type(again).__name__}), flush=True)
                return self.finish("failed", f"The update failed ({reason}) and so did putting the previous images back"
                                   + (f" (Authentik's database backup is {backup.name})" if backup else "")
                                   + ". Run the host deploy or provision.py on the Spark to restore the identity stack.", rolledBack=False)
            return self.finish("failed", f"The update failed ({reason}). " + ("Lucia put the previous images back"
                               + (" and restored Authentik's database." if backup else ".") if deploying else "No service restarted."),
                               rolledBack=deploying)
        names = ", ".join(f"{image['repository']} {tag}" for _, image, tag, _, _ in chosen)
        self.finish("succeeded", f"Updated {names}. Every service came back healthy.")

    def tick(self):
        if self.job and self.job.is_alive():
            return self.report()
        try:
            value = read_json(SHARED / "request.json", 16384)
        except FileNotFoundError:
            return self.report()
        if type(value) is not dict or value.get("generation") == self.status["generation"]:
            return self.report()
        try:
            updates = images.validate_request(value)
        except ValueError:
            self.report(generation=value.get("generation") if type(value.get("generation")) is int else self.status["generation"],
                        updates=[], startedAt=now())
            return self.finish("failed", "Lucia's platform update service refused this request.")
        self.job = threading.Thread(target=self.apply, args=(value["generation"], updates), daemon=True)
        self.job.start()


def platform_check():
    if sys.platform != "linux" or os.getuid() == 0:
        raise RuntimeError("Run the platform update service as the non-root Linux setup user.")
    stable = HOME / "bootstrap" / "app" / "tools" / "runners" / "platform_update_worker.py"
    if pathlib.Path(__file__).resolve() != stable:
        raise ValueError("Run the platform update service from Lucia's installed copy.")
    os.close(open_directory(stable.parent))
    if not shutil.which("docker"):
        raise RuntimeError("Docker is required.")
    return stable


def serve():
    platform_check()
    os.umask(0o077)
    private_directory(SHARED)
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
            print(json.dumps({"event": "platform-update-check-failed", "errorType": type(error).__name__}), flush=True)
        time.sleep(10)
    # An update in progress finishes or rolls back before the service stops (TimeoutStopSec allows it).
    while worker.job and worker.job.is_alive():
        worker.job.join(10)
        with contextlib.suppress(OSError, ValueError):
            worker.report()


def install():
    stable = platform_check()
    os.umask(0o077)
    linger = subprocess.run(["loginctl", "show-user", str(os.getuid()), "-p", "Linger", "--value"],
                            check=True, capture_output=True, text=True, timeout=30).stdout.strip()
    if linger != "yes":
        raise RuntimeError("Approve persistent user services before installing the platform update service.")
    private_directory(SHARED)
    unit = pathlib.Path.home() / ".config" / "systemd" / "user" / "lucia-platform-update.service"
    unit.parent.mkdir(parents=True, exist_ok=True)
    escaped = str(stable).replace("\\", "\\\\").replace('"', '\\"').replace("%", "%%")
    write_file(unit, "[Unit]\nDescription=Lucia platform image updates\n"
               "[Service]\nType=simple\nExecStart=/usr/bin/python3 \"" + escaped + "\" run\n"
               "Restart=on-failure\nRestartSec=5\nUMask=0077\nNoNewPrivileges=yes\n"
               "# A running update must finish or roll back, not die with the service.\nTimeoutStopSec=20min\n"
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
            raise ValueError("Usage: platform_update_worker.py install|run")
    except Exception:
        raise SystemExit("The platform update service stopped. Check Docker, native paths and approved user linger.")
