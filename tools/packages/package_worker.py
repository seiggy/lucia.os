"""Lucia's scoped apt worker for the DGX Spark.

Runs as a root systemd service. The unprivileged Lucia host exchanges bounded JSON files in
<data>/packages; the worker accepts only fixed actions (check, install named updates, changelog,
restart services, restart the Spark, repair) and never runs caller-supplied commands.
"""

import argparse
import datetime
import json
import os
import pathlib
import re
import secrets
import shutil
import signal
import stat
import subprocess
import sys
import threading
import time

UTC = datetime.timezone.utc
SCHEMA = 1
REQUEST_ID = re.compile(r"^[a-f0-9]{32}$")
PACKAGE = re.compile(r"^[a-z0-9][a-z0-9+.-]{0,127}$")
ACTIONS = {"check", "install", "changelog", "restart-services", "restart-spark", "repair"}
RESTART = {"none", "services", "spark"}
UNIT = "lucia-package-updates.service"
INSTALLED = pathlib.Path("/usr/local/lib/lucia/package_worker.py")
STATE = pathlib.Path("/var/lib/lucia/packages")
LOGS = pathlib.Path("/var/log/lucia/packages")
APT_ENV = {"DEBIAN_FRONTEND": "noninteractive", "NEEDRESTART_SUSPEND": "1", "APT_LISTCHANGES_FRONTEND": "none",
           "LC_ALL": "C.UTF-8", "PATH": "/usr/sbin:/usr/bin:/sbin:/bin"}
APT_OPTIONS = ["-o", "DPkg::Lock::Timeout=300", "-o", "Dpkg::Options::=--force-confdef",
               "-o", "Dpkg::Options::=--force-confold", "-o", "APT::Get::Assume-Yes=true"]

# restart: spark = takes effect after a Spark restart; lucia = restarts Docker (and Lucia) during install.
SPARK_RESTART = re.compile(r"^(linux-(image|modules|headers|firmware|nvidia|generic|signed|tools)|nvidia-(dkms|kernel|driver|"
                           r"modprobe|fabricmanager|firmware|utils|compute-utils)|libnvidia-|libc6$|libc-bin$|systemd$|"
                           r"systemd-sysv$|libsystemd|dbus|grub|shim|.*-microcode$|.*firmware)")
LUCIA_RESTART = re.compile(r"^(docker|containerd|runc$|nvidia-container|libnvidia-container)")
# Platform = kernel/boot, NVIDIA driver/CUDA/DGX stack, and the container runtime Lucia runs on.
PLATFORM_NAME = re.compile(r"^(linux-|nvidia|libnvidia|cuda|libcu(?!rl)|libnccl|libcudnn|tensorrt|dgx|grub|shim|.*firmware|.*-microcode$)")
NEEDRESTART_CONF = ("# Managed by Lucia package updates: never restart the worker or Lucia's container runtime here.\n"
                    "$nrconf{override_rc}{qr(^lucia-package-updates)} = 0;\n"
                    "$nrconf{override_rc}{qr(^docker)} = 0;\n"
                    "$nrconf{override_rc}{qr(^containerd)} = 0;\n")


def now():
    return datetime.datetime.now(UTC).isoformat()


# ---------------------------------------------------------------- safe file exchange

def open_directory(path, owner):
    """Walk from / without following links; every ancestor must be root- or owner-owned and not shared-writable."""
    if not path.is_absolute() or ".." in path.parts:
        raise ValueError("Exchange directories must be absolute.")
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
    descriptor = os.open("/", flags)
    try:
        for part in path.parts[1:]:
            child = os.open(part, flags, dir_fd=descriptor)
            os.close(descriptor)
            descriptor = child
            info = os.fstat(descriptor)
            if info.st_uid not in (0, owner) or info.st_mode & 0o022:
                raise ValueError("An exchange directory ancestor is not trusted.")
        return descriptor
    except BaseException:
        os.close(descriptor)
        raise


