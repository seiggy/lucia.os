"""Offline boot deployment preparation checks; Docker and bundle verification are mocked."""

import hashlib
import json
import os
import pathlib
import subprocess
import sys
import tempfile
from types import SimpleNamespace
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from tools.boot import provision_boot as boot


with tempfile.TemporaryDirectory(prefix="lucia-boot-provision-") as temporary:
    root = pathlib.Path(temporary)
    state = root / "identity"
    (state / "trust").mkdir(parents=True)
    ca = state / "trust/lucia-root-ca.crt"
    ca.write_bytes(b"synthetic-public-ca-fixture")
    data = root / "host/data"
    data.mkdir(parents=True)
    host = {
        "installation_id": "a" * 64, "data_directory": str(data),
        "authentication": {"public_origin": "https://spark.test"},
    }
    (state / "host-settings.json").write_text(json.dumps(host))
    bundle = root / "input-bundle"
    asset = bundle / "public/debian-installer/amd64/linux"
    asset.parent.mkdir(parents=True)
    asset.write_bytes(b"synthetic-boot-file")
    grub = asset.with_name("grubx64.efi")
    grub.write_bytes(b"synthetic-verified-grub-fixture")
    receipt = {
        "purpose": "read-only-discovery-no-installation",
        "controller": {
            "server": "https://spark.test", "discoveryUrl": "https://spark.test/api/boot/discovery.cfg",
            "connectAddress": "192.168.0.222", "publicCaSha256": hashlib.sha256(ca.read_bytes()).hexdigest(),
        },
        "artifacts": {"debian-installer/amd64/linux": hashlib.sha256(asset.read_bytes()).hexdigest(),
                      "debian-installer/amd64/grubx64.efi": hashlib.sha256(grub.read_bytes()).hexdigest()},
    }
    calls = []
    images = {}

    def run(command, check=True):
        calls.append(command)
        if command[:3] == ["docker", "image", "inspect"]:
            metadata = images.get(command[3])
            return subprocess.CompletedProcess(command, 0 if metadata else 1, json.dumps([metadata]) if metadata else "", "")
        if command[:2] == ["docker", "inspect"]:
            return subprocess.CompletedProcess(command, 1, "", "")
        if command[:2] == ["docker", "build"]:
            labels = dict(command[i + 1].split("=", 1) for i, value in enumerate(command) if value == "--label")
            images[command[command.index("--tag") + 1]] = {"Id": "sha256:" + "b" * 64, "Config": {"Labels": labels}}
            return subprocess.CompletedProcess(command, 0, "", "")
        raise AssertionError("Unexpected command: " + str(command))

    provisioner = SimpleNamespace(state=state, settings={"ports": {"authentik": 9443, "ldaps": 636, "ca": 9444}}, run=run)
    def prepare(**changes):
        return boot.prepare_boot(provisioner, bundle, **{
            "bind_address": "192.168.0.222", "network_cidr": "192.168.0.0/23", **changes,
        })
    with patch.object(boot.sys, "platform", "linux"), patch.object(boot, "verify_bundle", return_value=receipt), \
            patch.object(boot, "validate_settings", side_effect=lambda value, *_: value):
        previous_umask = os.umask(0o077)
        try:
            first = prepare()
        finally:
            os.umask(previous_umask)
        assert first["discovery_qualified"] is False
        assert len([command for command in calls if command[:2] == ["docker", "build"]]) == 1
        assert (pathlib.Path(first["assets_directory"]) / "debian-installer/amd64/linux").read_bytes() == asset.read_bytes()
        installed = pathlib.Path(first["assets_directory"])
        assert (installed / "grubx64.efi").read_bytes() == (installed / "debian-installer/amd64/grubx64.efi").read_bytes() == grub.read_bytes()
        assert not (installed / "grubx64.efi").is_symlink()
        installed_receipt = json.loads((installed.parent / "receipt.json").read_text())
        assert installed_receipt["tftpAliases"] == boot.TFTP_ALIASES
        assert installed_receipt["artifacts"]["grubx64.efi"] == receipt["artifacts"]["debian-installer/amd64/grubx64.efi"]
        assert not (data / "boot-control/admission.json").exists()
        assert prepare() == first
        assert len([command for command in calls if command[:2] == ["docker", "build"]]) == 1
        (installed / "grubx64.efi").write_bytes(b"tampered")
        try:
            prepare()
            raise AssertionError("A changed root GRUB fallback was accepted.")
        except boot.HostError:
            pass
        (installed / "grubx64.efi").write_bytes(grub.read_bytes())
        for changes in (
            {"network_cidr": "0.0.0.0/0"}, {"bind_address": "192.168.2.1"},
            {"http_port": 9443}, {"http_port": True}, {"http_port": 9081},
        ):
            try:
                prepare(**changes)
                raise AssertionError("Unsafe network change was accepted.")
            except boot.HostError:
                pass
        receipt["controller"]["discoveryUrl"] = "https://spark.test/pxe/discovery.cfg"
        try:
            prepare()
            raise AssertionError("An unmapped preseed URL was accepted.")
        except boot.HostError:
            pass
        receipt["controller"]["discoveryUrl"] = "https://spark.test/api/boot/discovery.cfg"
        receipt["controller"]["publicCaSha256"] = "0" * 64
        try:
            prepare()
            raise AssertionError("A different CA was accepted.")
        except boot.HostError:
            pass
        receipt["controller"]["publicCaSha256"] = hashlib.sha256(ca.read_bytes()).hexdigest()
        (pathlib.Path(first["assets_directory"]) / "unexpected.txt").write_text("not an approved boot asset")
        try:
            prepare()
            raise AssertionError("Unexpected public boot files were accepted.")
        except boot.HostError:
            pass
        (pathlib.Path(first["assets_directory"]) / "unexpected.txt").unlink()
        receipt["purpose"] = "approved-installation-with-managed-enrollment"
        (state / "host-settings.json").write_text(json.dumps({**host, "host_state": str(root / "host/state"), "artifact_sha256": "c" * 64}))
        (bundle / "installation-qualification.json").write_text(json.dumps({"schemaVersion": 0}))
        try:
            prepare(discovery_qualified=True, installation_qualified=True)
            raise AssertionError("An unqualified installation bundle was accepted.")
        except boot.HostError:
            pass
        skipped = prepare(discovery_qualified=True, installation_qualified=True, skip_qualification=True)
        assert skipped["installation_qualification_skipped"] is True and first["installation_qualification_skipped"] is False

print("Boot preparation checks passed: immutable ownership, preserved network, correct controller/CA, default-closed admission, and explicit qualification skips.")
