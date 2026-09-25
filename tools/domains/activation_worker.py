"""Narrow native bridge: reconcile only the owned Lucia OIDC callbacks and launch URL."""

import argparse
import contextlib
import datetime
import fcntl
import hashlib
import json
import os
import pathlib
import re
import secrets
import signal
import stat
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/identity"))
import application
from provision import Provisioner

UTC = datetime.timezone.utc
JOB = re.compile(r"^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$")


class ActivationProvisioner(Provisioner):
    deadline = None

    def remaining(self):
        value = self.deadline - time.monotonic() if self.deadline is not None else 120
        if value <= 0:
            raise TimeoutError("Scoped activation exceeded its execution budget.")
        return value

    def api(self, method, path, body=None, allow_missing=False):
        if self.remaining() < 30:
            raise TimeoutError("Insufficient time remains for an identity API operation.")
        return super().api(method, path, body, allow_missing)

    def run(self, command, *, check=True, input_text=None, cwd=None):
        result = subprocess.run(command, cwd=cwd or ROOT, env=self.environment, text=True,
                                input=input_text, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                timeout=min(45, self.remaining()))
        if check and result.returncode:
            raise RuntimeError("An identity verification command failed.")
        return result


def open_directory(path):
    if not path.is_absolute() or ".." in path.parts:
        raise ValueError("Activation directories must be absolute.")
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
    descriptor = os.open("/", flags)
    try:
        for part in path.parts[1:]:
            child = os.open(part, flags, dir_fd=descriptor)
            os.close(descriptor)
            descriptor = child
            info = os.fstat(descriptor)
            if info.st_uid not in (0, os.getuid()) or info.st_mode & 0o022:
                raise ValueError("Activation parent directory is not trusted.")
        return descriptor
    except BaseException:
        os.close(descriptor)
        raise


def regular(path, maximum=32768):
    parent = open_directory(path.parent)
    try:
        descriptor = os.open(path.name, os.O_RDONLY | os.O_NONBLOCK | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
        with os.fdopen(descriptor, "rb") as stream:
            info = os.fstat(stream.fileno())
            if (not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid() or info.st_nlink != 1
                    or info.st_size > maximum or info.st_mode & 0o077):
                raise ValueError("Activation files must be private bounded files owned by the setup user.")
            data = stream.read(maximum + 1)
            if len(data) > maximum:
                raise ValueError("Activation file grew beyond its bound.")
            return data
    finally:
        os.close(parent)


def write_file(path, content):
    data = content.encode() if isinstance(content, str) else content
    parent = open_directory(path.parent)
    temporary = ".activation-" + secrets.token_hex(16) + ".tmp"
    try:
        descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600, dir_fd=parent)
        with os.fdopen(descriptor, "wb") as output:
            output.write(data)
            output.flush()
            os.fsync(output.fileno())
        os.rename(temporary, path.name, src_dir_fd=parent, dst_dir_fd=parent)
        os.fsync(parent)
    finally:
        try:
            os.unlink(temporary, dir_fd=parent)
        except FileNotFoundError:
            pass
        os.close(parent)


def parse_json(data):
    def unique(pairs):
        result = {}
        for name, value in pairs:
            if name in result:
                raise ValueError("Duplicate activation field.")
            result[name] = value
        return result
    return json.loads(data, object_pairs_hook=unique)