def read_owned(path, owner, maximum=16384):
    parent = open_directory(path.parent, owner)
    try:
        descriptor = os.open(path.name, os.O_RDONLY | os.O_NONBLOCK | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
        with os.fdopen(descriptor, "rb") as stream:
            info = os.fstat(stream.fileno())
            if (not stat.S_ISREG(info.st_mode) or info.st_uid != owner or info.st_nlink != 1
                    or info.st_size > maximum or info.st_mode & 0o077):
                raise ValueError("Requests must be private bounded files owned by the Lucia host.")
            data = stream.read(maximum + 1)
            if len(data) > maximum:
                raise ValueError("Request grew beyond its bound.")
            return data
    finally:
        os.close(parent)


def remove_owned(path, owner):
    parent = open_directory(path.parent, owner)
    try:
        os.unlink(path.name, dir_fd=parent)
    except FileNotFoundError:
        pass
    finally:
        os.close(parent)


def write_owned(path, content, owner):
    """Atomically publish a 0600 file owned by the Lucia host account."""
    data = content.encode() if isinstance(content, str) else content
    parent = open_directory(path.parent, owner)
    temporary = ".worker-" + secrets.token_hex(12) + ".tmp"
    try:
        descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                             0o600, dir_fd=parent)
        with os.fdopen(descriptor, "wb") as output:
            if os.geteuid() == 0:
                os.fchown(output.fileno(), owner, owner)
            output.write(data)
            output.flush()
            os.fsync(output.fileno())
        os.rename(temporary, path.name, src_dir_fd=parent, dst_dir_fd=parent)
    finally:
        try:
            os.unlink(temporary, dir_fd=parent)
        except FileNotFoundError:
            pass
        os.close(parent)


def ensure_directory(path, owner):
    parent = open_directory(path.parent, owner)
    try:
        try:
            os.mkdir(path.name, 0o700, dir_fd=parent)
            if os.geteuid() == 0:
                os.chown(path.name, owner, owner, dir_fd=parent, follow_symlinks=False)
        except FileExistsError:
            pass
    finally:
        os.close(parent)
    os.close(open_directory(path, owner))


def parse_json(data):
    def unique(pairs):
        result = {}
        for name, value in pairs:
            if name in result:
                raise ValueError("Duplicate request field.")
            result[name] = value
        return result
    return json.loads(data, object_pairs_hook=unique)


def validate_request(value, request_id):
    """Return a normalized request or raise ValueError. Unknown fields are refused."""
    if not isinstance(value, dict) or value.get("schemaVersion") != SCHEMA or value.get("id") != request_id:
        raise ValueError("Unsupported request.")
    action = value.get("action")
    if action not in ACTIONS:
        raise ValueError("Unknown action.")
    allowed = {"schemaVersion", "id", "action", "requestedAt"}
    result = {"id": request_id, "action": action}
    if action == "install":
        allowed |= {"packages", "includePlatform", "restart"}
        packages = value.get("packages")
        if packages != "all" and (not isinstance(packages, list) or len(packages) > 1000
                                  or any(not isinstance(item, str) or not PACKAGE.fullmatch(item) for item in packages)
                                  or len(set(packages)) != len(packages)):
            raise ValueError("Packages must be 'all' or a list of unique package names.")
        if not isinstance(value.get("includePlatform"), bool) or value.get("restart") not in RESTART:
            raise ValueError("Install requests need includePlatform and a restart policy.")
        result.update(packages=packages, includePlatform=value["includePlatform"], restart=value["restart"])
    elif action == "changelog":
        allowed |= {"package"}
        if not isinstance(value.get("package"), str) or not PACKAGE.fullmatch(value["package"]):
            raise ValueError("A package name is required.")
        result["package"] = value["package"]
    if set(value) - allowed:
        raise ValueError("Unexpected request fields.")
    return result


# ---------------------------------------------------------------- classification and parsing

def classify(name, origins):
    """Return (platform, restart) where restart is spark|lucia|services."""
    vendor = any("nvidia" in (o.get("site", "") + o.get("origin", "") + o.get("label", "")).lower()
                 or "dgx" in (o.get("label", "") + o.get("origin", "")).lower() for o in origins)
    platform = bool(vendor or PLATFORM_NAME.match(name) or LUCIA_RESTART.match(name))
    restart = "lucia" if LUCIA_RESTART.match(name) else "spark" if SPARK_RESTART.match(name) else "services"
    return platform, restart


def source_label(origins):
    for origin in origins:
        archive = origin.get("archive", "")
        label = origin.get("label") or origin.get("origin") or origin.get("site") or "Unknown source"
        for suffix, word in (("-security", "security"), ("-updates", "updates"), ("-backports", "backports")):
            if archive.endswith(suffix):
                return label + " " + word
    if origins:
        first = origins[0]
        return first.get("label") or first.get("origin") or first.get("site") or "Unknown source"
    return "Unknown source"


