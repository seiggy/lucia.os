"""Offline preparation regression checks; never mounts or opens target disks."""

import argparse
import gzip
import io
import inspect
import json
import os
from pathlib import Path
import platform
import shutil
import signal
import stat
import subprocess
import sys
import tarfile
import time
import uuid
from unittest.mock import patch

import prepare as p


def rejects(action):
    try:
        action()
    except (ValueError, OSError, KeyError):
        return
    raise AssertionError("Unsafe/invalid input was accepted")


def archive(name, kind=tarfile.REGTYPE, link=""):
    result = io.BytesIO()
    with tarfile.open(fileobj=result, mode="w:gz") as tar:
        member = tarfile.TarInfo(name)
        member.type = kind
        member.linkname = link
        member.size = 1 if kind == tarfile.REGTYPE else 0
        tar.addfile(member, io.BytesIO(b"x") if member.size else None)
    return result.getvalue()


def unit_checks(root):
    # Independent of builder fixtures: server MapGroup("/api/boot") + MapGet("/discovery.cfg").
    assert p.DISCOVERY_ROUTE == "/api/boot/discovery.cfg"
    signature = inspect.signature(p.prepare_bundle)
    assert {"output", "node_agent", "ca_file", "server", "connect_address",
            "keyring", "replay", "boot_assets", "ca_der_sha256", "installation"} == set(signature.parameters)
    assert all(parameter.kind == inspect.Parameter.KEYWORD_ONLY
               for parameter in signature.parameters.values())
    assert p.verify is p.verify_bundle
    for name in ("../x", "/x", "a/../x", "a//b", "a\\b", "a/\0b", ""):
        rejects(lambda: p.safe_path(name))
    assert p.safe_path("./usr/lib/file") == "usr/lib/file"
    for value in ("http://spark-9423", "https://a/b", "https://a?x=1",
                  "https://user:password@a", "https://a/#fragment", "https://a';bad"):
        rejects(lambda: p.server_url(value))
    assert p.server_url("https://spark-9423/") == "https://spark-9423"
    rejects(lambda: p.manifest_entries(b"1  file\n"))
    digest = "a" * 64
    rejects(lambda: p.manifest_entries(f"{digest}  same\n{digest}  same\n".encode()))
    rejects(lambda: p.release_fields("Origin: Debian\nOrigin: Evil"))
    rejects(lambda: p.NoRedirect().redirect_request(None, None, 302, "", {}, "https://other.invalid"))
    for name in ("../escape", "/absolute", "usr/../escape"):
        rejects(lambda: p.tar_members(archive(name)))
    rejects(lambda: p.tar_members(archive("hardlink", tarfile.LNKTYPE, "../../escape")))
    rejects(lambda: p.tar_members(archive("device", tarfile.CHRTYPE)))
    assert p.tar_members(archive("./plain"))["plain"][1] == b"x"
    rejects(lambda: p.deb_members(b"not a deb"))
    files = {"usr/lib/lucia/test": (stat.S_IFREG | 0o755, b"read-only\n")}
    first = p.cpio_gz(files)
    assert first == p.cpio_gz(files), "Overlay must be deterministic"
    assert p.cpio_members(first)["usr/lib/lucia/test"] == files["usr/lib/lucia/test"]
    rejects(lambda: p.cpio_gz({"../outside": (stat.S_IFREG | 0o644, b"x")}))
    rejects(lambda: p.cpio_gz({"usr": (stat.S_IFREG | 0o644, b"x"), **files}))
    rejects(lambda: p.cpio_members(gzip.compress(b"bad cpio", mtime=0)))
    file = root / "bounded"
    p.write(file, b"abcd")
    rejects(lambda: p.write(file, b"do not overwrite"))
    rejects(lambda: p.bounded_file(file, 3))
    assert p.bounded_file(file, 4) == b"abcd"
    if sys.platform == "linux":
        link = root / "symlink"
        link.symlink_to(file)
        rejects(lambda: p.bounded_file(link, 10))
        hard = root / "hardlink"
        os.link(file, hard)
        rejects(lambda: p.bounded_file(file, 10))
        hard.unlink()
        file.chmod(0o666)
        rejects(lambda: p.bounded_file(file, 10))
        file.chmod(0o600)
    credentials = root / "publish"
    credentials.mkdir()
    p.write(credentials / "identity.pem", b"private material")
    rejects(lambda: p.agent_files(credentials))
    ca = root / "not-public.pem"
    p.write(ca, b"-----BEGIN PRIVATE KEY-----\nsecret\n-----END PRIVATE KEY-----")
    rejects(lambda: p.public_ca(ca))
    replay = root / "downloads"
    url = p.ARCHIVE + "example"
    p.write(replay / (p.sha(url.encode()) + ".bin"), b"tampered")
    rejects(lambda: p.Downloads(root / "copy", replay).get("example", 128, "0" * 64))
    assert not (root / "copy").exists()
    rejects(lambda: p.verify(root))
    firmware = "usr/lib/firmware/rtl_nic/rtl8125b-2.fw"
    license = "usr/share/doc/firmware-realtek/copyright"
    entries = {
        firmware: (stat.S_IFREG | 0o644, b"synthetic Realtek firmware"),
        license: (stat.S_IFREG | 0o644, b"synthetic license"),
        "usr/lib/firmware/rtlwifi/unrelated.bin": (stat.S_IFREG | 0o644, b"excluded"),
    }
    with patch.object(p, "package", return_value=entries):
        overlay = p.network_firmware_overlay(None, {}, {})
        assert set(overlay) == {firmware, license} and overlay[firmware] == entries[firmware]
    for invalid in ({license: entries[license]}, {firmware: entries[firmware]},
                    {**entries, firmware: (stat.S_IFLNK | 0o777, b"../../escape")}):
        with patch.object(p, "package", return_value=invalid):
            rejects(lambda: p.network_firmware_overlay(None, {}, {}))


