#!/usr/bin/env python3
"""Offline checks for the Spark skill runner: no Docker, no lab, no network.

Run python tools/skills/check_skill_worker.py on Linux from a private, trusted checkout (not a
group- or world-writable one, such as /mnt on WSL). The worker's native paths are redirected to a
random directory created under the checkout, which is the only thing removed afterwards. docker is
never started: execute() is mocked, except for the checks of its timeout, output cap and stdin that
run harmless local commands in its place.
"""

import hashlib
import importlib.util
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import threading
from unittest.mock import patch

if sys.platform != "linux":
    raise SystemExit("These checks need Linux (descriptor-relative I/O); run them in WSL or on the Spark.")

spec = importlib.util.spec_from_file_location("skill_worker", pathlib.Path(__file__).with_name("skill_worker.py"))
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)

ID = "11111111-1111-4111-8111-111111111111"
# The cross-check vector: the same files hashed by SkillStore.cs (AssistantChecks asserts this literal too).
VECTOR = {
    "SKILL.md": "---\nname: demo\ndescription: A demo.\n---\n\n# Demo\n",
    "scripts/run.py": "print('hi')\n",
    "scripts/deep/er/tool.sh": "echo hi\n",
    "references/notes.md": "Notes \u00e9\n",
    "assets/data.json": "{}",
}
VECTOR_HASH = "d4724d5896c46c242c69ddabe78ffcc81821a4e065c33f4a26e9cf67aa9eeca7"


def rejected(action, kind=w.Refused):
    try:
        action()
    except kind:
        return
    raise AssertionError("An unsafe skill runner input was accepted.")


def run_value(**changes):
    value = {"schemaVersion": 1, "id": ID, "kind": "run", "skill": "demo", "hash": "a" * 64, "script": "scripts/run.py",
             "language": "python", "arguments": {"x": 1}, "content": None, "timeoutSeconds": 60, "network": "none",
             "expiresAt": (w.now() + w.datetime.timedelta(minutes=5)).strftime("%Y-%m-%dT%H:%M:%SZ")}
    value.update(changes)
    return value


def check_value(**changes):
    return run_value(**{"kind": "check", "hash": None, "arguments": None, "content": "print(1)\n", **changes})


def write_skill(root, name, files):
    for relative, content in files.items():
        path = root / name / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content.encode() if isinstance(content, str) else content)
        os.chmod(path, 0o600)
    for directory in [root / name, *(p for p in (root / name).rglob("*") if p.is_dir())]:
        os.chmod(directory, 0o700)


def main():
    base = pathlib.Path(tempfile.mkdtemp(prefix=".check-", dir=pathlib.Path(__file__).resolve().parent))
    os.chmod(base, 0o700)
    try:
        run_checks(base)
    finally:
        shutil.rmtree(base, ignore_errors=True)


