import argparse
import contextlib
import fcntl
import hashlib
import json
import os
import pathlib
import shutil
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.request

os.umask(0o077)
sys.dont_write_bytecode = True
stage = pathlib.Path(__file__).resolve().parent
home = pathlib.Path.home()
root = home / ".local/share/lucia/bootstrap"
state = root.parent / "identity"
qual = home / ".cache/lucia-managed-qualification-cb41986e"
sha = lambda data: hashlib.sha256(data).hexdigest()
source_hash = (stage / "install-source.sha256").read_text().strip()
# The stable validator predates the enrollment files; use the reviewed one from the hash-pinned archive.
archive = (stage / "install-source.tar.gz").read_bytes()
assert sha(archive) == source_hash
import io, tarfile
with tarfile.open(fileobj=io.BytesIO(archive), mode="r:gz") as tar:
    reviewed = tar.extractfile("tools/desktop/bootstrap.py").read()
(stage / "validator").mkdir(exist_ok=True)
(stage / "validator/bootstrap.py").write_bytes(reviewed)
sys.path[:0] = [str(stage / "validator"), str(root / "app/tools/identity")]
import bootstrap
from provision import Provisioner, write_file

files, manifest = bootstrap.validated_payload(stage / "install-source.tar.gz", source_hash)
host = json.loads((state / "host-settings.json").read_text())
backend = pathlib.Path(host["host_state"]) / "releases" / host["artifact_sha256"] / "publish/Lucia.Homelab.Server.dll"
qualification = json.loads((qual / "installation-qualification.json").read_text())
native = files["tools/nodes/enrollment_worker.py"] + b"\0" + files["tools/nodes/prepare_directory.py"]
# Development iterations only: deploy an installer build that has not passed the disposable VM run.
SKIP = "--skip-qualification" in sys.argv[2:]
if not SKIP:
    assert qualification["schemaVersion"] == 1 and qualification["backendSha256"] == sha(backend.read_bytes()), "Backend differs from the qualified one."
    assert qualification["nativeEnrollmentSha256"] == sha(native), "Enrollment worker differs from the qualified one."
    for name in ("tools/boot/prepare.py", "tools/nodes/enrollment_worker.py", "tools/nodes/prepare_directory.py"):
        assert files[name] == (qual / "source" / name).read_bytes(), name
bundle = stage / "work/bundle-install"
boot = json.loads((state / "boot-settings.json").read_text())
current_bundle = home / ".cache/lucia-realtek-cb41986e/work/bundle-realtek"


def unpack_agent(archive):
    directory = stage / ("agent-" + sha(archive.read_bytes())[:16])
    if not directory.exists():
        directory.mkdir(mode=0o700)
        with tarfile.open(archive, "r:gz") as source:
            for member in source:
                path = pathlib.PurePosixPath(member.name)
                if member.name not in (".", "./") and (path.is_absolute() or ".." in path.parts or not (member.isdir() or member.isfile())):
                    raise RuntimeError("Unexpected agent archive path.")
            source.extractall(directory, filter="data")
    for path in directory.rglob("*"):
        if path.is_file(): path.chmod(0o700 if path.suffix == "" else 0o600)
    return directory


