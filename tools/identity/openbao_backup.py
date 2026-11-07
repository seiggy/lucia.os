"""Independent Raft snapshot -> existing restic repository job. Never initializes a vault or a repository."""

import argparse
import datetime
import gzip
import http.client
import json
import os
import pathlib
import re
import shutil
import ssl
import stat
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
import zlib

from provision import Provisioner, authority, validate_host, write_file

TAG = "lucia:foundation=openbao"
POLICY = "lucia-openbao-backup"
UTC = datetime.timezone.utc


class BackupError(RuntimeError):
    """A credential-free error suitable for the journal and status."""


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        response.close()
        raise BackupError("OpenBao redirected the backup request; configure its direct verified TLS endpoint.")


def private_file(path, maximum=16384):
    path = pathlib.Path(path).absolute()
    if path != path.resolve():
        raise BackupError("Backup inputs must not contain symbolic links.")
    try:
        info = path.stat()
    except FileNotFoundError:
        raise BackupError(f"Private backup input is missing: {path.name}.") from None
    if not stat.S_ISREG(info.st_mode) or info.st_size > maximum:
        raise BackupError("Backup input is not a bounded regular file.")
    if hasattr(os, "getuid") and (info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) & 0o077):
        raise BackupError("Backup inputs must be privately owned by the job user.")
    return path.read_text(encoding="utf-8")


def configuration(state):
    value = json.loads(private_file(state / "openbao/backup.json"))
    keys = {"schema_version", "repository", "mount", "keep_daily", "keep_weekly", "keep_monthly",
            "share_count", "share_threshold", "custody_confirmed", "backup_recovery_confirmed"}
    if (type(value) is not dict or set(value) != keys or type(value["schema_version"]) is not int
            or value["schema_version"] != 1):
        raise BackupError("Backup configuration requires exactly the documented schema 1 fields.")
    for key in ("keep_daily", "keep_weekly", "keep_monthly"):
        if type(value[key]) is not int or not 0 <= value[key] <= 999:
            raise BackupError("Backup retention must use explicit nonnegative counts, at most 999.")
    if not any(value[key] for key in ("keep_daily", "keep_weekly", "keep_monthly")):
        raise BackupError("At least one retention window must keep snapshots.")
    if (type(value["share_count"]) is not int or type(value["share_threshold"]) is not int
            or not 1 <= value["share_threshold"] <= value["share_count"] <= 255
            or value["custody_confirmed"] is not True or value["backup_recovery_confirmed"] is not True):
        raise BackupError("Record the offline custody threshold and independent backup recovery before enabling this job.")
    for key in ("repository", "mount"):
        if not isinstance(value[key], str):
            raise BackupError("Backup storage paths must be canonical absolute directories.")
        path = pathlib.Path(value[key])
        if not path.is_absolute() or path != path.resolve() or not path.is_dir():
            raise BackupError("Backup storage paths must be existing canonical absolute directories without links.")
    repository, mount = pathlib.Path(value["repository"]), pathlib.Path(value["mount"])
    if not repository.is_relative_to(mount):
        raise BackupError("The restic repository must remain inside the configured NAS mount.")
    return value


def require_nas_mount(config):
    mount = config["mount"]
    if not os.path.ismount(mount):
        raise BackupError("The backup NAS is not mounted; refusing to write into a local mount-point directory.")
    for line in pathlib.Path("/proc/self/mountinfo").read_text().splitlines():
        before, separator, after = line.partition(" - ")
        fields = before.split()
        if separator and len(fields) >= 5:
            target = re.sub(r"\\([0-7]{3})", lambda match: chr(int(match[1], 8)), fields[4])
            if target == mount and after.split()[0] in ("cifs", "nfs", "nfs4"):
                return
    raise BackupError("The backup destination must be a verified CIFS or NFS mount.")


