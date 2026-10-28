"""Checks the platform update worker's policy, request and NVIDIA device-list rules; runs anywhere, touches nothing.

Run: python tools/runners/check_platform_images.py (uv run --no-project python on Windows). The C# side has the same
cases in tests/Lucia.Homelab.DomainChecks/PlatformImageChecks.cs, so the worker and the host agree.
"""

import importlib.util
import json
import pathlib
import shutil
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import platform_images as images

spec = importlib.util.spec_from_file_location("package_worker", HERE.parent / "packages" / "package_worker.py")
package_worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package_worker)

passed = 0


def check(condition, message):
    global passed
    if not condition:
        raise SystemExit("FAILED: " + message)
    passed += 1


# Versions read as StackImages.Version reads them.
check(images.version("16.15-alpine") == ("0.0-alpine", [16, 15]), "PostgreSQL's tag keeps its suffix in the shape.")
check(images.version("v3.7.16") == ("v0.0.0", [3, 7, 16]), "Traefik's v prefix is part of the shape.")
check(images.version("latest") is None and images.version("sha-abcdef1") is None,
      "Tags without numbers have no version.")
check(images.compare("2026.8.5", "2026.8.6") > 0 and images.compare("2026.8.5", "2026.10.0") > 0, "Newer tags compare newer.")
check(images.compare("16.15-alpine", "16.16") == 0 and images.compare("v3.7.16", "3.7.17") == 0, "Different shapes never compare.")
check(images.compare("2026.8.5", "2026.8.5") == 0 and images.compare("2026.8.6", "2026.8.5") < 0, "Older and equal tags aren't newer.")

# Policy classes.
check(images.allowed("patch", "16.15-alpine", "16.16-alpine") and not images.allowed("patch", "16.15-alpine", "17.1-alpine"),
      "PostgreSQL takes only the last number.")
check(images.allowed("patch", "2.6.10", "2.6.11") and not images.allowed("patch", "2.6.10", "2.7.0"), "OpenLDAP takes only patches.")
check(images.allowed("minor", "v3.7.16", "v3.8.0") and not images.allowed("minor", "v3.7.16", "v4.0.0"), "Minor stays within the major.")
check(images.allowed("minor", "0.164.0", "0.170.2") and not images.allowed("minor", "0.164.0", "1.0.0"), "A 0.x major is still a major.")
tags = ["2026.8.5", "2026.8.6", "2026.10.0", "2026.10.1", "2026.12.0", "2026.10.0-rc1", "latest"]
check(images.allowed("authentik", "2026.8.5", "2026.8.6", tags), "Authentik patches its release line.")
check(images.allowed("authentik", "2026.8.5", "2026.10.1", tags), "Authentik moves to the next release line.")
check(not images.allowed("authentik", "2026.8.5", "2026.12.0", tags), "Authentik never skips a release line.")
check(images.allowed("authentik", "2026.12.3", "2027.2.0", ["2027.2.0", "2027.4.0"]) and not images.allowed("authentik", "2026.12.3", "2027.4.0", ["2027.2.0", "2027.4.0"]),
      "The next release line crosses the year.")
check(not images.allowed("plugin", "v1.8.1", "v1.9.0") and not images.allowed("unknown", "1.0.0", "1.0.1"), "The plugin and unknown policies apply nothing.")
check(images.release_line("2026.8.5", "2026.10.1") and not images.release_line("2026.8.5", "2026.8.6"), "Release lines are year.minor.")

# The web host's request: exact keys, known services, plain tags and sha256 digests.
digest = "sha256:" + "a" * 64
good = {"schemaVersion": 1, "generation": 4, "requestedAt": "2026-10-28T08:00:00+00:00",
        "updates": [{"service": "authentik", "tag": "2026.8.6", "digest": digest}]}
check(images.validate_request(good) == good["updates"], "A well-formed request is accepted.")
for change, why in (({"extra": 1}, "extra fields"), ({"schemaVersion": 2}, "another schema"), ({"generation": -1}, "a negative generation"),
                    ({"generation": True}, "a boolean generation"), ({"updates": []}, "no updates"),
                    ({"updates": [{"service": "portainer", "tag": "1", "digest": digest}]}, "an unknown service"),
                    ({"updates": [{"service": "gateway", "tag": "v3.7.17; rm -rf /", "digest": digest}]}, "a tag with a command"),
                    ({"updates": [{"service": "gateway", "tag": "v3.7.17", "digest": "sha256:XYZ"}]}, "a bad digest"),
                    ({"updates": [{"service": "gateway", "tag": "v3.7.17", "digest": digest, "command": "x"}]}, "extra update fields"),
                    ({"updates": [{"service": "ca", "tag": "0.30.3", "digest": digest}] * 2}, "a service twice")):
    try:
        images.validate_request({**good, **change})
    except ValueError:
        check(True, why)
    else:
        check(False, f"A request with {why} is refused.")