SIM_LINE = re.compile(r"^(Inst|Remv|Purg) (\S+)")


def parse_simulation(text):
    installs, removals = [], []
    for line in text.splitlines():
        match = SIM_LINE.match(line)
        if match:
            (installs if match.group(1) == "Inst" else removals).append(match.group(2).split(":")[0])
    return installs, removals


def parse_held(text):
    """Split apt's upgrade simulation into kept-back and phased (Ubuntu's staged rollout) packages."""
    held, section = {"keptBack": set(), "phased": set()}, None
    for line in text.splitlines():
        if line.startswith("The following packages have been kept back"):
            section = "keptBack"
        elif line.startswith("The following upgrades have been deferred due to phasing"):
            section = "phased"
        elif section and line.startswith(" "):
            held[section].update(line.split())
        else:
            section = None
    return {key: sorted(value) for key, value in held.items()}


def parse_needrestart(text):
    services, kernel_pending = [], False
    for line in text.splitlines():
        key, _, value = line.partition(":")
        value = value.strip()
        if key == "NEEDRESTART-SVC" and value:
            services.append(value)
        elif key == "NEEDRESTART-KSTA":
            kernel_pending = value.isdigit() and int(value) > 1
    return {"services": sorted(set(services)), "kernelPending": kernel_pending}


def plain_failure(output, code):
    text = output[-20000:]
    if "Could not get lock" in text or "Unable to acquire the dpkg frontend lock" in text:
        return "Another package tool (such as a manual apt command) was busy for more than five minutes. Try again when it finishes."
    if "dpkg was interrupted" in text:
        return "An earlier package install was interrupted. Use Repair package database, then try again."
    if "Temporary failure resolving" in text or "Could not resolve" in text or "Failed to fetch" in text:
        return "The Spark could not download from one or more package sources. Check its internet connection and try again."
    if "No space left on device" in text:
        return "The Spark ran out of disk space while updating. Free space on the system disk, then use Repair package database."
    if "nmet dependencies" in text:
        return "These updates depend on packages that could not be installed together. Try installing all updates, or wait for the source to publish matching versions."
    if code == -9:
        return "The task took longer than its time limit and was stopped. Use Repair package database before trying again."
    return "apt reported an error (exit code {}). The log shows the last lines it printed.".format(code)


def tail(text, lines=60):
    cleaned = re.sub(r"[\x00-\x08\x0b-\x1f\x7f]", "", text.replace("\r", "\n"))
    return [line[:400] for line in cleaned.splitlines() if line.strip()][-lines:]


def apt_progress(text, total):
    """Summarize apt-get install output as the furthest step reached: downloading, unpacking, configuring."""
    for step, pattern in (("configuring", r"^Setting up ([^\s:]+)"), ("unpacking", r"^Unpacking ([^\s:]+)"),
                          ("downloading", r"^Get:\d+ \S+ \S+ \S+ ([^\s:]+)")):
        names = re.findall(pattern, text, re.M)
        if names:
            return {"step": step, "done": min(len(names), total), "total": total, "package": names[-1]}
    return {"step": "downloading", "done": 0, "total": total, "package": None}


# ---------------------------------------------------------------- system inspection

def run(command, timeout, log=None):
    """Run a fixed command in its own session so a worker restart does not kill apt/dpkg."""
    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=dict(APT_ENV),
                               stdin=subprocess.DEVNULL, text=True, errors="replace", start_new_session=True)
    chunks = []

    def pump():
        for line in process.stdout:
            chunks.append(line)
            if log is not None:
                log.write(line)
                log.flush()
    reader = threading.Thread(target=pump, daemon=True)
    reader.start()
    deadline = time.monotonic() + timeout
    while process.poll() is None:
        if time.monotonic() > deadline:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()
            reader.join(5)
            return -9, "".join(chunks)
        time.sleep(0.5)
    reader.join(10)
    return process.returncode, "".join(chunks)


def release_value(path, key):
    try:
        for line in pathlib.Path(path).read_text().splitlines():
            if line.startswith(key + "="):
                return line.partition("=")[2].strip().strip('"')
    except OSError:
        pass
    return None