def status(state):
    path = state / "openbao/backup-status.json"
    value = json.loads(private_file(path)) if path.exists() else {"state": "never-backed-up", "last_success_at": None}
    fields = {"state", "started_at", "finished_at", "last_success_at", "snapshot", "error"}
    if (type(value) is not dict or set(value) - fields
            or value.get("state") not in ("never-backed-up", "running", "succeeded", "failed")):
        raise BackupError("The private backup status has an invalid shape.")
    now = datetime.datetime.now(UTC)
    if value.get("last_success_at") is not None and not isinstance(value["last_success_at"], str):
        raise BackupError("The private backup status has an invalid success timestamp.")
    last = datetime.datetime.fromisoformat(value["last_success_at"]) if value.get("last_success_at") else None
    if last is not None and last.tzinfo is None:
        raise BackupError("The private backup status timestamp must include its timezone.")
    age = (now - last).total_seconds() if last else None
    return {**value, "healthy": value["state"] == "succeeded" and age is not None and 0 <= age < 86400,
            "age_seconds": age, "checked_at": now.isoformat()}


def restic(config, state, arguments):
    require_nas_mount(config)
    environment = {key: value for key, value in os.environ.items() if not key.startswith(("RESTIC_", "BAO_", "VAULT_"))}
    password = state / "secrets/openbao-restic-password"
    if not private_file(password).strip():
        raise BackupError("The independently recoverable restic password file is empty.")
    environment.update(RESTIC_REPOSITORY=config["repository"], RESTIC_PASSWORD_FILE=str(password),
                       RESTIC_CACHE_DIR=str(state / "openbao/restic-cache"))
    with tempfile.TemporaryFile() as output, tempfile.TemporaryFile() as errors:
        result = subprocess.run(["restic", *arguments], env=environment, stdin=subprocess.DEVNULL,
                                stdout=output, stderr=errors, timeout=7200, check=False)
        if result.returncode:
            raise BackupError(f"restic {arguments[0]} failed with exit {result.returncode}; no successful backup is recorded.")
        output.seek(0)
        data = output.read(4 * 1024 * 1024 + 1)
        if len(data) > 4 * 1024 * 1024:
            raise BackupError("restic output exceeded its 4 MB limit.")
        return data.decode("utf-8")


def api(address, trust, method, route, body=None, token=None):
    context = ssl.create_default_context(cafile=str(trust))
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    request = urllib.request.Request(address + "/v1/" + route, method=method,
                                    data=json.dumps(body).encode() if body is not None else None,
                                    headers={"Content-Type": "application/json", **({"X-Vault-Token": token} if token else {})})
    try:
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=context), NoRedirect())
        return opener.open(request, timeout=60)
    except urllib.error.HTTPError as error:
        code = error.code
        error.close()
        raise BackupError(f"OpenBao {route} returned HTTP {code}; verify unseal, scoped auth and audit output.") from None
    except urllib.error.URLError:
        raise BackupError("OpenBao could not be reached over verified TLS.") from None


