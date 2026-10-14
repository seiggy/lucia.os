#!/usr/bin/env python3
"""Fixed-purpose, setup-user Assistant Skills runner; never run inside the web container.

install|run, from ~/.local/share/lucia/bootstrap/app/tools/skills/skill_worker.py.
The web host owns the queue under host/data/assistant/skill-runner/: private 0600
requests/<id>.json, answered by responses/<id>.json, and worker.json, the heartbeat
the web host checks before it queues anything. Requests have EXACT keys:
schemaVersion (1), id (lowercase GUID D, equal to the file name), kind (run|check),
skill (a skill name), hash (run: the skill's content hash; check: null), script (the
script's path inside the skill), language (shell|python|javascript|typescript|csharp,
the one the script's extension means), arguments (run: a JSON object of at most 32 KiB;
check: null), content (check: the file to check, at most 256 KiB; run: null),
timeoutSeconds (1..600), network (none|bridge) and expiresAt (UTC ISO8601, future,
at most 20 minutes ahead).
No path, flag, image or command comes from a request. The worker finds the skill folder
in the server's skill store by name, recomputes the folder's content hash exactly as
SkillStore does and refuses a mismatch, copies the verified bytes into a private work
directory and runs them in a throwaway container on an image it builds itself from
deployment/skill-runner/Dockerfile (base images pinned by digest). A request that was
claimed but not answered before a crash is answered "interrupted", never run again.

Responses carry the request's byte hash so a stale answer is never mistaken for a new
one: schemaVersion, id, requestHash, status (ok|rejected|expired|unavailable|interrupted),
exitCode, timedOut, truncated, output (the last 48 KiB of stdout and stderr together),
diagnostics (C# checks: {line, column, severity, message}), message, durationMs, finishedAt.
"""

import datetime
import hashlib
import json
import os
import pathlib
import re
import shutil
import signal
import stat
import subprocess
import sys
import threading
import time

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "domains"))
from activation_worker import open_directory, parse_json, private_directory, regular, write_file

UTC = datetime.timezone.utc
HOME = pathlib.Path.home() / ".local" / "share" / "lucia"
DATA = HOME / "host" / "data" / "assistant"
SKILLS = DATA / "skills"
QUEUE = DATA / "skill-runner"
PRIVATE = HOME / "skill-runner"
DOCKERFILE = ROOT / "deployment" / "skill-runner" / "Dockerfile"
IMAGE = "lucia-skill-runner"

