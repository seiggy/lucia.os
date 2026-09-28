"""Opt-in disposable Debian UEFI installation qualification.

Runs only inside an explicitly marked qualification container. Disk arguments
are newly created raw files; no host block devices or production state are used.
The host-side native fixture supplies CA/LDAP responses in the shared test tree.
"""
import argparse
import hashlib
import json
import os
import pathlib
import shlex
import shutil
import ssl
import subprocess
import time
import urllib.error
import urllib.request
from prepare import installation_payload_sha256

MARKER = b"LUCIA-UNSELECTED-DISK-MUST-NOT-CHANGE\n"
# Guest-visible fixture services: the controller and the gateway's LDAPS.
FIXTURE_PORTS = (19443, 8636)


class KvmHost:
    """Runs the disposable VM on a remote x86 KVM host (for example a Proxmox node) instead of emulating it.

    The VM keeps QEMU user networking, so it never joins the LAN. SSH carries the fixture's controller and
    LDAPS ports to the remote loopback that user networking maps to the fixture host, and brings the guest's
    SSH back to this container's loopback. The remote work directory is deleted afterwards.
    """
    OVMF = "/usr/share/pve-edk2-firmware"

    def __init__(self, target, keys):
        self.target = target
        self.ssh = ["ssh", "-i", str(keys / "id_ed25519"), "-o", "UserKnownHostsFile=" + str(keys / "known_hosts"),
                    "-o", "StrictHostKeyChecking=yes", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                    "-o", "ServerAliveInterval=30", "-o", "ServerAliveCountMax=4"]
        self.directory = None

    def run(self, script, stdin=None, timeout=120):
        result = subprocess.run(self.ssh + [self.target, script], stdin=stdin, capture_output=True, timeout=timeout)
        if result.returncode:
            raise RuntimeError(f"KVM host command failed (exit {result.returncode}): {result.stderr.decode(errors='replace')[-400:]}")
        return result.stdout.decode().strip()

    def prepare(self, tree):
        self.directory = self.run("mktemp -d /var/tmp/lucia-qual.XXXXXXXX")
        d = shlex.quote(self.directory)
        self.run(f"cd {d} && truncate -s 16G selected.raw && truncate -s 64M unselected.raw"
                 f" && printf %s {shlex.quote(MARKER.decode())} | dd of=unselected.raw conv=notrunc status=none"
                 f" && cp {self.OVMF}/OVMF_VARS_4M.fd vars.fd")
        with subprocess.Popen(["tar", "-C", str(tree.parent), "-cf", "-", tree.name], stdout=subprocess.PIPE) as archive:
            self.run(f"tar -C {d} -xf -", stdin=archive.stdout, timeout=300)
        return pathlib.PurePosixPath(self.directory)

    def digest(self):
        return self.run(f"sha256sum {shlex.quote(self.directory)}/unselected.raw").split()[0]

    def start(self, qemu, log):
        forwards = [arg for port in FIXTURE_PORTS for arg in ("-R", f"127.0.0.1:{port}:127.0.0.1:{port}")]
        remote = f"cd {shlex.quote(self.directory)} && exec timeout 5400 {shlex.join(qemu)}"
        return subprocess.Popen(self.ssh + ["-o", "ExitOnForwardFailure=yes", *forwards, "-L", "127.0.0.1:2222:127.0.0.1:2222",
                                            self.target, remote], stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT)

    def stop(self, guest):
        try: self.run(f"kill $(cat {shlex.quote(self.directory)}/qemu.pid) 2>/dev/null || true")
        finally:
            try: guest.wait(timeout=30)
            except subprocess.TimeoutExpired: guest.kill(); guest.wait(timeout=10)

    def cleanup(self, work):
        if self.directory is None: return
        d = shlex.quote(self.directory)
        try:
            with (work / "serial.log").open("wb") as serial:
                subprocess.run(self.ssh + [self.target, f"cat {d}/serial.log"], stdout=serial, timeout=120)
        finally:
            self.run(f"pkill -F {d}/qemu.pid 2>/dev/null; rm -rf -- {d}")
            self.directory = None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", required=True)
    parser.add_argument("--bundle", required=True)
    parser.add_argument("--controller", required=True)
    args = parser.parse_args()
    root, bundle = pathlib.Path(args.fixture), pathlib.Path(args.bundle)
    if (os.environ.get("LUCIA_DISPOSABLE_INSTALL") != "1"
            or (root / ".lucia-disposable-install-fixture").read_text().strip() != "no-live-disks-or-identities-v1"):
        raise RuntimeError("Explicit disposable qualification fixture required.")
    os.umask(0o077)
    work = root / "vm"
    work.mkdir(mode=0o700)
    target = os.environ.get("LUCIA_QUAL_KVM_HOST")
    kvm = KvmHost(target, pathlib.Path(os.environ["LUCIA_QUAL_KVM_KEYS"])) if target else None
    tree = work / "tftp"
    shutil.copytree(bundle / "public", tree)
    shutil.copyfile(tree / "debian-installer/amd64/grubx64.efi", tree / "grubx64.efi")
    grub = tree / "debian-installer/amd64/grub/grub.cfg"
    grub.write_text("serial --unit=0 --speed=115200\nterminal_input serial\nterminal_output serial\n"
                    + grub.read_text().replace(" quiet ", " ").replace(" ---", " console=ttyS0,115200n8 ---"))
    if kvm:
        vm = kvm.prepare(tree)
        firmware = KvmHost.OVMF + "/OVMF_CODE_4M.fd"
        digest = kvm.digest
    else:
        vm = work
        firmware = "/usr/share/OVMF/OVMF_CODE_4M.fd"
        for name, size in (("selected.raw", 16 * 1024 ** 3), ("unselected.raw", 64 * 1024 ** 2)):
            with (work / name).open("xb") as stream:
                stream.truncate(size)
        with (work / "unselected.raw").open("r+b") as stream:
            stream.write(MARKER)
        shutil.copyfile("/usr/share/OVMF/OVMF_VARS_4M.fd", work / "vars.fd")
        digest = lambda: hashlib.file_digest((work / "unselected.raw").open("rb"), "sha256").hexdigest()
    before = digest()

    def stop(guest):
        if kvm: return kvm.stop(guest)
        guest.terminate()
        try: guest.wait(timeout=20)
        except subprocess.TimeoutExpired: guest.kill(); guest.wait(timeout=10)
    tls = ssl.create_default_context(cafile=str(root / "root.crt"))
    owner_key = (root / "owner-key").read_text().strip()

    def api(path, method="GET", body=None):
        request = urllib.request.Request("https://192.168.0.222:19443" + path, method=method,
            headers={"Authorization": "Bearer " + owner_key, "Content-Type": "application/json"},
            data=None if body is None else json.dumps(body).encode())
        # Loopback is used only for this private fixture's controller transport.
        import http.client
        class LocalConnection(http.client.HTTPSConnection):
            def connect(self):
                import socket
                self.sock = self._context.wrap_socket(socket.create_connection(("127.0.0.1", 19443), timeout=15),
                                                      server_hostname="192.168.0.222")
        class LocalHTTPS(urllib.request.HTTPSHandler):
            def https_open(self, request):
                return self.do_open(lambda host, **kw: LocalConnection(host, context=tls, **kw), request)
        with urllib.request.build_opener(urllib.request.ProxyHandler({}), LocalHTTPS()).open(request, timeout=15) as response:
            return json.load(response)

    controller_log = (work / "controller.log").open("xb")
    controller = subprocess.Popen([args.controller, "--fixture", str(root)], stdout=controller_log, stderr=subprocess.STDOUT)
    guest = None
    try:
        until = time.monotonic() + 90
        while True:
            try:
                if api("/health/live")["fixture"]: break
            except (OSError, urllib.error.URLError):
                if controller.poll() is not None: raise RuntimeError("The disposable controller stopped; inspect controller.log.")
                if time.monotonic() >= until: raise
                time.sleep(1)
        api("/api/host/onboarding/window", "POST", {"minutes": 60})
        serial = vm / "serial.log"
        qemu_log = (work / "qemu.log").open("xb")
        command = [
            "qemu-system-x86_64", "-machine", "q35,accel=" + ("kvm" if kvm else "tcg"), "-cpu", "host" if kvm else "max",
            "-smp", "4" if kvm else "2", "-m", "3072",
            "-uuid", "6190bd33-c7a1-4ffc-a9f5-5d46bc7c1221", "-display", "none", "-monitor", "none",
            "-serial", "file:" + str(serial), "-boot", "once=n,order=c", "-pidfile", str(vm / "qemu.pid"),
            "-drive", f"if=pflash,format=raw,readonly=on,file={firmware}",
            "-drive", f"if=pflash,format=raw,file={vm / 'vars.fd'}",
            "-drive", f"if=none,id=selected,format=raw,file={vm / 'selected.raw'}",
            "-device", "virtio-blk-pci,drive=selected,serial=LUCIA-INSTALL-TARGET",
            "-drive", f"if=none,id=unselected,format=raw,file={vm / 'unselected.raw'}",
            "-device", "virtio-blk-pci,drive=unselected,serial=LUCIA-DO-NOT-TOUCH",
            "-netdev", f"user,id=net0,net=192.168.0.0/23,host=192.168.0.222,dhcpstart=192.168.1.100,dns=192.168.0.221,tftp={vm / 'tftp'},bootfile=debian-installer/amd64/bootnetx64.efi,hostfwd=tcp:127.0.0.1:2222-:22"
            # A KVM host's resolver may return AAAA records that slirp cannot reach; the installer's wget would not fall back.
            + (",ipv6=off" if kvm else ""),
            "-device", "virtio-net-pci,netdev=net0,romfile="
        ]
        if kvm:
            # OVMF's PXE stack needs a secure RNG; hosts without RDRAND (older Xeons) otherwise get no network boot entry.
            command += ["-device", "virtio-rng-pci"]
        guest = kvm.start(command, qemu_log) if kvm else subprocess.Popen(command, stdout=qemu_log, stderr=subprocess.STDOUT)
        until = time.monotonic() + 3600  # A full network install of trixie takes ~30 minutes before enrollment.
        approved = None
        last_phase = None
        managed = None
        while time.monotonic() < until:
            if guest.poll() is not None: raise RuntimeError("Disposable VM exited before qualification.")
            snapshot = api("/api/host/onboarding")
            for device in snapshot["devices"]:
                if device["hardware"]["hardwareUuid"] != "6190bd33-c7a1-4ffc-a9f5-5d46bc7c1221":
                    raise RuntimeError("Unexpected hardware entered the isolated fixture.")
                if device["phase"] != last_phase:
                    print(json.dumps({"phase": device["phase"], "fixture": True}), flush=True)
                    last_phase = device["phase"]
                if approved is None:
                    target = [d for d in device["hardware"]["disks"] if d["id"] == "/dev/disk/by-id/virtio-LUCIA-INSTALL-TARGET"]
                    protected = [d for d in device["hardware"]["disks"] if d["id"] == "/dev/disk/by-id/virtio-LUCIA-DO-NOT-TOUCH"]
                    if len(target) != 1 or len(protected) != 1 or target[0]["sizeBytes"] != 16 * 1024 ** 3:
                        raise RuntimeError("Disposable-disk identity mismatch; no approval sent.")
                    approved = api("/api/host/devices/" + device["id"] + "/approve-install", "POST",
                        {"hostname": "qualification-node", "diskId": target[0]["id"], "confirmation": "ERASE",
                         "recoveryPublicKey": (root / "recovery-key.pub").read_text().strip()})
                if device["phase"] == "Failed": raise RuntimeError("Disposable installation reported failure; inspect private fixture logs.")
                if device["phase"] == "Managed":
                    managed = api("/api/host/nodes")
                    if len(managed) == 1 and managed[0]["status"] is not None: break
            if managed is not None: break
            time.sleep(5)
        if managed is None: raise RuntimeError("Disposable install/enrollment timed out; no qualification issued.")
        ssh = ["ssh", "-p", "2222", "-o", "StrictHostKeyChecking=accept-new",
               "-o", "UserKnownHostsFile=" + str(work / "known-hosts"), "-o", "ConnectTimeout=10",
               "-i", str(root / "recovery-key"), "lucia-recovery@127.0.0.1"]
        result = subprocess.run(ssh + ["sudo", "-n", "sh", "-c",
            "'test \"$(. /etc/os-release; echo $ID)\" = debian && test \"$(hostname)\" = qualification-node && getent passwd qualification-owner && getent group lucia-owners'"],
            capture_output=True, text=True, timeout=60)
        if result.returncode:
            raise RuntimeError(f"Installed recovery SSH or directory identity checks failed (exit {result.returncode}): {result.stderr.strip()[-400:]}")
        # Password login is intentionally a separate mandatory check, not inferred
        # from getent or a heartbeat. The host runner supplies its isolated owner.
        password = (root / "owner-password").read_text().strip()
        askpass = work / "askpass"
        askpass.write_text("#!/bin/sh\ncat " + str(root / "owner-password") + "\n")
        askpass.chmod(0o700)
        environment = {**os.environ, "SSH_ASKPASS": str(askpass), "SSH_ASKPASS_REQUIRE": "force", "DISPLAY": "fixture:0"}
        login = subprocess.run(["ssh", "-p", "2222", "-o", "StrictHostKeyChecking=yes",
            "-o", "UserKnownHostsFile=" + str(work / "known-hosts"), "-o", "PreferredAuthentications=password",
            "-o", "PubkeyAuthentication=no", "-o", "NumberOfPasswordPrompts=1", "-o", "ConnectTimeout=10",
            "qualification-owner@127.0.0.1", "id", "-un"], stdin=subprocess.DEVNULL, capture_output=True,
            text=True, env=environment, start_new_session=True, timeout=60)
        del password
        if login.returncode or login.stdout.strip() != "qualification-owner":
            raise RuntimeError(f"Real LDAP SSH password login did not pass (exit {login.returncode}): {login.stderr.strip()[-400:]}")
        # Owner keys added in Lucia must reach the node through heartbeats, and removal must revoke them.
        # Restart the agent first: sssd rewrites its config modes, and only a healthy restarted agent can deliver keys.
        restart = subprocess.run(ssh + ["sudo", "-n", "systemctl", "restart", "lucia-node-agent"], capture_output=True, text=True, timeout=60)
        if restart.returncode:
            raise RuntimeError(f"Managed agent restart failed (exit {restart.returncode}): {restart.stderr.strip()[-400:]}")
        owner_ssh = work / "owner-ssh-key"
        subprocess.run(["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-C", "qualification", "-f", str(owner_ssh)], check=True, timeout=30)
        added = api("/api/host/ssh-keys", "POST", {"publicKey": (work / "owner-ssh-key.pub").read_text().strip(), "label": None})
        if added["username"] != "qualification-owner" or len(added["keys"]) != 1:
            raise RuntimeError("The owner SSH key was not stored for the directory account.")
        def key_login():
            return subprocess.run(["ssh", "-p", "2222", "-o", "StrictHostKeyChecking=yes", "-o", "BatchMode=yes",
                "-o", "UserKnownHostsFile=" + str(work / "known-hosts"), "-o", "PreferredAuthentications=publickey",
                "-o", "IdentitiesOnly=yes", "-o", "ConnectTimeout=10", "-i", str(owner_ssh),
                "qualification-owner@127.0.0.1", "id", "-un"], stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=60)
        def wait_for(accepted):
            until = time.monotonic() + 150
            while True:
                attempt = key_login()
                if (attempt.returncode == 0 and attempt.stdout.strip() == "qualification-owner") == accepted: return
                if time.monotonic() >= until:
                    raise RuntimeError(f"Owner SSH key {'login' if accepted else 'revocation'} did not converge: {attempt.stderr.strip()[-400:]}")
                time.sleep(10)
        wait_for(True)
        api("/api/host/ssh-keys/" + added["keys"][0]["id"], "DELETE")
        wait_for(False)
        active = subprocess.run(ssh + ["systemctl", "show", "-p", "NRestarts", "--value", "lucia-node-agent"], capture_output=True, text=True, timeout=60)
        if active.returncode or active.stdout.strip() != "0":
            raise RuntimeError(f"The restarted managed agent crashed and was restarted by systemd: {active.stdout.strip()} {active.stderr.strip()[-200:]}")
        stop(guest)
        guest = None
        if digest() != before: raise RuntimeError("The unselected disposable disk changed.")
        receipt = json.loads((bundle / "receipt.json").read_text())
        result = {"schemaVersion": 1, "artifacts": receipt["artifacts"], "publicCaDerSha256": receipt["controller"]["publicCaDerSha256"],
            "installationPayloadSha256": installation_payload_sha256(receipt),
            "backendSha256": hashlib.sha256((pathlib.Path(args.controller).parent / "Lucia.Homelab.Server.dll").read_bytes()).hexdigest(),
            "nativeEnrollmentSha256": hashlib.sha256((pathlib.Path(__file__).resolve().parents[1] / "nodes/enrollment_worker.py").read_bytes()
                + b"\0" + (pathlib.Path(__file__).resolve().parents[1] / "nodes/prepare_directory.py").read_bytes()).hexdigest(),
            "selectedDiskInstalled": True, "otherDiskUnchanged": True, "managedHeartbeatVerified": True, "directoryLoginVerified": True, "ownerKeyLoginVerified": True,
            "recoverySshVerified": True, "unselectedDiskSha256": before, "fixtureOnly": True}
        (root / "installation-result.json").write_text(json.dumps(result, indent=2) + "\n")
        print(json.dumps({"qualificationPassed": True, "physicalDevicesModified": False}), flush=True)
    finally:
        try:
            if guest is not None and guest.poll() is None: stop(guest)
        finally:
            if kvm: kvm.cleanup(work)
        controller.terminate()
        try: controller.wait(timeout=15)
        except subprocess.TimeoutExpired: controller.kill(); controller.wait(timeout=10)
        controller_log.close()


if __name__ == "__main__":
    main()
