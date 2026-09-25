"""Prepare read-only boot serving; only the existing Identity AppHost deploys it.

The input is an already verified Lucia discovery bundle, not an arbitrary TFTP
directory. Admission remains closed. Installation and enrollment stay disabled.
"""

import hashlib
import ipaddress
import json
import os
import pathlib
import shutil
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from tools.boot.prepare import verify_bundle, installation_payload_sha256
from tools.host.package import HostError, json_bytes, regular_file, sha256_file
from tools.host.provision_host import absolute_path, owned, preparation_lock, private_directory, read_json, validate_settings, write_private

TFTP_ALIASES = {"grubx64.efi": "debian-installer/amd64/grubx64.efi"}


def prepare_boot(provisioner, bundle_directory, *, bind_address, network_cidr, http_port=9080,
                 discovery_qualified=False, installation_qualified=False):
    if sys.platform != "linux":
        raise HostError("Prepare managed boot services on the Linux Spark, not the desktop.")
    address = ipaddress.IPv4Address(bind_address)
    network = ipaddress.IPv4Network(network_cidr)
    if str(address) != bind_address or address not in network or not any(
            network.subnet_of(ipaddress.IPv4Network(value))
            for value in ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16")):
        raise HostError("Choose an explicit private provisioning subnet containing the Spark bind address.")
    if (type(http_port) is not int or not 1024 <= http_port <= 65535
            or type(discovery_qualified) is not bool or type(installation_qualified) is not bool):
        raise HostError("Boot HTTP port and discovery qualification must be explicit, valid settings.")
    state = absolute_path(provisioner.state)
    host = validate_settings(read_json(state / "host-settings.json"), provisioner.settings, state)
    if http_port in provisioner.settings["ports"].values():
        raise HostError("Boot HTTP port conflicts with an identity endpoint.")
    bundle = absolute_path(bundle_directory)
    owned(bundle)
    receipt = verify_bundle(bundle)
    if receipt.get("purpose") not in ("read-only-discovery-no-installation", "approved-installation-with-managed-enrollment"):
        raise HostError("Only Lucia's verified discovery or approved-installation bundle formats are supported.")
    if installation_qualified:
        if not discovery_qualified or receipt["purpose"] != "approved-installation-with-managed-enrollment":
            raise HostError("Installation requires an explicitly qualified installation-profile bundle.")
        qualification = read_json(bundle / "installation-qualification.json")
        backend = pathlib.Path(host["host_state"]) / "releases" / host["artifact_sha256"] / "publish/Lucia.Homelab.Server.dll"
        native_sources = (ROOT / "tools/nodes/enrollment_worker.py").read_bytes() + b"\0" + (ROOT / "tools/nodes/prepare_directory.py").read_bytes()
        if (qualification.get("schemaVersion") != 1
                or qualification.get("installationPayloadSha256") != installation_payload_sha256(receipt)
                or qualification.get("backendSha256") != sha256_file(backend)
                or qualification.get("nativeEnrollmentSha256") != hashlib.sha256(native_sources).hexdigest()
                or qualification.get("selectedDiskInstalled") is not True
                or qualification.get("otherDiskUnchanged") is not True or qualification.get("managedHeartbeatVerified") is not True
                or qualification.get("directoryLoginVerified") is not True):
            raise HostError("This exact installation bundle has not completed disposable-disk and managed-enrollment qualification.")
    origin = host["authentication"]["public_origin"]
    if receipt["controller"]["server"] != origin or receipt["controller"]["discoveryUrl"] != origin + "/api/boot/discovery.cfg":
        raise HostError("The discovery bundle points to a different controller or preseed endpoint.")
    if receipt["controller"]["publicCaSha256"] != sha256_file(state / "trust/lucia-root-ca.crt"):
        raise HostError("The discovery bundle does not trust this installation's current public CA.")
    if receipt["controller"]["connectAddress"] not in ("", bind_address):
        raise HostError("The discovery bundle contains a different controller connection address.")
    for name in receipt["artifacts"]:
        regular_file(bundle / "public" / name)
    # Some UEFI implementations lose shim's loaded directory and request its
    # next stage from the TFTP root. Serve the same verified Debian bytes there.
    artifacts = {**receipt["artifacts"], **{alias: receipt["artifacts"][source] for alias, source in TFTP_ALIASES.items()}}
    root = state.parent / "boot"
    owner_path = root / "owner.json"
    ownership = {"schema_version": 1, "component": "boot", "identity_state": str(state)}
    if root.exists() and not owner_path.exists():
        entries = list(root.iterdir())
        if entries:
            if len(entries) != 1 or entries[0].name != "prepare.lock" or regular_file(entries[0]).st_size != 0:
                raise HostError("The boot state directory contains unowned data; it was not adopted.")
            owned(entries[0])
    private_directory(root)
    with preparation_lock(root):
        if owner_path.exists() and read_json(owner_path) != ownership:
            raise HostError("Boot state belongs to another installation.")
        write_private(owner_path, json_bytes(ownership))
        settings_path = state / "boot-settings.json"
        previous = read_json(settings_path) if settings_path.exists() else None
        if previous and any(previous.get(name) != value for name, value in
                            (("bind_address", bind_address), ("network_cidr", network_cidr), ("http_port", http_port))):
            raise HostError("Boot network settings changed. Review a network migration instead of replacing them implicitly.")
        image_source = (ROOT / "deployment/boot/Dockerfile").read_bytes() + b"\0" + (ROOT / "deployment/boot/serve.py").read_bytes()
        source_hash = hashlib.sha256(image_source).hexdigest()
        image_key = hashlib.sha256((host["installation_id"] + ":" + source_hash).encode()).hexdigest()
        tag = "lucia-boot:" + image_key
        labels = {
            "io.lucia.component": "boot", "io.lucia.host-installation": host["installation_id"],
            "io.lucia.boot-source-sha256": source_hash,
        }
        container = provisioner.run(["docker", "inspect", "lucia-boot"], check=False)
        if container.returncode == 0:
            current = json.loads(container.stdout)[0]["Config"]["Labels"] or {}
            if any(current.get(name) != value for name, value in labels.items() if name != "io.lucia.boot-source-sha256"):
                raise HostError("The lucia-boot container is not owned by this installation.")
        image = provisioner.run(["docker", "image", "inspect", tag], check=False)
        if image.returncode != 0:
            with tempfile.TemporaryDirectory(prefix=".image-", dir=root) as temporary:
                for name in ("Dockerfile", "serve.py"):
                    shutil.copyfile(ROOT / "deployment/boot" / name, pathlib.Path(temporary) / name)
                command = ["docker", "build", "--tag", tag]
                for name, value in labels.items():
                    command.extend(["--label", name + "=" + value])
                provisioner.run([*command, temporary])
            image = provisioner.run(["docker", "image", "inspect", tag])
        metadata = json.loads(image.stdout)[0]
        if any((metadata["Config"].get("Labels") or {}).get(name) != value for name, value in labels.items()):
            raise HostError("The prepared boot image's ownership or source hash does not match.")
        write_private(root / "images" / (image_key + ".json"),
                      json_bytes({"image_id": metadata["Id"], "image_tag": tag, "source_sha256": source_hash, "labels": labels}))
        bundle_key = hashlib.sha256(json_bytes(artifacts)).hexdigest()
        bundles = root / "bundles"
        private_directory(bundles)
        installed = bundles / bundle_key
        public = installed / "public"
        if not installed.exists():
            with tempfile.TemporaryDirectory(prefix=".bundle-", dir=bundles) as temporary:
                staging = pathlib.Path(temporary) / "release"
                staging.mkdir(mode=0o700)
                for name, fingerprint in artifacts.items():
                    destination = staging / "public" / name
                    destination.parent.mkdir(parents=True, exist_ok=True, mode=0o755)
                    shutil.copyfile(bundle / "public" / TFTP_ALIASES.get(name, name), destination)
                    os.chmod(destination, 0o644)
                    if sha256_file(destination) != fingerprint:
                        raise HostError("A boot artifact changed while being installed.")
                for directory in (staging / "public", *(path for path in (staging / "public").rglob("*") if path.is_dir())):
                    os.chmod(directory, 0o755)
                write_private(staging / "receipt.json", json_bytes({**receipt, "artifacts": artifacts, "tftpAliases": TFTP_ALIASES}))
                os.rename(staging, installed)
        actual = set()
        for path in public.rglob("*"):
            if path.is_dir():
                absolute_path(path)
                continue
            regular_file(path)
            name = path.relative_to(public).as_posix()
            actual.add(name)
            if sha256_file(path) != artifacts.get(name):
                raise HostError("An installed boot artifact changed; preserve it and investigate.")
        if actual != set(artifacts):
            raise HostError("The installed public boot directory has missing or unexpected files.")
        control = pathlib.Path(host["data_directory"]) / "boot-control"
        absolute_path(control)
        try:
            control.mkdir(mode=0o755)
        except FileExistsError:
            pass
        else:
            os.chmod(control, 0o755)
        owned(control)
        if not control.is_dir() or (os.name != "nt" and (control.stat().st_mode & 0o777) != 0o755):
            raise HostError("The dedicated boot-control directory must already use 0755; permissions were not changed.")
        record = {
            "schema_version": 1, "component": "boot", "installation_id": host["installation_id"],
            "boot_state": str(root), "assets_directory": str(public), "bundle_sha256": bundle_key,
            "control_directory": str(control), "image_id": metadata["Id"], "image_tag": tag,
            "image_source_sha256": source_hash, "bind_address": bind_address,
            "network_cidr": network_cidr, "http_port": http_port,
            "discovery_qualified": discovery_qualified,
            "installation_qualified": installation_qualified,
        }
        write_private(settings_path, json_bytes(record))
        return record
