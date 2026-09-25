"""Run only on a disposable --internal Docker network, with empty /boot,/control tmpfs."""

import datetime
import json
import os
import pathlib
import socket
import struct
import subprocess
import sys
import time
import urllib.error
import urllib.request

routes = pathlib.Path("/proc/net/route").read_text().splitlines()[1:]
if os.environ.get("LUCIA_ISOLATED_BOOT_CHECK") != "1" or any(row.split()[1] == "00000000" for row in routes):
    raise RuntimeError("Boot checks require explicit opt-in and an isolated container without a default route.")
if os.getuid() != 65534 or pathlib.Path("/opt/lucia").stat().st_mode & 0o777 != 0o755:
    raise RuntimeError("The boot image must run as nobody with a traversable application directory.")
if pathlib.Path("/opt/lucia/serve.py").stat().st_mode & 0o777 != 0o644:
    raise RuntimeError("The boot entrypoint must be readable independently of source-staging permissions.")
root, control = pathlib.Path("/boot"), pathlib.Path("/control")
if any(root.iterdir()) or any(control.iterdir()):
    raise RuntimeError("Boot checks require empty disposable tmpfs mounts, never installation state.")
payload = b"Lucia synthetic boot transport fixture\n"
relative_kernel = "debian-installer/amd64/linux"
(root / "debian-installer/amd64").mkdir(parents=True)
(root / relative_kernel).write_bytes(payload)
(root / relative_kernel).chmod(0o644)
grub_payload = b"Identical verified GRUB at both supported TFTP paths\n"
for name in ("grubx64.efi", "debian-installer/amd64/grubx64.efi"):
    (root / name).write_bytes(grub_payload)
    (root / name).chmod(0o644)
lease = control / "admission.json"
process = subprocess.Popen([sys.executable, "/opt/lucia/serve.py"])


def http_status():
    try:
        with urllib.request.urlopen("http://127.0.0.1:8080/" + relative_kernel, timeout=2) as response:
            assert response.read() == payload
            return response.status
    except urllib.error.HTTPError as error:
        return error.code


def tftp_request(name=relative_kernel, write=False):
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
        client.settimeout(1)
        client.sendto(struct.pack("!H", 2 if write else 1) + name.encode() + b"\0octet\0", ("127.0.0.1", 69))
        packet, peer = client.recvfrom(4096)
        if packet[:2] == b"\0\3":
            client.sendto(b"\0\4" + packet[2:4], peer)
        return packet


try:
    deadline = time.monotonic() + 10
    while True:
        try:
            assert http_status() == 403
            break
        except urllib.error.URLError:
            if time.monotonic() >= deadline:
                raise
            time.sleep(0.1)
    try:
        tftp_request()
        raise AssertionError("TFTP answered while admission was closed.")
    except socket.timeout:
        pass
    now = datetime.datetime.now(datetime.timezone.utc)
    lease.write_text(json.dumps({
        "schemaVersion": 1, "issuedAt": now.isoformat(),
        "expiresAt": (now + datetime.timedelta(seconds=10)).isoformat(),
        "windowExpiresAt": (now + datetime.timedelta(minutes=30)).isoformat(),
    }))
    time.sleep(0.7)
    assert http_status() == 200
    assert tftp_request() == b"\0\3\0\1" + payload
    for name in ("grubx64.efi", "debian-installer/amd64/grubx64.efi"):
        assert tftp_request(name) == b"\0\3\0\1" + grub_payload
    assert tftp_request("../etc/passwd")[:2] == b"\0\5"
    assert tftp_request(write=True)[:2] == b"\0\5", "TFTP accepted an upload."
    lease.unlink()
    time.sleep(0.7)
    assert http_status() == 403
    try:
        tftp_request()
        raise AssertionError("TFTP still answered after admission closed.")
    except socket.timeout:
        pass
    assert process.poll() is None
    print("Isolated boot transport passed: HTTP/TFTP open only during admission; traversal/uploads rejected.", flush=True)
finally:
    process.terminate()
    process.wait(timeout=10)