def backup(state):
    config = configuration(state)
    require_nas_mount(config)
    identity = json.loads(private_file(state / "settings.json"))
    provisioner = Provisioner(argparse.Namespace(state=state))
    provisioner.settings = identity
    bao = provisioner.validate_openbao_settings(json.loads(private_file(state / "openbao-settings.json")))
    host = "localhost" if bao["bind_address"] == "127.0.0.1" else validate_host(identity["public_host"])
    address = "https://" + authority(host, bao["port"])
    trust = state / "trust/lucia-root-ca.crt"
    role = private_file(state / "secrets/openbao-backup-role-id").strip()
    secret = private_file(state / "secrets/openbao-backup-secret-id").strip()
    if not role or not secret:
        raise BackupError("The independently provisioned backup AppRole inputs are missing.")
    # Never initialize a missing repository or mistake a bad key for an empty one.
    restic(config, state, ["cat", "config", "--no-lock"])
    with api(address, trust, "GET", "sys/seal-status") as response:
        data = response.read(4097)
    if len(data) > 4096:
        raise BackupError("OpenBao seal status exceeded 4 KB.")
    seal = json.loads(data)
    if (seal.get("initialized") is not True or seal.get("sealed") is not False
            or type(seal.get("n")) is not int or type(seal.get("t")) is not int
            or seal["n"] != config["share_count"] or seal["t"] != config["share_threshold"]):
        raise BackupError("OpenBao must be initialized and unsealed with the recorded Shamir custody threshold.")
    with tempfile.TemporaryDirectory(prefix=".openbao-snapshot-", dir=state / "openbao") as temporary:
        snapshot = pathlib.Path(temporary) / "openbao-raft.snap"
        token = None
        try:
            with api(address, trust, "POST", "auth/openbao-backup/login", {"role_id": role, "secret_id": secret}) as response:
                data = response.read(65537)
            if len(data) > 65536:
                raise BackupError("OpenBao authentication response exceeded 64 KB.")
            auth = json.loads(data)["auth"]
            token = auth["client_token"]
            if (not isinstance(token, str) or not token or len(token) > 16384
                    or set(auth["policies"]) - {"default", POLICY} or POLICY not in auth["policies"]
                    or type(auth["lease_duration"]) is not int or not 0 < auth["lease_duration"] <= 3600):
                raise BackupError("Backup authentication must issue only the scoped backup policy with a lifetime of at most one hour.")
            with api(address, trust, "GET", "sys/storage/raft/snapshot", token=token) as response, snapshot.open("xb") as output:
                os.chmod(snapshot, 0o600)
                shutil.copyfileobj(response, output, 1024 * 1024)
                output.flush()
                os.fsync(output.fileno())
        finally:
            if token:
                with api(address, trust, "POST", "auth/token/revoke-self", token=token):
                    pass
        try:
            with gzip.open(snapshot, "rb") as source:
                expanded = 0
                while chunk := source.read(1024 * 1024):
                    expanded += len(chunk)
                if not expanded:
                    raise BackupError("OpenBao returned an empty Raft snapshot.")
        except (OSError, EOFError, zlib.error):
            raise BackupError("OpenBao returned an incomplete or invalid compressed Raft snapshot.") from None
        result = restic(config, state, ["backup", str(snapshot), "--json", "--quiet", "--host", "lucia-foundation",
                                      "--tag", TAG, "--tag", "lucia:version=1", "--retry-lock", "30m"])
        summaries = [json.loads(line) for line in result.splitlines() if line.startswith("{")]
        summary = next((item for item in summaries if item.get("message_type") == "summary"), None)
        snapshot_id = summary.get("snapshot_id", "") if summary else ""
        if not isinstance(snapshot_id, str) or not re.fullmatch(r"[a-f0-9]{8,64}", snapshot_id):
            raise BackupError("restic did not confirm the stored snapshot.")
    restic(config, state, ["forget", "--tag", TAG, "--group-by", "",
                          "--keep-daily", str(config["keep_daily"]), "--keep-weekly", str(config["keep_weekly"]),
                          "--keep-monthly", str(config["keep_monthly"]), "--prune", "--retry-lock", "30m"])
    return snapshot_id