GUID = re.compile(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}")
SKILL_NAME = re.compile(r"[a-z0-9]+(?:-[a-z0-9]+)*")
SEGMENT = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,63}")
TIMESTAMP = re.compile(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?(?:Z|\+00:00)")
FIELDS = {"schemaVersion", "id", "kind", "skill", "hash", "script", "language", "arguments", "content",
          "timeoutSeconds", "network", "expiresAt"}
MAX_REQUEST = 1024 * 1024
MAX_ARGUMENTS = 32 * 1024
MAX_CONTENT = 256 * 1024
OUTPUT_TAIL = 48 * 1024
MAX_EXPIRY = datetime.timedelta(minutes=20)
KEEP = datetime.timedelta(hours=1)
BUILD_SECONDS = 1800
RETRY_BUILD = 600

# Keep in step with SkillStore.cs and SkillLanguages in SkillModels.cs.
MAX_FILES, MAX_FILE_BYTES, MAX_SKILL_BYTES, MAX_DEPTH = 200, 1024 * 1024, 5 * 1024 * 1024, 5
FOLDERS = ("scripts", "references", "assets")
RESOURCES = {".md", ".json", ".yaml", ".yml", ".txt", ".csv", ".tmpl", ".template", ".conf", ".ini", ".toml"}
SCRIPTS = {".sh": "shell", ".py": "python", ".js": "javascript", ".mjs": "javascript", ".ts": "typescript",
           ".mts": "typescript", ".cs": "csharp"}

# Every container gets these. /tmp is the only writable place, and HOME lives there.
ENVIRONMENT = ("HOME=/tmp", "DOTNET_CLI_HOME=/tmp", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "DOTNET_NOLOGO=1",
               "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1", "MSBUILDDISABLENODEREUSE=1", "UseSharedCompilation=false",
               "PYTHONDONTWRITEBYTECODE=1", "PYTHONPYCACHEPREFIX=/tmp/pycache")


class Refused(Exception):
    """A request the worker answers with a plain message instead of running it."""

    def __init__(self, message, status="rejected"):
        super().__init__(message)
        self.status = status


def require(condition, message="Invalid skill runner request."):
    if not condition:
        raise Refused(message)


def now():
    return datetime.datetime.now(UTC)


def extension(path):
    return os.path.splitext(path)[1].lower()


def instant(text):
    return datetime.datetime.fromisoformat(text.replace("Z", "+00:00"))


# ---- requests ----

def request(value, name):
    require(isinstance(value, dict) and set(value) == FIELDS, "The request has the wrong shape.")
    require(type(value["schemaVersion"]) is int and value["schemaVersion"] == 1)
    require(isinstance(value["id"], str) and GUID.fullmatch(value["id"]) and value["id"] == name)
    require(value["kind"] in ("run", "check"))
    require(isinstance(value["skill"], str) and len(value["skill"]) <= 64 and SKILL_NAME.fullmatch(value["skill"]))
    segments = value["script"].split("/") if isinstance(value["script"], str) else []
    require(2 <= len(segments) <= MAX_DEPTH and segments[0] == "scripts" and all(SEGMENT.fullmatch(s) for s in segments)
            and extension(value["script"]) in SCRIPTS, "That path isn't a script Lucia runs.")
    require(SCRIPTS[extension(value["script"])] == value["language"], "The language doesn't match the script.")
    require(type(value["timeoutSeconds"]) is int and 1 <= value["timeoutSeconds"] <= 600)
    require(value["network"] in ("none", "bridge"))
    require(isinstance(value["expiresAt"], str) and TIMESTAMP.fullmatch(value["expiresAt"]) is not None)
    if value["kind"] == "run":
        require(isinstance(value["hash"], str) and re.fullmatch(r"[0-9a-f]{64}", value["hash"]))
        require(isinstance(value["arguments"], dict) and value["content"] is None)
        require(len(json.dumps(value["arguments"], separators=(",", ":"))) <= MAX_ARGUMENTS, "The arguments are too large.")
    else:
        require(value["hash"] is None and value["arguments"] is None)
        require(isinstance(value["content"], str) and len(value["content"].encode()) <= MAX_CONTENT, "The file is too large to check.")
    return value


# ---- skill folders ----

def accepted(relative):
    """Whether SkillStore manages the file at this path (SkillStore.Accepted)."""
    segments = relative.split("/")
    if relative == "SKILL.md":
        return True
    if not 2 <= len(segments) <= MAX_DEPTH or segments[0] not in FOLDERS or not all(SEGMENT.fullmatch(s) for s in segments):
        return False
    suffix = extension(relative)
    if segments[0] == "scripts":
        return suffix in SCRIPTS or suffix in RESOURCES
    return suffix in RESOURCES


def read_member(directory_fd, name):
    """A skill file's bytes through a descriptor, refusing links and anything not the setup user's own regular file."""
    try:
        fd = os.open(name, os.O_RDONLY | os.O_NONBLOCK | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=directory_fd)
    except OSError:
        raise Refused("A skill file is a link or can't be read.")
    with os.fdopen(fd, "rb") as stream:
        info = os.fstat(stream.fileno())
        require(stat.S_ISREG(info.st_mode) and info.st_uid == os.getuid() and info.st_nlink == 1 and not info.st_mode & 0o022,
                "A skill file isn't a private regular file.")
        require(info.st_size <= MAX_FILE_BYTES, "A skill file is larger than 1 MiB.")
        data = stream.read(MAX_FILE_BYTES + 1)
        require(len(data) == info.st_size, "A skill file changed while it was read.")
        return data


def open_child(parent_fd, name):
    try:
        fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent_fd)
    except OSError:
        raise Refused("A skill folder is a link or can't be read.")
    info = os.fstat(fd)
    if info.st_uid != os.getuid() or info.st_mode & 0o022:
        os.close(fd)
        raise Refused("A skill folder isn't private to the setup user.")
    return fd


