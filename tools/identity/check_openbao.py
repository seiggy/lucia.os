"""Offline OpenBao foundation checks; --render also publishes disposable Aspire artifacts.

--stage-fixture is for check_openbao.mjs only and requires a fresh, empty directory.
No command deploys, initializes, or accesses an operational service.
"""

import argparse
import contextlib
import io
import json
import os
import pathlib
import re
import tempfile
from unittest.mock import patch

from provision import Provisioner, ROOT, write_file


def backup_checks(directory, active):
    import datetime
    import gzip
    import subprocess
    import openbao_backup as backup
    state = active.state
    mount = directory / "synthetic-nas"
    repository = mount / "restic"
    repository.mkdir(parents=True)
    config = {"schema_version": 1, "mount": str(mount), "repository": str(repository),
              "keep_daily": 7, "keep_weekly": 4, "keep_monthly": 6, "share_count": 3, "share_threshold": 2,
              "custody_confirmed": True, "backup_recovery_confirmed": True}
    path = state / "openbao/backup.json"
    write_file(path, json.dumps(config))
    assert backup.configuration(state) == config
    for update in ({"schema_version": True}, {"extra": True}, {"keep_daily": True},
                   {"keep_daily": 0, "keep_weekly": 0, "keep_monthly": 0},
                   {"keep_monthly": -1}, {"share_threshold": 4}, {"custody_confirmed": False},
                   {"backup_recovery_confirmed": False}, {"repository": str(directory)},
                   {"repository": "relative"}, {"mount": None}):
        write_file(path, json.dumps({**config, **update}))
        try:
            backup.configuration(state)
        except backup.BackupError:
            pass
        else:
            raise AssertionError("Invalid backup configuration or missing custody gate accepted.")
    write_file(path, json.dumps(config))
    assert not backup.status(state)["healthy"]
    status_path = state / "openbao/backup-status.json"
    now = datetime.datetime.now(datetime.timezone.utc)
    for age, expected in ((0, True), (86390, True), (86401, False), (-3600, False)):
        write_file(status_path, json.dumps({"state": "succeeded", "last_success_at":
                                          (now - datetime.timedelta(seconds=age)).isoformat()}))
        assert backup.status(state)["healthy"] == expected
    write_file(status_path, json.dumps({"state": "failed", "last_success_at": now.isoformat()}))
    assert not backup.status(state)["healthy"]
    try:
        backup.require_nas_mount(config)
    except backup.BackupError:
        pass
    else:
        raise AssertionError("A local directory was accepted as an off-host NAS mount.")
    for name in ("openbao-restic-password", "openbao-backup-role-id", "openbao-backup-secret-id"):
        write_file(state / "secrets" / name, "synthetic-private-input")
    calls = []
    compressed = gzip.compress(b"synthetic-snapshot")
    identity_path = state / "settings.json"
    identity = identity_path.read_bytes()
    write_file(identity_path, json.dumps({**json.loads(identity), "public_host": "controller.test"}))
    expected_address = "https://localhost:8200"

    def api(address, trust, method, route, body=None, token=None):
        calls.append(route)
        assert address == expected_address
        if route == "sys/seal-status":
            return io.BytesIO(b'{"initialized":true,"sealed":false,"n":3,"t":2}')
        if route == "auth/openbao-backup/login":
            assert body == {"role_id": "synthetic-private-input", "secret_id": "synthetic-private-input"}
            return io.BytesIO(json.dumps({"auth": {"client_token": "synthetic-job-token",
                               "policies": ["default", backup.POLICY], "lease_duration": 900}}).encode())
        assert token == "synthetic-job-token"
        return io.BytesIO(compressed if route == "sys/storage/raft/snapshot" else b"")

    commands = []

    def restic(command, **kwargs):
        commands.append(command)
        assert command[0] == "restic" and "synthetic-private-input" not in command
        assert "RESTIC_PASSWORD" not in kwargs["env"]
        assert kwargs["env"]["RESTIC_PASSWORD_FILE"] == str(state / "secrets/openbao-restic-password")
        if command[1] == "backup":
            assert calls[-1] == "auth/token/revoke-self", "The API token must not stay alive while restic waits for a lock."
            snapshot = pathlib.Path(command[2])
            assert snapshot.read_bytes() == compressed
            assert snapshot.name == "openbao-raft.snap" and snapshot.parent.parent == state / "openbao"
            kwargs["stdout"].write(b'{"message_type":"summary","snapshot_id":"' + b"a" * 64 + b'"}\n')
        return subprocess.CompletedProcess(command, 0)

    with patch.object(backup, "require_nas_mount"), patch.object(backup, "api", side_effect=api), \
            patch.object(backup.subprocess, "run", side_effect=restic), \
            patch.dict(backup.os.environ, {"RESTIC_PASSWORD": "synthetic-inherited-password"}):
        assert backup.backup(state) == "a" * 64
        assert calls == ["sys/seal-status", "auth/openbao-backup/login", "sys/storage/raft/snapshot", "auth/token/revoke-self"]
        assert [command[1] for command in commands] == ["cat", "backup", "forget"]
        forget = commands[-1]
        assert forget[forget.index("--tag") + 1] == backup.TAG
        assert forget[forget.index("--group-by") + 1] == "" and "--prune" in forget
        assert not list((state / "openbao").glob(".openbao-snapshot-*"))
        settings_path = state / "openbao-settings.json"
        original_settings = settings_path.read_bytes()
        write_file(settings_path, json.dumps({**json.loads(original_settings), "bind_address": "192.168.10.20"}))
        expected_address = "https://controller.test:8200"
        assert backup.backup(state) == "a" * 64
        write_file(settings_path, original_settings)
        expected_address = "https://localhost:8200"
    calls.clear()
    with patch.object(backup, "require_nas_mount"), patch.object(backup, "api", side_effect=api), \
            patch.object(backup, "restic", side_effect=["", backup.BackupError("synthetic-restic-failure")]):
        try:
            backup.backup(state)
        except backup.BackupError:
            pass
        else:
            raise AssertionError("A failed restic backup was reported as successful.")
        assert calls[-1] == "auth/token/revoke-self"
        assert not list((state / "openbao").glob(".openbao-snapshot-*"))
    calls.clear()
    commands.clear()
    def truncated(*args, **kwargs):
        result = api(*args, **kwargs)
        if args[3] == "sys/storage/raft/snapshot":
            result.close()
            return io.BytesIO(compressed[:-3])
        return result
    with patch.object(backup, "require_nas_mount"), patch.object(backup, "api", side_effect=truncated), \
            patch.object(backup.subprocess, "run", side_effect=restic):
        try:
            backup.backup(state)
        except backup.BackupError as error:
            assert "incomplete" in str(error)
        else:
            raise AssertionError("A truncated Raft snapshot could be archived as a successful backup.")
        assert [command[1] for command in commands] == ["cat"]
        assert calls[-1] == "auth/token/revoke-self"
        assert not list((state / "openbao").glob(".openbao-snapshot-*"))
    try:
        backup.NoRedirect().redirect_request(None, io.BytesIO(), 307, "", {}, "https://unrelated.invalid")
    except backup.BackupError:
        pass
    else:
        raise AssertionError("An authenticated request could be redirected.")
    write_file(identity_path, identity)


