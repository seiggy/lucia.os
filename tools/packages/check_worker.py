"""Pure request, classification and apt-output checks; never runs apt."""

import importlib.util
import pathlib

spec = importlib.util.spec_from_file_location("package_worker", pathlib.Path(__file__).with_name("package_worker.py"))
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)

request_id = "a" * 32
install = {"schemaVersion": 1, "id": request_id, "action": "install", "packages": ["netplan.io", "libc6"],
           "includePlatform": False, "restart": "services", "requestedAt": "2026-01-01T00:00:00Z"}
assert worker.validate_request(install, request_id)["packages"] == ["netplan.io", "libc6"]
assert worker.validate_request({**install, "packages": "all"}, request_id)["packages"] == "all"
assert worker.validate_request({**install, "packages": []}, request_id)["packages"] == []
assert worker.validate_request({"schemaVersion": 1, "id": request_id, "action": "changelog", "package": "curl"},
                               request_id)["package"] == "curl"
for change in (
    {"schemaVersion": 2}, {"id": "b" * 32}, {"action": "shell"}, {"command": "rm -rf /"},
    {"packages": ["curl; reboot"]}, {"packages": ["-o=APT::Foo"]}, {"packages": ["curl", "curl"]},
    {"packages": "some"}, {"includePlatform": "yes"}, {"restart": "now"}, {"package": "curl"},
):
    try:
        worker.validate_request({**install, **change}, request_id)
        raise AssertionError("Unsafe package request was accepted: {}".format(change))
    except ValueError:
        pass
try:
    worker.parse_json('{"id":"a","id":"b"}')
    raise AssertionError("Duplicate fields were accepted.")
except ValueError:
    pass

ubuntu = [{"origin": "Ubuntu", "label": "Ubuntu", "archive": "noble-updates", "site": "ports.ubuntu.com"}]
security = [{"origin": "Ubuntu", "label": "Ubuntu", "archive": "noble-security", "site": "ports.ubuntu.com"}]
nvidia = [{"origin": "NVIDIA", "label": "NVIDIA CUDA", "archive": "", "site": "developer.download.nvidia.com"}]
assert worker.classify("netplan.io", ubuntu) == (False, "services")
assert worker.classify("libc6", security) == (False, "spark")
assert worker.classify("linux-image-7.0.0-1020-nvidia", ubuntu) == (True, "spark")
assert worker.classify("nvidia-driver-580", ubuntu) == (True, "spark")
assert worker.classify("docker-ce", ubuntu) == (True, "lucia")
assert worker.classify("libnvidia-container1", ubuntu) == (True, "lucia")
assert worker.classify("cuda-toolkit-13-0", nvidia) == (True, "services")
assert worker.classify("some-tool", nvidia)[0] is True
assert worker.classify("libcurl4t64", security)[0] is False
assert worker.classify("libcublas12", ubuntu)[0] is True
assert worker.source_label(security) == "Ubuntu security"
assert worker.source_label(ubuntu) == "Ubuntu updates"
assert worker.source_label(nvidia) == "NVIDIA CUDA"
assert worker.source_label([]) == "Unknown source"

simulation = """Reading package lists...
Inst netplan.io [1.1.1] (1.1.2 Ubuntu:24.04/noble-updates [arm64])
Inst libnetplan1:arm64 [1.1.1] (1.1.2 Ubuntu:24.04/noble-updates [arm64])
Remv oldthing [1.0]
Conf netplan.io (1.1.2 Ubuntu:24.04/noble-updates [arm64])
"""
assert worker.parse_simulation(simulation) == (["netplan.io", "libnetplan1"], ["oldthing"])
upgrade = """The following packages have been kept back:
  linux-image-nvidia linux-nvidia
  nvidia-driver-580
The following upgrades have been deferred due to phasing:
  libnetplan1 netplan.io
The following packages will be upgraded:
  curl
"""
assert worker.parse_held(upgrade) == {"keptBack": ["linux-image-nvidia", "linux-nvidia", "nvidia-driver-580"],
                                      "phased": ["libnetplan1", "netplan.io"]}
restart = """NEEDRESTART-VER: 3.6
NEEDRESTART-KCUR: 7.0.0-1019-nvidia
NEEDRESTART-KEXP: 7.0.0-1020-nvidia
NEEDRESTART-KSTA: 3
NEEDRESTART-SVC: dgx-dashboard.service
NEEDRESTART-SVC: dgx-dashboard-admin.service
"""
assert worker.parse_needrestart(restart) == {"services": ["dgx-dashboard-admin.service", "dgx-dashboard.service"],
                                             "kernelPending": True}