def hook_checks():
    deployment = Path(__file__).resolve().parents[2] / "deployment/boot"
    discovery = (deployment / "discover-and-wait").read_text()
    guard = (deployment / "partitioner-guard").read_text()
    template = (deployment / "grub.cfg.in").read_text()
    assert "while :; do sleep 3600; done" in discovery and "while :; do sleep 3600; done" in guard
    assert "trap '' HUP INT TERM" in discovery and "trap '' HUP INT TERM" in guard
    assert "--state-directory /run/lucia" in discovery
    assert "status " not in discovery and "anna-install " not in discovery
    assert '[ -f /etc/lucia/installation-enabled ]' in guard and 'installation-guard --server' in guard
    assert 'db_set partman-auto/disk "$disk"' in guard and '/dev/disk/by-id/*:/dev/[a-z]*)' in guard
    assert "initrd /debian-installer/amd64/initrd.gz /lucia/lucia-overlay.cpio.gz" in template
    assert "url=@DISCOVERY_URL@" in template and "partman" not in template
    rendered = template.replace("@DISCOVERY_URL@", "https://spark-9423" + p.DISCOVERY_ROUTE).encode()
    p.validate_grub(rendered, "https://spark-9423")
    p.validate_grub(rendered.replace(p.READ_ONLY_TITLE, p.INSTALL_TITLE), "https://spark-9423")
    assert p.READ_ONLY_TITLE in rendered
    theme = (deployment / "grub-theme.txt").read_text(encoding="utf-8")
    # Stock font.pf2 carries ASCII, arrows and these box-drawing glyphs only.
    assert {c for c in theme if ord(c) > 126} <= set("━┃┏┓┗┛│┌┐└┘←↑→↓"), "GRUB theme glyph missing from font.pf2"
    assert 'id = "__timeout__"' in theme and "%d" in theme
    screen = (deployment / "screen.sh").read_text(encoding="utf-8")
    finish = (deployment / "finish-install").read_text(encoding="utf-8")
    assert "/dev/tty5" in screen and "read " not in screen, "Status screen must never wait for input"
    for unsafe in (
        rendered.replace(b"/api/boot/discovery.cfg", b"/pxe/discovery.cfg"),
        rendered.replace(b" /lucia/lucia-overlay.cpio.gz", b""),
        rendered + b"\nmenuentry 'Unchecked installer' {\n}\n",
        rendered + b"\nconfigfile /debian-installer/amd64/grub/stock.cfg\n",
    ):
        rejects(lambda: p.validate_grub(unsafe, "https://spark-9423"))
    if sys.platform != "linux":
        return "not run (Linux required)"
    for script in (discovery, guard, screen, finish):
        subprocess.run(["sh", "-n"], input=script.encode(), check=True)
    process = subprocess.Popen(["sh", "-c", guard],
                               stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
                               start_new_session=True)
    try:
        time.sleep(0.2)
        assert process.poll() is None, "Partitioner guard returned"
        os.kill(process.pid, signal.SIGTERM)
        time.sleep(0.2)
        assert process.poll() is None, "Termination incorrectly released the guard"
    finally:
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGKILL)
        _, stderr = process.communicate(timeout=5)
    assert b"permanently disabled" in stderr
    return "syntax and blocking/TERM refusal passed"