def exercise_backup(state, image):
    """Run the real API/restic pipeline against the runner's synthetic local repository."""
    import subprocess
    import openbao_backup as backup
    if (state / "backup-fixture/openbao-raft.snap").read_bytes()[:2] != b"\x1f\x8b":
        raise ValueError("This mode requires the shared runner's synthetic recovery fixture.")
    if not re.fullmatch(r"restic/restic:\d+\.\d+\.\d+@sha256:[a-f0-9]{64}", image or ""):
        raise ValueError("The synthetic restic image must be immutable.")
    execute = subprocess.run
    environment_file = state / "job-restic.env"

    def invoke(command, **kwargs):
        assert command[0] == "restic"
        environment = kwargs.pop("env")
        for key in ("RESTIC_REPOSITORY", "RESTIC_PASSWORD_FILE"):
            relative = pathlib.Path(environment[key]).relative_to(state)
            environment[key] = "/fixture/" + relative.as_posix()
        environment["RESTIC_CACHE_DIR"] = "/tmp/cache"
        write_file(environment_file, "\n".join(f"{key}={value}" for key, value in environment.items()
                                            if key.startswith("RESTIC_")) + "\n")
        arguments = command[1:]
        if arguments[0] == "backup":
            arguments[1] = "/fixture/" + pathlib.Path(arguments[1]).relative_to(state).as_posix()
        return execute(["docker", "run", "--rm", "--read-only", "--cap-drop", "ALL",
                        "--security-opt", "no-new-privileges:true", "--tmpfs", "/tmp",
                        "--user", f"{os.getuid()}:{os.getgid()}" if hasattr(os, "getuid") and os.getuid() else "1000:1000",
                        "--env-file", str(environment_file), "--mount", f"type=bind,source={state},target=/fixture",
                        image, *arguments], **kwargs)

    # Only the NAS mount check is replaced: this disposable repository is deliberately local.
    with patch.object(backup, "require_nas_mount"), patch.object(backup.subprocess, "run", side_effect=invoke):
        snapshot = backup.backup(state)
    print(json.dumps({"snapshot": snapshot, "pipeline": "real OpenBao API and real restic"}))


def fixture(directory, enabled=True):
    args = argparse.Namespace(state=str(directory), host="localhost", certificate_mode="private-ca",
                              auth_port=None, ldap_port=None, ca_port=None,
                              openbao_bind_address="127.0.0.1" if enabled else None, openbao_port=None)
    instance = Provisioner(args)
    with patch.object(Provisioner, "install_plugin"), contextlib.redirect_stdout(io.StringIO()):
        instance.prepare()
    return instance