def prepare():
    for name, content in files.items():
        bootstrap.write_bytes(stage / "source" / name, content)
    sys.path.insert(0, str(stage / "source"))
    from tools.boot import prepare as p
    # The previous bundle predates the theme artifact; only its controller settings are reused.
    old = json.loads((current_bundle / "receipt.json").read_text())
    live = json.loads((pathlib.Path(boot["assets_directory"]).parent / "receipt.json").read_text())
    assert old["controller"] == live["controller"], "Live boot receipt differs from the realtek source bundle."
    ca = stage / "public-ca.crt"
    shutil.copyfile(state / "trust/lucia-root-ca.crt", ca)
    assert old["controller"]["publicCaSha256"] == sha(ca.read_bytes())
    (stage / "work").mkdir(exist_ok=True)
    shutil.rmtree(bundle, ignore_errors=True)
    if SKIP:
        agent = unpack_agent(stage / "managed-node-agent.tar.gz")
    else:
        qualified = p.verify_bundle(sorted(qual.glob("vm-*/host/bundle"))[-1])
        assert p.installation_payload_sha256(qualified) == qualification["installationPayloadSha256"]
        agent, = [path for path in qual.glob("agent-*") if path.is_dir()
                  and {item.name: sha(item.read_bytes()) for item in path.iterdir() if item.is_file()} == qualified["agentFiles"]]
    command = ["docker", "run", "--rm", "--network", "none", "--user", f"{os.getuid()}:{os.getgid()}",
        "--mount", f"type=bind,src={stage / 'source'},dst=/src,readonly",
        "--mount", f"type=bind,src={agent},dst=/agent,readonly",
        "--mount", f"type=bind,src={qual / 'installation-replay'},dst=/replay,readonly",
        "--mount", f"type=bind,src={stage / 'work'},dst=/work",
        "--mount", f"type=bind,src={ca},dst=/public-ca.crt,readonly",
        "--workdir", "/work", "--entrypoint", "python3", "lucia-install-qualification:cb41986e",
        "/src/tools/boot/prepare.py", "build", "--installation", "--output", "/work/bundle-install", "--node-agent", "/agent",
        "--ca-file", "/public-ca.crt", "--ca-der-sha256", old["controller"]["publicCaDerSha256"],
        "--server", old["controller"]["server"], "--replay", "/replay"]
    if old["controller"]["connectAddress"]:
        command.extend(["--connect-address", old["controller"]["connectAddress"]])
    result = subprocess.run(command, capture_output=True, text=True, timeout=900)
    if result.returncode:
        raise RuntimeError(f"Bundle build failed:\n{result.stdout[-4000:]}\n{result.stderr[-4000:]}")
    receipt = p.verify_bundle(bundle)
    assert receipt["purpose"] == "approved-installation-with-managed-enrollment"
    assert receipt["controller"] == old["controller"]
    if not SKIP:
        assert p.installation_payload_sha256(receipt) == qualification["installationPayloadSha256"], "Bundle differs from the VM-qualified content."
        write_file(bundle / "installation-qualification.json", (qual / "installation-qualification.json").read_bytes())
    print(json.dumps({"phase": "prepared", "bundle": str(bundle), "qualificationSkipped": SKIP, "productionModified": False}))


