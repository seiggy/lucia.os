import argparse
import contextlib
import fcntl
import hashlib
import json
import os
import pathlib
import ssl
import subprocess
import sys
import urllib.error
import urllib.request

os.umask(0o077)
sys.dont_write_bytecode = True
stage = pathlib.Path(__file__).resolve().parent
root = pathlib.Path.home() / ".local/share/lucia/bootstrap"
app = root / "app"
state = root.parent / "identity"
sys.path[:0] = [str(app / "tools/identity"), str(app / "tools/host")]
import application
from provision import Provisioner, write_file

SOURCES = ("deployment/host/Dockerfile", "tools/host/package.py", "tools/skills/skill_worker.py", "deployment/skill-runner/Dockerfile")
expected = json.loads((stage / "manifest.json").read_text())["sha256"]
p = Provisioner(argparse.Namespace(state=str(state), host=None, certificate_mode="private-ca",
                                  auth_port=None, ldap_port=None, ca_port=None))
with contextlib.ExitStack() as stack:
    for path in (root / ".run.lock", state / ".provision.lock"):
        lock = stack.enter_context(path.open("a+b"))
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    p.load_existing()
    settings_path = state / "host-settings.json"
    previous_bytes = settings_path.read_bytes()
    previous = json.loads(previous_bytes)
    data = pathlib.Path(previous["data_directory"])
    key = (state / "secrets/host-owner-api-key").read_text().strip()
    inference_key = (state / "secrets/host-inference-api-key").read_text().strip()
    origin = previous["authentication"]["public_origin"]
    tls = ssl.create_default_context(cafile=str(state / "trust/lucia-root-ca.crt"))
    public_origin = json.loads((data / "domains/active.json").read_text())["canonicalLuciaOrigin"]

    def get(path, key=key, public=False, raw=False, method="GET"):
        call = urllib.request.Request((public_origin if public else origin) + path,
                                      headers={"Authorization": "Bearer " + key} if key else {}, method=method)
        with urllib.request.urlopen(call, context=ssl.create_default_context() if public else tls, timeout=90) as response:
            return response.read().decode() if raw else json.load(response)

    def slots():
        value = get("/api/host/status")
        return {slot: None if value[slot] is None else value[slot]["id"] for slot in ("chat", "embedding")}

    before_slots = slots()
    before_keys = get("/api/host/inference-keys")
    before_domain = get("/api/host/domains/status")["active"]
    assert not get("/api/host/onboarding")["window"]["isOpen"], "Discovery is open; defer the host restart."
    protected = [data / "domains/active.json", data / "domains/workflow.json",
                 state / "owner.json", state / "host-auth.json", state / "boot-settings.json",
                 state / "trust/lucia-root-ca.crt", state / "certificates/identity.key",
                 pathlib.Path(previous["model_directory"]) / "active-models.json",
                 *[path for path in (state / "secrets").iterdir() if path.is_file()]]
    for directory in ("data-protection", "provider-credentials", "network-credentials", "inference-keys", "domains/certificates"):
        protected.extend(path for path in (data / directory).rglob("*") if path.is_file())
    fingerprints = {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in protected}
    hardware_before = json.loads((data / "onboarding/state.json").read_text())
    previous_manifest = (app / "manifest.json").read_bytes()
    old = {name: (app / name).read_bytes() if (app / name).exists() else None for name in SOURCES}
    write_file(stage / "previous-host-settings.json", previous_bytes)
    write_file(stage / "previous-source-manifest.json", previous_manifest)
    attempted = False
    try:
        manifest = json.loads(previous_manifest)
        entries = {item["path"]: item for item in manifest["files"]}
        for name in SOURCES:
            content = (stage / "payload" / name).read_bytes()
            (app / name).parent.mkdir(mode=0o700, parents=True, exist_ok=True)
            write_file(app / name, content)
            entries[name] = {"path": name, "sha256": hashlib.sha256(content).hexdigest()}
        manifest["files"] = sorted(entries.values(), key=lambda item: item["path"])
        write_file(app / "manifest.json", json.dumps(manifest, indent=2) + "\n")
        sys.path.insert(0, str(app / "tools/host"))
        import provision_host
        prepared = provision_host.prepare_host(p, stage / "host-linux-arm64.tar.gz", stage / "manifest.json", previous["authentication"])
        assert prepared["artifact_sha256"] == expected
        for name in ("model_directory", "context_tokens", "voice_reserve_gib", "authentication"):
            assert prepared[name] == previous[name]
        p.publish()
        assert not get("/api/host/onboarding")["window"]["isOpen"], "Discovery opened while staging; defer deployment."
        attempted = True
        p.deploy()
        p.wait_container("lucia-homelab-host", timeout=300)
        environment = json.loads(p.run(["docker", "inspect", "lucia-homelab-host"]).stdout)[0]["Config"]["Env"]
        assert "PackageUpdates__Directory=/data/packages" in environment
        assert "HardwareOnboarding__InstallationEnabled=true" in environment
        assert slots() == before_slots and get("/api/host/inference-keys") == before_keys
        assert get("/api/host/domains/status")["active"] == before_domain
        packages = get("/api/host/packages", public=True)
        assert packages["worker"]["state"] in ("missing", "stale", "ready")
        for credential, status, path in ((None, 401, "/api/host/packages"), (inference_key, 403, "/api/host/packages"), (inference_key, 403, "/api/host/ssh-keys"), (key, 403, "/api/host/ssh-keys"), (None, 401, "/api/host/connections/unifi")):
            try:
                get(path, key=credential, public=True)
            except urllib.error.HTTPError as error:
                assert error.code == status
            else:
                raise RuntimeError("Owner authorization or browser-only SSH key access was not enforced.")
        assert application.application_status(p, origin)["ready"]
        changed = [name for name in fingerprints if hashlib.sha256(pathlib.Path(name).read_bytes()).hexdigest() != fingerprints[name]]
        assert not changed, changed
        hardware_after = json.loads((data / "onboarding/state.json").read_text())
        live = ("updatedAt", "lastSeenAt", "lastHeartbeatAt")
        stable = lambda devices: [{**item, "device": {k: v for k, v in item["device"].items() if k not in live}} for item in devices]
        assert stable(hardware_after["devices"]) == stable(hardware_before["devices"]) and hardware_after["tasks"] == hardware_before["tasks"]
        assert hardware_after["events"][:len(hardware_before["events"])] == hardware_before["events"]
        # The Assistant Skills runner is a setup-user service; installing it again is a no-op restart that picks up the new files.
        subprocess.run([sys.executable, str(app / "tools/skills/skill_worker.py"), "install"], check=True, capture_output=True, timeout=120)
        result = {"agent_jobs": True, "artifact": expected, "worker_state": packages["worker"]["state"],
                  "models_preserved": before_slots, "installation_enabled": True, "persistent_state_preserved": True}
        write_file(stage / "result.json", json.dumps(result, indent=2) + "\n")
        print(json.dumps(result), flush=True)
    except Exception:
        for name, content in old.items():
            if content is None:
                (app / name).unlink(missing_ok=True)
            else:
                write_file(app / name, content)
        write_file(app / "manifest.json", previous_manifest)
        write_file(settings_path, previous_bytes)
        if attempted:
            print("Restoring the previous managed host image; persistent data is preserved.", flush=True)
            p.publish()
            p.deploy()
            p.wait_container("lucia-homelab-host", timeout=300)
        raise