def run_checks(base):
    os.umask(0o077)
    skills = base / "assistant" / "skills"
    w.DATA, w.SKILLS, w.QUEUE, w.PRIVATE = base / "assistant", skills, base / "assistant" / "skill-runner", base / "private"
    skills.mkdir(parents=True)
    os.chmod(skills, 0o700)
    for path in (w.QUEUE, w.QUEUE / "requests", w.QUEUE / "responses", w.PRIVATE, w.PRIVATE / "claimed", w.PRIVATE / "work"):
        w.private_directory(path)
    write_skill(skills, "demo", VECTOR)

    # --- content hash: SkillStore's algorithm, with a vector shared with the C# checks ---
    files, digest = w.load_skill("demo")
    assert digest == VECTOR_HASH, digest
    assert set(files) == set(VECTOR)
    manual = hashlib.sha256()
    for path in sorted(VECTOR):
        data = VECTOR[path].encode()
        manual.update(path.encode() + b"\0" + len(data).to_bytes(8, "little") + data)
    assert manual.hexdigest() == digest, "The hash is the path, a zero, a little-endian int64 size and the bytes, in path order."
    # Files SkillStore doesn't manage never reach the hash or the container.
    write_skill(skills, "demo", {"scripts/run.bin": "x", "references/evil.sh": "x", "notes.txt": "x", "other/a.md": "x",
                                 "scripts/Bad Name.py": "x", "assets/.hidden.md": "x"})
    assert w.load_skill("demo")[1] == VECTOR_HASH
    write_skill(skills, "demo", {"references/extra.md": "more"})
    assert w.load_skill("demo")[1] != VECTOR_HASH
    (skills / "demo" / "references" / "extra.md").unlink()
    assert w.load_skill("demo")[1] == VECTOR_HASH

    # --- skill folder safety ---
    rejected(lambda: w.load_skill("missing"))
    rejected(lambda: w.load_skill("../demo"))
    rejected(lambda: w.load_skill("Demo"))
    (skills / "demo" / "scripts" / "link.py").symlink_to(skills / "demo" / "scripts" / "run.py")
    rejected(lambda: w.load_skill("demo"))
    (skills / "demo" / "scripts" / "link.py").unlink()
    (skills / "evil").symlink_to(skills / "demo")
    rejected(lambda: w.load_skill("evil"))
    (skills / "evil").unlink()
    os.chmod(skills / "demo" / "scripts" / "run.py", 0o666)
    rejected(lambda: w.load_skill("demo"))
    os.chmod(skills / "demo" / "scripts" / "run.py", 0o600)
    write_skill(skills, "big", {"SKILL.md": "x", "scripts/big.py": b"x" * (w.MAX_FILE_BYTES + 1)})
    rejected(lambda: w.load_skill("big"))
    write_skill(skills, "many", {"SKILL.md": "x", **{f"references/f{n}.md": "x" for n in range(201)}})
    rejected(lambda: w.load_skill("many"))
    write_skill(skills, "deep", {"SKILL.md": "x", "scripts/a/b/c/ok.py": "x", "scripts/a/b/c/d/too-deep.py": "x"})
    assert set(w.load_skill("deep")[0]) == {"SKILL.md", "scripts/a/b/c/ok.py"}
    os.chmod(skills / "demo", 0o770)
    rejected(lambda: w.load_skill("demo"))
    os.chmod(skills / "demo", 0o700)

    # --- request validation ---
    assert w.request(run_value(), ID)["kind"] == "run"
    assert w.request(check_value(), ID)["kind"] == "check"
    assert w.request(run_value(script="scripts/a/b.mts", language="typescript"), ID)
    for bad in (
        {"extra": 1}, {"schemaVersion": 2}, {"id": "22222222-2222-4222-8222-222222222222"}, {"id": "NOT-A-GUID"},
        {"kind": "exec"}, {"skill": "../x"}, {"skill": "Demo"}, {"skill": ""}, {"hash": "A" * 64}, {"hash": "abc"}, {"hash": None},
        {"script": "/etc/passwd"}, {"script": "scripts/../../x.sh", "language": "shell"}, {"script": "scripts/x.exe", "language": "shell"},
        {"script": "references/x.py"}, {"script": "scripts/.x.py"}, {"script": "SKILL.md", "language": "text"}, {"script": "scripts/a b.py"},
        {"script": 5}, {"language": "shell"}, {"language": "docker"}, {"timeoutSeconds": 0}, {"timeoutSeconds": 601},
        {"timeoutSeconds": True}, {"timeoutSeconds": "60"}, {"network": "host"}, {"network": "container:x"}, {"network": None},
        {"arguments": []}, {"arguments": "x"}, {"arguments": None}, {"arguments": {"x": "a" * (w.MAX_ARGUMENTS + 1)}},
        {"content": "print(1)"}, {"expiresAt": "tomorrow"}, {"expiresAt": 5}, {"schemaVersion": True},
    ):
        rejected(lambda: w.request(run_value(**bad), ID))
    for key in w.FIELDS:
        value = run_value()
        del value[key]
        rejected(lambda: w.request(value, ID))
    for bad in ({"content": None}, {"content": 5}, {"hash": "a" * 64}, {"arguments": {}}, {"content": "x" * (w.MAX_CONTENT + 1)}):
        rejected(lambda: w.request({**check_value(), **bad}, ID))
    rejected(lambda: w.request([], ID))
    # Prompt-injection style fields from a "model" are not fields at all.
    for key in ("image", "command", "flags", "path", "volume", "docker", "entrypoint"):
        rejected(lambda: w.request({**run_value(), key: "x"}, ID))

    # --- the docker command, exactly ---
    command = w.docker_command("lucia-skill-runner:abc", "lucia-skill-" + ID, pathlib.Path("/work/skill"), "none", '{"x":1}',
                               w.interpreter("scripts/run.py"))
    head = [
        "docker", "run", "--rm", "--name", "lucia-skill-" + ID, "-i", "--read-only", "--tmpfs", "/tmp:rw,exec,nosuid,nodev,size=512m",
        "--network", "none", "--cpus", "2", "--memory", "1g", "--pids-limit", "256", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges", "--user", "65534:65534", "-v", "/work/skill:/skill:ro", "-w", "/skill",
        "-e", "HOME=/tmp", "-e", "DOTNET_CLI_HOME=/tmp", "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "-e", "DOTNET_NOLOGO=1"]
    assert command[:len(head)] == head, command
    assert command[-3:] == ["lucia-skill-runner:abc", "python3", "scripts/run.py"]
    assert "LUCIA_SKILL_ARGS={\"x\":1}" in command and command.count("-e") == len(w.ENVIRONMENT) + 1
    assert "--privileged" not in command and "docker.sock" not in " ".join(command) and "--pull" not in command
    bridged = w.docker_command("i", "n", pathlib.Path("/w"), "bridge", None, ["bash", "-n", "x.sh"])
    assert bridged[bridged.index("--network") + 1] == "bridge" and not any(item.startswith("LUCIA_SKILL_ARGS") for item in bridged)
    assert [w.interpreter(p) for p in ("a.sh", "a.py", "a.js", "a.mjs", "a.ts", "a.mts", "a.cs")] == [
        ["bash", "a.sh"], ["python3", "a.py"], ["node", "a.js"], ["node", "a.mjs"], ["node", "--experimental-strip-types", "a.ts"],
        ["node", "--experimental-strip-types", "a.mts"], ["dotnet", "run", "a.cs"]]
    assert [w.checker(p) for p in ("a.sh", "a.py", "a.js", "a.ts")] == [
        ["bash", "-n", "a.sh"], ["python3", "-m", "py_compile", "a.py"], ["node", "--check", "a.js"],
        ["node", "--check", "--experimental-strip-types", "a.ts"]]
    assert w.checker("check.cs")[:3] == ["dotnet", "build", "check.cs"]

    # --- staging ---
    work = w.PRIVATE / "work" / "stage"
    work.mkdir(mode=0o700)
    staged = w.stage(work, files)
    assert (staged / "scripts" / "deep" / "er" / "tool.sh").read_bytes() == b"echo hi\n"
    assert oct((staged / "scripts" / "run.py").stat().st_mode & 0o777) == "0o644"
    assert oct((staged / "scripts" / "deep").stat().st_mode & 0o777) == "0o755" and oct(staged.stat().st_mode & 0o777) == "0o755"
    shutil.rmtree(work)

    # --- handling requests (docker mocked) ---
    worker = w.Worker()
    worker.image = "lucia-skill-runner:test"
    calls = []

    def fake(command, name, stdin, timeout, output="out\n", code=0, timed_out=False):
        calls.append((command, name, stdin, timeout))
        tail = w.Tail()
        tail.add(output.encode())
        return code, timed_out, tail

    def submit(value, name=ID, raw=None):
        path = w.QUEUE / "requests" / (name + ".json")
        w.write_file(path, raw if raw is not None else json.dumps(value))
        worker.handle(path)
        assert not path.exists(), "A handled request must leave the queue."
        assert not list((w.PRIVATE / "claimed").iterdir()), "A claim is released once answered."
        response = json.loads(w.regular(w.QUEUE / "responses" / (name + ".json"), w.MAX_REQUEST))
        return response

    with patch.object(w, "execute", fake):
        got = submit(run_value(hash=VECTOR_HASH))
        assert got["status"] == "ok" and got["exitCode"] == 0 and got["output"] == "out\n" and got["timedOut"] is False
        assert got["schemaVersion"] == 1 and got["id"] == ID and len(got["requestHash"]) == 64
        command, name, stdin, timeout = calls[-1]
        assert name == "lucia-skill-" + ID and stdin == b'{"x":1}' and timeout == 60
        assert command[-2:] == ["python3", "scripts/run.py"] and "LUCIA_SKILL_ARGS={\"x\":1}" in command
        assert command[command.index("--network") + 1] == "none"
        assert not (w.PRIVATE / "work" / ID).exists(), "The work directory is removed."
        mount = command[command.index("-v") + 1]
        assert mount.endswith(ID + "/skill:/skill:ro"), mount

        calls.clear()
        got = submit(run_value(hash="b" * 64))
        assert got["status"] == "rejected" and "changed" in got["message"] and not calls, "A hash mismatch must never run."
        got = submit(run_value(hash=VECTOR_HASH, script="scripts/missing.py"))
        assert got["status"] == "rejected" and not calls
        got = submit(run_value(hash=VECTOR_HASH, skill="nope"))
        assert got["status"] == "rejected" and "skill store" in got["message"] and not calls
        got = submit(run_value(hash=VECTOR_HASH, network="host"))
        assert got["status"] == "rejected" and not calls
        got = submit({"not": "valid"})
        assert got["status"] == "rejected" and not calls
        got = submit(None, raw="{ not json")
        assert got["status"] == "rejected" and not calls
        got = submit(run_value(hash=VECTOR_HASH, expiresAt="2020-01-01T00:00:00Z"))
        assert got["status"] == "expired" and not calls
        far = (w.now() + w.datetime.timedelta(hours=2)).strftime("%Y-%m-%dT%H:%M:%SZ")
        got = submit(run_value(hash=VECTOR_HASH, expiresAt=far))
        assert got["status"] == "rejected" and not calls

        got = submit(run_value(hash=VECTOR_HASH, network="bridge", timeoutSeconds=600, arguments={}))
        assert got["status"] == "ok"
        assert calls[-1][0][calls[-1][0].index("--network") + 1] == "bridge" and calls[-1][3] == 600

        # A check stages the editor's content as check.<ext>; the skill is not read.
        calls.clear()
        got = submit(check_value(skill="never-created", script="scripts/new.sh", language="shell", content="echo ok\n"))
        assert got["status"] == "ok"
        command, _, stdin, _ = calls[-1]
        assert command[-3:] == ["bash", "-n", "check.sh"] and stdin == b"" and not any(i.startswith("LUCIA_SKILL_ARGS") for i in command)
        calls.clear()
        got = submit(check_value(script="scripts/App.cs", language="csharp", content="int x = ;"))
        assert calls[-1][0][-6:-3] == ["dotnet", "build", "check.cs"]

        # A non-zero exit and a timeout are results, not errors.
        with patch.object(w, "execute", lambda *a: fake(*a, output="boom", code=3)):
            got = submit(run_value(hash=VECTOR_HASH, timeoutSeconds=61))
            assert got["status"] == "ok" and got["exitCode"] == 3 and got["output"] == "boom"
        with patch.object(w, "execute", lambda *a: fake(*a, output="", code=None, timed_out=True)):
            got = submit(run_value(hash=VECTOR_HASH, timeoutSeconds=62))
            assert got["timedOut"] is True and got["exitCode"] is None

        # A C# build's complaints become markers; the same line printed twice is one marker.
        build = ("/skill/check.cs(1,9): error CS1525: Invalid expression term ';' [/tmp/x/check.csproj]\n"
                 "/skill/check.cs(1,9): error CS1525: Invalid expression term ';' [/tmp/x/check.csproj]\n"
                 "check.cs(3,1): warning CS0168: The variable 'y' is declared but never used\n")
        with patch.object(w, "execute", lambda *a: fake(*a, output=build, code=1)):
            got = submit(check_value(script="scripts/App.cs", language="csharp", content="int x = ;"))
            assert got["diagnostics"] == [{"line": 1, "column": 9, "severity": "error", "message": "CS1525: Invalid expression term ';'"},
                                          {"line": 3, "column": 1, "severity": "warning",
                                           "message": "CS0168: The variable 'y' is declared but never used"}], got["diagnostics"]
        with patch.object(w, "execute", lambda *a: fake(*a, output="File \"check.py\", line 1\n", code=1)):
            assert submit(check_value())["diagnostics"] == [], "Only C# diagnostics are parsed here."

        # An answered request is never run again, even if its file comes back (a crash between answer and cleanup).
        calls.clear()
        again = w.QUEUE / "requests" / (ID + ".json")
        first = json.loads(w.regular(w.QUEUE / "responses" / (ID + ".json"), w.MAX_REQUEST))
        w.write_file(again, json.dumps(check_value()))
        digest = hashlib.sha256(again.read_bytes()).hexdigest()
        assert digest == first["requestHash"]
        worker.handle(again)
        assert not again.exists() and not calls, "An answered request must not run twice."

    # --- the image isn't ready ---
    idle = w.Worker()
    path = w.QUEUE / "requests" / (ID + ".json")
    w.write_file(path, json.dumps(run_value(hash=VECTOR_HASH, timeoutSeconds=63)))
    with patch.object(w, "execute", fake):
        idle.handle(path)
    got = json.loads(w.regular(w.QUEUE / "responses" / (ID + ".json"), w.MAX_REQUEST))
    assert got["status"] == "unavailable" and "image" in got["message"]

    # --- a crash after claiming: answered "interrupted", never run ---
    path = w.PRIVATE / "claimed" / (ID + ".json")
    w.write_file(path, json.dumps(run_value(hash=VECTOR_HASH)))
    with patch.object(w, "execute", fake):
        worker.recover()
    got = json.loads(w.regular(w.QUEUE / "responses" / (ID + ".json"), w.MAX_REQUEST))
    assert got["status"] == "interrupted" and not path.exists()

    # --- docker unavailable becomes a safe answer ---
    def broken(*args):
        raise FileNotFoundError("docker")
    w.write_file(w.QUEUE / "requests" / (ID + ".json"), json.dumps(run_value(hash=VECTOR_HASH, timeoutSeconds=64)))
    with patch.object(w, "execute", broken):
        worker.handle(w.QUEUE / "requests" / (ID + ".json"))
    assert json.loads(w.regular(w.QUEUE / "responses" / (ID + ".json"), w.MAX_REQUEST))["status"] == "unavailable"

    # --- unsafe queue files ---
    other = w.QUEUE / "requests" / ("22222222-2222-4222-8222-222222222222.json")
    other.write_text("{}")
    os.chmod(other, 0o644)
    rejected(lambda: worker.handle(other), (OSError, ValueError))
    other.unlink()
    (w.QUEUE / "requests" / "not-a-guid.json").write_text("{}")
    assert w.candidates(w.QUEUE / "requests") == [], "Only GUID-named files are requests."
    (w.QUEUE / "requests" / "not-a-guid.json").unlink()
    target = base / "secret.json"
    target.write_text("{}")
    link = w.QUEUE / "requests" / "33333333-3333-4333-8333-333333333333.json"
    link.symlink_to(target)
    rejected(lambda: worker.handle(link), (OSError, ValueError))
    link.unlink()

    # --- heartbeat ---
    worker.state = "ready"
    worker.beat()
    beat = json.loads(w.regular(w.QUEUE / "worker.json"))
    assert beat["schemaVersion"] == 1 and beat["ready"] is True and beat["image"] == "ready" and "checkedAt" in beat
    worker.beat(ready=False)
    assert json.loads(w.regular(w.QUEUE / "worker.json"))["ready"] is False

    # --- image: built once from the pinned Dockerfile, tagged by its hash, never pulled by tag ---
    dockerfile = w.DOCKERFILE.read_text()
    assert w.tag() == "lucia-skill-runner:" + hashlib.sha256(w.DOCKERFILE.read_bytes()).hexdigest()[:16]
    froms = [line for line in dockerfile.splitlines() if line.startswith("FROM ")]
    assert len(froms) == 2 and all("@sha256:" in line for line in froms), "Base images must be pinned by digest."
    ran = []

    def fake_run(command, **kwargs):
        ran.append((command, kwargs))
        return subprocess.CompletedProcess(command, 1 if command[1:3] == ["image", "inspect"] else 0, b"", b"")
    with patch.object(w.subprocess, "run", fake_run):
        assert w.build_image() == (w.tag(), None)
    assert [c[0][1] for c in ran] == ["image", "build"] and ran[1][0][2:] == ["-t", w.tag(), "-"]
    assert ran[1][1]["input"] == w.DOCKERFILE.read_bytes() and "--pull" not in ran[1][0]
    ran.clear()
    with patch.object(w.subprocess, "run", lambda c, **k: subprocess.CompletedProcess(c, 0, b"", b"")):
        assert w.build_image() == (w.tag(), None)
    with patch.object(w.subprocess, "run", lambda c, **k: subprocess.CompletedProcess(c, 1, b"", b"step 3\nboom\n")):
        image, problem = w.build_image()
        assert image is None and problem == "boom"

    # --- real process handling: output cap, stdin, timeout (harmless local commands instead of docker) ---
    code, timed_out, tail = w.execute(["sh", "-c", "head -c 300000 /dev/zero | tr '\\0' x; echo; echo done >&2"], "n", b"", 30)
    assert code == 0 and not timed_out and tail.truncated and len(tail.data) == w.OUTPUT_TAIL and tail.text().rstrip().endswith("done")
    code, _, tail = w.execute(["cat"], "n", b'{"a":1}', 30)
    assert code == 0 and tail.text() == '{"a":1}'
    code, _, tail = w.execute(["sh", "-c", "exit 7"], "n", b"x" * 500000, 30)
    assert code == 7, "A script that ignores its input must not hang the worker."
    killed = []
    real_run = subprocess.run
    with patch.object(w.subprocess, "run", lambda command, **k: killed.append(command) or real_run(["true"])):
        started = w.time.monotonic()
        code, timed_out, tail = w.execute(["sh", "-c", "echo before; sleep 30"], "lucia-skill-x", b"", 1)
    elapsed = w.time.monotonic() - started
    assert timed_out and code is None and elapsed < 15 and "before" in tail.text(), (timed_out, code, elapsed, tail.text())
    assert killed[0] == ["docker", "kill", "lucia-skill-x"] and killed[1] == ["docker", "rm", "-f", "lucia-skill-x"]
    assert tail.text() == tail.text() and Tail_is_bounded()

    # --- the unit ---
    assert "lucia-skill-runner.service" in pathlib.Path(w.__file__).read_text()
    print("Skill runner checks passed.")


def Tail_is_bounded():
    tail = w.Tail(10)
    tail.add(b"0123456789abcdef")
    return tail.data == b"6789abcdef" and tail.truncated


if __name__ == "__main__":
    main()