def collect(directory_fd, relative, depth, into):
    for entry in sorted(os.listdir(directory_fd)):
        path = relative + "/" + entry
        info = os.stat(entry, dir_fd=directory_fd, follow_symlinks=False)
        if stat.S_ISLNK(info.st_mode):
            raise Refused(path + " is a link, which Lucia doesn't follow.")
        if stat.S_ISDIR(info.st_mode):
            if depth < MAX_DEPTH:
                child = open_child(directory_fd, entry)
                try:
                    collect(child, path, depth + 1, into)
                finally:
                    os.close(child)
        elif stat.S_ISREG(info.st_mode) and accepted(path):
            into.append((path, read_member(directory_fd, entry)))


def content_hash(files):
    """SkillStore's content hash: SHA-256 over each file, in ordinal path order, of its path (UTF-8),
    a zero byte, its size as a little-endian int64 and its bytes."""
    digest = hashlib.sha256()
    for path, data in sorted(files, key=lambda item: item[0].encode("utf-16-be")):
        digest.update(path.encode())
        digest.update(b"\0")
        digest.update(len(data).to_bytes(8, "little"))
        digest.update(data)
    return digest.hexdigest()


def load_skill(name, skills=None):
    """The skill's files and content hash, read once from the server's skill store."""
    require(SKILL_NAME.fullmatch(name) is not None)
    parent = open_directory(skills or SKILLS)
    try:
        folder = open_child(parent, name)
    except Refused:
        raise Refused("That skill isn't in the skill store.")
    finally:
        os.close(parent)
    try:
        files = [("SKILL.md", read_member(folder, "SKILL.md"))]
        for top in FOLDERS:
            try:
                info = os.stat(top, dir_fd=folder, follow_symlinks=False)
            except FileNotFoundError:
                continue
            require(stat.S_ISDIR(info.st_mode), top + " is a link, which Lucia doesn't follow.")
            child = open_child(folder, top)
            try:
                collect(child, top, 1, files)
            finally:
                os.close(child)
    finally:
        os.close(folder)
    require(len(files) - 1 <= MAX_FILES, "The skill has more than 200 files.")
    require(sum(len(data) for _, data in files) <= MAX_SKILL_BYTES, "The skill is larger than 5 MiB.")
    return dict(files), content_hash(files)


def stage(work, files):
    """Writes the verified bytes world-readable, because the container's user is not the setup user."""
    base = work / "skill"
    for path, data in files.items():
        target = base
        for part in pathlib.PurePosixPath(path).parts[:-1]:
            target = target / part
            target.mkdir(exist_ok=True)
            os.chmod(target, 0o755)
        base.mkdir(exist_ok=True)
        os.chmod(base, 0o755)
        file = base / path
        file.write_bytes(data)
        os.chmod(file, 0o644)
    return base


# ---- docker ----

def tag():
    return IMAGE + ":" + hashlib.sha256(DOCKERFILE.read_bytes()).hexdigest()[:16]


DOTNET_PROPERTIES = ("-p:PublishAot=false", "-p:NuGetAudit=false")


def interpreter(path):
    suffix = extension(path)
    if suffix == ".sh":
        return ["bash", path]
    if suffix == ".py":
        return ["python3", path]
    if suffix in (".js", ".mjs"):
        return ["node", path]
    if suffix in (".ts", ".mts"):
        return ["node", "--experimental-strip-types", path]
    # .NET 10 file-based apps default to PublishAot, whose restore fetches the AOT compiler; without network that fails.
    return ["dotnet", "run", path, *DOTNET_PROPERTIES]


def checker(path):
    suffix = extension(path)
    if suffix == ".sh":
        return ["bash", "-n", path]
    if suffix == ".py":
        return ["python3", "-m", "py_compile", path]
    if suffix in (".js", ".mjs"):
        return ["node", "--check", path]
    if suffix in (".ts", ".mts"):
        return ["node", "--check", "--experimental-strip-types", path]
    return ["dotnet", "build", path, "-nologo", "-v:q", "-clp:NoSummary", *DOTNET_PROPERTIES]


