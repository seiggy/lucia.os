import argparse
import datetime
import hashlib
import importlib.util
import json
import os
import pathlib
import secrets
import shutil
import subprocess
import sys
import tarfile
import time

os.umask(0o077)
sys.dont_write_bytecode = True
stage = pathlib.Path(__file__).resolve().parent
source = stage / "source"
sys.path[:0] = [str(source), str(source / "tools/nodes")]
import enrollment_worker as worker
import prepare_directory
import qualify_native
from tools.boot import prepare


def command(args, timeout=600):
    result = subprocess.run(args, capture_output=True, text=True, timeout=timeout)
    if result.returncode:
        raise RuntimeError(f"Disposable qualification command failed ({args[0]}): {result.stdout[-3000:]}\n{result.stderr[-3000:]}")
    return result.stdout.strip()


def unpack(name, directory):
    digest = hashlib.file_digest((stage / name).open("rb"), "sha256").hexdigest()
    directory = directory.with_name(directory.name + "-" + digest[:16])
    if not directory.exists():
        directory.mkdir(mode=0o700)
        with tarfile.open(stage / name, "r:gz") as archive:
            for member in archive:
                if member.name in (".", "./"): continue
                path = pathlib.PurePosixPath(member.name)
                if path.is_absolute() or ".." in path.parts or not (member.isdir() or member.isfile()):
                    raise RuntimeError("Unexpected qualification binary path.")
            archive.extractall(directory, filter="data")
    for path in directory.rglob("*"):
        if path.is_file(): path.chmod(0o700 if path.suffix == "" or path.name in ("Lucia.ManagedNode.Qualification", "Lucia.NodeAgent.Checks") else 0o600)
    return directory


