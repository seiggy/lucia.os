"""Pure request/rollback checks with fake transports; never contacts Authentik."""

import contextlib
import datetime
import importlib.util
import io
import json
import os
import pathlib
import sys
import tempfile
from types import SimpleNamespace
from unittest.mock import Mock, patch

if sys.platform != "linux":
    sys.modules["fcntl"] = SimpleNamespace(LOCK_EX=2, LOCK_NB=4, flock=Mock())
spec = importlib.util.spec_from_file_location("domain_worker", pathlib.Path(__file__).with_name("activation_worker.py"))
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)

job_id = "11111111-1111-4111-8111-111111111111"
value = {"schemaVersion": 1, "jobId": job_id, "expiresAt": (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(minutes=10)).isoformat(),
         "action": "prepare", "profile": {"profileId": job_id}}
assert worker.request(value, job_id) == value
for change in (
    {"schemaVersion": 2}, {"schemaVersion": True}, {"action": "shell"}, {"command": "anything"},
    {"jobId": "../escape"}, {"expiresAt": "2020-01-01T00:00:00Z"}, {"profile": {"profileId": "different"}},
    {"expiresAt": (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(hours=1)).isoformat()},
):
    try:
        worker.request({**value, **change}, job_id)
        raise AssertionError("Unsafe activation request was accepted.")
    except ValueError:
        pass

with tempfile.TemporaryDirectory(prefix="lucia-domain-worker-check-") as folder:
    root = pathlib.Path(folder)
    state = root / "identity"
    directory = root / "domains"
    state.mkdir()
    directory.mkdir()
    (directory / "activation-requests").mkdir()
    request_path = directory / "activation-requests" / (job_id + ".json")
    request_path.write_text(json.dumps(value))
    (state / "host-settings.json").write_text(json.dumps({"authentication": {"public_origin": "https://spark"}}))
    provider = {"pk": 123, "redirect_uris": [{"url": "https://spark/signin-oidc"}]}
    app = {"meta_launch_url": "https://spark/"}
    desired = {"provider": {"redirect_uris": [{"url": "https://new.example.com/signin-oidc"}]},
               "application": {"meta_launch_url": "https://new.example.com/"}}
    p = SimpleNamespace(state=state, api=Mock(side_effect=lambda method, path, body=None: provider if "providers" in path else app))
    with patch.object(worker, "read_json", side_effect=lambda path: json.loads(path.read_text())), \
            patch.object(worker, "write_file", side_effect=lambda path, data: path.write_text(data)), \
            patch.object(worker, "private_directory", side_effect=lambda path: path.mkdir(exist_ok=True)), \
            patch.object(worker.application, "_load", return_value={}), \
            patch.object(worker.application, "_domain_profile", side_effect=lambda profile, _: profile), \
            patch.object(worker.application, "_snapshot", return_value={"provider": provider, "application": app}), \
            patch.object(worker.application, "_desired", return_value=desired), \
            patch.object(worker.application, "application_status", return_value={"ready": True}), \
            patch.object(worker.application, "prepare_domain_application", return_value={"prepared": True}) as prepare:
        assert worker.process(p, directory, job_id) == "prepare"
        assert prepare.call_count == 1
        # The immutable snapshot, not a replacement request file, determines the action.
        request_path.write_text(json.dumps({**value, "action": "rollback"}))
        assert worker.process(p, directory, job_id, value) == "prepare"
        assert prepare.call_count == 2
        receipt = json.loads((state / "domain-activation" / (job_id + ".json")).read_text())
        assert set(receipt) == {"schemaVersion", "profile", "provider_id", "original_redirects", "original_launch"}
        request_path.write_text(json.dumps({**value, "action": "rollback"}))
        provider["redirect_uris"] = desired["provider"]["redirect_uris"]
        app["meta_launch_url"] = desired["application"]["meta_launch_url"]
        assert worker.process(p, directory, job_id) == "rollback"
        writes = [call for call in p.api.call_args_list if call.args[0] == "PATCH"]
        assert len(writes) == 2
        assert set(writes[0].args[2]) == {"redirect_uris"} and set(writes[1].args[2]) == {"meta_launch_url"}
        (directory / "active.json").write_text("{}")
        try:
            worker.process(p, directory, job_id)
            raise AssertionError("An active profile was rolled back as a pending operation.")
        except ValueError:
            pass
        (directory / "active.json").unlink()
        provider["redirect_uris"] = [{"url": "https://unrelated.example.com/callback"}]
        try:
            worker.process(p, directory, job_id)
            raise AssertionError("Unrelated callback changes were overwritten.")
        except ValueError:
            pass

