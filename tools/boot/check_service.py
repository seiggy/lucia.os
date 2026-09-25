"""Offline checks for the fail-closed boot-file service; no listeners or containers."""

import datetime
import importlib.util
import json
import pathlib
import tempfile

source = pathlib.Path(__file__).resolve().parents[2] / "deployment/boot/serve.py"
spec = importlib.util.spec_from_file_location("boot_service", source)
service = importlib.util.module_from_spec(spec)
spec.loader.exec_module(service)
now = datetime.datetime.now(datetime.timezone.utc)

with tempfile.TemporaryDirectory(prefix="lucia-boot-check-") as temporary:
    root = pathlib.Path(temporary)
    lease = root / "admission.json"
    assert not service.admission_open(lease, now)
    value = {
        "schemaVersion": 1,
        "issuedAt": now.isoformat(),
        "expiresAt": (now + datetime.timedelta(seconds=15)).isoformat(),
        "windowExpiresAt": (now + datetime.timedelta(minutes=30)).isoformat(),
    }
    lease.write_text(json.dumps(value))
    assert service.admission_open(lease, now)
    assert not service.admission_open(lease, now + datetime.timedelta(seconds=15))
    for changes in (
        {"schemaVersion": 2}, {"extra": True},
        {"issuedAt": (now + datetime.timedelta(seconds=3)).isoformat()},
        {"expiresAt": (now + datetime.timedelta(seconds=16)).isoformat()},
        {"issuedAt": "2026-01-01T00:00:00"},
    ):
        lease.write_text(json.dumps({**value, **changes}))
        try:
            service.admission_open(lease, now)
            raise AssertionError("Unsafe boot admission lease was accepted.")
        except ValueError:
            pass
    lease.write_text("{invalid")
    try:
        service.admission_open(lease, now)
        raise AssertionError("Malformed boot admission lease was accepted.")
    except ValueError:
        pass
    (root / "debian-installer/amd64").mkdir(parents=True)
    kernel = root / "debian-installer/amd64/linux"
    kernel.write_bytes(b"synthetic-kernel-fixture")
    assert service.artifact(root, "/debian-installer/amd64/linux") == kernel
    (root / "grubx64.efi").write_bytes(b"synthetic-verified-grub-fixture")
    assert service.artifact(root, "/grubx64.efi") == root / "grubx64.efi"
    for name in ("/", "/../linux", "/%2e%2e/linux", "/%6cinux", "//linux", "/admission.json", "/etc/passwd", "/linux"):
        assert service.artifact(root, name) is None, name

print("Boot service checks passed: closed by default, heartbeat expiry, bounded leases, and fixed public paths.")