def automatic_updates():
    installed = shutil.which("unattended-upgrade") is not None
    code, output = run(["apt-config", "shell", "V", "APT::Periodic::Unattended-Upgrade"], 30)
    return {"installed": installed, "enabled": installed and code == 0 and "V='1'" in output}


def scan():
    import apt  # python3-apt; imported lazily so checks run without it.
    cache = apt.Cache()
    updates = {}
    for package in cache:
        if not package.is_upgradable or package.shortname in updates:
            continue
        candidate = package.candidate
        origins = [{"origin": o.origin or "", "label": o.label or "", "archive": o.archive or "", "site": o.site or ""}
                   for o in candidate.origins]
        platform, restart = classify(package.shortname, origins)
        updates[package.shortname] = {
            "name": package.shortname, "summary": (candidate.summary or "")[:200],
            "currentVersion": package.installed.version if package.installed else None,
            "candidateVersion": candidate.version, "source": source_label(origins),
            "security": any(o["archive"].endswith("-security") for o in origins),
            "platform": platform, "restart": restart, "downloadBytes": int(candidate.size or 0),
            "keptBack": False, "phased": False,
        }
    code, output = run(["apt-get", "-s", "-o", "Debug::NoLocking=1", "upgrade", "--with-new-pkgs"], 120)
    for key, names in (parse_held(output) if code == 0 else {}).items():
        for name in names:
            if name in updates:
                updates[name][key] = True
    code, output = run(["needrestart", "-b"], 120) if shutil.which("needrestart") else (1, "")
    restart = parse_needrestart(output) if code == 0 else {"services": [], "kernelPending": False}
    try:
        reasons = sorted(set(pathlib.Path("/var/run/reboot-required.pkgs").read_text().split()))[:100]
    except OSError:
        reasons = []
    code, audit = run(["dpkg", "--audit"], 60)
    healthy = code == 0 and not audit.strip()
    marks = [p.stat().st_mtime for p in (pathlib.Path("/var/lib/apt/periodic/update-success-stamp"),
                                         pathlib.Path("/var/lib/apt/lists")) if p.exists()]
    return {
        "system": {"os": release_value("/etc/os-release", "PRETTY_NAME") or "Linux",
                   "dgx": release_value("/etc/dgx-release", "DGX_PRETTY_NAME"),
                   "kernel": os.uname().release, "architecture": os.uname().machine},
        "lastUpdateCheckAt": datetime.datetime.fromtimestamp(max(marks), UTC).isoformat() if marks else None,
        "updates": sorted(updates.values(), key=lambda item: (not item["security"], item["name"]))[:2000],
        "restart": {"sparkRequired": pathlib.Path("/var/run/reboot-required").exists() or restart["kernelPending"],
                    "sparkReasons": reasons, "kernelPending": restart["kernelPending"],
                    "services": restart["services"][:200]},
        "automaticUpdates": automatic_updates(),
        "packageDatabase": {"healthy": healthy, "message": None if healthy else "The package database has unfinished installs."},
        "scannedAt": now(),
    }


# ---------------------------------------------------------------- worker