def checks(directory, render):
    disabled = fixture(directory / "disabled", enabled=False)
    assert disabled.openbao_settings() is None
    assert not (disabled.state / "openbao").exists()
    active = fixture(directory / "active")
    config = active.openbao_settings()
    assert config == {"schema_version": 1, "bind_address": "127.0.0.1", "port": 8200}
    text = (active.state / "openbao/config/server.hcl").read_text()
    assert 'storage "raft"' in text and 'tls_min_version = "tls12"' in text
    assert 'audit "file" "lucia-file"' in text and 'log_raw = "false"' in text and 'hmac_accessor = "true"' in text
    assert "unsafe_allow_api_audit_creation" not in text
    assert "tls_disable =" not in text and 'tls_disable_client_certs = false' in text
    assert 'path = "/bao/data"' in text and "seal " not in text
    assert all(b"\r" not in path.read_bytes() for path in (active.state / "openbao/config").glob("*.sh"))
    (active.state / "openbao/data/sentinel").write_text("synthetic-preserved")
    assert fixture(active.state).openbao_settings() == config
    assert (active.state / "openbao/data/sentinel").read_text() == "synthetic-preserved"
    for update in ({"bind_address": "0.0.0.0"}, {"bind_address": "203.0.113.1"}, {"bind_address": "::1"},
                   {"port": 9443}, {"port": 443}, {"port": 8445}, {"port": True}, {"extra": 1}):
        try:
            active.validate_openbao_settings({**config, **update})
        except ValueError:
            pass
        else:
            raise AssertionError("Invalid or conflicting OpenBao settings accepted.")
    (active.state / "boot-settings.json").write_text('{"http_port":8200}')
    try:
        active.openbao_settings()
    except ValueError:
        pass
    else:
        raise AssertionError("Boot endpoint collision accepted.")
    (active.state / "boot-settings.json").unlink()
    active.args.openbao_port = 8202
    try:
        active.prepare_openbao()
    except ValueError:
        pass
    else:
        raise AssertionError("Implicit endpoint migration accepted.")
    active.args.openbao_port = None
    orphan = directory / "orphan"
    (orphan / "openbao/data").mkdir(parents=True)
    (orphan / "openbao/data/sentinel").write_text("synthetic-old-state")
    try:
        fixture(orphan)
    except ValueError:
        pass
    else:
        raise AssertionError("Unrecorded OpenBao state adopted.")
    for code, expected in ((200, "ready"), (501, "uninitialized"), (503, "sealed"), (429, "standby")):
        if code == 200:
            from unittest.mock import MagicMock
            response = MagicMock()
            response.__enter__.return_value.status = code
            transport = patch("provision.urllib.request.urlopen", return_value=response)
        else:
            import urllib.error
            transport = patch("provision.urllib.request.urlopen",
                              side_effect=urllib.error.HTTPError("https://fixture", code, "", {}, None))
        with patch("provision.ssl.create_default_context"), transport:
            assert active.openbao_status() == expected
    with patch("provision.ssl.create_default_context"), \
            patch("provision.urllib.request.urlopen", side_effect=ConnectionRefusedError()):
        assert active.openbao_status() == "unavailable"
    backup_checks(directory, active)
    if render:
        for instance, enabled in ((disabled, False), (active, True)):
            # The sentinel isn't a Raft database; publishing never opens it or starts a service.
            with contextlib.redirect_stdout(io.StringIO()):
                instance.install_plugin()
                instance.publish()
            compose_files = list((instance.state / "deployment").rglob("*compose*.y*ml"))
            assert compose_files, "Aspire did not publish Compose artifacts."
            compose = "\n".join(path.read_text() for path in compose_files).replace('"', '').replace("'", "")
            assert ("container_name: lucia-identity-openbao" in compose) == enabled
            if enabled:
                for value in ("127.0.0.1:8200:8200", "read_only: true", "ALL",
                              "/bao/config", "/bao/data", "/bao/audit", "openbao-health.sh"):
                    assert value in compose, value
                assert "openbao/openbao@sha256:" in compose
                # Other services must never wait for the operator's unseal action.
                assert not re.search(r"(?m)^ {6}identity-openbao:", compose)
            manifest = json.loads((instance.state / "platform-images.json").read_text())
            assert any(item["service"] == "openbao" for item in manifest["images"]) == enabled
    print("OpenBao identity and backup checks passed" + ("; opt-in and disabled Aspire rendering passed." if render else "."))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--render", action="store_true")
    parser.add_argument("--stage-fixture", type=pathlib.Path)
    parser.add_argument("--exercise-backup", type=pathlib.Path)
    parser.add_argument("--restic-image")
    args = parser.parse_args()
    if args.exercise_backup:
        exercise_backup(args.exercise_backup.absolute(), args.restic_image)
        return
    if args.stage_fixture:
        target = args.stage_fixture.absolute()
        if not target.is_dir() or target.is_symlink() or any(target.iterdir()):
            raise ValueError("The synthetic fixture requires a fresh empty directory.")
        fixture(target)
        print("Synthetic foundation configuration staged.")
        return
    with tempfile.TemporaryDirectory(prefix=".openbao-check-", dir=ROOT / "tools/identity") as temporary:
        checks(pathlib.Path(temporary), args.render)


if __name__ == "__main__":
    main()