def bundle_checks(path):
    receipt = p.verify_bundle(path)
    root = Path(path)
    expected_url = receipt["controller"]["server"] + "/api/boot/discovery.cfg"
    assert receipt["controller"]["discoveryUrl"] == expected_url
    grub = (root / "public" / p.BOOT / "grub/grub.cfg").read_text()
    assert "url=" + expected_url + " " + p.CONSOLE_ARGS + " ---" in grub and "/pxe/discovery.cfg" not in grub
    overlay = p.cpio_members((root / "public/lucia/lucia-overlay.cpio.gz").read_bytes())
    for name, expected in receipt["overlayFiles"].items():
        assert overlay[name][0] == expected["mode"]
        assert p.sha(overlay[name][1]) == expected["sha256"]
    regular = {name for name, (mode, _) in overlay.items() if not stat.S_ISDIR(mode)}
    assert regular == set(receipt["overlayFiles"])
    required = {
        "usr/lib/lucia/discover-and-wait", "usr/lib/partman/init.d/00lucia-approval",
        "usr/lib/lucia/screen.sh",
        "usr/lib/lucia/agent/lucia-node-agent", "usr/lib/lucia/agent/ld-linux-x86-64.so.2",
        "usr/lib/lucia/agent/libssl.so.3", "usr/lib/lucia/agent/libcrypto.so.3",
        "etc/wgetrc", "etc/lucia/public-ca.crt", "etc/lucia/discovery.conf",
    }
    if "firmware-realtek" in receipt["packages"]:
        firmware = "usr/lib/firmware/rtl_nic/rtl8125b-2.fw"
        license = "usr/share/doc/firmware-realtek/copyright"
        required.update((firmware, license))
        assert stat.S_ISREG(overlay[firmware][0]) and overlay[firmware][1]
        assert receipt["packages"]["firmware-realtek"]["Architecture"] == "all"
        assert receipt["packages"]["firmware-realtek"]["Filename"].startswith("pool/non-free-firmware/")
    if receipt["purpose"] == "approved-installation-with-managed-enrollment":
        required.update(("etc/lucia/installation-enabled", "usr/lib/lucia/installation-guard", "usr/lib/lucia/finish-install"))
    else:
        assert "etc/lucia/installation-enabled" not in overlay
    assert required <= regular
    assert "preseed.cfg" not in overlay, "Network hook must not execute before networking"
    assert not any(name.startswith(("run/", "tmp/", "root/", "home/")) for name in overlay)
    assert all(not name.endswith((".key", ".pfx", ".p12", "/identity.pem", "/discovery.json"))
               for name in overlay)
    assert b"check_certificate = off" not in overlay["etc/wgetrc"][1]
    assert (b"ca_certificate = /etc/lucia/installer-ca-bundle.crt" if receipt["purpose"] == "approved-installation-with-managed-enrollment"
            else b"ca_certificate = /etc/lucia/public-ca.crt") in overlay["etc/wgetrc"][1]
    ca = overlay["etc/lucia/public-ca.crt"][1]
    assert p.sha(ca) == receipt["controller"]["publicCaSha256"]
    if sys.platform == "linux":
        assert p.sha(p.run(["openssl", "x509", "-outform", "DER"], input=ca)) == \
            receipt["controller"]["publicCaDerSha256"]
    assert b"while :; do sleep 3600; done" in overlay["usr/lib/partman/init.d/00lucia-approval"][1]
    for module in ("nvme", "ahci", "ata_piix", "virtio_blk", "virtio_scsi"):
        assert any(name.endswith("/" + module + ".ko.xz") for name in overlay)
    assert receipt["runtimeCheck"]["cliExecuted"] is True
    assert receipt["qualification"]["uefiVmBoot"] is False
    return {"installerBuild": receipt["installerBuild"], "kernelAbi": receipt["kernelAbi"],
            "publicFiles": len(p.PUBLIC), "overlayFiles": len(regular),
            "vmQualified": False}


