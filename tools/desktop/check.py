"""Offline stdlib checks: python tools\\desktop\\check.py.

All writable fixtures are beneath this checkout; Docker, Aspire and LDAP calls
are mocked. Linux additionally runs a short-lived worker with an isolated HOME
and fake provisioning to test real flock/session handoff after launcher exit.
"""

import contextlib
import hashlib
import io
import importlib.util
import json
import os
import pathlib
import signal
import ssl
import subprocess
import sys
import tarfile
import tempfile
import time
import types
import unittest
from unittest.mock import Mock, patch

sys.dont_write_bytecode = True
import bootstrap as b

ROOT = pathlib.Path(__file__).resolve().parents[2]
PASSWORD = "test-owner-private-password-aA1!"


def request():
    return {"schema_version": 1, "public_host": "192.0.2.10", "owner_username": "owner",
            "owner_password": PASSWORD, "sudo_password": None, "install_prerequisites": True, "verify_only": False}


def archive_at(path, changed=None, extra=None, manifest_change=None):
    contents = {name: (ROOT / name).read_bytes() for name in b.FILES}
    manifest = {"schema_version": 1, "version": "0.1.0",
                "files": [{"path": name, "sha256": hashlib.sha256(data).hexdigest()} for name, data in contents.items()]}
    if manifest_change:
        manifest_change(manifest)
    if changed:
        contents.update(changed)
    contents["manifest.json"] = json.dumps(manifest).encode()
    with tarfile.open(path, "w:gz") as tar:
        for name, data in contents.items():
            member = tarfile.TarInfo(name)
            member.size = len(data)
            tar.addfile(member, io.BytesIO(data))
        if extra:
            tar.addfile(extra, io.BytesIO(b"x") if extra.size else None)
    return hashlib.sha256(path.read_bytes()).hexdigest()