def docker_command(image, name, work, network, arguments, argv):
    """The one docker run every request becomes. Nothing in it comes from a request except the network
    choice, the skill's arguments (one environment value) and the interpreter's script argument."""
    command = ["docker", "run", "--rm", "--name", name, "-i", "--read-only", "--tmpfs", "/tmp:rw,exec,nosuid,nodev,size=512m",
               "--network", network, "--cpus", "2", "--memory", "1g", "--pids-limit", "256", "--cap-drop", "ALL",
               "--security-opt", "no-new-privileges", "--user", "65534:65534", "-v", f"{work}:/skill:ro", "-w", "/skill"]
    for item in ENVIRONMENT:
        command += ["-e", item]
    if arguments is not None:
        command += ["-e", "LUCIA_SKILL_ARGS=" + arguments]
    return command + [image, *argv]


class Tail:
    def __init__(self, limit=OUTPUT_TAIL):
        self.limit, self.data, self.truncated = limit, bytearray(), False

    def add(self, chunk):
        self.data += chunk
        if len(self.data) > self.limit:
            del self.data[:len(self.data) - self.limit]
            self.truncated = True

    def text(self):
        return self.data.decode("utf-8", errors="replace").replace("\0", "")


def execute(command, name, stdin, timeout):
    """Runs docker, keeping a bounded tail of stdout and stderr, and kills the container at the deadline."""
    process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               start_new_session=True)
    tail = Tail()

    def drain():
        while chunk := process.stdout.read(8192):
            tail.add(chunk)

    def feed():
        try:
            process.stdin.write(stdin)
            process.stdin.close()
        except OSError:
            pass  # The script didn't read its input.

    reader = threading.Thread(target=drain, daemon=True)
    reader.start()
    threading.Thread(target=feed, daemon=True).start()
    timed_out = False
    try:
        code = process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        timed_out = True
        subprocess.run(["docker", "kill", name], capture_output=True, timeout=30, check=False)
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except OSError:
            process.kill()
        process.wait()
        code = None
    reader.join(10)
    if timed_out:
        subprocess.run(["docker", "rm", "-f", name], capture_output=True, timeout=30, check=False)
    return code, timed_out, tail


DIAGNOSTIC = re.compile(r"^(?:.*[/\\])?check\.cs\((\d+),(\d+)\): (error|warning) (\w+): (.*?)(?: \[[^\]]*\])?\s*$", re.M)


def diagnostics(output):
    seen, found = set(), []
    for line, column, severity, code, message in DIAGNOSTIC.findall(output):
        item = (int(line), int(column), severity, f"{code}: {message.strip()}"[:300])
        if item not in seen:
            seen.add(item)
            found.append({"line": item[0], "column": item[1], "severity": item[2], "message": item[3]})
    return found[:50]


def build_image():
    """Builds the runner image from the pinned Dockerfile unless it exists; returns (image, problem)."""
    image = tag()
    if subprocess.run(["docker", "image", "inspect", image], capture_output=True, timeout=60, check=False).returncode == 0:
        return image, None
    # The Dockerfile has no COPY from the context, so it goes in on stdin and nothing else is sent.
    result = subprocess.run(["docker", "build", "-t", image, "-"], input=DOCKERFILE.read_bytes(), capture_output=True,
                            timeout=BUILD_SECONDS, check=False)
    if result.returncode:
        last = (result.stderr or result.stdout).decode("utf-8", errors="replace").strip().splitlines()
        return None, (last[-1] if last else "docker build failed")[:300]
    return image, None


def remove(path):
    parent = open_directory(path.parent)
    try:
        os.unlink(path.name, dir_fd=parent)
    except FileNotFoundError:
        pass
    finally:
        os.close(parent)


def candidates(directory):
    return [path for path in sorted(directory.glob("*.json")) if GUID.fullmatch(path.stem)][:16]