def install(state):
    config = configuration(state)
    require_nas_mount(config)
    if shutil.which("restic") is None:
        raise BackupError("Install restic explicitly before installing the backup timer.")
    linger = subprocess.run(["loginctl", "show-user", str(os.getuid()), "-p", "Linger", "--value"],
                            check=True, capture_output=True, text=True, timeout=30).stdout.strip()
    if linger != "yes":
        raise BackupError("Approve persistent user services before installing the backup timer.")
    unit = pathlib.Path.home() / ".config/systemd/user"
    if unit != unit.resolve():
        raise BackupError("The systemd user unit directory must not contain symbolic links.")
    unit.mkdir(parents=True, exist_ok=True)
    for name in ("lucia-openbao-backup.service", "lucia-openbao-backup.timer"):
        if (unit / name).is_symlink() or (unit / (name + ".new")).is_symlink():
            raise BackupError("The backup systemd unit files must not be symbolic links.")
    def quote(value):
        if any(ord(character) < 32 or ord(character) == 127 for character in str(value)):
            raise BackupError("Systemd execution paths must not contain control characters.")
        return '"' + str(value).replace("\\", "\\\\").replace('"', '\\"').replace("%", "%%").replace("$", "$$") + '"'
    script = pathlib.Path(__file__).resolve()
    write_file(unit / "lucia-openbao-backup.service",
               "[Unit]\nDescription=OpenBao Raft snapshot to restic\n"
               "[Service]\nType=oneshot\nUMask=0077\nNoNewPrivileges=yes\n"
               f"ExecStart={quote(sys.executable)} {quote(script)} run --state {quote(state)}\nTimeoutStartSec=3h\n")
    write_file(unit / "lucia-openbao-backup.timer", "[Unit]\nDescription=Daily OpenBao off-host backup\n"
               "[Timer]\nOnCalendar=*-*-* 03:00:00 UTC\nPersistent=true\n"
               "[Install]\nWantedBy=timers.target\n")
    for arguments in (["daemon-reload"], ["enable", "--now", "lucia-openbao-backup.timer"]):
        subprocess.run(["systemctl", "--user", *arguments], check=True, capture_output=True, timeout=30)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("run", "status", "install"))
    parser.add_argument("--state", type=pathlib.Path, required=True)
    args = parser.parse_args()
    os.umask(0o077)
    state = args.state.expanduser().absolute()
    for directory in (state, state / "openbao"):
        if directory != directory.resolve() or not directory.is_dir():
            raise BackupError("Use the existing canonical private identity state directory without links.")
        info = directory.stat()
        if hasattr(os, "getuid") and (info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) & 0o077):
            raise BackupError("The identity state and OpenBao directories must be privately owned by the job user.")
    if args.command == "status":
        result = status(state)
        print(json.dumps(result))
        return 0 if result["healthy"] else 2
    if sys.platform != "linux" or os.getuid() == 0:
        raise BackupError("The backup job runs as the non-root Linux identity setup user.")
    import fcntl
    lock_path = state / "openbao/.backup.lock"
    with os.fdopen(os.open(lock_path, os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600), "a+b") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise BackupError("Another OpenBao backup or timer setup is already running.") from None
        if args.command == "install":
            install(state)
            print("Daily OpenBao restic backup timer installed.")
            return 0
        previous = status(state)
        record = {"state": "running", "started_at": datetime.datetime.now(UTC).isoformat(),
                  "last_success_at": previous.get("last_success_at"), "snapshot": previous.get("snapshot")}
        path = state / "openbao/backup-status.json"
        write_file(path, json.dumps(record) + "\n")
        try:
            record.update(snapshot=backup(state), state="succeeded", last_success_at=datetime.datetime.now(UTC).isoformat())
        except (OSError, ValueError, RuntimeError, KeyError, TypeError, http.client.HTTPException, subprocess.SubprocessError) as error:
            message = str(error) if isinstance(error, BackupError) else f"Backup input, I/O or process failure ({type(error).__name__})."
            record.update(state="failed", error=message)
            print(message, file=sys.stderr)
        record["finished_at"] = datetime.datetime.now(UTC).isoformat()
        write_file(path, json.dumps(record) + "\n")
        return 0 if record["state"] == "succeeded" else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, KeyError, TypeError, http.client.HTTPException, subprocess.SubprocessError) as error:
        message = str(error) if isinstance(error, BackupError) else f"Backup input, I/O or process failure ({type(error).__name__})."
        print(message, file=sys.stderr)
        raise SystemExit(1)
