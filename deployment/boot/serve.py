"""Public boot files only. DHCP, installer secrets, and enrollment are not served here."""

import datetime
import http.server
import json
import os
import pathlib
import shutil
import signal
import subprocess
import threading
import urllib.parse

UTC = datetime.timezone.utc
ROOT = pathlib.Path("/boot")
LEASE = pathlib.Path("/control/admission.json")
ALLOWED = frozenset((
    "debian-installer/amd64/bootnetx64.efi", "debian-installer/amd64/grubx64.efi",
    "grubx64.efi",
    "debian-installer/amd64/grub/grub.cfg", "debian-installer/amd64/grub/font.pf2",
    "debian-installer/amd64/linux", "debian-installer/amd64/initrd.gz",
    "lucia/lucia-overlay.cpio.gz",
))


def admission_open(path=LEASE, now=None):
    now = now or datetime.datetime.now(UTC)
    try:
        info = path.lstat()
    except FileNotFoundError:
        return False
    if path.is_symlink() or not path.is_file() or info.st_size > 2048:
        raise ValueError("Boot admission lease is not a bounded regular file.")
    value = json.loads(path.read_bytes())
    if set(value) != {"schemaVersion", "issuedAt", "expiresAt", "windowExpiresAt"} or value["schemaVersion"] != 1:
        raise ValueError("Boot admission lease has an unsupported schema.")
    times = [datetime.datetime.fromisoformat(value[key].replace("Z", "+00:00"))
             for key in ("issuedAt", "expiresAt", "windowExpiresAt")]
    if any(item.tzinfo is None for item in times):
        raise ValueError("Boot admission timestamps must include a timezone.")
    issued, expires, window = times
    if issued > now + datetime.timedelta(seconds=1) or expires - issued > datetime.timedelta(seconds=15):
        raise ValueError("Boot admission lease is future-dated or exceeds its heartbeat limit.")
    return issued <= now < expires <= window


def artifact(root, request_path):
    name = urllib.parse.urlsplit(request_path).path.removeprefix("/")
    if name not in ALLOWED:
        return None
    path = root / name
    if any(item.is_symlink() for item in (path, *path.parents)) or not path.is_file():
        return None
    return path


def emit(event, **details):
    print(json.dumps({"event": event, **details}), flush=True)


class BootFiles(http.server.BaseHTTPRequestHandler):
    server_version = "LuciaBoot/1"

    def do_GET(self):
        self.serve(head=False)

    def do_HEAD(self):
        self.serve(head=True)

    def serve(self, head):
        try:
            enabled = admission_open()
        except (OSError, ValueError, TypeError, KeyError):
            emit("invalid-admission-lease")
            self.send_error(503, "Boot admission is unavailable.")
            return
        if not enabled:
            self.send_error(403, "New hardware onboarding is closed.")
            return
        path = artifact(ROOT, self.path)
        if path is None:
            self.send_error(404, "Boot artifact not found.")
            return
        try:
            with path.open("rb") as source:
                self.send_response(200)
                self.send_header("Content-Type", "application/octet-stream")
                self.send_header("Content-Length", str(os.fstat(source.fileno()).st_size))
                self.send_header("Cache-Control", "no-store")
                self.end_headers()
                if not head:
                    shutil.copyfileobj(source, self.wfile)
        except (BrokenPipeError, ConnectionResetError):
            emit("boot-transfer-disconnected")
        except OSError:
            emit("boot-artifact-unreadable")
            self.close_connection = True

    def log_message(self, format, *args):
        # Do not log arbitrary request targets or query strings.
        pass


def stop_tftp(process):
    if process is None:
        return
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        return
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=5)


def main():
    if not ROOT.is_dir() or ROOT.is_symlink():
        raise RuntimeError("Mount the verified public boot artifact directory at /boot.")
    stopped = threading.Event()
    for number in (signal.SIGTERM, signal.SIGINT):
        signal.signal(number, lambda *_: stopped.set())
    server = http.server.ThreadingHTTPServer(("0.0.0.0", 8080), BootFiles)
    server.daemon_threads = True
    threading.Thread(target=server.serve_forever, daemon=True).start()
    process = None
    previous_error = None
    emit("boot-admission-closed")
    try:
        while not stopped.wait(0.5):
            try:
                enabled = admission_open()
                previous_error = None
            except (OSError, ValueError, TypeError, KeyError) as error:
                enabled = False
                if previous_error != type(error).__name__:
                    emit("invalid-admission-lease", errorType=type(error).__name__)
                previous_error = type(error).__name__
            if process is not None and process.poll() is not None:
                raise RuntimeError("The TFTP service exited unexpectedly; boot admission cannot be provided.")
            if enabled and process is None:
                process = subprocess.Popen([
                    "/usr/sbin/dnsmasq", "--no-daemon", "--port=0", "--no-hosts", "--no-resolv",
                    "--enable-tftp", "--tftp-root=" + str(ROOT), "--tftp-port-range=40000,40016",
                    "--log-facility=-",
                ], start_new_session=True)
                emit("boot-admission-open")
            elif not enabled and process is not None:
                stop_tftp(process)
                process = None
                emit("boot-admission-closed")
    finally:
        stop_tftp(process)
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    main()
