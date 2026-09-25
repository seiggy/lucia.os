"""Offline stdlib regressions. No real Docker, SSH, deployment, or credentials.

python -B -m unittest discover -s tools/host -v
Set LUCIA_TEST_APPHOST=1 to additionally publish both optional-resource variants
locally through Aspire --non-interactive, using disposable fixture settings.
"""

import copy
import hashlib
import io
import json
import os
import pathlib
import re
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch

import package as artifact
import provision_host as host


class FakeProvisioner:
    def __init__(self, root):
        self.state = root / "identity"
        self.state.mkdir(mode=0o700)
        for directory in ("secrets", "trust", "gateway", "config", "certificates", "ca", "ldap/data", "ldap/config"):
            (self.state / directory).mkdir(parents=True, mode=0o700)
        self.settings = {
            "schema_version": 1, "certificate_mode": "private-ca", "public_host": "192.0.2.10",
            "ports": {"authentik": 9443, "ldaps": 636, "ca": 9444}, "ldap_base_dn": "dc=lucia,dc=home,dc=arpa",
            "uid": os.getuid() if hasattr(os, "getuid") else 1000,
            "gid": os.getgid() if hasattr(os, "getgid") else 1000,
        }
        (self.state / "settings.json").write_bytes(artifact.json_bytes(self.settings))
        (self.state / "trust/lucia-root-ca.crt").write_text("fixture-public-CA-not-a-real-certificate")
        secret = self.state / "secrets/oidc-client"
        secret.write_text("fixture-OIDC-secret-not-a-real-credential")
        secret.chmod(0o600)
        self.authentication = {
            "authority": "https://192.0.2.10:9443/application/o/lucia/",
            "client_id": "fixture-client", "client_secret_file": str(secret), "public_origin": "https://192.0.2.10",
        }
        self.values = {}
        self.calls = []
        self.images = {}
        self.tags = {}
        self.container = None
        self.network_id = "b" * 64
        self.network_name = "aspire-identity-198a1597_aspire"
        self.project_name = "aspire-identity-198a1597"
        self.builds = 0

    def run(self, command, **kwargs):
        self.calls.append(command)
        output = ""
        if command[:3] == ["docker", "image", "ls"]:
            if "--filter" in command:
                tag = command[-1].removeprefix("reference=")
                output = self.tags.get(tag, "")
            else:
                output = "\n".join(self.images)
        elif command[:3] == ["docker", "image", "inspect"]:
            reference = command[3]
            output = json.dumps(self.images[self.tags.get(reference, reference)])
        elif command[:2] == ["docker", "build"]:
            self.builds += 1
            labels = dict(command[i + 1].split("=", 1) for i, value in enumerate(command) if value == "--label")
            image_id = "sha256:" + "a" * 64
            self.images[image_id] = {"id": image_id, "architecture": "arm64", "os": "linux", "labels": labels}
            self.tags[command[command.index("--tag") + 1]] = image_id
        elif command[:2] == ["docker", "ps"]:
            output = "fixture-container" if self.container is not None else ""
        elif command[:3] == ["docker", "inspect", host.CONTAINER]:
            output = json.dumps(self.container)
        elif command[:3] == ["docker", "inspect", "lucia-identity-gateway"]:
            output = json.dumps({
                "labels": {"io.lucia.component": "identity", "com.docker.compose.project": self.project_name},
                "networks": {self.network_name: {"NetworkID": self.network_id}},
            })
        elif command[:3] == ["docker", "network", "inspect"]:
            assert command[3] == self.network_name
            output = json.dumps([{
                "Id": self.network_id, "Driver": "bridge",
                "Labels": {"com.docker.compose.project": self.project_name},
                "IPAM": {"Config": [{"Subnet": "172.24.0.0/16"}]},
            }])
        elif command == ["nvidia-ctk", "cdi", "list"]:
            output = "nvidia.com/gpu=all\nnvidia.com/gpu=0\n"
        elif command[0] == "nvidia-smi":
            output = "Fixture GB10, GPU-fixture, fixture-driver, 128000\n"
        else:
            raise AssertionError("Unexpected external operation: " + repr(command))
        return subprocess.CompletedProcess(command, 0, output, "")


class HostTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="lucia-host-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = pathlib.Path(self.temporary.name)
        self.publish = self.root / "publish"
        (self.publish / "wwwroot/assets").mkdir(parents=True)
        (self.publish / artifact.ENTRYPOINT).write_bytes(b"fixture-managed-assembly")
        (self.publish / "Microsoft.IdentityModel.Tokens.dll").write_bytes(b"fixture-managed-token-assembly")
        (self.publish / "Lucia.Homelab.Server").write_bytes(b"fixture-arm64-apphost")
        (self.publish / "libGgmlOps.so").write_bytes(b"fixture-native-library")
        (self.publish / "Magick.Native-Q8-arm64.dll.so").write_bytes(b"fixture-magick-native-library")
        (self.publish / "wwwroot/index.html").write_text("<html>Fixture SPA</html>")
        (self.publish / "wwwroot/assets/app.js").write_text("console.log('fixture')")
        self.archive = self.root / "host.tar.gz"
        self.manifest_path = pathlib.Path(str(self.archive) + ".manifest.json")
        self.manifest = artifact.package(self.publish, self.archive)
        self.provisioner = FakeProvisioner(self.root)
        self.host_root = self.root / "host"
        self.addCleanup(patch.stopall)
        patch.object(host, "HOST_ROOT", self.host_root).start()
        patch.object(host.secrets, "token_urlsafe", return_value="fixture-machine-key-not-a-real-credential-00000000").start()

    def prepare(self, **kwargs):
        return host.prepare_host(self.provisioner, self.archive, self.manifest_path,
                                 self.provisioner.authentication, **kwargs)

    def rewrite_archive(self, members):
        with tarfile.open(self.archive, "w:gz") as tar:
            for member, content in members:
                tar.addfile(member, io.BytesIO(content) if content is not None else None)
        self.manifest.update(sha256=artifact.sha256_file(self.archive), size=self.archive.stat().st_size)
        self.manifest_path.write_bytes(artifact.json_bytes(self.manifest))

    def test_deterministic_allowlist_and_permissions(self):
        excluded = (".env", "appsettings.json", "appsettings.Production.json", "owner.key", "server.pfx",
                    "model.gguf", "weights.safetensors", "weights.bin", "NuGet.Config", "source.cs",
                    "wwwroot/credentials.json", "obj/private.dll", "bin/private.dll", "models/weights.dll")
        for name in excluded:
            path = self.publish / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("not-package-content")
        for file in self.publish.rglob("*"):
            if file.is_file():
                os.utime(file, (123456, 123456))
        second = self.root / "second.tar.gz"
        manifest = artifact.package(self.publish, second)
        self.assertEqual(self.manifest, manifest)
        self.assertEqual(self.archive.read_bytes(), second.read_bytes())
        files = {entry["path"]: entry for entry in manifest["files"]}
        self.assertIn("Microsoft.IdentityModel.Tokens.dll", files)
        self.assertEqual(set(manifest), {"schema_version", "rid", "version", "sha256", "size", "entrypoint", "files"})
        self.assertTrue(all(set(entry) == {"path", "sha256", "size"} for entry in files.values()))
        with tarfile.open(second, "r:gz") as tar:
            self.assertEqual(tar.getmember("Lucia.Homelab.Server").mode, 0o755)
            self.assertEqual(tar.getmember("libGgmlOps.so").mode, 0o755)
            self.assertEqual(tar.getmember("Magick.Native-Q8-arm64.dll.so").mode, 0o755)
            self.assertEqual(tar.getmember(artifact.ENTRYPOINT).mode, 0o644)
        self.assertFalse(set(excluded) & files.keys())

    def test_desktop_payload_cli_and_manifest_shape(self):
        archive = self.root / "HostPayload/host-linux-arm64.tar.gz"
        manifest = archive.with_name("manifest.json")
        result = subprocess.run(
            [sys.executable, "-B", str(pathlib.Path(artifact.__file__)), str(self.publish),
             str(archive), "--manifest", str(manifest)],
            text=True, encoding="utf-8", stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(artifact.load_manifest(manifest), self.manifest)
        self.assertEqual(json.loads(result.stdout),
                         {"sha256": self.manifest["sha256"], "size": self.manifest["size"], "files": len(self.manifest["files"])})
        self.assertEqual({item.name for item in archive.parent.iterdir()}, {"host-linux-arm64.tar.gz", "manifest.json"})
        self.assertEqual(self.archive.read_bytes(), archive.read_bytes())

    def test_invalid_paths_manifest_and_missing_entrypoint(self):
        for name in ("../escape.dll", "/escape.dll", "C:/escape.dll", "wwwroot\\index.html",
                     "wwwroot//index.html", "./index.html", "wwwroot/../index.html"):
            with self.subTest(name=name), self.assertRaises(artifact.HostError):
                artifact.canonical_path(name)
        for change in (
            lambda m: m.update(rid="linux-x64"), lambda m: m.update(schema_version=True),
            lambda m: m["files"][0].update(mode=0o777), lambda m: m["files"].append(copy.deepcopy(m["files"][0])),
            lambda m: m["files"][0].update(path="model.gguf"),
            lambda m: m["files"][0].update(size=artifact.MAX_FILE + 1),
        ):
            invalid = copy.deepcopy(self.manifest)
            change(invalid)
            with self.assertRaises(artifact.HostError):
                artifact.validate_manifest(invalid)
        (self.publish / "wwwroot/index.html").unlink()
        with self.assertRaises(artifact.HostError):
            artifact.package(self.publish, self.root / "missing.tar.gz")

    def test_packager_rejects_symlinks_and_output_collisions(self):
        with self.assertRaises(artifact.HostError):
            artifact.package(self.publish, self.archive)
        with self.assertRaises(artifact.HostError):
            artifact.package(self.publish, self.publish / "bad.tar.gz")
        link = self.publish / "linked.dll"
        try:
            link.symlink_to(self.publish / artifact.ENTRYPOINT)
        except OSError:
            # Windows developer mode is not required to test rejection of a link provider.
            original = pathlib.Path.is_symlink
            with patch.object(pathlib.Path, "is_symlink", lambda p: p == link or original(p)):
                with self.assertRaises(artifact.HostError):
                    artifact.no_links(link)
        else:
            with self.assertRaises(artifact.HostError):
                artifact.package(self.publish, self.root / "linked.tar.gz")

    def test_archive_hash_and_file_hash(self):
        self.archive.write_bytes(self.archive.read_bytes()[:-1] + b"x")
        with self.assertRaisesRegex(artifact.HostError, "SHA-256"):
            host.extract_release(self.archive, self.manifest_path, self.host_root)
        self.archive.unlink()
        self.manifest_path.unlink()
        self.manifest = artifact.package(self.publish, self.archive)
        self.manifest["files"][0]["sha256"] = "f" * 64
        self.manifest_path.write_bytes(artifact.json_bytes(self.manifest))
        with self.assertRaisesRegex(artifact.HostError, "member failed"):
            host.extract_release(self.archive, self.manifest_path, self.host_root)

    def test_archive_traversal_links_duplicates_and_modes(self):
        for kind in ("traversal", "symlink", "hardlink", "duplicate", "mode"):
            header = tarfile.TarInfo("../escape.dll" if kind == "traversal" else artifact.ENTRYPOINT)
            header.mode = 0o777 if kind == "mode" else 0o644
            content = (self.publish / artifact.ENTRYPOINT).read_bytes()
            if kind in ("symlink", "hardlink"):
                header.type = tarfile.SYMTYPE if kind == "symlink" else tarfile.LNKTYPE
                header.linkname = "../escape.dll"
                content = None
            else:
                header.size = len(content)
            members = [(header, content)] * (2 if kind == "duplicate" else 1)
            self.rewrite_archive(members)
            with self.subTest(kind=kind), self.assertRaises(artifact.HostError):
                host.extract_release(self.archive, self.manifest_path, self.host_root)
            self.assertFalse((self.root / "escape.dll").exists())

    def test_release_reuse_and_tampering(self):
        release, _ = host.extract_release(self.archive, self.manifest_path, self.host_root)
        second, _ = host.extract_release(self.archive, self.manifest_path, self.host_root)
        self.assertEqual(release, second)
        victim = release / "publish" / artifact.ENTRYPOINT
        victim.chmod(0o600)
        victim.write_text("tampered")
        with self.assertRaisesRegex(artifact.HostError, "immutable"):
            host.extract_release(self.archive, self.manifest_path, self.host_root)

    def test_immutable_release_rename_stays_in_same_parent(self):
        rename = os.rename

        def same_parent(source, destination):
            self.assertEqual(pathlib.Path(source).parent, pathlib.Path(destination).parent)
            return rename(source, destination)

        with patch.object(host.os, "rename", side_effect=same_parent):
            host.extract_release(self.archive, self.manifest_path, self.host_root)

    def test_prepare_preserves_keys_models_data_and_exact_image(self):
        legacy = self.root / "existing-models"
        legacy.mkdir()
        sentinel = legacy / "existing.gguf"
        sentinel.write_text("existing-model-do-not-copy-or-delete")
        first = self.prepare(model_directory=str(legacy))
        keys = {name: (self.provisioner.state / "secrets" / name).read_bytes() for name in host.KEY_NAMES}
        data = pathlib.Path(first["data_directory"]) / "data-protection/key.xml"
        data.write_text("persistent-data-fixture")
        second = self.prepare()
        self.assertEqual(first, second)
        self.assertEqual(self.provisioner.builds, 1)
        self.assertEqual(keys, {name: (self.provisioner.state / "secrets" / name).read_bytes() for name in host.KEY_NAMES})
        self.assertEqual(sentinel.read_text(), "existing-model-do-not-copy-or-delete")
        self.assertEqual(data.read_text(), "persistent-data-fixture")
        self.assertEqual(first["context_tokens"], 8192)
        self.assertEqual(first["voice_reserve_gib"], 8)
        self.assertEqual(first["trusted_proxy_networks"], ["172.24.0.0/16"])
        self.assertEqual(first["identity_network"]["name"], self.provisioner.network_name)
        self.provisioner.tags.clear()
        self.prepare()
        self.assertEqual(self.provisioner.builds, 1, "An existing exact untagged image must not be rebuilt.")
        self.assertFalse(any("compose" in command or "stop" in command or "deploy" in command for command in self.provisioner.calls))
        with self.assertRaisesRegex(artifact.HostError, "Model directory changed"):
            self.prepare(model_directory=str(self.root / "replacement-models"))
        (self.provisioner.state / "secrets" / host.KEY_NAMES[0]).unlink()
        with self.assertRaisesRegex(artifact.HostError, "never regenerate"):
            self.prepare()

    def test_owned_image_identity_and_collision(self):
        record = self.prepare()
        image = self.provisioner.images[record["image_id"]]
        image["labels"]["io.lucia.component"] = "someone-else"
        with self.assertRaisesRegex(artifact.HostError, "unowned"):
            self.prepare()
        image["labels"]["io.lucia.component"] = host.COMPONENT
        image["id"] = "sha256:" + "c" * 64
        with self.assertRaisesRegex(artifact.HostError, "changed identity"):
            self.prepare()

    def test_interrupted_build_preserves_explicit_model_directory(self):
        models = self.root / "legacy-models"
        models.mkdir()
        original = self.provisioner.run

        def fail_build(command, **kwargs):
            if command[:2] == ["docker", "build"]:
                raise artifact.HostError("Fixture build interruption.")
            return original(command, **kwargs)

        with patch.object(self.provisioner, "run", side_effect=fail_build):
            with self.assertRaisesRegex(artifact.HostError, "interruption"):
                self.prepare(model_directory=str(models))
        record = self.prepare()
        self.assertEqual(record["model_directory"], str(models))
        self.assertEqual(self.provisioner.builds, 1)

    def test_unowned_state_and_container_collision(self):
        self.host_root.mkdir(mode=0o700)
        with self.assertRaisesRegex(artifact.HostError, "unowned"):
            self.prepare()
        self.host_root.rmdir()
        self.provisioner.container = {"labels": {"io.lucia.component": "unrelated"}}
        with self.assertRaisesRegex(artifact.HostError, "another deployment"):
            self.prepare()
        self.assertEqual(self.provisioner.builds, 0)

    def test_config_validation_and_status_are_read_only(self):
        with_slash = dict(self.provisioner.authentication, public_origin="https://192.0.2.10/")
        self.assertEqual(host.validate_authentication(with_slash, self.provisioner.settings), self.provisioner.authentication)
        for key, value in (("authority", "http://192.0.2.10:9443/application/o/lucia/"),
                           ("public_origin", "https://other-host"), ("client_id", "bad\nclient"),
                           ("client_secret_file", "../secret")):
            invalid = dict(self.provisioner.authentication, **{key: value})
            with self.subTest(key=key), self.assertRaises(artifact.HostError):
                host.validate_authentication(invalid, self.provisioner.settings)
        for networks in ([], ["0.0.0.0/0"], ["::/0"], ["127.0.0.0/8"], ["8.8.8.0/24"], ["172.24.0.1/16"], [None]):
            with self.subTest(networks=networks), self.assertRaises(artifact.HostError):
                host.validate_subnets(networks)
        record = self.prepare()
        for key, value in (("voice_reserve_gib", 0), ("context_tokens", True), ("image_id", "latest"),
                           ("data_directory", str(self.root / "elsewhere"))):
            invalid = dict(record, **{key: value})
            with self.subTest(key=key), self.assertRaises(artifact.HostError):
                host.validate_settings(invalid, self.provisioner.settings, self.provisioner.state)
        self.provisioner.container = {"image_id": record["image_id"], "running": True, "status": "running", "health": "healthy",
            "labels": {"io.lucia.component": host.COMPONENT, "io.lucia.host-installation": record["installation_id"]}}
        self.provisioner.calls.clear()
        with patch.object(host, "write_private", side_effect=AssertionError("Status must be read-only")):
            status = host.host_status(self.provisioner)
        self.assertTrue(status["expected_image_running"])
        self.assertTrue(status["gpu"]["available"])
        self.assertEqual(status["model_readiness"], "not_checked")
        self.assertNotIn("fixture-machine-key", json.dumps(status))
        self.assertNotIn("fixture-OIDC-secret", json.dumps(status))
        self.assertFalse(any("build" in command for command in self.provisioner.calls))

    def test_missing_settings_and_sensitive_model_path_fail_closed(self):
        with self.assertRaisesRegex(artifact.HostError, "separate"):
            self.prepare(model_directory=str(self.provisioner.state / "secrets"))
        record = self.prepare()
        (self.provisioner.state / "host-settings.json").unlink()
        with self.assertRaisesRegex(artifact.HostError, "settings are missing"):
            self.prepare()
        (self.provisioner.state / "host-settings.json").write_bytes(artifact.json_bytes(record))
        pathlib.Path(record["data_directory"], "data-protection").rmdir()
        with self.assertRaisesRegex(artifact.HostError, "data is missing"):
            self.prepare()

    @unittest.skipUnless(os.environ.get("LUCIA_TEST_APPHOST") == "1", "Opt-in local Aspire publication")
    def test_apphost_optional_publish_preview(self):
        record = self.prepare()
        environment = os.environ.copy()
        environment["LUCIA_IDENTITY_STATE"] = str(self.provisioner.state)
        project = host.ROOT / "src/Lucia.Homelab.Identity.AppHost/Lucia.Homelab.Identity.AppHost.csproj"

        def publish(name, success=True):
            output = self.root / name
            result = subprocess.run(
                ["aspire", "publish", "--apphost", str(project), "--output-path", str(output),
                 "--environment", "Production", "--non-interactive"],
                env=environment, cwd=self.root, text=True, encoding="utf-8",
                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
            if success:
                self.assertEqual(result.returncode, 0, result.stdout)
                return (output / "docker-compose.yaml").read_text()
            self.assertNotEqual(result.returncode, 0, "Invalid managed host config was accepted.")
            return result.stdout

        enabled = publish("enabled")
        self.assertIn("nvidia.com/gpu=all", enabled)
        self.assertIn("lucia-homelab-host", enabled)
        self.assertIn(record["image_id"], enabled)
        self.assertIn("0.0.0.0:443:8444", enabled)
        self.assertNotIn("8080:8080", enabled)
        self.assertNotIn("runtime: nvidia", enabled)
        self.assertIn("HostAuthentication__TrustedProxyNetworks__0", enabled)
        self.assertIn("sha256sum -c ready.sha256", enabled)
        self.assertNotIn("fixture-machine-key", enabled)
        self.assertNotIn("fixture-OIDC-secret", enabled)
        service = re.search(r"(?ms)^  lucia-host:\n(.*?)(?=^  [a-z]|\Z)", enabled)
        self.assertIsNotNone(service, enabled)
        self.assertNotIn("ports:", service[1], "Backend must have no published ports.")
        self.assertNotIn("depends_on:", service[1], "Host startup must not depend on OIDC discovery.")
        self.assertIn("http://127.0.0.1:8080/health/live", service[1])
        self.assertNotIn("http://127.0.0.1:8080/api/auth/session", service[1],
                         "Production auth endpoints require the public HTTPS origin, even for health checks.")
        self.assertNotIn("extra_hosts:", service[1], "IP literals do not need a host-gateway alias.")
        self.assertIn('pull_policy: "never"', service[1])
        self.assertIn('restart: "unless-stopped"', service[1])
        self.assertIn('    networks:\n      - "aspire"', service[1])
        self.assertIn('    devices:\n      - "nvidia.com/gpu=all"', service[1])
        self.assertIn(f'    user: "{self.provisioner.settings["uid"]}:{self.provisioner.settings["gid"]}"', service[1])
        invalid = dict(record, trusted_proxy_networks=["0.0.0.0/0"])
        (self.provisioner.state / "host-settings.json").write_bytes(artifact.json_bytes(invalid))
        publish("invalid", success=False)
        (self.provisioner.state / "host-settings.json").unlink()
        disabled = publish("disabled")
        self.assertNotIn("lucia-homelab-host", disabled)
        self.assertNotIn("443:8444", disabled)
        self.assertNotIn("entrypoints.host.", disabled)
        self.assertIn("sha256sum -c ready.sha256", disabled)
        for port in ("9443:8443", "636:8636", "9444:9000"):
            self.assertIn(port, disabled)
        for resource in ("identity-db", "identity-ca", "identity-ldap", "identity-server", "identity-worker", "identity-renewer"):
            pattern = rf"(?ms)^  {resource}:\n(.*?)(?=^  [a-z]|\Z)"
            self.assertEqual(re.search(pattern, enabled)[1], re.search(pattern, disabled)[1], resource)
        for public_host, origin_host, needs_alias in (
                ("spark-9423", "spark-9423", True), ("2001:db8::10", "[2001:db8::10]", False)):
            self.provisioner.settings["public_host"] = public_host
            (self.provisioner.state / "settings.json").write_bytes(artifact.json_bytes(self.provisioner.settings))
            configured = copy.deepcopy(record)
            configured["authentication"].update(public_origin=f"https://{origin_host}",
                                                authority=f"https://{origin_host}:9443/application/o/lucia/")
            (self.provisioner.state / "host-settings.json").write_bytes(artifact.json_bytes(configured))
            preview = publish("dns-host" if needs_alias else "ipv6-host")
            service = re.search(r"(?ms)^  lucia-host:\n(.*?)(?=^  [a-z]|\Z)", preview)[1]
            if needs_alias:
                self.assertIn('    extra_hosts:\n      spark-9423: "host-gateway"', service)
            else:
                self.assertNotIn("extra_hosts:", service)


if __name__ == "__main__":
    unittest.main()