class Worker:
    def __init__(self):
        self.lock = threading.Lock()
        self.image, self.state, self.message, self.busy = None, "missing", None, False
        self.retry_at = 0.0
        self.stopping = threading.Event()

    def beat(self, ready=True):
        with self.lock:
            value = {"schemaVersion": 1, "ready": ready, "image": self.state, "message": self.message, "busy": self.busy,
                     "checkedAt": now().isoformat()}
        write_file(QUEUE / "worker.json", json.dumps(value) + "\n")

    def heartbeat(self):
        # A long image build or script must not make the web host think the service is gone.
        while not self.stopping.wait(5):
            try:
                self.beat()
            except (OSError, ValueError):
                print('{"event":"skill-runner-heartbeat-failed"}', flush=True)

    def ensure_image(self):
        if self.image or time.monotonic() < self.retry_at:
            return
        with self.lock:
            self.state, self.message = "building", None
        try:
            image, problem = build_image()
        except (OSError, subprocess.SubprocessError) as error:
            image, problem = None, type(error).__name__
        with self.lock:
            self.image = image
            self.state = "ready" if image else "failed"
            self.message = None if image else problem
        if not image:
            self.retry_at = time.monotonic() + RETRY_BUILD

    def respond(self, name, digest, status, message=None, **fields):
        value = {"schemaVersion": 1, "id": name, "requestHash": digest, "status": status, "exitCode": None, "timedOut": False,
                 "truncated": False, "output": "", "diagnostics": [], "message": message, "durationMs": 0,
                 "finishedAt": now().isoformat()}
        value.update(fields)
        write_file(QUEUE / "responses" / (name + ".json"), json.dumps(value) + "\n")

    def run_request(self, value):
        started = time.monotonic()
        check = value["kind"] == "check"
        if check:
            file = "check" + extension(value["script"])
            files, argv, arguments = {file: value["content"].encode()}, checker(file), None
        else:
            files, digest = load_skill(value["skill"])
            require(digest == value["hash"], "The skill changed after the request was made. Try again.")
            require(value["script"] in files, "That script isn't part of the skill.")
            argv = interpreter(value["script"])
            arguments = json.dumps(value["arguments"], separators=(",", ":"))
        if self.image is None:
            raise Refused("The skill runner's image isn't ready: " + (self.message or "it is still being built."), "unavailable")
        work = PRIVATE / "work" / value["id"]
        try:
            work.mkdir(mode=0o700)
            base = stage(work, files)
            name = "lucia-skill-" + value["id"]
            command = docker_command(self.image, name, base, value["network"], arguments, argv)
            with self.lock:
                self.busy = True
            try:
                code, timed_out, tail = execute(command, name, (arguments or "").encode(), value["timeoutSeconds"])
            finally:
                with self.lock:
                    self.busy = False
        finally:
            shutil.rmtree(work, ignore_errors=True)
        output = tail.text()
        return {"exitCode": code, "timedOut": timed_out, "truncated": tail.truncated, "output": output,
                "diagnostics": diagnostics(output) if check and extension(value["script"]) == ".cs" else [],
                "durationMs": int((time.monotonic() - started) * 1000)}

    def handle(self, path):
        data = regular(path, MAX_REQUEST)
        digest = hashlib.sha256(data).hexdigest()
        name = path.stem
        try:
            if parse_json(regular(QUEUE / "responses" / path.name, MAX_REQUEST)).get("requestHash") == digest:
                remove(path)
                return
        except (FileNotFoundError, ValueError):
            pass
        # Claim before running, so a crash never runs a script twice.
        claimed = PRIVATE / "claimed" / path.name
        write_file(claimed, data)
        remove(path)
        try:
            try:
                value = request(parse_json(data), name)
                at = instant(value["expiresAt"])
                require(at <= now() + MAX_EXPIRY, "The request lasts too long.")
                if at <= now():
                    raise Refused("The request expired before it ran.", "expired")
                self.respond(name, digest, "ok", **self.run_request(value))
            except Refused as refusal:
                self.respond(name, digest, refusal.status, str(refusal))
            except ValueError:
                self.respond(name, digest, "rejected", "The request is invalid.")
            except (OSError, subprocess.SubprocessError) as error:
                print(json.dumps({"event": "skill-runner-failed", "errorType": type(error).__name__}), flush=True)
                self.respond(name, digest, "unavailable", "Docker could not run the skill.")
        finally:
            remove(claimed)

    def recover(self):
        for path in candidates(PRIVATE / "claimed"):
            try:
                digest = hashlib.sha256(regular(path, MAX_REQUEST)).hexdigest()
                self.respond(path.stem, digest, "interrupted", "The skill runner restarted while this was running; it was not run again.")
                remove(path)
            except (OSError, ValueError):
                print('{"event":"skill-runner-unsafe-claim"}', flush=True)

    def sweep(self):
        cutoff = time.time() - KEEP.total_seconds()
        for directory in (QUEUE / "requests", QUEUE / "responses", PRIVATE / "work"):
            for path in directory.iterdir():
                try:
                    if path.lstat().st_mtime < cutoff:
                        shutil.rmtree(path, ignore_errors=True) if path.is_dir() and not path.is_symlink() else remove(path)
                except OSError:
                    pass