class Worker:
    def __init__(self, data, owner):
        self.owner = owner
        self.exchange = data / "packages"
        self.requests = self.exchange / "requests"
        self.responses = self.exchange / "responses"
        self.state_path = STATE / "state.json"
        self.snapshot = None
        self.lock = threading.RLock()
        self.stopping = False
        self.state = self.load_state()

    def load_state(self):
        try:
            value = json.loads(self.state_path.read_text())
        except (OSError, ValueError):
            value = {}
        value.setdefault("history", [])
        value.setdefault("processed", [])
        current = value.get("current")
        if current and current.get("state") == "running":
            current.update(state="interrupted", finishedAt=now(),
                           message="Lucia's update service restarted during this task. Check the package database before trying again.")
            value["history"].insert(0, current)
        value["current"] = None
        return value

    def save_state(self):
        STATE.mkdir(parents=True, exist_ok=True, mode=0o700)
        temporary = self.state_path.with_suffix(".tmp")
        temporary.write_text(json.dumps(self.state))
        os.replace(temporary, self.state_path)

    def publish(self):
        with self.lock:
            status = {"schemaVersion": SCHEMA, "ready": True, "checkedAt": now(), **(self.snapshot or {}),
                      "scanning": self.snapshot is None, "operation": self.state.get("current"),
                      "history": self.state["history"][:15], "rebootingAt": self.state.get("rebootingAt")}
            write_owned(self.exchange / "status.json", json.dumps(status) + "\n", self.owner)

    def rescan(self):
        try:
            self.snapshot = scan()
        except Exception as error:  # Keep the last snapshot; never publish raw exception text.
            print(json.dumps({"event": "package-scan-failed", "errorType": type(error).__name__}), flush=True)
        self.publish()

    @staticmethod
    def watched():
        values = []
        for path in ("/var/lib/dpkg/status", "/var/lib/apt/lists", "/var/run/reboot-required",
                     "/var/lib/apt/periodic/update-success-stamp"):
            try:
                values.append(os.stat(path).st_mtime_ns)
            except OSError:
                values.append(0)
        return tuple(values)

    def respond(self, request_id, success, message, **extra):
        write_owned(self.responses / (request_id + ".json"), json.dumps({
            "schemaVersion": SCHEMA, "id": request_id, "success": success, "message": message,
            "checkedAt": now(), **extra}) + "\n", self.owner)

    def begin(self, request, packages=None, restart=None):
        operation = {"id": request["id"], "action": request["action"], "state": "running", "startedAt": now(),
                     "finishedAt": None, "message": None, "packages": (packages or [])[:1000], "restart": restart, "log": [],
                     "progress": {"step": "preparing", "done": 0, "total": 0, "package": None}}
        with self.lock:
            self.state["current"] = operation
            self.save_state()
        self.publish()
        return operation

    def finish(self, operation, success, message, output=""):
        operation.update(state="succeeded" if success else "failed", finishedAt=now(), message=message, log=tail(output), progress=None)
        with self.lock:
            self.state["current"] = None
            self.state["history"].insert(0, operation)
            del self.state["history"][30:]
            self.save_state()
        self.respond(operation["id"], success, message)
        self.publish()

    def apt(self, operation, arguments, timeout, step, total=0):
        LOGS.mkdir(parents=True, exist_ok=True, mode=0o700)
        path = LOGS / (operation["id"] + ".log")
        start = path.stat().st_size if path.exists() else 0
        operation["progress"] = {"step": step, "done": 0, "total": total, "package": None}
        self.publish()
        with open(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_APPEND | os.O_NOFOLLOW, 0o600), "a", encoding="utf-8") as log:
            stop = threading.Event()

            def progress():  # Share a live tail and counts so the portal can show what apt is doing.
                while not stop.wait(3):
                    try:
                        with open(path, "rb") as handle:
                            handle.seek(start)
                            text = handle.read().decode(errors="replace")
                        operation["log"] = tail(text[-40000:], 20)
                        if total:
                            operation["progress"] = apt_progress(text, total)
                        self.publish()
                    except OSError:
                        pass
            watcher = threading.Thread(target=progress, daemon=True)
            watcher.start()
            try:
                return run(arguments, timeout, log)
            finally:
                stop.set()
                watcher.join(10)

    def install(self, request):
        current = {item["name"]: item for item in (self.snapshot or scan())["updates"]}
        wanted = request["packages"]
        if wanted == "all":
            wanted = [name for name, item in current.items() if request["includePlatform"] or not item["platform"]]
        operation = self.begin(request, wanted, request["restart"])
        missing = [name for name in wanted if name not in current]
        if missing:
            return self.finish(operation, False, "{} is no longer an available update. Check for updates and review the list again.".format(
                ", ".join(missing[:5])))
        platform = [name for name in wanted if current[name]["platform"]]
        if platform and not request["includePlatform"]:
            return self.finish(operation, False, "Platform updates need their own confirmation: " + ", ".join(platform[:8]) + ".")
        output = ""
        if wanted:
            code, output = run(["apt-get", "-s", "-o", "Debug::NoLocking=1", "install", "--only-upgrade", *wanted], 300)
            if code:
                return self.finish(operation, False, plain_failure(output, code), output)
            installs, removals = parse_simulation(output)
            if removals:
                return self.finish(operation, False, "Installing these updates would remove {}. Lucia doesn't remove packages during updates.".format(
                    ", ".join(removals[:8])), output)
            pulled = [name for name in installs if name not in wanted and
                      (current[name]["platform"] if name in current else classify(name, [])[0])]
            if pulled and not request["includePlatform"]:
                return self.finish(operation, False, "These updates also need platform updates ({}). Include platform updates, or choose fewer updates.".format(
                    ", ".join(pulled[:8])), output)
            code, output = self.apt(operation, ["apt-get", *APT_OPTIONS, "install", "--only-upgrade", *wanted], 90 * 60,
                                    "downloading", len(installs))
            if code:
                self.rescan()
                return self.finish(operation, False, plain_failure(output, code), output)
        count = len(wanted)
        message = "Installed {} update{}.".format(count, "" if count == 1 else "s") if count else "No updates needed installing."
        if request["restart"] in ("services", "spark") and shutil.which("needrestart"):
            code, more = self.apt(operation, ["needrestart", "-r", "a", "-q"], 15 * 60, "restarting")
            output += more
            message += " Services using updated files were restarted." if code == 0 else " Some services could not be restarted."
        self.rescan()
        if request["restart"] == "spark" and (self.snapshot or {}).get("restart", {}).get("sparkRequired"):
            self.finish(operation, True, message + " Restarting the Spark to finish.", output)
            return self.reboot()
        self.finish(operation, True, message, output)

    def reboot(self):
        with self.lock:
            self.state["rebootingAt"] = now()
            self.save_state()
        self.publish()
        time.sleep(3)
        subprocess.run(["systemctl", "reboot"], check=False)

    def handle(self, request):
        action = request["action"]
        if action == "changelog":
            available = {item["name"] for item in (self.snapshot or {}).get("updates", [])}
            if request["package"] not in available:
                return self.respond(request["id"], False, "Changelogs are shown only for available updates.")
            code, output = run(["apt-get", "changelog", request["package"]], 90)
            output = re.sub(r"\A(?:(?:Get|Hit|Fetched)[: ][^\n]*\n)+", "", output)
            if code or not output.strip():
                return self.respond(request["id"], False, "This package's source doesn't publish a changelog Lucia can read.")
            return self.respond(request["id"], True, "Changelog loaded.", changelog=output[:131072])
        if action == "install":
            return self.install(request)
        operation = self.begin(request)
        if action == "check":
            code, output = self.apt(operation, ["apt-get", "-o", "DPkg::Lock::Timeout=300", "update"], 20 * 60, "refreshing")
            self.rescan()
            count = len((self.snapshot or {}).get("updates", []))
            return self.finish(operation, code == 0, "Checked every package source. {} update{} available.".format(
                count, " is" if count == 1 else "s are") if code == 0 else plain_failure(output, code), output)
        if action == "restart-services":
            code, output = self.apt(operation, ["needrestart", "-r", "a", "-q"], 15 * 60, "restarting")
            self.rescan()
            remaining = len((self.snapshot or {}).get("restart", {}).get("services", []))
            message = "Restarted the services using updated files." if not remaining else \
                "Restarted what could be restarted safely. {} will pick up updates when the Spark restarts.".format(
                    "1 service" if remaining == 1 else "{} services".format(remaining))
            return self.finish(operation, code == 0, message if code == 0 else plain_failure(output, code), output)
        if action == "repair":
            code, output = self.apt(operation, ["dpkg", "--configure", "-a"], 60 * 60, "repairing")
            if code == 0:
                code, more = self.apt(operation, ["apt-get", *APT_OPTIONS, "-f", "install"], 60 * 60, "repairing")
                output += more
            self.rescan()
            return self.finish(operation, code == 0, "The package database is consistent again." if code == 0
                               else plain_failure(output, code), output)
        if action == "restart-spark":
            self.finish(operation, True, "Restarting the Spark.")
            return self.reboot()

    def poll_requests(self):
        try:
            names = sorted(os.listdir(self.requests))
        except OSError:
            return
        for name in names[:50]:
            stem, _, suffix = name.partition(".")
            if suffix != "json" or not REQUEST_ID.fullmatch(stem):
                continue
            path = self.requests / name
            try:
                data = read_owned(path, self.owner)
                remove_owned(path, self.owner)
                if stem in self.state["processed"]:
                    continue
                self.state["processed"] = [stem, *self.state["processed"][:499]]
                self.save_state()
                request = validate_request(parse_json(data), stem)
            except (OSError, ValueError) as error:
                print(json.dumps({"event": "package-request-rejected", "errorType": type(error).__name__}), flush=True)
                try:
                    self.respond(stem, False, "Lucia's update service refused this request.")
                except (OSError, ValueError):
                    pass
                continue
            if request["action"] == "changelog":
                threading.Thread(target=self.guarded, args=(request,), daemon=True).start()
            else:
                self.guarded(request)

    def guarded(self, request):
        try:
            self.handle(request)
        except Exception as error:
            print(json.dumps({"event": "package-task-failed", "action": request["action"],
                              "errorType": type(error).__name__}), flush=True)
            current = self.state.get("current")
            if current and current["id"] == request["id"]:
                self.finish(current, False, "Lucia's update service hit an unexpected error. apt's own state was left unchanged.")
            else:
                self.respond(request["id"], False, "Lucia's update service hit an unexpected error.")

    def run(self):
        for path in (self.exchange, self.requests, self.responses):
            ensure_directory(path, self.owner)
        self.state.pop("rebootingAt", None)
        self.save_state()
        signal.signal(signal.SIGTERM, lambda *_: setattr(self, "stopping", True))
        self.publish()

        def heartbeat():  # Long tasks block the request loop; keep the portal's readiness fresh.
            while not self.stopping:
                time.sleep(15)
                try:
                    self.publish()
                except (OSError, ValueError):
                    pass
        threading.Thread(target=heartbeat, daemon=True).start()
        self.rescan()
        fingerprint, last_scan = self.watched(), time.monotonic()
        while not self.stopping:
            self.poll_requests()
            current = self.watched()
            if current != fingerprint or time.monotonic() - last_scan > 600:
                fingerprint, last_scan = current, time.monotonic()
                self.rescan()
            time.sleep(5)