def deploy():
    p = Provisioner(argparse.Namespace(state=str(state), host=None, certificate_mode="private-ca",
                                      auth_port=None, ldap_port=None, ca_port=None))
    with contextlib.ExitStack() as stack:
        for path in (root / ".run.lock", state / ".provision.lock"):
            lock = stack.enter_context(path.open("a+b"))
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        p.load_existing()
        key = (state / "secrets/host-owner-api-key").read_text().strip()
        tls = ssl.create_default_context(cafile=str(state / "trust/lucia-root-ca.crt"))
        origin = host["authentication"]["public_origin"]

        def get(path, method="GET"):
            request = urllib.request.Request(origin + path, headers={"Authorization": "Bearer " + key}, method=method)
            with urllib.request.urlopen(request, context=tls, timeout=60) as response:
                return json.load(response)

        get("/api/host/onboarding/window", method="DELETE")
        assert get("/api/host/onboarding")["window"]["isOpen"] is False
        previous_manifest = (root / "app/manifest.json").read_bytes()
        old_files = {}
        for entry in json.loads(previous_manifest)["files"]:
            old_files[entry["path"]] = (root / "app" / entry["path"]).read_bytes()
            assert sha(old_files[entry["path"]]) == entry["sha256"]
        assert set(old_files) <= set(files)
        previous_boot = (state / "boot-settings.json").read_bytes()
        identity = ["lucia-identity-gateway", "lucia-identity-server", "lucia-identity-worker", "lucia-identity-db",
                    "lucia-identity-ldap", "lucia-identity-ca", "lucia-identity-renewer"]
        before = {item["Name"]: item["Id"] for item in json.loads(p.run(["docker", "inspect", *identity]).stdout)}
        for name, content in old_files.items():
            bootstrap.write_bytes(stage / "rollback" / name, content)
        bootstrap.write_bytes(stage / "previous-source-manifest.json", previous_manifest)
        write_file(stage / "previous-boot-settings.json", previous_boot)
        for name, content in files.items():
            bootstrap.write_bytes(stage / "payload" / name, content)
        bootstrap.write_json(stage / "payload/manifest.json", manifest)
        attempted = False
        try:
            bootstrap.stable_payload(root, stage)
            app = root / "app"
            sys.path[:0] = [str(app), str(app / "tools/nodes")]
            import enrollment_worker as node_enrollment
            import prepare_directory as node_directory
            worker = node_enrollment.Native(argparse.Namespace(state=str(state)))
            worker.deadline = time.monotonic() + 120
            worker.load_scoped()
            worker.verify_containers()
            print(json.dumps({"phase": "directory-policy", **node_directory.prepare(worker, node_enrollment)}), flush=True)
            p.run([sys.executable, str(app / "tools/nodes/enrollment_worker.py"), "install"])
            from tools.boot.provision_boot import prepare_boot
            prepared = prepare_boot(p, bundle, bind_address=boot["bind_address"], network_cidr=boot["network_cidr"],
                                    http_port=boot["http_port"], discovery_qualified=True, installation_qualified=True,
                                    **({"skip_qualification": True} if SKIP else {}))
            assets = pathlib.Path(prepared["assets_directory"])
            p.publish()
            assert not get("/api/host/onboarding")["window"]["isOpen"], "Discovery opened while staging; defer deployment."
            attempted = True
            p.deploy()
            p.wait_container("lucia-boot", timeout=60)
            after = {item["Name"]: item["Id"] for item in json.loads(p.run(["docker", "inspect", *identity]).stdout)}
            assert before == after, "An identity service was recreated."
            overlay = sha((assets / "lucia/lucia-overlay.cpio.gz").read_bytes())
            assert p.run(["docker", "exec", "lucia-boot", "sha256sum", "/boot/lucia/lucia-overlay.cpio.gz"]).stdout.split()[0] == overlay
            deadline = time.monotonic() + 180
            while True:
                try:
                    current = get("/api/host/onboarding")
                    if current["readiness"]["canInstall"]:
                        break
                except (urllib.error.URLError, OSError, ValueError):
                    current = None
                if time.monotonic() > deadline:
                    raise AssertionError(f"Installation readiness did not become available: {current and current['readiness']}")
                time.sleep(3)
            assert not current["window"]["isOpen"] and current["readiness"]["canDiscover"]
            status = subprocess.run(["systemctl", "--user", "is-active", "lucia-node-enrollment.service"], capture_output=True, text=True)
            assert status.stdout.strip() == "active", "Node enrollment service is not running."
            print(json.dumps({"phase": "deployed", "canInstall": True, "windowOpen": False, "qualificationSkipped": SKIP, "assets": str(assets)}), flush=True)
        except BaseException:
            print("Deployment failed; restoring the previous source and boot settings.", flush=True)
            for name, content in old_files.items():
                bootstrap.write_bytes(root / "app" / name, content)
            for name in set(files) - set(old_files):
                (root / "app" / name).unlink(missing_ok=True)
            bootstrap.write_bytes(root / "app/manifest.json", previous_manifest)
            write_file(state / "boot-settings.json", previous_boot)
            subprocess.run(["systemctl", "--user", "disable", "--now", "lucia-node-enrollment.service"], capture_output=True)
            if attempted:
                p.publish()
                p.deploy()
            raise


{"prepare": prepare, "deploy": deploy}[sys.argv[1]]()