def platform_check():
    if sys.platform != "linux" or os.getuid() == 0:
        raise RuntimeError("Run the skill runner as the non-root Linux setup user.")
    stable = HOME / "bootstrap" / "app" / "tools" / "skills" / "skill_worker.py"
    require(pathlib.Path(__file__).resolve() == stable)
    os.close(open_directory(stable.parent))
    require(shutil.which("docker"))
    require(DOCKERFILE.is_file())
    return stable


def run():
    platform_check()
    os.umask(0o077)
    for path in (QUEUE, QUEUE / "requests", QUEUE / "responses", PRIVATE, PRIVATE / "claimed", PRIVATE / "work"):
        private_directory(path)
    worker = Worker()
    signal.signal(signal.SIGTERM, lambda *_: worker.stopping.set())
    signal.signal(signal.SIGINT, lambda *_: worker.stopping.set())
    threading.Thread(target=worker.heartbeat, daemon=True).start()
    try:
        worker.recover()
        while not worker.stopping.is_set():
            worker.ensure_image()
            for path in candidates(QUEUE / "requests"):
                if worker.stopping.is_set():
                    break
                try:
                    worker.handle(path)
                except (OSError, ValueError, Refused):
                    # Unreadable, linked or oversized input has no safe snapshot to answer.
                    print('{"event":"skill-runner-unsafe-queue-file"}', flush=True)
                    try:
                        remove(path)
                    except OSError:
                        pass
            worker.sweep()
            worker.stopping.wait(2)
    finally:
        worker.stopping.set()
        try:
            worker.beat(ready=False)
        except (OSError, ValueError):
            pass


def install():
    stable = platform_check()
    os.umask(0o077)
    linger = subprocess.run(["loginctl", "show-user", str(os.getuid()), "-p", "Linger", "--value"],
                            check=True, capture_output=True, text=True, timeout=30).stdout.strip()
    if linger != "yes":
        raise RuntimeError("Approve persistent user services before installing the skill runner.")
    unit = pathlib.Path.home() / ".config" / "systemd" / "user" / "lucia-skill-runner.service"
    unit.parent.mkdir(parents=True, exist_ok=True)
    escaped = str(stable).replace("\\", "\\\\").replace('"', '\\"').replace("%", "%%")
    write_file(unit, "[Unit]\nDescription=Lucia Assistant Skills runner\n"
               "[Service]\nType=simple\nExecStart=/usr/bin/python3 \"" + escaped + "\" run\n"
               "Restart=on-failure\nRestartSec=5\nUMask=0077\nNoNewPrivileges=yes\n"
               "[Install]\nWantedBy=default.target\n")
    for args in (["daemon-reload"], ["enable", unit.name], ["restart", unit.name]):
        subprocess.run(["systemctl", "--user", *args], check=True, capture_output=True, timeout=30)


if __name__ == "__main__":
    try:
        if sys.argv[1:] == ["install"]:
            install()
        elif sys.argv[1:] == ["run"]:
            run()
        else:
            raise ValueError("Usage: skill_worker.py install|run")
    except Exception:
        raise SystemExit("The skill runner stopped. Check Docker, native paths and approved user linger.")