class Checks(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix=".desktop-check-", dir=ROOT / "tools/desktop")
        self.addCleanup(self.scratch.cleanup)
        self.home = pathlib.Path(self.scratch.name)
        self.stack = contextlib.ExitStack()
        self.addCleanup(self.stack.close)
        self.stack.enter_context(patch("bootstrap.pathlib.Path.home", return_value=self.home))
        self.root, self.state = b.locations()

    def linux(self):
        self.stack.enter_context(patch("bootstrap.sys.platform", "linux"))
        self.stack.enter_context(patch("bootstrap.platform.machine", return_value="aarch64"))
        self.stack.enter_context(patch("bootstrap.operating_system", return_value={
            "ID": "ubuntu", "VERSION_ID": "24.04", "PRETTY_NAME": "Ubuntu 24.04"}))
        self.stack.enter_context(patch("bootstrap.os.getuid", return_value=0 if os.name == "nt" else os.getuid(), create=True))
        self.stack.enter_context(patch("bootstrap.os.getgid", return_value=0 if os.name == "nt" else os.getgid(), create=True))
        self.stack.enter_context(patch("bootstrap.lock_held", return_value=False))
        self.stack.enter_context(patch("bootstrap.listening_ports", return_value=set()))
        self.stack.enter_context(patch("bootstrap.shutil.disk_usage", return_value=types.SimpleNamespace(free=20 * 1024 ** 3)))
        self.stack.enter_context(patch("bootstrap.shutil.which", side_effect=lambda name, **kw: name))
        self.stack.enter_context(patch("bootstrap.run_read", side_effect=self.command))

    @staticmethod
    def command(command, **kwargs):
        output = ""
        if command[1] == "info":
            output = "28.1.0"
        elif command[1] == "compose":
            output = "2.39.0"
        elif command[:3] == ["nvidia-ctk", "cdi", "list"]:
            output = "nvidia.com/gpu=all"
        return subprocess.CompletedProcess(command, 0, output, "")

    def state_files(self, complete=None, legacy=False):
        self.state.mkdir(parents=True)
        settings = {"schema_version": 1, "certificate_mode": "private-ca", "public_host": "192.0.2.10",
                    "ldap_base_dn": "dc=lucia,dc=home,dc=arpa", "ports": {"authentik": 9443, "ldaps": 636, "ca": 9444},
                    "uid": os.getuid(), "gid": os.getgid()}
        (self.state / "settings.json").write_text(json.dumps(settings))
        (self.state / "secrets").mkdir()
        for name in b.SECRET_NAMES:
            (self.state / "secrets" / name).write_text("existing-private-secret-" + "x" * 32)
        if complete is not None:
            (self.state / "owner.json").write_text(json.dumps({"schema_version": 1, "username": "owner", "complete": complete}))
        if legacy:
            (self.state / "apphost-path.txt").write_text("/home/user/developer-checkout/" + b.APPHOST)
        return settings

    def job_files(self, alive=True):
        job_id = "a" * 32
        directory = self.root / "jobs" / job_id
        directory.mkdir(parents=True)
        (self.root / ".run.lock").touch()
        b.write_json(self.root / "active.json", {"job_id": job_id})
        b.write_json(directory / "request.json", {key: value for key, value in request().items() if key in b.REVIEW_FIELDS})
        b.write_json(directory / "worker.json", {"pid": 123, "start_ticks": "100"})
        b.write_json(directory / "status.json", {"schema_version": 1, "job_id": job_id, "status": "running",
                                                "events": [], "error": None, "result": None})
        self.stack.enter_context(patch("bootstrap.process_identity", return_value="100" if alive else None))
        return job_id, directory

    def test_standalone_inspect_without_file_or_writes(self):
        source = (ROOT / "tools/desktop/bootstrap.py").read_text()
        scope = {"__name__": "standalone"}
        exec(compile(source, "<bundled-source>", "exec"), scope)
        self.assertNotIn("__file__", scope)
        with patch("os.mkdir", side_effect=AssertionError("inspect wrote a directory")), \
                patch("os.chmod", side_effect=AssertionError("inspect changed permissions")), \
                patch("os.replace", side_effect=AssertionError("inspect replaced a file")), \
                patch("subprocess.run", return_value=subprocess.CompletedProcess([], 1, "", "")):
            result = scope["inspect"]()
        self.assertEqual(result["schema_version"], 1)
        self.assertFalse(result["is_installed"])
        self.assertFalse(result["can_install"])
        self.assertEqual(list(self.home.iterdir()), [])

    def test_fresh_and_partial_inspection(self):
        self.linux()
        result = b.inspect()
        self.assertTrue(result["can_install"])
        self.assertFalse(result["is_installed"])
        self.assertFalse(result["requires_sudo"])
        self.state_files(complete=False)
        before = (self.state / "settings.json").read_bytes()
        result = b.inspect()
        self.assertTrue(result["can_install"])
        self.assertTrue(result["is_installed"])
        self.assertFalse(result["owner_ready"])
        self.assertEqual(before, (self.state / "settings.json").read_bytes())

    def test_partial_state_without_settings_never_looks_fresh(self):
        self.linux()
        (self.state / "ca").mkdir(parents=True)
        (self.state / "ca/valuable-data").write_text("preserve")
        result = b.inspect()
        self.assertTrue(result["is_installed"])
        self.assertFalse(result["can_install"])
        self.assertFalse(result["owner_ready"])

    def test_interrupted_owned_prepare_preserves_and_resumes_inputs(self):
        self.linux()
        settings = self.state_files()
        (self.state / "settings.json").rename(self.state / "desktop-prepare.json")
        existing_secret = (self.state / "secrets" / b.SECRET_NAMES[0]).read_bytes()
        (self.state / "secrets" / b.SECRET_NAMES[-1]).unlink()
        result = b.inspect()
        self.assertTrue(result["can_install"])
        self.assertTrue(result["is_installed"])
        self.assertEqual(result["public_host"], settings["public_host"])
        self.assertEqual(existing_secret, (self.state / "secrets" / b.SECRET_NAMES[0]).read_bytes())
        self.assertFalse((self.state / "secrets" / b.SECRET_NAMES[-1]).exists(), "Read-only inspect must not generate missing secrets.")
        (self.state / "ca").mkdir()
        (self.state / "ca/existing-root").write_text("do-not-reinitialize")
        self.assertFalse(b.inspect()["can_install"], "A preparation marker cannot authorize reinitializing existing CA data.")

    def test_legacy_complete_reused_and_ports_owned(self):
        self.linux()
        self.state_files(complete=True, legacy=True)

        def run(command, **kwargs):
            if command[1] == "ps":
                output = json.dumps({"Names": "lucia-identity-gateway", "Labels": "io.lucia.component=identity"})
            elif command[1] == "inspect" and command[3] == "{{json .NetworkSettings.Ports}}":
                output = json.dumps({"8443/tcp": [{"HostPort": "9443"}], "8636/tcp": [{"HostPort": "636"}]})
            elif command[1] == "inspect":
                output = json.dumps([{"Type": "bind", "Source": str(self.state / "gateway")}])
            else:
                return self.command(command, **kwargs)
            return subprocess.CompletedProcess(command, 0, output, "")

        with patch("bootstrap.run_read", side_effect=run), patch("bootstrap.listening_ports", return_value={9443, 636}):
            result = b.inspect()
        self.assertTrue(result["can_install"])
        self.assertTrue(result["owner_ready"])
        self.assertEqual(result["authentik_url"], "https://192.0.2.10:9443")
        self.assertTrue(any(c["name"] in ("dotnet", "aspire") for c in result["checks"]),
                        "An enrolled owner still needs AppHost prerequisites for host deployment.")
        self.assertIn("preserve", result["planned_changes"][0])
        self.assertFalse(result["host_ready"])
        self.assertFalse(result["application_ready"])
        with self.assertRaises(b.SafeError):
            b.check_review({**request(), "public_host": "192.0.2.99"}, result)
        with self.assertRaises(b.SafeError):
            b.check_review({**request(), "owner_username": "someone"}, result)

    def test_legacy_partial_cannot_relocate(self):
        self.linux()
        self.state_files(complete=False, legacy=True)
        result = b.inspect()
        self.assertTrue(result["is_installed"])
        self.assertFalse(result["can_install"])

    def test_docker_access_and_collisions_block(self):
        self.linux()
        with patch("bootstrap.run_read", return_value=subprocess.CompletedProcess([], 1, "", "")):
            result = b.inspect()
        self.assertFalse(result["can_install"])
        self.assertFalse(result["requires_sudo"], "No privileged installation is implemented or requested.")
        with self.assertRaisesRegex(b.SafeError, "Docker"):
            b.check_review(request(), result)
        with patch("bootstrap.listening_ports", return_value={9443}):
            self.assertFalse(b.inspect()["can_install"])

    def test_request_validation(self):
        self.assertEqual(b.validate_request(request())["owner_password"], PASSWORD)
        for changes in ({"owner_password": ""}, {"owner_password": None}, {"schema_version": True}, {"verify_only": "false"},
                        {"sudo_password": "not-supported"}, {"owner_username": "admin"}, {"public_host": "https://host"},
                        {"extra_secret": PASSWORD}):
            with self.assertRaises(b.SafeError) as error:
                b.validate_request({**request(), **changes})
            self.assertNotIn(PASSWORD, str(error.exception))

    def test_additive_host_request_and_legacy_review(self):
        old = b.validate_request(request())
        self.assertNotIn("configure_host", old, "Legacy requests remain identity-only.")
        self.assertEqual(set(b.validate_review({k: old[k] for k in b.REVIEW_FIELDS})), b.REVIEW_FIELDS)
        modern = b.validate_request({**request(), "configure_host": True, "model_directory": None, "host_package": None})
        self.assertTrue(modern["configure_host"])
        for change in ({"configure_host": "true"}, {"host_package": {"secret": PASSWORD}},
                       {"model_directory": "models"}, {"model_directory": "/home/user/../models"},
                       {"model_directory": "/"}, {"model_directory": str(self.state)},
                       {"model_directory": str(self.root)}, {"model_directory": str(self.state.parent / "host/data")}):
            with self.assertRaises(b.SafeError):
                b.validate_request({**modern, **change})
        with self.assertRaises(b.SafeError):
            b.check_review(modern, {"can_install": True, "public_host": None, "owner_username": None,
                                    "owner_ready": False, "host_package_required": True})

    def host_upload(self):
        """DummyFixture: transport bytes only, never a deployable host artifact."""
        stage = self.home / ".cache/lucia-desktop" / ("c" * 32)
        stage.mkdir(parents=True, mode=0o700)
        os.chmod(stage.parent, 0o700)
        archive = stage / "host.tar.gz"
        with archive.open("wb") as output:
            output.truncate(17 * 1024 * 1024)
        os.chmod(archive, 0o600)
        with archive.open("rb") as source:
            digest = hashlib.file_digest(source, "sha256").hexdigest()
        package = {"archive_path": str(archive), "manifest_path": str(stage / "host-manifest.json"),
                   "size": archive.stat().st_size, "sha256": digest}
        manifest = {"schema_version": 1, "version": "0.1.0", "rid": "linux-arm64",
                    "entrypoint": "Lucia.Homelab.Server.dll", "sha256": digest, "size": package["size"],
                    "files": [{"path": name, "sha256": digest, "size": 1}
                              for name in ("Lucia.Homelab.Server.dll", "wwwroot/index.html")]}
        b.write_json(pathlib.Path(package["manifest_path"]), manifest)
        return package, manifest

    def test_streamed_host_archive_hash_paths_size_and_stage(self):
        package, manifest = self.host_upload()
        archive, _ = b.validated_host_package(package)
        self.assertGreater(archive.stat().st_size, b.MAX_PAYLOAD)
        with patch.object(pathlib.Path, "read_bytes", side_effect=AssertionError("archive loaded into memory")):
            b.validated_host_package(package)
        with archive.open("r+b") as output:
            output.write(b"changed")
        with self.assertRaisesRegex(b.SafeError, "SHA-256"):
            b.validated_host_package(package)
        with self.assertRaises(b.SafeError):
            b.validated_host_package({**package, "size": package["size"] + 1})
        with self.assertRaises(b.SafeError):
            b.validated_host_package({**package, "archive_path": str(self.home / "unowned.tar.gz")})
        with self.assertRaises(b.SafeError):
            b.validated_host_package(package, self.home / "another-stage")
        for bad_path in ("../escape", "/root/file", r"wwwroot\index.html", "wwwroot//index.html"):
            manifest["files"][0]["path"] = bad_path
            b.write_json(pathlib.Path(package["manifest_path"]), manifest)
            with self.assertRaises(b.SafeError):
                b.validated_host_package(package)
        with patch("bootstrap.os.getuid", return_value=987654, create=True):
            with self.assertRaises(b.SafeError):
                b.upload_file(archive)

    def test_real_host_packer_manifest_and_extractor_contract_offline(self):
        """DummyFixture: tiny synthetic managed/static files; never Docker or a runnable host."""
        spec = importlib.util.spec_from_file_location("desktop_fixture_package", ROOT / "tools/host/package.py")
        packer = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(packer)
        spec = importlib.util.spec_from_file_location("desktop_fixture_provision_host", ROOT / "tools/host/provision_host.py")
        host = importlib.util.module_from_spec(spec)
        with patch.dict(sys.modules, {"package": packer}):
            spec.loader.exec_module(host)
        publish = self.home / "dummy-publish"
        (publish / "wwwroot").mkdir(parents=True)
        (publish / "Lucia.Homelab.Server.dll").write_bytes(b"DummyFixture-not-executable")
        (publish / "wwwroot/index.html").write_bytes(b"<html>DummyFixture</html>")
        for name in packer.NODE_AGENT_REQUIRED:
            target = publish / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(b"DummyFixture-not-executable")
        (publish / "boot/node-agent-linux-x64/libhostfxr.so").write_bytes(b"DummyFixture-native-not-executable")
        (publish / "boot/node-agent-linux-x64/appsettings.json").write_text('{"fixture":"must-not-be-packaged"}')
        stage = self.home / ".cache/lucia-desktop" / ("d" * 32)
        stage.mkdir(parents=True, mode=0o700)
        os.chmod(stage.parent, 0o700)
        archive, manifest_path = stage / "host.tar.gz", stage / "host-manifest.json"
        manifest = packer.package(publish, archive, manifest_path)
        names = {item["path"] for item in manifest["files"]}
        self.assertTrue(packer.NODE_AGENT_REQUIRED <= names)
        self.assertNotIn("boot/node-agent-linux-x64/appsettings.json", names)
        self.assertEqual(packer.file_mode("boot/node-agent-linux-x64/libhostfxr.so"), 0o755)
        self.assertEqual(packer.file_mode("boot/node-agent-linux-x64/createdump"), 0o755)
        self.assertEqual(packer.file_mode("boot/node-agent-linux-x64/lucia-node-agent.pdb"), 0o644)
        self.assertIsNone(packer.file_mode("boot/node-agent-linux-x64/process-dump.bin"))
        self.assertIsNone(packer.file_mode("boot/node-agent-linux-x64/identity.pem"))
        incomplete = {**manifest, "files": [item for item in manifest["files"]
                                          if item["path"] != "boot/node-agent-linux-x64/lucia-node-agent"]}
        with self.assertRaises(packer.HostError):
            packer.validate_manifest(incomplete)
        os.chmod(archive, 0o600)
        os.chmod(manifest_path, 0o600)
        package = {"archive_path": str(archive), "manifest_path": str(manifest_path), "sha256": manifest["sha256"], "size": manifest["size"]}
        self.assertEqual(b.validated_host_package(package), (archive, manifest_path))
        host_root = self.home / "fixture-host"
        release, extracted = host.extract_release(archive, manifest_path, host_root)
        self.assertEqual(extracted, manifest)
        self.assertEqual((release / "publish/wwwroot/index.html").read_bytes(), b"<html>DummyFixture</html>")
        # The helper makes releases immutable; restore fixture permissions solely for temporary cleanup.
        for path in [*release.rglob("*"), release]:
            os.chmod(path, 0o700 if path.is_dir() else 0o600)
        with tarfile.open(archive, "w:gz") as tar:
            item = tarfile.TarInfo("../escape")
            item.size = 1
            tar.addfile(item, io.BytesIO(b"x"))
        manifest.update(sha256=packer.sha256_file(archive), size=archive.stat().st_size)
        manifest_path.write_bytes(packer.json_bytes(manifest))
        with self.assertRaises(packer.HostError):
            host.extract_release(archive, manifest_path, host_root)
        self.assertFalse((self.home / "escape").exists())

    def test_model_directory_ownership_and_preserved_selection(self):
        models = self.home / "models"
        models.mkdir()
        with patch("bootstrap.os.getuid", return_value=987654, create=True):
            with self.assertRaises(b.SafeError):
                b.model_directory_value(str(models))
        supplied = {**request(), "configure_host": True, "model_directory": "/home/user/new"}
        with self.assertRaisesRegex(b.SafeError, "model directory"):
            b.check_review(supplied, {"can_install": True, "public_host": None, "owner_username": None,
                                     "owner_ready": False, "host_package_required": False, "model_directory": "/home/user/original"})

    def test_existing_owner_missing_host_and_registration_require_repairs(self):
        self.linux()
        self.state_files(complete=True)
        before = {path.name: path.read_bytes() for path in (self.state / "secrets").iterdir()}
        result = b.inspect()
        self.assertTrue(result["owner_ready"])
        self.assertFalse(result["host_ready"])
        self.assertFalse(result["application_ready"])
        self.assertTrue(result["host_package_required"])
        self.assertEqual(next(c for c in result["checks"] if c["name"] == "Lucia sign-in")["status"], "action")
        self.assertEqual(before, {path.name: path.read_bytes() for path in (self.state / "secrets").iterdir()})
        with patch("bootstrap.listening_ports", return_value={443}):
            self.assertFalse(b.inspect()["can_install"])
        with patch("bootstrap.run_read", side_effect=lambda command, **kwargs:
                   subprocess.CompletedProcess(command, 1, "", "") if command[0] == "nvidia-ctk" else self.command(command, **kwargs)):
            self.assertFalse(b.inspect()["can_install"])

    def test_actual_application_probe_detects_deleted_provider_and_binding(self):
        self.linux()
        settings = self.state_files(complete=True)
        record = {"schema_version": 1, "public_origin": "https://192.0.2.10",
                  "authority": "https://192.0.2.10:9443/application/o/lucia/",
                  "client_id": "dummy-fixture-client", "markers": {"application": "dummy-owned-app", "provider": "dummy-owned-provider"},
                  "resources": {"application": "app", "provider": 7, "signing_key": "signer", "scope_mapping": "scope", "owner_user": 8,
                                "owner_group": "owners", "access_group": "users", "bindings": {"owner": {"id": "one"}, "inference": {"id": "two"}}}}
        b.write_json(self.state / "host-auth.json", record)
        b.write_bytes(self.state / "secrets/host-oidc-client-secret", b"dummy-fixture-secret")
        app = {"pk": "app", "provider": 7, "meta_description": "dummy-owned-app", "meta_launch_url": "https://192.0.2.10/",
               "meta_hide": False, "policy_engine_mode": "any"}
        provider = {"name": "dummy-owned-provider", "client_id": "dummy-fixture-client", "client_secret": "dummy-fixture-secret",
                    "client_type": "confidential", "signing_key": "signer", "property_mappings": ["scope"],
                    "grant_types": ["authorization_code"], "issuer_mode": "per_provider",
                    "redirect_uris": [
                        {"matching_mode": "strict", "url": "https://192.0.2.10/signin-oidc", "redirect_uri_type": "authorization"},
                        {"matching_mode": "strict", "url": "https://192.0.2.10/signout-callback-oidc", "redirect_uri_type": "logout"}]}
        bindings = {"results": [{"pk": name, "target": "app", "group": group, "enabled": True, "negate": False, "policy": None, "user": None}
                                for name, group in (("one", "owners"), ("two", "users"))]}
        with patch("bootstrap.service_json", return_value=None) as api:
            ready, reason = b.application_probe(self.state, settings)
            self.assertFalse(ready)
            self.assertIn("missing", reason)
            self.assertIn("/api/v3/core/applications/lucia/", api.call_args.args[1])
        with patch("bootstrap.service_json", side_effect=[app, None]):
            self.assertIn("provider", b.application_probe(self.state, settings)[1])
        with patch("bootstrap.service_json", side_effect=[app, provider, {"results": []}]):
            self.assertFalse(b.application_probe(self.state, settings)[0])
        with patch("bootstrap.service_json", side_effect=[app, provider, bindings, {"passing": True}, {"preview": {"lucia_role": "Owner"}}]):
            ready, reason = b.application_probe(self.state, settings)
            self.assertTrue(ready)
            self.assertIn("browser SSO has not been tested", reason)
            self.assertNotIn("dummy-fixture-secret", reason)

    def test_host_readiness_requires_owned_image_container_and_verified_http(self):
        self.linux()
        settings = self.state_files(complete=True)
        root = self.state.parent / "host"
        root.mkdir(mode=0o700)
        installation = hashlib.sha256(str(self.state).encode()).hexdigest()
        image_id = "sha256:" + "a" * 64
        record = {"schema_version": 1, "component": "managedhost", "installation_id": installation,
                  "host_state": str(root), "authentication": {"public_origin": "https://192.0.2.10"},
                  "image_id": image_id, "artifact_sha256": "b" * 64, "model_directory": "/home/user/models"}
        b.write_json(self.state / "host-settings.json", record)
        b.write_json(root / "owner.json", {"component": "managedhost", "identity_state": str(self.state),
                                         "configured": True, "model_directory": "/home/user/models"})
        labels = {"io.lucia.component": "managedhost", "io.lucia.host-installation": installation,
                  "io.lucia.artifact-sha256": "b" * 64}
        container = {"image": image_id, "state": {"Running": True, "Health": {"Status": "healthy"}}, "labels": labels}

        def docker(command, **kwargs):
            return subprocess.CompletedProcess(command, 0, json.dumps(labels if command[1] == "image" else container), "")

        with patch("bootstrap.run_read", side_effect=docker), patch("bootstrap.service_json", return_value={"enabled": True, "authenticated": False}):
            ready, required, endpoint, models = b.host_probe(self.state, settings, "docker", {})
            self.assertTrue(ready)
            self.assertFalse(required, "Installed owned images allow app/host repairs without reupload.")
            self.assertEqual(endpoint, "https://192.0.2.10")
            self.assertEqual(models, "/home/user/models")
            container["state"]["Health"]["Status"] = "unhealthy"
            self.assertFalse(b.host_probe(self.state, settings, "docker", {})[0])
            container["labels"] = {**labels, "io.lucia.host-installation": "unowned"}
            with self.assertRaises(b.SafeError):
                b.host_probe(self.state, settings, "docker", {})
        with patch("bootstrap.run_read", return_value=subprocess.CompletedProcess([], 1, "", "")):
            ready, required, _, _ = b.host_probe(self.state, settings, "docker", {})
            self.assertFalse(ready)
            self.assertTrue(required)

    def test_host_phase_resume_reads_nonsecret_metadata_only(self):
        self.linux()
        job_id, directory = self.job_files()
        metadata = {k: v for k, v in request().items() if k in b.REVIEW_FIELDS}
        metadata.update(configure_host=True, model_directory="/home/user/models", host_package=None)
        b.write_json(directory / "request.json", metadata)
        status = b.read_json(directory / "status.json")
        status["events"] = [{"phase": "host-deploy", "message": "Waiting for managed host.", "level": "info"}]
        b.write_json(directory / "status.json", status)
        with patch("bootstrap.lock_held", return_value=True), patch("bootstrap.read_bytes", wraps=b.read_bytes) as read:
            result = b.inspect()
        self.assertEqual(result["active_job_id"], job_id)
        self.assertTrue(result["active_job_configure_host"])
        self.assertEqual(result["model_directory"], "/home/user/models")
        self.assertFalse(any(call.args[0].name == "owner-password" for call in read.call_args_list))

    def test_password_policy_uses_recorded_owner_not_requested_verify_flag(self):
        fresh = {"can_install": True, "public_host": None, "owner_username": None, "owner_ready": False}
        existing = {**fresh, "public_host": request()["public_host"], "owner_username": "owner", "owner_ready": True}
        for password in ("old", "x\n", "x\r", "x\0", "long-but-newline\nunsafe"):
            supplied = b.validate_request({**request(), "owner_password": password})
            self.assertEqual(supplied["owner_password"], password)
            b.check_review(supplied, existing)
            with self.assertRaises(b.SafeError):
                b.check_review(supplied, fresh)
            with self.assertRaises(b.SafeError):
                b.check_review({**supplied, "verify_only": True}, fresh)
        b.check_review(request(), fresh)
        with self.assertRaises(b.SafeError):
            b.check_review({**request(), "verify_only": True}, fresh)

    def test_payload_hash_and_allowed_sources(self):
        path = self.home / "payload.tar.gz"
        digest = archive_at(path)
        files, manifest = b.validated_payload(path, digest)
        self.assertEqual(set(files), b.FILES)
        self.assertEqual(manifest["version"], "0.1.0")
        with self.assertRaises(b.SafeError):
            b.validated_payload(path, "0" * 64)
        digest = archive_at(path, changed={"tools/identity/owner.py": b"tampered"})
        with self.assertRaises(b.SafeError):
            b.validated_payload(path, digest)

    def test_archive_paths_links_duplicates_and_manifest(self):
        path = self.home / "payload.tar.gz"
        for name in ("../escape", "/etc/passwd", r"tools\identity\owner.py", "state/secrets/password", "tools/identity/owner.py"):
            entry = tarfile.TarInfo(name)
            entry.size = 1
            digest = archive_at(path, extra=entry)
            with self.assertRaises(b.SafeError):
                b.validated_payload(path, digest)
        for kind in (tarfile.SYMTYPE, tarfile.LNKTYPE, tarfile.FIFOTYPE):
            entry = tarfile.TarInfo("unapproved")
            entry.type = kind
            entry.linkname = "../../escape"
            digest = archive_at(path, extra=entry)
            with self.assertRaises(b.SafeError):
                b.validated_payload(path, digest)
        for change in (lambda m: m.update(schema_version=True), lambda m: m.update(version="9"),
                       lambda m: m["files"].pop(), lambda m: m["files"].append(m["files"][0])):
            digest = archive_at(path, manifest_change=change)
            with self.assertRaises(b.SafeError):
                b.validated_payload(path, digest)
        self.assertFalse((self.home / "escape").exists())

    def test_active_and_crashed_jobs(self):
        self.linux()
        job_id, directory = self.job_files()
        with patch("bootstrap.lock_held", return_value=True):
            result = b.job_status(job_id)
            self.assertEqual(result["status"], "running")
            inspection = b.inspect()
            self.assertEqual(inspection["active_job_id"], job_id)
            self.assertTrue(inspection["can_install"], "An active desktop job must be reconnectable.")
            self.assertEqual(inspection["public_host"], request()["public_host"])
            self.assertEqual(inspection["owner_username"], request()["owner_username"])
            self.assertFalse(inspection["owner_ready"], "Reviewed inputs do not mean owner enrollment is complete.")
            self.assertFalse(inspection["is_installed"])
        (directory / "owner-password").write_text(PASSWORD)
        result = b.job_status(job_id)
        self.assertEqual(result["status"], "failed")
        self.assertFalse((directory / "owner-password").exists())
        self.assertNotIn(PASSWORD, json.dumps(result))
        self.assertEqual(b.read_json(directory / "status.json")["status"], "failed")

    def test_active_review_is_read_without_password_and_rejects_secret_fields(self):
        self.linux()
        job_id, directory = self.job_files()
        (directory / "owner-password").write_text(PASSWORD)
        with patch("bootstrap.lock_held", return_value=True), patch("bootstrap.read_bytes", wraps=b.read_bytes) as read:
            result = b.inspect()
        self.assertEqual(result["active_job_id"], job_id)
        self.assertNotIn(PASSWORD, json.dumps(result))
        self.assertFalse(any(call.args[0].name == "owner-password" for call in read.call_args_list))
        metadata = b.read_json(directory / "request.json")
        b.write_json(directory / "request.json", {**metadata, "owner_password": PASSWORD})
        with patch("bootstrap.lock_held", return_value=True):
            result = b.inspect()
        self.assertFalse(result["can_install"])
        self.assertIsNone(result["active_job_id"])
        self.assertNotIn(PASSWORD, json.dumps(result))

    def test_dead_pid_is_not_revived_by_another_lock(self):
        self.linux()
        job_id, _ = self.job_files(alive=False)
        with patch("bootstrap.lock_held", return_value=True):
            self.assertEqual(b.job_status(job_id)["status"], "failed")

    def test_duplicate_start_returns_same_job_without_archive_or_worker(self):
        self.linux()
        job_id, _ = self.job_files()
        fake_fcntl = types.SimpleNamespace(LOCK_EX=2, LOCK_NB=4, flock=Mock(side_effect=BlockingIOError))
        with patch.dict(sys.modules, {"fcntl": fake_fcntl}), patch("bootstrap.lock_held", return_value=True), \
                patch("bootstrap.subprocess.Popen") as launch, patch("bootstrap.validated_payload") as validate:
            result = b.start(self.home / "not-even-uploaded", "0" * 64, request())
        self.assertEqual(result, {"schema_version": 1, "job_id": job_id, "status": "running"})
        launch.assert_not_called()
        validate.assert_not_called()

    def test_start_stages_private_payload_and_detaches_worker(self):
        self.linux()
        package, _ = self.host_upload()
        upload = pathlib.Path(package["archive_path"]).parent
        archive = upload / "payload.tar.gz"
        digest = archive_at(archive)
        os.chmod(archive, 0o600)
        modern_request = {**request(), "configure_host": True, "model_directory": None, "host_package": package}
        fake_fcntl = types.SimpleNamespace(LOCK_EX=2, LOCK_NB=4, flock=Mock())
        output = io.StringIO()
        with patch.dict(sys.modules, {"fcntl": fake_fcntl}), \
                patch("bootstrap.stat.S_IMODE", return_value=0o700), \
                patch("bootstrap.process_identity", return_value="100"), \
                patch("bootstrap.sys.argv", ["bootstrap", "start", "--archive", str(archive), "--sha256", digest]), \
                patch("bootstrap.sys.stdin", io.StringIO(json.dumps(modern_request) + "\n")), \
                contextlib.redirect_stdout(output), \
                patch("bootstrap.subprocess.Popen", return_value=Mock(pid=321)) as launch:
            self.assertEqual(b.main(), 0)
        self.assertEqual(len(output.getvalue().splitlines()), 1, "The start command must emit exactly one JSON object.")
        self.assertNotIn(PASSWORD, output.getvalue())
        result = json.loads(output.getvalue())
        self.assertEqual(result["status"], "running")
        self.assertRegex(result["job_id"], r"^[a-f0-9]{32}$")
        directory = self.root / "jobs" / result["job_id"]
        self.assertNotIn(PASSWORD, (directory / "request.json").read_text())
        self.assertEqual(b.read_json(directory / "request.json")["host_package"], package)
        self.assertTrue(pathlib.Path(package["archive_path"]).is_file(), "The detached worker retains access to the uploaded runtime.")
        self.assertNotIn(PASSWORD, (directory / "status.json").read_text())
        self.assertEqual((directory / "owner-password").read_text(), PASSWORD)
        self.assertFalse((self.root / "app").exists(), "Only the worker activates the stable payload.")
        command = launch.call_args.args[0]
        options = launch.call_args.kwargs
        self.assertNotIn(PASSWORD, repr(command) + repr(options))
        self.assertTrue(options["start_new_session"])
        self.assertTrue(options["close_fds"])
        self.assertEqual(len(options["pass_fds"]), 1)
        self.assertEqual(options["stdin"], subprocess.DEVNULL)
        self.assertEqual(options["stdout"], subprocess.DEVNULL)
        self.assertEqual(options["stderr"], subprocess.DEVNULL)
        app = b.stable_payload(self.root, directory)
        self.assertEqual(app, self.root / "app")
        self.assertEqual((app / b.APPHOST).read_bytes(), (ROOT / b.APPHOST).read_bytes())
        self.assertEqual(b.read_json(app / "src/Lucia.Homelab.Identity.AppHost/global.json")["sdk"]["version"], b.DOTNET_VERSION)
        (app / "manifest.json").unlink()
        (app / b.APPHOST).unlink()
        self.assertEqual(b.stable_payload(self.root, directory), app, "An interrupted owned payload activation must resume.")
        self.assertTrue((app / b.APPHOST).is_file())

    def test_invalid_job_state_never_succeeds(self):
        self.linux()
        job_id, directory = self.job_files()
        (directory / "status.json").write_text('{"status":"succeeded","owner_password":"should-not-echo"}')
        result = b.job_status(job_id)
        self.assertEqual(result["status"], "failed")
        self.assertNotIn("should-not-echo", json.dumps(result))
        with self.assertRaises(b.SafeError):
            b.job_status("../escape")
        with patch("bootstrap.job_directory", side_effect=b.SafeError("Unsafe managed path.")):
            result = b.job_status(job_id)
        self.assertEqual(result["status"], "failed")
        self.assertEqual(set(result), {"schema_version", "job_id", "status", "events", "error", "result"})

    def test_worker_redacts_exceptions_and_removes_only_job_secret(self):
        self.linux()
        job_id, directory = self.job_files()
        (directory / "owner-password").write_text(PASSWORD)
        preserved = directory / "unrelated-password"
        preserved.write_text("do-not-delete")
        b.write_json(directory / "request.json", {k: v for k, v in request().items() if k not in ("owner_password", "sudo_password")})
        fd = os.open(self.root / ".run.lock", os.O_RDWR)
        fake_fcntl = types.SimpleNamespace(LOCK_EX=2, LOCK_NB=4, flock=Mock())

        def fail_with_progress(directory, request, password, event):
            for _ in range(100):
                event("owner", "Credential-free progress.")
            raise RuntimeError("private exception: " + password)

        with patch("bootstrap.lock_held", return_value=True), \
                patch.dict(sys.modules, {"fcntl": fake_fcntl}), \
                patch("bootstrap.execute_setup", side_effect=fail_with_progress):
            b.worker(job_id, fd)
        result = b.read_json(directory / "status.json")
        self.assertEqual(result["status"], "failed")
        self.assertEqual(len(result["events"]), b.MAX_EVENTS)
        self.assertNotIn(PASSWORD, json.dumps(result))
        self.assertFalse((directory / "owner-password").exists())
        self.assertEqual(preserved.read_text(), "do-not-delete")

    def test_success_requires_real_result_contract(self):
        certificates = ssl.create_default_context().get_ca_certs(binary_form=True)
        if not certificates:
            self.skipTest("No platform public CA certificates available for result validation.")
        der = certificates[0]
        result = {"authentik_url": "https://192.0.2.10:9443", "ldap_url": "ldaps://192.0.2.10:636",
                  "owner_username": "owner", "owner_login_verified": True,
                  "root_certificate_pem": ssl.DER_cert_to_PEM_cert(der),
                  "root_fingerprint": hashlib.sha256(der).hexdigest()}
        b.validate_result(result)
        with self.assertRaises(b.SafeError):
            b.validate_result(result, configure_host=True)
        hosted = {**result, "host_url": "https://192.0.2.10", "host_ready": True,
                  "application_ready": True, "sso_verified": False}
        b.validate_result(hosted, configure_host=True)
        for changes in ({"host_ready": False}, {"application_ready": False}, {"host_url": "https://evil.invalid"},
                        {"sso_verified": True}, {"host_url": None}):
            with self.assertRaises(b.SafeError):
                b.validate_result({**hosted, **changes}, configure_host=True)
        for changes in ({"owner_login_verified": False}, {"root_fingerprint": "0" * 64},
                        {"authentik_url": "http://192.0.2.10:9443"}, {"ldap_url": "ldaps://owner:secret@host:636"}):
            with self.assertRaises(b.SafeError):
                b.validate_result({**result, **changes})

    def test_worker_persists_only_verified_flow_result_and_removes_request_secret(self):
        certificates = ssl.create_default_context().get_ca_certs(binary_form=True)
        if not certificates:
            self.skipTest("No platform public CA certificates available.")
        der = certificates[0]
        self.linux()
        self.state_files(complete=True, legacy=True)
        job_id, directory = self.job_files()
        legacy_password = "x\n"
        (directory / "owner-password").write_bytes(legacy_password.encode())
        result = {"authentik_url": "https://192.0.2.10:9443", "ldap_url": "ldaps://192.0.2.10:636",
                  "owner_username": "owner", "owner_login_verified": True,
                  "root_certificate_pem": ssl.DER_cert_to_PEM_cert(der),
                  "root_fingerprint": hashlib.sha256(der).hexdigest()}
        fake_fcntl = types.SimpleNamespace(LOCK_EX=2, LOCK_NB=4, flock=Mock())
        descriptor = os.open(self.root / ".run.lock", os.O_RDWR)
        with patch.dict(sys.modules, {"fcntl": fake_fcntl}), patch("bootstrap.lock_held", return_value=True), \
                patch("bootstrap.execute_setup", return_value=result) as execute:
            b.worker(job_id, descriptor)
        self.assertEqual(execute.call_args.args[2], legacy_password)
        self.assertEqual(set(execute.call_args.args[1]), b.REVIEW_FIELDS)
        self.assertFalse((directory / "owner-password").exists())
        status = b.job_status(job_id)
        self.assertEqual(status["status"], "succeeded")
        self.assertIsNone(status["error"])
        self.assertEqual(status["result"], result)
        self.assertNotIn("owner_password", json.dumps(status))
        status["result"]["owner_login_verified"] = False
        b.write_json(directory / "status.json", status)
        self.assertEqual(b.job_status(job_id)["status"], "failed", "An unverified result must never be returned as successful.")

    def test_prerequisites_require_approval_and_verified_origin(self):
        with patch("bootstrap.shutil.which", return_value=None), patch("bootstrap.download") as download:
            with self.assertRaises(b.SafeError):
                b.prerequisites(self.root, self.home, False, Mock())
        download.assert_not_called()
        for address in ("http://github.com/file", "https://evil.invalid/file", "https://github.com@evil.invalid/file"):
            with self.assertRaises(b.SafeError):
                b.approved_download_url(address)
        b.approved_download_url("https://builds.dotnet.microsoft.com/dotnet/file")
        b.approved_download_url("https://release-assets.githubusercontent.com/file")

    def test_prerequisite_hash_tamper_never_extracts_or_executes(self):
        metadata = {"releases": [{"sdks": [{"version": b.DOTNET_VERSION, "files": [{
            "rid": "linux-arm64", "name": "dotnet-sdk-linux-arm64.tar.gz",
            "url": "https://builds.dotnet.microsoft.com/dotnet/sdk.tar.gz", "hash": "0" * 128,
        }]}]}]}
        with patch("bootstrap.shutil.which", return_value=None), \
                patch("bootstrap.platform.machine", return_value="aarch64"), \
                patch("bootstrap.download", side_effect=[json.dumps(metadata).encode(), "1" * 128]), \
                patch("bootstrap.extract_distribution") as extract:
            with self.assertRaisesRegex(b.SafeError, "SHA-512"):
                b.prerequisites(self.root, self.home, True, Mock())
        extract.assert_not_called()
        self.assertFalse((self.root / "runtimes").exists())
        with patch("bootstrap.urllib.request.build_opener", return_value=Mock(open=Mock(side_effect=OSError(PASSWORD)))):
            with self.assertRaises(b.SafeError) as error:
                b.download("https://builds.dotnet.microsoft.com/dotnet/metadata.json", 1024)
        self.assertNotIn(PASSWORD, str(error.exception))
        self.assertIn("outbound HTTPS", str(error.exception))

    def test_legacy_worker_verifies_login_without_deploy_or_prerequisites(self):
        certificates = ssl.create_default_context().get_ca_certs(binary_form=True)
        if not certificates:
            self.skipTest("No platform public CA certificates available.")
        self.linux()
        settings = self.state_files(complete=True, legacy=True)
        der = certificates[0]
        (self.state / "trust").mkdir()
        (self.state / "trust/lucia-root-ca.crt").write_text(ssl.DER_cert_to_PEM_cert(der))
        (self.state / "trust/fingerprint.txt").write_text(hashlib.sha256(der).hexdigest())
        record = {"schema_version": 1, "username": "owner", "complete": True}
        calls = []

        class ExistingProvisioner:
            def __init__(instance, args):
                instance.state = pathlib.Path(args.state)
                instance.environment, instance.values, instance.settings = {}, {}, settings

            def load_existing(instance):
                calls.append("load")

            def verify(instance):
                calls.append("verify")

        fake_provision = types.SimpleNamespace(Provisioner=ExistingProvisioner)
        fake_owner = types.SimpleNamespace(load_owner=Mock(return_value=record), owner_identity=Mock(return_value=5),
                                           verify_login=Mock(), remove_request_credential=Mock())
        fake_fcntl = types.SimpleNamespace(LOCK_EX=2, LOCK_NB=4, flock=Mock())
        with patch.dict(sys.modules, {"provision": fake_provision, "owner": fake_owner, "fcntl": fake_fcntl}), \
                patch("bootstrap.prerequisites") as prerequisites, patch("bootstrap.stable_payload") as activate:
            result = b.execute_setup(self.home, {k: v for k, v in request().items() if k not in ("owner_password", "sudo_password")},
                                     "old", Mock())
        self.assertTrue(result["owner_login_verified"])
        self.assertEqual(calls, ["load", "verify"])
        self.assertEqual(fake_owner.verify_login.call_args.kwargs, {"password": "old"})
        prerequisites.assert_not_called()
        activate.assert_not_called()

    def test_fresh_worker_routes_password_and_uses_stable_payload(self):
        certificates = ssl.create_default_context().get_ca_certs(binary_form=True)
        if not certificates:
            self.skipTest("No platform public CA certificates available.")
        der = certificates[0]
        self.linux()
        settings = {"schema_version": 1, "certificate_mode": "private-ca", "public_host": "192.0.2.10",
                    "ldap_base_dn": "dc=lucia,dc=home,dc=arpa", "ports": {"authentik": 9443, "ldaps": 636, "ca": 9444},
                    "uid": os.getuid(), "gid": os.getgid()}
        calls = []
        record = {"schema_version": 1, "username": "owner", "complete": False}

        class FreshProvisioner:
            def __init__(instance, args):
                instance.state = pathlib.Path(args.state)
                instance.environment, instance.values, instance.settings = {}, {}, settings

            def prepare(instance):
                calls.append("prepare")
                b.write_json(instance.state / "settings.json", settings)

            def publish(instance):
                calls.append("publish")

            def deploy(instance):
                calls.append("deploy")

            def certificates(instance):
                calls.append("certificates")
                b.write_bytes(instance.state / "trust/lucia-root-ca.crt", ssl.DER_cert_to_PEM_cert(der).encode())
                b.write_bytes(instance.state / "trust/fingerprint.txt", hashlib.sha256(der).hexdigest().encode())

            def ldap(instance):
                calls.append("ldap")

            def api(instance, method, path):
                return {"pk": "ldap-source", "peer_certificate": "ca"}

            def verify(instance):
                calls.append("verify")

            def summary(instance):
                calls.append("summary")

        fake_provision = types.SimpleNamespace(
            Provisioner=FreshProvisioner, settings_for=Mock(return_value=settings),
            configure_authentik=Mock(side_effect=lambda p: calls.append("authentik")),
            sync_ldap=Mock(side_effect=lambda *args: calls.append("sync")),
        )
        fake_owner = types.SimpleNamespace(
            enroll_ldap_owner=Mock(side_effect=lambda *args, **kwargs: (calls.append("owner"), record)[1]),
            finish_owner=Mock(side_effect=lambda *args, **kwargs: calls.append("owner-login")),
        )
        fake_fcntl = types.SimpleNamespace(LOCK_EX=2, LOCK_NB=4, flock=Mock())
        with patch.dict(sys.modules, {"provision": fake_provision, "owner": fake_owner, "fcntl": fake_fcntl}), \
                patch("bootstrap.prerequisites", return_value={}) as prerequisites, \
                patch("bootstrap.stable_payload", return_value=self.root / "app") as activate:
            result = b.execute_setup(self.home, {k: v for k, v in request().items() if k not in ("owner_password", "sudo_password")},
                                     PASSWORD, Mock())
        self.assertTrue(result["owner_login_verified"])
        self.assertEqual(calls, ["prepare", "publish", "deploy", "certificates", "ldap", "authentik", "owner",
                                 "sync", "owner-login", "verify", "summary"])
        self.assertEqual(fake_owner.enroll_ldap_owner.call_args.kwargs, {"password": PASSWORD})
        self.assertEqual(fake_owner.finish_owner.call_args.kwargs, {"password": PASSWORD})
        self.assertEqual(fake_owner.enroll_ldap_owner.call_args.args[1], "owner")
        prerequisites.assert_called_once()
        self.assertTrue(prerequisites.call_args.args[2])
        activate.assert_called_once_with(self.root, self.home)
        self.assertFalse((self.state / "desktop-prepare.json").exists())
        self.assertNotIn(PASSWORD, (self.state / "settings.json").read_text())

    def test_host_repairs_run_after_owner_verification_even_in_verify_only_mode(self):
        certificates = ssl.create_default_context().get_ca_certs(binary_form=True)
        if not certificates:
            self.skipTest("No platform public CA certificates available.")
        self.linux()
        settings = self.state_files(complete=True)
        der = certificates[0]
        b.write_bytes(self.state / "trust/lucia-root-ca.crt", ssl.DER_cert_to_PEM_cert(der).encode())
        b.write_bytes(self.state / "trust/fingerprint.txt", hashlib.sha256(der).hexdigest().encode())
        calls = []
        record = {"schema_version": 1, "username": "owner", "complete": True}
        authentication = {"authority": "https://192.0.2.10:9443/application/o/lucia/", "client_id": "DummyFixture",
                          "client_secret_file": str(self.state / "secrets/host-oidc-client-secret"), "public_origin": "https://192.0.2.10"}
        b.write_json(self.state / "host-settings.json", {"authentication": authentication})

        class DummyFixtureProvisioner:
            def __init__(instance, args):
                instance.state, instance.settings = pathlib.Path(args.state), settings
                instance.values, instance.environment = {}, {}

            def load_existing(instance):
                calls.append("load")

            def verify(instance):
                calls.append("verify")

            def publish(instance):
                calls.append("publish")

            def deploy(instance):
                calls.append("deploy")

        fake_owner = types.SimpleNamespace(load_owner=Mock(return_value=record), owner_identity=Mock(return_value=5),
            verify_login=Mock(side_effect=lambda *a, **kw: calls.append("owner-login")), remove_request_credential=Mock())
        fake_app = types.SimpleNamespace(
            prepare_application=Mock(side_effect=lambda *a: (calls.append("prepare-app"), authentication)[1]),
            reconcile_application=Mock(side_effect=lambda *a: (calls.append("reconcile-app"), {"ready": True, "sso_verified": False})[1]))
        fake_host = types.SimpleNamespace(host_status=Mock(return_value={"configured": True, "image_present": True}), prepare_host=Mock())
        metadata = {k: v for k, v in request().items() if k in b.REVIEW_FIELDS}
        metadata.update(configure_host=True, verify_only=True, host_package=None, model_directory=None)
        with patch.dict(sys.modules, {"provision": types.SimpleNamespace(Provisioner=DummyFixtureProvisioner),
                                    "owner": fake_owner, "application": fake_app, "provision_host": fake_host}), \
                patch("bootstrap.prerequisites", return_value={}) as prerequisites, \
                patch("bootstrap.stable_payload", return_value=self.root / "app") as stable, \
                patch("bootstrap.host_probe", return_value=(False, False, "https://192.0.2.10", None)) as probe, \
                patch("bootstrap.retain_apphost_location"), \
                patch("bootstrap.wait_host", side_effect=lambda p: calls.append("health")):
            result = b.provision_locked(self.root, self.state, self.home, metadata, "old", True, Mock())
            self.assertEqual(calls, ["load", "verify", "owner-login", "prepare-app", "publish", "deploy", "health", "reconcile-app", "health"])
            self.assertTrue(result["host_ready"] and result["application_ready"])
            self.assertFalse(result["sso_verified"])
            prerequisites.assert_called_once()
            stable.assert_called_once()
            fake_host.prepare_host.assert_not_called()
            self.assertEqual(fake_owner.verify_login.call_args.kwargs, {"password": "old"})
            calls.clear()
            prerequisites.reset_mock()
            probe.return_value = (True, False, "https://192.0.2.10", None)
            b.provision_locked(self.root, self.state, self.home, metadata, "old", True, Mock(), host_ready=True)
            prerequisites.assert_not_called()
            self.assertEqual(calls, ["load", "verify", "owner-login", "prepare-app", "health", "reconcile-app", "health"],
                             "A healthy host still reconciles deleted registration, without requiring a deployment SDK.")
            calls.clear()
            fake_app.reconcile_application.return_value = {"ready": False}
            fake_app.reconcile_application.side_effect = None
            with self.assertRaises(b.SafeError):
                b.provision_locked(self.root, self.state, self.home, metadata, "old", True, Mock())

    def test_original_apphost_path_cannot_be_silently_relocated_or_overwritten(self):
        self.state.mkdir(parents=True)
        original = self.home / "legacy" / "Lucia.Homelab.Identity.AppHost.csproj"
        original.parent.mkdir()
        original.write_text("owned-but-old-project")
        (original.parent / "AppHost.cs").write_text("old apphost")
        b.write_bytes(self.state / "apphost-path.txt", str(original).encode())
        source = self.home / "reviewed"
        approved = source / b.APPHOST
        approved.parent.mkdir(parents=True)
        (approved.parent / "AppHost.cs").write_text("new managed host apphost")
        with self.assertRaises(b.SafeError):
            b.retain_apphost_location(types.SimpleNamespace(), self.state, source)
        self.assertEqual((original.parent / "AppHost.cs").read_text(), "old apphost")

    @unittest.skipUnless(sys.platform == "linux", "Native flock/session handoff requires Linux.")
    def test_native_linux_worker_survives_launcher_exit_without_deploying(self):
        upload = self.home / ".cache/lucia-desktop" / ("b" * 32)
        upload.mkdir(parents=True, mode=0o700)
        os.chmod(upload.parent, 0o700)
        archive = upload / "payload.tar.gz"
        digest = archive_at(archive)
        os.chmod(archive, 0o600)
        driver = self.home / "isolated-worker.py"
        driver.write_text("""
import os, pathlib, sys, time
scope = {"__name__": "isolated_bootstrap"}
exec(compile(pathlib.Path(sys.argv.pop(1)).read_text(), "bootstrap.py", "exec"), scope)
def offline(directory, request, password, event):
    event("offline", "Kernel-only fixture; no provisioning or external commands.")
    scope["write_json"](directory / "test-entered.json", {"pid": os.getpid(), "sid": os.getsid(0)})
    deadline = time.monotonic() + 30
    while not (directory / "test-release").exists() and time.monotonic() < deadline:
        time.sleep(0.02)
    raise RuntimeError("Deliberate offline fixture failure; no deployment was attempted.")
scope["execute_setup"] = offline
raise SystemExit(scope["main"]())
""")
        launcher = self.home / "isolated-launcher.py"
        launcher.write_text("""
import pathlib, subprocess, sys
scope = {"__name__": "isolated_bootstrap"}
exec(compile(pathlib.Path(sys.argv.pop(1)).read_text(), "bootstrap.py", "exec"), scope)
driver = sys.argv.pop(1)
scope["inspect"] = lambda *args: {"can_install": True, "public_host": None, "owner_username": None, "owner_ready": False}
original = subprocess.Popen
def launch(command, **kwargs):
    return original([command[0], "-B", driver, *command[1:]], **kwargs)
subprocess.Popen = launch
sys.argv.insert(1, "start")
raise SystemExit(scope["main"]())
""")
        directory = None
        environment = {
            **os.environ, "HOME": str(self.home), "PYTHONDONTWRITEBYTECODE": "1",
            "PATH": str(self.home / "no-external-programs"),
            "DOCKER_HOST": "unix://" + str(self.home / "no-docker-daemon.sock"),
            "DOCKER_CONFIG": str(self.home / "docker-config"),
        }
        environment.pop("DOCKER_CONTEXT", None)
        try:
            launched = subprocess.run(
                [sys.executable, "-B", str(launcher), str(ROOT / "tools/desktop/bootstrap.py"), str(driver),
                 "--archive", str(archive), "--sha256", digest],
                input=json.dumps(request()) + "\n", stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                text=True, timeout=15, env=environment,
            )
            self.assertEqual(launched.returncode, 0, launched.stderr)
            self.assertNotIn(PASSWORD, launched.stdout + launched.stderr)
            started = json.loads(launched.stdout)
            directory = self.root / "jobs" / started["job_id"]
            entered = directory / "test-entered.json"
            deadline = time.monotonic() + 10
            while not entered.exists() and time.monotonic() < deadline:
                time.sleep(0.02)
            self.assertTrue(entered.exists(), "The isolated worker did not enter its fake provisioning function.")
            identity = b.read_json(entered)
            self.assertEqual(identity["sid"], identity["pid"], "The worker must lead its own session.")
            self.assertNotEqual(identity["sid"], os.getsid(0))
            self.assertEqual(b.job_status(started["job_id"])["status"], "running",
                             "The actual worker lock must survive exit of the launcher process.")
            with patch("bootstrap.run_read", side_effect=self.command), \
                    patch("bootstrap.shutil.which", side_effect=lambda name, **kwargs: name):
                inspection = b.inspect()
            self.assertEqual(inspection["active_job_id"], started["job_id"])
            self.assertEqual(inspection["public_host"], request()["public_host"])
            self.assertEqual(inspection["owner_username"], request()["owner_username"])
            self.assertFalse(inspection["owner_ready"])
            with patch("bootstrap.inspect", return_value={
                "can_install": True, "public_host": None, "owner_username": None, "owner_ready": False,
            }):
                duplicate = b.start(self.home / "intentionally-absent.tar.gz", "0" * 64, request())
            self.assertEqual(duplicate["job_id"], started["job_id"], "The native flock must prevent another worker.")
            (directory / "test-release").touch()
            deadline = time.monotonic() + 10
            while b.job_status(started["job_id"])["status"] == "running" and time.monotonic() < deadline:
                time.sleep(0.02)
            result = b.job_status(started["job_id"])
            self.assertEqual(result["status"], "failed")
            self.assertIsNone(result["result"], "A kernel-only probe must never claim successful identity setup.")
            self.assertFalse((directory / "owner-password").exists())
            self.assertFalse(self.state.exists(), "The probe must not create even its isolated identity state.")
        finally:
            if directory is None and (self.root / "active.json").exists():
                directory = self.root / "jobs" / b.read_json(self.root / "active.json")["job_id"]
            if directory and directory.exists():
                (directory / "test-release").touch()
                worker = b.read_json(directory / "worker.json")
                token = worker.get("start_ticks")
                deadline = time.monotonic() + 5
                while token and b.process_identity(worker["pid"]) == token and time.monotonic() < deadline:
                    time.sleep(0.02)
                if token and b.process_identity(worker["pid"]) == token:
                    try:
                        os.kill(worker["pid"], signal.SIGTERM)
                    except ProcessLookupError:
                        pass


if __name__ == "__main__":
    unittest.main(verbosity=2)