manifest = {"schemaVersion": 1, "images": [
    {"service": service, "registry": "ghcr.io" if service == "authentik" else "docker.io", "repository": "goauthentik/server" if service == "authentik" else "library-x",
     "tag": "1.0.0", "digest": digest, "policy": policy, "containers": ["identity-" + service], "builtIn": {"tag": "1.0.0", "digest": digest}, "override": False}
    for service, policy in images.SERVICES.items()]}
read = images.validate_manifest(manifest)
check(set(read) == set(images.SERVICES), "The AppHost's manifest lists every platform image.")
entry = images.override_entry(read["authentik"], "1.0.1", "sha256:" + "b" * 64)
check(entry == {"repository": "ghcr.io/goauthentik/server", "tag": "1.0.1", "digest": "sha256:" + "b" * 64,
                "replaces": {"tag": "1.0.0", "digest": digest}}, "An override names the built-in pin it replaces.")
broken = json.loads(json.dumps(manifest))
broken["images"][0]["policy"] = "minor"
try:
    images.validate_manifest(broken)
    check(False, "A manifest whose policy differs from the worker's is refused.")
except ValueError:
    check(True, "policy mismatch")

# The NVIDIA device list: a careful line scan of hostPath, read the same way by every worker.
fixture = HERE / ".check-platform-images"


def rooted(path):
    """An absolute path that starts with /, as on the Spark (on Windows, relative to the current drive)."""
    return path.as_posix()[len(path.drive):]


shutil.rmtree(fixture, ignore_errors=True)
fixture.mkdir()
try:
    present = fixture / "libnvidia-egl-wayland.so.1.1.23"
    present.write_text("")
    spec_file = fixture / "nvidia.yaml"
    spec_file.write_text("\n".join([
        "cdiVersion: 0.5.0", "kind: nvidia.com/gpu", "containerEdits:", "  mounts:",
        f"  - hostPath: {rooted(present)}", f"    containerPath: {rooted(present)}",
        f"  - hostPath: \"{rooted(fixture / 'libnvidia-egl-wayland.so.1.1.21')}\"",
        "  deviceNodes:", "  - path: /dev/nvidia-missing-but-not-a-host-path"]) + "\n")
    for module in (images, package_worker):
        result = module.gpu_devices(spec_file)
        check(result["present"] and not result["current"] and result["missingCount"] == 1 and result["paths"] == 2
              and result["missing"][0].endswith("1.1.21"), f"{module.__name__} finds the stale driver file.")
        (fixture / "libnvidia-egl-wayland.so.1.1.21").write_text("")
        check(module.gpu_devices(spec_file)["current"], f"{module.__name__} reads a current list as current.")
        (fixture / "libnvidia-egl-wayland.so.1.1.21").unlink()
        missing = module.gpu_devices(fixture / "absent.yaml")
        check(not missing["present"] and not missing["current"] and missing["message"], f"{module.__name__} reports a missing list.")
        check(not module.gpu_devices(spec_file, maximum=10)["current"], f"{module.__name__} refuses an oversized list.")
finally:
    shutil.rmtree(fixture, ignore_errors=True)

# After a deploy, a container counts as updated by its digest: Aspire writes repository@sha256:..., with no tag.
digest = "sha256:" + "b" * 64
check(images.moved("ghcr.io/goauthentik/server@" + digest, digest) and images.moved("traefik:v3.7.17@" + digest, digest)
      and not images.moved("ghcr.io/goauthentik/server@sha256:" + "a" * 64, digest) and not images.moved("traefik:v3.7.17", digest),
      "A container moved when it runs the new digest, whether or not its image names a tag.")

# The root package worker's fixed actions.
check("refresh-gpu-devices" in package_worker.ACTIONS, "Refreshing the NVIDIA device list is a fixed package-worker action.")
check(package_worker.validate_request({"schemaVersion": 1, "id": "a" * 32, "action": "refresh-gpu-devices"}, "a" * 32)
      == {"id": "a" * 32, "action": "refresh-gpu-devices"}, "A refresh request carries nothing else.")
try:
    package_worker.validate_request({"schemaVersion": 1, "id": "a" * 32, "action": "refresh-gpu-devices", "output": "/etc/passwd"}, "a" * 32)
    check(False, "A refresh request can't name an output path.")
except ValueError:
    check(True, "refresh output refused")
check(all(package_worker.NVIDIA_PACKAGE.match(name) for name in ("libnvidia-egl-wayland1", "nvidia-container-toolkit", "cuda-toolkit-13", "nvidia-driver-580"))
      and not any(package_worker.NVIDIA_PACKAGE.match(name) for name in ("openssl", "linux-image-nvidia", "libcudnn9")),
      "NVIDIA, libnvidia, CUDA and container-toolkit packages trigger a device-list refresh.")
check(package_worker.HOST_PATH.pattern == images.HOST_PATH.pattern, "Both workers scan the device list the same way.")

print(f"Platform image worker checks passed ({passed} assertions). Nothing on the Spark was touched.")