assert worker.parse_needrestart("NEEDRESTART-KSTA: 1\n")["kernelPending"] is False

assert "busy" in worker.plain_failure("E: Could not get lock /var/lib/dpkg/lock-frontend", 100)
assert "Repair" in worker.plain_failure("E: dpkg was interrupted, you must manually run", 100)
assert "internet" in worker.plain_failure("Err:1 Temporary failure resolving 'ports.ubuntu.com'", 100)
assert "disk space" in worker.plain_failure("No space left on device", 100)
assert "depend" in worker.plain_failure("The following packages have unmet dependencies:", 100)
assert "time limit" in worker.plain_failure("", -9)
assert "exit code 100" in worker.plain_failure("anything", 100)
assert worker.tail("a\r\nb\x1b[0m\n\n") == ["a", "b[0m"]
apt_log = """Get:1 http://ports.ubuntu.com/ubuntu-ports noble-updates/main arm64 bison arm64 2:3.8.2 [748 kB]
Get:2 http://ports.ubuntu.com/ubuntu-ports noble-updates/main arm64 curl arm64 8.5.0 [227 kB]
"""
assert worker.apt_progress("", 3) == {"step": "downloading", "done": 0, "total": 3, "package": None}
assert worker.apt_progress(apt_log, 3) == {"step": "downloading", "done": 2, "total": 3, "package": "curl"}
apt_log += "Unpacking bison (2:3.8.2) over (2:3.8.1) ...\nUnpacking libcurl4t64:arm64 (8.5.0) over (8.4) ...\n"
assert worker.apt_progress(apt_log, 3) == {"step": "unpacking", "done": 2, "total": 3, "package": "libcurl4t64"}
apt_log += "Setting up bison (2:3.8.2) ...\nSetting up a (1) ...\nSetting up b (1) ...\nSetting up c (1) ...\n"
assert worker.apt_progress(apt_log, 3) == {"step": "configuring", "done": 3, "total": 3, "package": "c"}
# The NVIDIA device list: refreshing it is a fixed action with no fields, and NVIDIA package changes trigger it.
refresh = {"schemaVersion": 1, "id": request_id, "action": "refresh-gpu-devices"}
assert worker.validate_request(refresh, request_id) == {"id": request_id, "action": "refresh-gpu-devices"}
for change in ({"output": "/etc/passwd"}, {"packages": ["nvidia-utils-580"]}, {"command": "nvidia-ctk cdi generate"}):
    try:
        worker.validate_request({**refresh, **change}, request_id)
        raise AssertionError("A refresh request with extra fields was accepted: {}".format(change))
    except ValueError:
        pass
assert all(worker.NVIDIA_PACKAGE.match(name) for name in ("libnvidia-egl-wayland1", "nvidia-container-toolkit", "cuda-compat-13", "nvidia-utils-580"))
assert not any(worker.NVIDIA_PACKAGE.match(name) for name in ("openssl", "linux-image-nvidia", "libcudnn9", "xserver-xorg-video-nvidia"))


def refreshed(unit, start_code, current_after_start, ctk):
    """Runs refresh_gpu_devices against fakes; returns (success, commands run)."""
    commands, real = [], (worker.run, worker.gpu_devices, worker.shutil.which)
    state = {"current": False}

    def run(command, timeout, log=None):
        commands.append(command[:2])
        if command[:2] == ["systemctl", "cat"]:
            return (0 if unit else 1), ""
        if command[:2] == ["systemctl", "start"]:
            state["current"] = current_after_start
            return start_code, "started"
        if command[0] == "nvidia-ctk":
            state["current"] = True
            return 0, "generated"
        raise AssertionError(command)
    worker.run = run
    worker.gpu_devices = lambda *_: {"present": True, "current": state["current"], "message": None if state["current"] else "stale"}
    worker.shutil.which = lambda name: "/usr/bin/nvidia-ctk" if ctk and name == "nvidia-ctk" else None
    try:
        success, message, _ = worker.refresh_gpu_devices()
    finally:
        worker.run, worker.gpu_devices, worker.shutil.which = real
    return success, commands


assert refreshed(True, 0, True, True) == (True, [["systemctl", "cat"], ["systemctl", "start"]])
assert refreshed(False, 0, False, True) == (True, [["systemctl", "cat"], ["nvidia-ctk", "cdi"]])
assert refreshed(True, 0, False, True) == (True, [["systemctl", "cat"], ["systemctl", "start"], ["nvidia-ctk", "cdi"]])
assert refreshed(False, 0, False, False)[0] is False
assert refreshed(True, 1, False, False)[0] is False
print("package worker checks passed")