def uefi_smoke(bundle, destination, root_shim=False):
    """Bounded offline firmware-chain smoke, not a successful discovery claim."""
    p.verify_bundle(bundle)
    destination = p.no_symlinks(destination)
    assert not destination.exists(), "UEFI output must be a new owned directory"
    code = Path("/usr/share/OVMF/OVMF_CODE_4M.fd")
    variables = Path("/usr/share/OVMF/OVMF_VARS_4M.fd")
    assert shutil.which("qemu-system-x86_64") and code.is_file() and variables.is_file(), \
        "Build the Dockerfile's optional qualification target"
    destination.mkdir(mode=0o700)
    tree = destination / "tftp"
    shutil.copytree(Path(bundle) / "public", tree)
    bootfile = p.BOOT + "bootnetx64.efi"
    if root_shim:
        # Reproduce firmware that loses the bootloader directory before shim's
        # second-stage lookup, without changing either signed EFI binary.
        for name in ("bootnetx64.efi", "grubx64.efi"):
            shutil.copyfile(tree / p.BOOT / name, tree / name)
        bootfile = "bootnetx64.efi"
    grub = tree / p.BOOT / "grub/grub.cfg"
    config = grub.read_text()
    # Only the private test copy changes: expose the real UEFI boot on serial.
    config = ("serial --unit=0 --speed=115200\nterminal_input serial\n"
              "terminal_output serial\n" + config.replace(" quiet ", " ").replace(" ---", " console=ttyS0,115200n8 ---"))
    grub.write_text(config)
    shutil.copyfile(variables, destination / "vars.fd")
    disk = destination / "disposable.raw"
    with disk.open("xb") as stream:
        stream.truncate(64 * 1024 * 1024)
    before = p.sha(disk.read_bytes())
    serial = destination / "serial.log"
    command = [
        "qemu-system-x86_64", "-machine", "q35,accel=tcg", "-cpu", "max", "-m", "1536",
        "-display", "none", "-monitor", "none", "-serial", "file:" + str(serial),
        "-no-reboot", "-boot", "order=n",
        "-drive", f"if=pflash,format=raw,readonly=on,file={code}",
        "-drive", f"if=pflash,format=raw,file={destination / 'vars.fd'}",
        "-drive", f"if=virtio,format=raw,file={disk}",
        "-netdev", f"user,id=net0,restrict=on,tftp={tree},bootfile={bootfile}",
        "-device", "virtio-net-pci,netdev=net0,romfile=",
    ]
    started = time.monotonic()
    with (destination / "qemu.log").open("xb") as errors:
        process = subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=errors)
        try:
            while process.poll() is None and time.monotonic() - started < 300:
                time.sleep(2)
                if serial.exists():
                    log = serial.read_bytes()
                    assert len(log) <= 8 * 1024 * 1024, "Unexpected serial output size"
                    if (b"Failed to retrieve the preconfiguration file" in log or
                            b"Name server addresses:" in log):
                        break
        finally:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=10)
    after = p.sha(disk.read_bytes())
    log = serial.read_bytes() if serial.exists() else b""
    result = {
        "transport": "private QEMU user-net TFTP; restrict=on; no host ports or bridges",
        "boot": "OVMF Secure Boot off, Debian shim -> GRUB -> kernel (no -kernel)",
        "rootShimCompatibility": root_shim,
        "diagnosticChange": "private GRUB copy adds serial console and drops quiet only",
        "kernelReached": b"Linux version 6.12.107+deb13-amd64" in log,
        "preseedUnavailableObserved": b"Failed to retrieve the preconfiguration file" in log,
        "networkConfigurationPromptObserved": b"Name server addresses:" in log,
        "discoveryRegistered": False, "vmDiscoveryQualified": False,
        "diskSha256Before": before, "diskSha256After": after,
        "diskUnchanged": before == after, "elapsedSeconds": round(time.monotonic() - started),
    }
    p.write(destination / "result.json", (json.dumps(result, indent=2) + "\n").encode())
    assert before == after, "Disposable disk changed during read-only smoke"
    assert result["kernelReached"], "UEFI smoke did not reach the signed Debian kernel; inspect serial.log"
    return result