with tempfile.TemporaryDirectory(prefix="lucia-directory-check-") as folder:
    root = pathlib.Path(folder)
    state, directory = root / "identity", root / "domains"
    for path in (state, directory / "directory-requests", directory / "directory-responses"):
        path.mkdir(parents=True)
    (state / "host-settings.json").write_text(json.dumps({"authentication": {"public_origin": "https://spark"}}))
    ids = ["20260101000000000-" + str(n) * 32 for n in (1, 2, 3)]
    for index, name in enumerate(ids):
        (directory / "directory-requests" / (name + ".json")).write_text("{" if index == 2 else json.dumps({"n": index}))
    (directory / "directory-requests" / "not-a-request.json").write_text("{}")

    def apply(p, value, request_id, installation):
        assert installation == "install-1"
        if value["n"] == 1:
            raise worker.people.Refused("That name is already taken.")

    p = SimpleNamespace(load_existing=Mock())
    timing = {"due": 0, "hold": 0}
    with patch.object(worker, "ROOT", root / "tools"), \
            patch.object(worker, "regular", side_effect=lambda path, maximum: path.read_bytes()), \
            patch.object(worker, "read_json", side_effect=lambda path: json.loads(path.read_text())), \
            patch.object(worker, "write_file", side_effect=lambda path, data: path.write_text(data)), \
            patch.object(worker.application, "_load", return_value={"installation_id": "install-1"}), \
            patch.object(worker.people, "enable_password_changes") as enable, \
            patch.object(worker.people, "apply", side_effect=apply), \
            patch.object(worker.people, "snapshot", return_value={"schemaVersion": 1, "users": []}), \
            contextlib.redirect_stdout(io.StringIO()) as log:
        worker.directory_changes(p, state, directory, timing)
        answers = [json.loads((directory / "directory-responses" / (name + ".json")).read_text()) for name in ids]
        assert [answer["success"] for answer in answers] == [True, False, False]
        assert answers[1]["message"] == "That name is already taken."
        assert "couldn't finish" in answers[2]["message"] and enable.call_count == 1
        assert sorted(path.name for path in (directory / "directory-requests").iterdir()) == ["not-a-request.json"]
        assert json.loads((directory / "directory.json").read_text())["schemaVersion"] == 1
        assert timing["due"] > 0 and "{" not in "".join(a["message"] or "" for a in answers)
        worker.people.snapshot.reset_mock()
        worker.directory_changes(p, state, directory, timing)
        assert worker.people.snapshot.call_count == 0, "An idle queue republished before the minute was up."

if sys.platform == "linux":
    with tempfile.TemporaryDirectory(prefix=".lucia-native-activation-", dir=pathlib.Path.home()) as temporary:
        root = pathlib.Path(temporary)
        root.chmod(0o700)
        target = root / "activation-worker.json"
        outside = root / "must-remain.txt"
        outside.write_text("preserved")
        outside.chmod(0o600)
        target.with_name(target.name + ".new").symlink_to(outside)
        worker.write_file(target, '{"ready":true}')
        assert outside.read_text() == "preserved"
        assert json.loads(worker.regular(target))["ready"]
        assert not target.is_symlink()
        linked = root / "linked"
        linked.symlink_to(root, target_is_directory=True)
        try:
            worker.write_file(linked / "escape.json", "{}")
            raise AssertionError("Shared-directory symlink was followed.")
        except OSError:
            pass
        gateway = root / "gateway"
        (gateway / "domains").mkdir(parents=True, mode=0o700)
        gateway.chmod(0o700)
        marker = gateway / "tls.yml"
        marker.write_text('{"tls":{}}')
        marker.chmod(0o600)
        os.utime(marker, (1, 1))
        fingerprint = worker.refresh_ingress(root, None)
        assert fingerprint == "missing" and marker.stat().st_mtime_ns > 1_000_000_000
        previous_time = marker.stat().st_mtime_ns
        assert worker.refresh_ingress(root, fingerprint) == fingerprint and marker.stat().st_mtime_ns == previous_time
        worker.write_file(gateway / "domains/domain.yml", '{"http":{}}')
        fingerprint = worker.refresh_ingress(root, fingerprint)
        assert fingerprint != "missing" and marker.read_text() == '{"tls":{}}'
        (gateway / "domains/domain.yml").unlink()
        assert worker.refresh_ingress(root, fingerprint) == "missing"
        marker.unlink()
        marker.symlink_to(outside)
        try:
            worker.refresh_ingress(root, None)
            raise AssertionError("Gateway reload followed a marker symlink.")
        except OSError:
            pass
        fifo = root / "11111111-1111-4111-8111-111111111111.json"
        os.mkfifo(fifo, 0o600)
        try:
            worker.regular(fifo)
            raise AssertionError("A FIFO was treated as a queue request.")
        except ValueError:
            pass
        target.unlink()
        target.symlink_to(outside)
        try:
            worker.regular(target)
            raise AssertionError("A request symlink was followed.")
        except OSError:
            pass
else:
    print("SKIP: descriptor-relative queue filesystem checks require Linux.")

print("Scoped activation worker checks passed: bounded requests, owned fields only, rollback collision/active-profile guards. No live API calls.")