# ---------------------------------------------------------------- install

def install(data, owner):
    if sys.platform != "linux" or os.geteuid() != 0:
        raise SystemExit("Run the installer with sudo on the Spark.")
    try:
        import apt  # noqa: F401
    except ImportError:
        raise SystemExit("python3-apt is required.")
    os.close(open_directory(data, owner))
    INSTALLED.parent.mkdir(parents=True, exist_ok=True, mode=0o755)
    shutil.copyfile(__file__, INSTALLED)
    os.chown(INSTALLED, 0, 0)
    os.chmod(INSTALLED, 0o644)
    pathlib.Path("/etc/needrestart/conf.d").mkdir(parents=True, exist_ok=True)
    pathlib.Path("/etc/needrestart/conf.d/50-lucia.conf").write_text(NEEDRESTART_CONF)
    unit = pathlib.Path("/etc/systemd/system") / UNIT
    unit.write_text(
        "[Unit]\nDescription=Lucia package updates\nAfter=network-online.target\nWants=network-online.target\n"
        "[Service]\nType=simple\nExecStart=/usr/bin/python3 {} run --data {} --owner {}\n"
        "Restart=always\nRestartSec=5\nUMask=0077\n"
        "# apt/dpkg children must outlive a worker restart.\nKillMode=process\n"
        "[Install]\nWantedBy=multi-user.target\n".format(INSTALLED, data, owner))
    subprocess.run(["systemctl", "daemon-reload"], check=True)
    subprocess.run(["systemctl", "enable", UNIT], check=True)
    subprocess.run(["systemctl", "restart", UNIT], check=True)
    print("Lucia package updates installed. Open Spark updates in Lucia.")


def main():
    parser = argparse.ArgumentParser(description="Lucia scoped apt worker")
    parser.add_argument("command", choices=["install", "run"])
    parser.add_argument("--data", type=pathlib.Path)
    parser.add_argument("--owner", type=int)
    arguments = parser.parse_args()
    owner, data = arguments.owner, arguments.data
    if arguments.command == "install":
        if owner is None and os.environ.get("SUDO_UID"):
            owner = int(os.environ["SUDO_UID"])
        if not owner:
            raise SystemExit("Run with sudo from the Spark setup account, or pass --owner.")
        if data is None:
            import pwd
            data = pathlib.Path(pwd.getpwuid(owner).pw_dir) / ".local/share/lucia/host/data"
        return install(data.resolve(), owner)
    if data is None or owner is None or os.geteuid() != 0:
        raise SystemExit("run requires root, --data and --owner.")
    Worker(data, owner).run()


if __name__ == "__main__":
    main()