def agent_runtime_checks(bundle, publish, destination):
    p.verify_bundle(bundle)
    overlay = p.cpio_members((Path(bundle) / "public/lucia/lucia-overlay.cpio.gz").read_bytes())
    runtime = destination / "runtime"
    for name, (mode, data) in overlay.items():
        if not name.startswith("usr/lib/lucia/agent/") or stat.S_ISDIR(mode):
            continue
        path = runtime / Path(name).name
        if stat.S_ISREG(mode):
            p.write(path, data, mode & 0o777)
        elif stat.S_ISLNK(mode):
            assert "/" not in data.decode()
            path.symlink_to(data.decode())
    for suffix in ("", ".dll", ".pdb", ".deps.json", ".runtimeconfig.json"):
        name = "Lucia.NodeAgent.Checks" + suffix
        p.write(runtime / name, p.bounded_file(Path(publish) / name, 1024 * 1024),
                0o755 if suffix == "" else 0o644)
    command = [str(runtime / "ld-linux-x86-64.so.2"), "--inhibit-cache",
               "--library-path", str(runtime), str(runtime / "Lucia.NodeAgent.Checks")]
    if platform.machine() not in ("x86_64", "AMD64"):
        command.insert(0, "qemu-x86_64")
    try:
        output = p.run(command, cwd=destination,
                       env=dict(os.environ, DOTNET_EnableDiagnostics="0",
                                DOTNET_SYSTEM_GLOBALIZATION_INVARIANT="1"))
    except subprocess.CalledProcessError as error:
        # The private checks harness emits diagnostics, never live credentials.
        sys.stderr.buffer.write(error.stdout or b"")
        sys.stderr.buffer.write(error.stderr or b"")
        raise
    return output.decode()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle")
    parser.add_argument("--uefi-output", help="Optional new directory for bounded, offline OVMF smoke evidence")
    parser.add_argument("--uefi-root-shim", action="store_true", help="Exercise shim and its GRUB fallback from the TFTP root")
    parser.add_argument("--agent-checks-publish", help="Optional trusted native Checks publish for staged-runtime tests")
    args = parser.parse_args()
    # All scratch files stay in the caller-owned working directory, never /tmp.
    root = Path.cwd() / (".lucia-prepare-check-" + uuid.uuid4().hex)
    root.mkdir(mode=0o700)
    try:
        unit_checks(root)
        result = {"offlineUnitChecks": "passed", "guardChecks": hook_checks()}
        if args.bundle:
            result["bundle"] = bundle_checks(args.bundle)
        if args.uefi_output:
            assert args.bundle, "--uefi-output requires --bundle"
            result["uefiSmoke"] = uefi_smoke(args.bundle, args.uefi_output, args.uefi_root_shim)
        if args.agent_checks_publish:
            assert args.bundle, "--agent-checks-publish requires --bundle"
            result["nativeAgentChecks"] = agent_runtime_checks(
                args.bundle, args.agent_checks_publish, root)
        print(json.dumps(result))
    finally:
        shutil.rmtree(root)


if __name__ == "__main__":
    main()