agent_directory = unpack("managed-node-agent.tar.gz", stage / "agent")
controller_directory = unpack("managed-controller.tar.gz", stage / "controller")
image = "lucia-install-qualification:cb41986e"
command(["docker", "build", "--quiet", "--target", "qualification", "--tag", image, str(source / "tools/boot")])
qualify_native.HOST = "192.168.0.222"
fixture_path = stage / ("vm-" + datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S"))
container = None
with qualify_native.Run(worker, fixture_path, preserve=True) as fixture:
    fixture.deadline = time.monotonic() + 5400
    fixture.settings = {"schema_version": 1, "certificate_mode": "private-ca", "public_host": qualify_native.HOST,
        "ldap_base_dn": qualify_native.BASE, "uid": os.getuid(), "gid": os.getgid(),
        "ports": {"authentik": 9443, "ldaps": 8636, "ca": 9444}}
    fixture.put(fixture.state / "settings.json", fixture.settings)
    fixture.put(fixture.state / "host-settings.json", {"data_directory": str(fixture.data)})
    p = fixture.provisioner()
    p.deadline = time.monotonic() + 120
    p.load_scoped()
    fixture.seed_owner(p)
    prepare_directory.prepare(p, worker)
    fixture.verify_owner(p)
    host = fixture_path / "host"
    control = fixture.data / "boot-control"
    control.mkdir(mode=0o755)
    control.chmod(0o755)
    fixture.put(host / ".lucia-disposable-install-fixture", "no-live-disks-or-identities-v1\n")
    fixture.put(host / "owner-key", secrets.token_urlsafe(48))
    for name, source_path in (("root.crt", fixture.state / "trust/lucia-root-ca.crt"),
                              ("server.crt", fixture.state / "certificates/gateway.crt"),
                              ("server.key", fixture.state / "certificates/gateway.key"),
                              ("owner-password", fixture_path / "owner-password")):
        fixture.put(host / name, source_path.read_bytes())
    command(["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", str(host / "recovery-key")])
    expected_ca = hashlib.sha256(worker.pem_blocks((host / "root.crt").read_text(), "CERTIFICATE")[0]).hexdigest()
    previous = pathlib.Path.home() / ".cache/lucia-realtek-cb41986e/work/bundle-realtek"
    replay = stage / "installation-replay"
    if not replay.exists():
        shutil.copytree(previous / "evidence/downloads", replay)
        old = prepare.verify_bundle(previous)
        entry = old["downloads"][prepare.DIST + "main/binary-amd64/Packages.xz"]
        index = prepare.package_index((replay / entry["file"]).read_bytes())
        prepare.package(prepare.Downloads(replay), index, "ca-certificates", {})
    bundle = host / "bundle"
    print(json.dumps({"phase": "building-disposable-install-image", "productionModified": False}), flush=True)
    result = command(["docker", "run", "--rm", "--network", "none", "--user", f"{os.getuid()}:{os.getgid()}",
        "--mount", f"type=bind,src={source},dst=/src,readonly",
        "--mount", f"type=bind,src={agent_directory},dst=/agent,readonly",
        "--mount", f"type=bind,src={replay},dst=/replay,readonly",
        "--mount", f"type=bind,src={host},dst=/fixture",
        "--workdir", "/fixture", "--entrypoint", "python3", image,
        "/src/tools/boot/prepare.py", "build", "--installation", "--output", "/fixture/bundle", "--node-agent", "/agent",
        "--ca-file", "/fixture/root.crt", "--ca-der-sha256", expected_ca, "--server", "https://192.168.0.222:19443",
        "--connect-address", "192.168.0.222", "--replay", "/replay"], timeout=600)
    print(result, flush=True)
    receipt = prepare.verify_bundle(bundle)
    assert receipt["purpose"] == "approved-installation-with-managed-enrollment"
    # Only this disposable gateway gains egress for guest Debian package downloads.
    # It publishes no host ports and shares no production network or state.
    command(["docker", "network", "connect", "bridge", fixture.containers["gateway"]])
    name = fixture.prefix + "-vm"
    # OpenSSH refuses to run for a uid without a passwd entry.
    passwd = host / "passwd"
    passwd.write_text(f"root:x:0:0:root:/root:/bin/sh\nqualifier:x:{os.getuid()}:{os.getgid()}::/fixture/vm:/bin/sh\n")
    passwd.chmod(0o644)
    kvm_keys = pathlib.Path.home() / ".cache/lucia-qual-kvm"
    kvm = []
    if (kvm_keys / "host").is_file():
        # Real x86 KVM instead of TCG emulation; see KvmHost in qualify_install.py.
        kvm = ["--mount", f"type=bind,src={kvm_keys},dst=/kvm,readonly",
               "-e", "LUCIA_QUAL_KVM_HOST=" + (kvm_keys / "host").read_text().strip(), "-e", "LUCIA_QUAL_KVM_KEYS=/kvm"]
    container = command(["docker", "create", "--name", name, "--network", "container:" + fixture.containers["gateway"],
        "--user", f"{os.getuid()}:{os.getgid()}", "--label", qualify_native.LABEL + "=" + fixture.token,
        "--mount", f"type=bind,src={passwd},dst=/etc/passwd,readonly",
        "--mount", f"type=bind,src={source},dst=/src,readonly",
        "--mount", f"type=bind,src={host},dst=/fixture",
        "--mount", f"type=bind,src={controller_directory},dst=/controller,readonly", *kvm,
        "--workdir", "/fixture", "-e", "LUCIA_DISPOSABLE_INSTALL=1", "--entrypoint", "python3", image,
        "/src/tools/boot/qualify_install.py", "--fixture", "/fixture", "--bundle", "/fixture/bundle",
        "--controller", "/controller/Lucia.ManagedNode.Qualification"])
    fixture.containers["vm"] = container
    command(["docker", "start", container])
    deadline = time.monotonic() + 3800
    requests = fixture.data / "nodes/enrollment-requests"
    responses = fixture.data / "nodes/enrollment-responses"
    worker.private_directory(requests)
    worker.private_directory(responses)
    seen = {}
    while time.monotonic() < deadline:
        running = command(["docker", "inspect", "--format", "{{.State.Running}}", container])
        if running != "true": break
        for path in requests.glob("*.json"):
            raw = worker.regular(path, 32768)
            fingerprint = hashlib.sha256(raw).hexdigest()
            if seen.get(path.stem) == fingerprint: continue
            value = worker.request(worker.parse_json(raw), path.stem)
            p.deadline = time.monotonic() + 120
            configuration = worker.process(p, value, fingerprint)
            worker.write_file(responses / path.name, json.dumps({
                "schemaVersion": 1, "nodeId": value["nodeId"], "taskId": value["taskId"], "requestHash": fingerprint,
                "success": True, "message": "Disposable node identity prepared.",
                "checkedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(), "configuration": configuration
            }) + "\n")
            seen[path.stem] = fingerprint
        worker.write_file(fixture.data / "nodes/enrollment-worker.json", json.dumps({
            "schemaVersion": 1, "ready": True, "checkedAt": datetime.datetime.now(datetime.timezone.utc).isoformat()
        }) + "\n")
        time.sleep(2)
    logs = subprocess.run(["docker", "logs", "--tail", "60", container], capture_output=True, text=True, check=True)
    print(logs.stdout + logs.stderr, flush=True)
    code = command(["docker", "inspect", "--format", "{{.State.ExitCode}}", container])
    if code != "0" or not (host / "installation-result.json").exists():
        raise RuntimeError("Full disposable installation did not qualify. Its private serial/controller logs are retained.")
    result = json.loads((host / "installation-result.json").read_text())
    assert result["selectedDiskInstalled"] and result["otherDiskUnchanged"] and result["managedHeartbeatVerified"] and result["directoryLoginVerified"]
    (stage / "installation-qualification.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps({"phase": "qualified", "result": str(stage / "installation-qualification.json"), "productionModified": False}), flush=True)