def refresh_ingress(state, previous):
    path = state / "gateway/domains/domain.yml"
    try:
        fingerprint = hashlib.sha256(regular(path, 65536)).hexdigest()
    except FileNotFoundError:
        fingerprint = "missing"
    if fingerprint == previous:
        return previous
    parent = open_directory(state / "gateway")
    try:
        descriptor = os.open("tls.yml", os.O_RDONLY | os.O_NONBLOCK | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
        try:
            info = os.fstat(descriptor)
            if (not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid()
                    or info.st_nlink != 1 or info.st_mode & 0o077):
                raise ValueError("Gateway reload marker must be a private owned regular file.")
            # Traefik loads nested config but watches only its top-level directory/files.
            os.utime(descriptor, None)
        finally:
            os.close(descriptor)
    finally:
        os.close(parent)
    return fingerprint


def read_json(path):
    return parse_json(regular(path))


def private_directory(path):
    parent = open_directory(path.parent)
    try:
        try:
            os.mkdir(path.name, 0o700, dir_fd=parent)
        except FileExistsError:
            pass
        descriptor = os.open(path.name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
        try:
            info = os.fstat(descriptor)
            if info.st_uid != os.getuid() or info.st_mode & 0o077:
                raise ValueError("Activation directories must be private and owned by the setup user.")
        finally:
            os.close(descriptor)
    finally:
        os.close(parent)


def request(value, job_id):
    if (not isinstance(value, dict) or set(value) != {"schemaVersion", "jobId", "expiresAt", "action", "profile"}
            or type(value["schemaVersion"]) is not int or value["schemaVersion"] != 1 or value["jobId"] != job_id
            or value["action"] not in ("prepare", "rollback") or not isinstance(value["profile"], dict)):
        raise ValueError("Invalid activation request.")
    expires = datetime.datetime.fromisoformat(value["expiresAt"].replace("Z", "+00:00"))
    now = datetime.datetime.now(UTC)
    if expires.tzinfo is None or not now < expires <= now + datetime.timedelta(minutes=30):
        raise ValueError("Activation review expired.")
    if value["profile"].get("profileId", "").lower() != job_id:
        raise ValueError("Activation profile identity mismatch.")
    return value


def process(p, directory, job_id, snapshot=None):
    value = request(snapshot if snapshot is not None else read_json(directory / "activation-requests" / (job_id + ".json")), job_id)
    host = read_json(p.state / "host-settings.json")
    origin = host["authentication"]["public_origin"]
    native = p.state / "domain-activation"
    private_directory(native)
    receipt_path = native / (job_id + ".json")
    record = application._load(p, origin)
    profile = application._domain_profile(value["profile"], record)
    snapshot = application._snapshot(p, record)
    provider, app = snapshot["provider"], snapshot["application"]
    if provider is None or app is None:
        raise ValueError("The owned Lucia registration must exist before domain activation.")
    proposed = {**snapshot, "domain_profile": profile}
    desired = application._desired(record, proposed)
    if receipt_path.exists():
        receipt = read_json(receipt_path)
        if receipt["profile"] != profile or receipt["provider_id"] != provider["pk"]:
            raise ValueError("A different activation was recorded for this request.")
    else:
        if value["action"] == "rollback":
            if application.application_status(p, origin)["ready"] and not (directory / "active.json").exists():
                return "rollback"
            raise ValueError("No unchanged registration or recorded preparation exists to roll back.")
        if not application.application_status(p, origin)["ready"]:
            raise ValueError("The existing Lucia registration needs repair before domain activation.")
        receipt = {"schemaVersion": 1, "profile": profile, "provider_id": provider["pk"],
                   "original_redirects": provider["redirect_uris"], "original_launch": app["meta_launch_url"]}
        write_file(receipt_path, json.dumps(receipt) + "\n")
    if value["action"] == "prepare":
        result = application.prepare_domain_application(p, origin, profile)
        if result.get("prepared") is not True:
            raise ValueError("Domain callback preparation was not verified.")
    else:
        active = directory / "active.json"
        if active.exists():
            raise ValueError("An active domain must not be rolled back as an unactivated request.")
        provider = p.api("GET", f"/api/v3/providers/oauth2/{receipt['provider_id']}/")
        app = p.api("GET", "/api/v3/core/applications/lucia/")
        if provider["redirect_uris"] not in (receipt["original_redirects"], desired["provider"]["redirect_uris"]):
            raise ValueError("Provider callbacks changed; automatic rollback refused.")
        if app["meta_launch_url"] not in (receipt["original_launch"], desired["application"]["meta_launch_url"]):
            raise ValueError("Application launch URL changed; automatic rollback refused.")
        p.api("PATCH", f"/api/v3/providers/oauth2/{receipt['provider_id']}/",
              {"redirect_uris": receipt["original_redirects"]})
        p.api("PATCH", "/api/v3/core/applications/lucia/", {"meta_launch_url": receipt["original_launch"]})
        if not application.application_status(p, origin)["ready"]:
            raise ValueError("The original registration was not restored.")
    return value["action"]


def run():
    if sys.platform != "linux" or os.getuid() == 0:
        raise RuntimeError("Run this worker as the non-root Spark setup user.")
    os.umask(0o077)
    state = pathlib.Path.home() / ".local/share/lucia/identity"
    args = argparse.Namespace(state=str(state), host=None, certificate_mode="private-ca",
                              auth_port=None, ldap_port=None, ca_port=None)
    p = ActivationProvisioner(args)
    p.load_existing()
    host = read_json(state / "host-settings.json")
    expected = pathlib.Path.home() / ".local/share/lucia/host/data"
    if pathlib.Path(host["data_directory"]) != expected:
        raise ValueError("The domain worker requires the owned managed host data path.")
    directory = expected / "domains"
    private_directory(directory)
    for name in ("activation-requests", "activation-responses"):
        private_directory(directory / name)
    stopping = False
    def stop(*_):
        nonlocal stopping
        stopping = True
    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    seen = {}
    ingress_fingerprint = None
    while not stopping:
        ingress_ready = True
        try:
            ingress_fingerprint = refresh_ingress(state, ingress_fingerprint)
        except (OSError, ValueError) as error:
            ingress_ready = False
            print(json.dumps({"event": "domain-ingress-reload-failed", "errorType": type(error).__name__}), flush=True)
        write_file(directory / "activation-worker.json", json.dumps({
            "schemaVersion": 1, "ready": ingress_ready, "ingressReloadVersion": 1,
            "checkedAt": datetime.datetime.now(UTC).isoformat(),
        }) + "\n")
        if not ingress_ready:
            time.sleep(5)
            continue
        candidates = sorted((directory / "activation-requests").iterdir())
        if len(candidates) > 100:
            raise ValueError("Too many activation request files; review the queue.")
        for path in candidates:
            if path.suffix != ".json" or not JOB.fullmatch(path.stem):
                continue
            fingerprint = None
            snapshot = None
            try:
                data = regular(path)
                fingerprint = hashlib.sha256(data).hexdigest()
                if seen.get(path.stem) == fingerprint:
                    continue
                previous_response = directory / "activation-responses" / path.name
                if previous_response.exists() and read_json(previous_response).get("requestHash") == fingerprint:
                    seen[path.stem] = fingerprint
                    continue
                snapshot = request(parse_json(data), path.stem)
                action = snapshot["action"]
                with contextlib.ExitStack() as stack:
                    for lock_path in (ROOT.parent / ".run.lock", state / ".provision.lock"):
                        lock = stack.enter_context(lock_path.open("a+b"))
                        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    p.load_existing()
                    p.deadline = time.monotonic() + 120
                    process(p, directory, path.stem, snapshot)
                result = {"success": True, "message": "Owned application registration updated and verified."}
            except BlockingIOError:
                continue
            except Exception as error:
                print(json.dumps({"event": "domain-activation-failed", "jobId": path.stem,
                                  "errorType": type(error).__name__}), flush=True)
                result = {"success": False, "message": "The scoped activation service could not update the owned registration. Check its private state before retrying."}
                action = snapshot["action"] if snapshot is not None else "unknown"
            if fingerprint is None:
                continue
            seen[path.stem] = fingerprint
            write_file(directory / "activation-responses" / (path.stem + ".json"), json.dumps({
                "schemaVersion": 1, "jobId": path.stem, "action": action, **result,
                "requestHash": fingerprint,
                "checkedAt": datetime.datetime.now(UTC).isoformat(),
            }) + "\n")
        time.sleep(5)
    write_file(directory / "activation-worker.json", json.dumps({
        "schemaVersion": 1, "ready": False, "checkedAt": datetime.datetime.now(UTC).isoformat(),
    }) + "\n")


def install():
    if sys.platform != "linux" or os.getuid() == 0:
        raise RuntimeError("Install the user worker as the Spark setup account.")
    stable = pathlib.Path.home() / ".local/share/lucia/bootstrap/app/tools/domains/activation_worker.py"
    if pathlib.Path(__file__).resolve() != stable:
        raise ValueError("Install only from the stable desktop-managed bootstrap source.")
    linger = subprocess.run(["loginctl", "show-user", str(os.getuid()), "-p", "Linger", "--value"],
                            check=True, capture_output=True, text=True).stdout.strip()
    if linger != "yes":
        raise RuntimeError("Approve and enable persistent user services before installing domain activation.")
    unit = pathlib.Path.home() / ".config/systemd/user/lucia-domain-activation.service"
    unit.parent.mkdir(parents=True, exist_ok=True)
    escaped = str(stable).replace("\\", "\\\\").replace('"', '\\"').replace("%", "%%")
    write_file(unit, "[Unit]\nDescription=Lucia scoped domain activation\n"
               "[Service]\nType=simple\nExecStart=/usr/bin/python3 \"" + escaped + "\" run\n"
               "Restart=on-failure\nRestartSec=5\nUMask=0077\nNoNewPrivileges=yes\n"
               "[Install]\nWantedBy=default.target\n")
    subprocess.run(["systemctl", "--user", "daemon-reload"], check=True)
    subprocess.run(["systemctl", "--user", "enable", "lucia-domain-activation.service"], check=True)
    subprocess.run(["systemctl", "--user", "restart", "lucia-domain-activation.service"], check=True)


if __name__ == "__main__":
    if sys.argv[1:] == ["install"]:
        install()
    elif sys.argv[1:] == ["run"]:
        run()
    else:
        raise SystemExit("Usage: activation_worker.py install|run")
