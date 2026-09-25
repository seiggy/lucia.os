#!/usr/bin/env python3
"""Disposable, real step-CA/OpenLDAP enrollment qualification; no production state.

  python3 tools/nodes/qualify_native.py check
  python3 tools/nodes/qualify_native.py run
  python3 tools/nodes/qualify_native.py run --output-dir .node-native-fixture --keep-containers

Run on Linux as a non-root Docker-capable user, from a trusted private checkout.
Requires the three exact cached AppHost image digests; NEVER pulls images.
Creates an internal network, three containers with no published ports and fresh
private fixture state BELOW cwd. No actual lucia-identity-* container is addressed.
Production enrollment_worker.py is imported unchanged. Only its fixture subclass
maps container names to IDs created here and redirects the fixed host-data root.
CA signing, LDAP operations and certificate verification are REAL. The explicit
prepare_directory.py helper installs ONLY its scoped node-reader prefix in this
disposable suffix, retaining every original ACL; production is never prepared.

Default teardown removes ONLY this run's containers/network and fixture directory.
--output-dir must name a NEW relative directory; it preserves private artifacts.
--keep-containers additionally holds the foreground runner for at most one hour
after qualification, for same-network VM tests; SIGINT/SIGTERM/exit tears down.
No restart policy, detached runner or host ports. The private fixture.json records
generated IDs and file paths; never publish it or the retained directory.
Strict password/read-only verification remains unchanged; no ACL is relaxed.

Python reuse inside a parent-owned Linux test process:
  with Run(worker_module, new_private_path_below_cwd, preserve=True) as fixture:
      fixture.qualify()
      # Use fixture.containers/network/state/data while this context is alive.
Gateway LDAPS is identity-gateway:8636, CA HTTPS identity-ca:9000 on that internal
network; there are NO host ports and NO enrollment HTTP API. The parent must
provide VM routing/DNS and its own application API. Exit always tears down.
"""

import argparse
import ast
import contextlib
import datetime
import hashlib
import importlib.util
import json
import os
import pathlib
import re
import secrets
import shutil
import signal
import subprocess
import sys
import time
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[2]
IMAGES = {
    "ca": "smallstep/step-ca@sha256:a2b17872915c193259b75a5474c398326f41bd199f0842093e52cf4182bc8270",
    "ldap": "vegardit/openldap@sha256:b81f6c360830b21b9e6d8878563e3e11bd81e243693c83fa371efeee34556ce9",
    "gateway": "traefik@sha256:31267173a15b4944e797a76ffd9c419707c8d8b32fe5b610f80cd0cfa05f372d",
}
LABEL = "io.lucia.node-qualification"
BASE = "dc=lucia,dc=home,dc=arpa"
HOST = "identity-gateway"
UTC = datetime.timezone.utc


class QualificationError(RuntimeError):
    def __init__(self, code):
        self.code = code
        super().__init__(code)


def need(condition, code):
    if not condition:
        raise QualificationError(code)


def emit(**values):
    print(json.dumps(values, separators=(",", ":")), flush=True)


def route(command, containers):
    """Deny every Docker target except explicit IDs created by this runner."""
    command = list(command)
    if command[0] == "openssl":
        return command
    need(command[:2] in (["docker", "exec"], ["docker", "inspect"]), "unexpected-worker-command")
    allowed = {"lucia-identity-ca": containers["ca"], "lucia-identity-ldap": containers["ldap"]}
    targets = [part for part in command if part in allowed]
    need(len(targets) == 1, "unmapped-worker-container")
    command[command.index(targets[0])] = allowed[targets[0]]
    return command


def output_path(value):
    path = pathlib.Path(value)
    need(not path.is_absolute() and bool(path.parts) and not any(part in (".", "..") for part in path.parts),
         "output-directory-must-be-new-relative-child")
    return pathlib.Path.cwd() / path


def directory_policy():
    spec = importlib.util.spec_from_file_location("prepare_directory", pathlib.Path(__file__).with_name("prepare_directory.py"))
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    return helper


def ldap_summary(command, result):
    """Only allowlisted stage names, counts and booleans; never LDIF values."""
    def argument(flag):
        return command[command.index(flag) + 1] if flag in command else ""
    tool = next((name for name in ("ldapsearch", "ldapadd", "ldapmodify", "ldapwhoami") if name in command), None)
    if tool is None:
        return None
    context = "external" if "EXTERNAL" in command else "config-admin"
    if tool == "ldapsearch" and argument("-b") == "cn=config":
        operation = "ldap-config-search-" + context
    elif tool == "ldapmodify" and ("EXTERNAL" in command or argument("-D") == "uid=admin," + BASE):
        operation = "ldap-config-modify-" + context
    elif tool != "ldapsearch":
        operation = {"ldapadd": "ldap-add", "ldapmodify": "ldap-modify", "ldapwhoami": "ldap-bind"}[tool]
    elif argument("-D") == "uid=admin," + BASE:
        operation = "ldap-admin-search"
    elif argument("-b") == "ou=Users," + BASE:
        operation = "reader-users-container" if argument("-s") == "base" else "reader-user-entries"
    elif argument("-b") == "ou=Groups," + BASE:
        operation = "reader-groups-container"
    elif argument("-b") == "cn=lucia-owners,ou=Groups," + BASE:
        operation = "reader-owner-group"
    elif argument("-b") == argument("-D"):
        operation = "reader-self-entry"
    else:
        operation = "reader-specific-entry"
    return {
        "operation": operation, "exitCode": result.returncode,
        "entryCount": len(re.findall(r"(?im)^dn:", result.stdout)),
        "passwordAttributeVisible": re.search(r"(?im)^userPassword(?:;[^:\r\n]+)?:", result.stdout) is not None,
        "uidAttributeVisible": re.search(r"(?im)^uid:", result.stdout) is not None,
        "ouAttributeVisible": re.search(r"(?im)^ou:", result.stdout) is not None,
    }


def check():
    """Offline command-boundary checks; safe on Windows, no Docker calls."""
    source = (ROOT / "src" / "Lucia.Homelab.Identity.AppHost" / "AppHost.cs").read_text()
    for image in IMAGES.values():
        need(image.split("@sha256:")[1] in source, "pinned-image-drift")
    fake = {"ca": "a" * 64, "ldap": "b" * 64}
    assert route(["docker", "exec", "lucia-identity-ca", "step", "ca", "health"], fake)[2] == fake["ca"]
    assert route(["docker", "inspect", "--format", "{}", "lucia-identity-ldap"], fake)[-1] == fake["ldap"]
    assert route(["openssl", "version"], fake) == ["openssl", "version"]
    for command in (["docker", "exec", "actual-production-id", "ldapadd"], ["docker", "rm", "lucia-identity-ca"],
                    ["sh", "-c", "anything"], ["docker", "inspect", "lucia-identity-gateway"]):
        try:
            route(command, fake)
        except QualificationError:
            continue
        raise AssertionError("Unowned container command escaped.")
    for value in ("../escape", str(pathlib.Path.cwd())):
        try:
            output_path(value)
        except QualificationError:
            continue
        raise AssertionError("Unconfined output directory accepted.")
    secret = "qualification-secret-must-not-appear"
    summary = ldap_summary(
        ["ldapsearch", "-D", "uid=node-safe,ou=Services," + BASE, "-b", "ou=Users," + BASE, "-s", "one"],
        subprocess.CompletedProcess([], 0, "dn: uid=" + secret + "\nuserPassword:: " + secret + "\nuid: " + secret, secret))
    assert summary["operation"] == "reader-user-entries" and summary["passwordAttributeVisible"]
    assert summary["entryCount"] == 1 and secret not in json.dumps(summary)
    harness = Run(None, pathlib.Path.cwd() / ".unused-native-check", False)
    harness.network = "d" * 64
    commands = []
    def capture(command, **kwargs):
        commands.append(command)
        return subprocess.CompletedProcess(command, 0, "c" * 64, "")
    harness.command = capture
    for kind in IMAGES:
        harness.create(kind, [])
        command = commands[-2]
        assert command[:3] == ["docker", "create", "--pull=never"]
        assert "--restart=no" in command and "--publish" not in command and "-p" not in command
        assert command[command.index("--network") + 1] == harness.network
        if kind == "ca":
            assert command[-2:] == [IMAGES[kind], "/config/start-ca.sh"]
        elif kind == "gateway":
            assert command[command.index(IMAGES[kind]) + 1] == "--entrypoints.ldaps.address=:8636"
    ast.parse(pathlib.Path(__file__).read_text())
    directory_policy().check()
    emit(success=True, phase="offline-check", liveServicesContacted=False,
         checks=["pinned-images", "container-ID-routing", "no-publish-or-restart", "entrypoint-arguments", "LDAP-diagnostic-redaction",
                 "output-directory-confinement", "syntax"])


class Run:
    def __init__(self, worker, folder, preserve):
        self.w, self.folder, self.preserve = worker, folder, preserve
        self.token = uuid.uuid4().hex
        self.prefix = "lucia-node-qual-" + self.token
        self.network = None
        self.containers = {}
        self.deadline = time.monotonic() + 600
        self.phase = "preflight"
        self.last_operation = "none"
        self.stopping = False
        self.sign_count = 0
        self.state = folder / "identity"
        self.data = folder / "host" / "data"

    def __enter__(self):
        need(sys.platform == "linux" and os.getuid() != 0, "nonroot-linux-required")
        need(self.folder.is_relative_to(pathlib.Path.cwd()) and not self.folder.exists()
             and not self.folder.is_symlink(), "fixture-output-must-be-new-cwd-child")
        descriptor = self.w.open_directory(self.folder.parent)
        os.close(descriptor)
        os.umask(0o077)
        try:
            self.prepare()
            self.start_services()
            return self
        except BaseException:
            self.cleanup()
            raise

    def __exit__(self, error_type, *_):
        cleaned = self.cleanup()
        if error_type is None:
            need(cleaned, "fixture-teardown-incomplete")

    def command(self, command, *, input_text=None, check=True, timeout=45, cleanup=False):
        remaining = self.deadline - time.monotonic()
        if not cleanup:
            need(remaining > 0 and not self.stopping, "qualification-deadline-or-stop")
        result = subprocess.run(command, input=input_text, text=True, encoding="utf-8", capture_output=True,
                                timeout=timeout if cleanup else min(timeout, remaining), cwd=ROOT,
                                env={**os.environ, "LC_ALL": "C"})
        need(len(result.stdout) + len(result.stderr) <= 262144, "command-output-bound")
        if check and result.returncode:
            # No command arguments, service logs, LDIF, passwords or stderr reach the caller.
            raise QualificationError("native-command-failed")
        return result

    def mkdir(self, path):
        self.w.private_directory(path)

    def put(self, path, value):
        self.w.write_file(path, json.dumps(value) + "\n" if isinstance(value, dict) else value)

    def mount(self, path, target, readonly=False):
        need("," not in str(path), "unsupported-fixture-path")
        return ["--mount", f"type=bind,source={path},target={target}" + (",readonly" if readonly else "")]

    def create(self, kind, arguments):
        entry_arguments = {
            "ca": ["/config/start-ca.sh"],
            "ldap": [],
            "gateway": ["--entrypoints.ldaps.address=:8636", "--providers.file.directory=/config",
                        "--api.dashboard=false", "--log.level=ERROR"],
        }
        command = ["docker", "create", "--pull=never", "--name", self.prefix + "-" + kind,
                   "--label", LABEL + "=" + self.token, "--label", "io.lucia.component=identity",
                   "--network", self.network, "--network-alias", "identity-" + kind,
                   "--restart=no", "--security-opt", "no-new-privileges:true",
                   *arguments, IMAGES[kind], *entry_arguments[kind]]
        result = self.command(command)
        identifier = result.stdout.strip()
        need(re.fullmatch(r"[0-9a-f]{64}", identifier) is not None, "invalid-created-container-ID")
        self.containers[kind] = identifier  # Record before start; finally owns cleanup even on start failure.
        self.command(["docker", "start", identifier])
        return identifier

    def wait(self, command, seconds=120):
        until = min(self.deadline, time.monotonic() + seconds)
        while time.monotonic() < until and not self.stopping:
            if self.command(command, check=False, timeout=15).returncode == 0:
                return
            time.sleep(2)
        raise QualificationError("fixture-service-readiness-timeout")

    def prepare(self):
        for image in IMAGES.values():
            self.command(["docker", "image", "inspect", "--format", "{{.Id}}", image])
        # Refuse remote Docker contexts: bind mounts must belong to this same Linux filesystem.
        info = self.command(["docker", "info", "--format", "{{.OSType}}"]).stdout.strip()
        need(info == "linux" and not os.environ.get("DOCKER_HOST", "").startswith(("ssh:", "tcp:")),
             "local-linux-docker-required")
        context = self.command(["docker", "context", "inspect", "--format",
                                '{{(index .Endpoints "docker").Host}}']).stdout.strip()
        need(context.startswith("unix://"), "local-unix-docker-context-required")
        self.mkdir(self.folder)
        for relative in ("identity", "host", "host/data", "host/data/nodes", "host/data/onboarding"):
            self.mkdir(self.folder / relative)
        for relative in ("ca", "certificates", "secrets", "trust", "config", "gateway", "ldap", "ldap/data", "ldap/config"):
            self.mkdir(self.state / relative)
        for name in ("ca-password", "ldap-admin-password", "authentik-ldap-password"):
            self.put(self.state / "secrets" / name, secrets.token_urlsafe(36) + "aA1!")
        for name in ("start-ca.sh", "init_org_tree.ldif", "init_org_entries.ldif"):
            content = (ROOT / "deployment" / "identity" / name).read_text()
            self.put(self.state / "config" / name, content)
        network = self.command(["docker", "network", "create", "--internal", "--label", LABEL + "=" + self.token,
                                self.prefix]).stdout.strip()
        need(re.fullmatch(r"[0-9a-f]{64}", network) is not None, "invalid-created-network-ID")
        self.network = network
        self.phase = "ca-start"
        self.create("ca", [
            "--user", f"{os.getuid()}:{os.getgid()}", "--entrypoint", "/bin/sh",
            "-e", "LUCIA_CA_NAME=Lucia disposable node qualification",
            "-e", "LUCIA_PUBLIC_HOST=" + HOST,
            *self.mount(self.state / "ca", "/home/step"),
            *self.mount(self.state / "certificates", "/certificates"),
            *self.mount(self.state / "config", "/config", True),
            *self.mount(self.state / "secrets" / "ca-password", "/run/secrets/ca-password", True),
        ])
    def start_services(self):
        ca = self.containers["ca"]
        self.wait(["docker", "exec", ca, "step", "ca", "health", "--ca-url", "https://localhost:9000",
                   "--root", "/home/step/certs/root_ca.crt"])
        root = self.w.safe_bytes(self.state / "ca" / "certs" / "root_ca.crt", public=True).decode()
        self.put(self.state / "trust" / "lucia-root-ca.crt", root)
        root_der = self.w.pem_blocks(root, "CERTIFICATE")[0]
        self.put(self.state / "trust" / "fingerprint.txt", hashlib.sha256(root_der).hexdigest() + "\n")
        self.phase = "ldap-start"
        self.create("ldap", [
            "-e", "LDAP_INIT_ORG_DN=" + BASE, "-e", "LDAP_INIT_ORG_NAME=Lucia",
            "-e", "LDAP_INIT_ROOT_USER_DN=uid=admin," + BASE,
            "-e", "LDAP_INIT_ROOT_USER_PW_FILE=/run/secrets/ldap-admin-password",
            "-e", "LDAP_INIT_RFC2307BIS_SCHEMA=1", "-e", "LDAP_INIT_PASSWORD_HASH=ARGON2",
            "-e", "LDAP_INIT_PPOLICY_PW_MIN_LENGTH=14", "-e", "LDAP_TLS_ENABLED=false",
            *self.mount(self.state / "ldap" / "data", "/var/lib/ldap"),
            *self.mount(self.state / "ldap" / "config", "/etc/ldap/slapd.d"),
            *self.mount(self.state / "config" / "init_org_tree.ldif", "/opt/ldifs/init_org_tree.ldif", True),
            *self.mount(self.state / "config" / "init_org_entries.ldif", "/opt/ldifs/init_org_entries.ldif", True),
            *self.mount(self.state / "secrets" / "ldap-admin-password", "/run/secrets/ldap-admin-password", True),
            *self.mount(self.state / "secrets" / "authentik-ldap-password", "/run/secrets/authentik-ldap-password", True),
            *self.mount(self.state / "trust", "/trust", True),
        ])
        ldap = self.containers["ldap"]
        self.wait(["docker", "exec", ldap, "ldapsearch", "-LLL", "-x", "-H", "ldap://127.0.0.1:389",
                   "-D", "uid=admin," + BASE, "-y", "/run/secrets/ldap-admin-password",
                   "-b", "ou=Services," + BASE, "-s", "base", "dn"])
        self.phase = "gateway-certificate"
        self.command(["docker", "exec", ca, "step", "ca", "certificate", HOST,
                      "/certificates/gateway.crt", "/certificates/gateway.key",
                      "--san", HOST, *(["--san", "identity-gateway"] if HOST != "identity-gateway" else []),
                      "--not-after", "24h", "--provisioner", "lucia-installer",
                      "--provisioner-password-file", "/run/secrets/ca-password",
                      "--ca-url", "https://localhost:9000", "--root", "/home/step/certs/root_ca.crt"])
        self.put(self.state / "gateway" / "routes.yml", {
            "tcp": {"routers": {"ldap": {"entryPoints": ["ldaps"], "rule": "HostSNI(`*`)",
                                         "service": "ldap", "tls": {}}},
                    "services": {"ldap": {"loadBalancer": {"servers": [{"address": "identity-ldap:389"}]}}}},
            "tls": {"certificates": [{"certFile": "/certificates/gateway.crt", "keyFile": "/certificates/gateway.key"}],
                    "stores": {"default": {"defaultCertificate": {
                        "certFile": "/certificates/gateway.crt", "keyFile": "/certificates/gateway.key"}}}},
        })
        self.phase = "gateway-start"
        self.create("gateway", ["--entrypoint", "traefik",
                               *self.mount(self.state / "gateway", "/config", True),
                               *self.mount(self.state / "certificates", "/certificates", True)])
        self.wait(["docker", "exec", "-e", "LDAPTLS_CACERT=/trust/lucia-root-ca.crt",
                   "-e", "LDAPTLS_REQCERT=demand", ldap, "ldapwhoami", "-x",
                   "-H", "ldaps://identity-gateway:8636", "-D", "uid=admin," + BASE,
                   "-y", "/run/secrets/ldap-admin-password"])

    def fixture(self):
        self.phase = "fixture-journal"
        node, task, grant = (str(uuid.uuid4()) for _ in range(3))
        self.command(["openssl", "ecparam", "-name", "prime256v1", "-genkey", "-noout",
                      "-out", str(self.folder / "node.key")])
        self.command(["openssl", "req", "-new", "-key", str(self.folder / "node.key"),
                      "-subj", "/CN=" + node, "-addext", "subjectAltName=DNS:qual-node",
                      "-out", str(self.folder / "node.csr")])
        self.command(["openssl", "pkey", "-in", str(self.folder / "node.key"), "-pubout", "-outform", "DER",
                      "-out", str(self.folder / "node-spki.der")])
        fingerprint = hashlib.sha256(self.w.regular(self.folder / "node-spki.der")).hexdigest()
        value = {"schemaVersion": 1, "nodeId": node, "taskId": task, "hostname": "qual-node",
                 "publicKeyFingerprint": fingerprint, "csrPem": self.w.regular(self.folder / "node.csr").decode(),
                 "expiresAt": (datetime.datetime.now(UTC) + datetime.timedelta(minutes=10)).isoformat()}
        self.put(self.folder / "request.json", value)
        self.settings = {"schema_version": 1, "certificate_mode": "private-ca", "public_host": HOST,
                         "ldap_base_dn": BASE, "uid": os.getuid(), "gid": os.getgid(),
                         "ports": {"authentik": 9443, "ldaps": 8636, "ca": 9444}}
        self.put(self.state / "settings.json", self.settings)
        self.put(self.state / "host-settings.json", {"data_directory": str(self.data)})
        self.put(self.data / "onboarding" / "state.json", {
            "version": 3,
            "devices": [{"device": {"id": node, "taskId": task, "phase": "AwaitingEnrollment", "inventoryRevision": 1},
                         "sessionKeyFingerprint": fingerprint.upper(), "inventoryHash": "A" * 64}],
            "tasks": [{"task": {"id": task, "deviceId": node, "hostname": "qual-node",
                               "phase": "AwaitingEnrollment", "inventoryRevision": 1},
                       "sessionKeyFingerprint": fingerprint.upper(), "inventoryHash": "A" * 64,
                       "grantRequestId": grant}],
        })
        return value

    def provisioner(self):
        harness = self
        worker = self.w
        class Fixture(worker.Native):
            def load_scoped(self):
                self.settings = worker.read_json(self.state / "settings.json")
                need(self.settings == harness.settings, "fixture-settings-drift")
                need(worker.read_json(self.state / "host-settings.json") == {"data_directory": str(harness.data)},
                     "fixture-data-path-drift")
                return harness.data / "nodes"

            def run(self, command, *, check=True, input_text=None, cwd=None):
                mapped = route(command, harness.containers)
                if command[:2] == ["docker", "exec"] and "step" in command and "sign" in command:
                    harness.sign_count += 1
                remaining = self.deadline - time.monotonic() if self.deadline else 120
                need(remaining > 0, "worker-action-deadline")
                result = harness.command(mapped, check=False, input_text=input_text,
                                         timeout=min(remaining, 45 if "step" in command else 30))
                summary = ldap_summary(command, result)
                if summary is not None:
                    harness.last_operation = summary["operation"]
                    emit(phase=harness.phase, **summary)
                if check and result.returncode:
                    raise QualificationError("native-command-failed")
                return result
        return Fixture(argparse.Namespace(state=str(self.state)))

    def seed_owner(self, p):
        self.phase = "fixture-owner"
        owner_dn = "uid=qualification-owner,ou=Users," + BASE
        password = secrets.token_urlsafe(36) + "aA1!"
        salt = secrets.token_bytes(16)
        import base64
        hashed = "{SSHA}" + base64.b64encode(hashlib.sha1(password.encode() + salt).digest() + salt).decode()
        self.put(self.folder / "owner-password", password)
        self.w.owner.ldap_command(p, "ldapadd", input_text=(
            f"dn: {owner_dn}\nobjectClass: top\nobjectClass: inetOrgPerson\nobjectClass: posixAccount\n"
            f"uid: qualification-owner\ncn: Qualification owner\nsn: Owner\nuidNumber: 15000\ngidNumber: 15000\n"
            f"homeDirectory: /home/qualification-owner\nloginShell: /bin/bash\nuserPassword: {hashed}\n\n"
            f"dn: cn=lucia-owners,ou=Groups,{BASE}\nobjectClass: top\nobjectClass: groupOfUniqueNames\n"
            f"objectClass: posixGroup\ncn: lucia-owners\ngidNumber: 15000\nuniqueMember: {owner_dn}\n"))

    def qualify(self):
        self.phase = "network-isolation"
        internal = self.command(["docker", "network", "inspect", "--format", "{{.Internal}}", self.network]).stdout.strip()
        need(internal == "true", "fixture-network-not-internal")
        for identifier in self.containers.values():
            info = self.command(["docker", "inspect", "--format",
                                 '{"ports":{{json .HostConfig.PortBindings}},"networks":{{json .NetworkSettings.Networks}}}',
                                 identifier])
            details = json.loads(info.stdout)
            need(not details["ports"] and len(details["networks"]) == 1
                 and next(iter(details["networks"].values()))["NetworkID"] == self.network,
                 "fixture-container-not-isolated")
        value = self.fixture()
        p = self.provisioner()
        p.load_scoped()
        self.seed_owner(p)
        self.phase = "directory-policy"
        p.deadline = time.monotonic() + 120
        p.verify_containers()
        helper = directory_policy()
        try:
            prepared = helper.prepare(p, self.w)
            backup = self.w.regular(self.state / "node-directory-acl.json", 262144)
            repeated = helper.prepare(p, self.w)
            need(prepared["changed"] and not repeated["changed"], "directory-policy-not-idempotent")
            need(self.w.regular(self.state / "node-directory-acl.json", 262144) == backup, "directory-backup-changed-on-replay")
        except helper.PolicyError as error:
            raise QualificationError(error.code) from None
        emit(success=True, phase=self.phase, **prepared, replayStable=True)
        self.verify_owner(p)
        self.phase = "node-ca-sign"
        p.verify_containers()
        root = p.trust()
        self.mkdir(self.state / "certificates" / "node-enrollment")
        workspace = self.state / "certificates" / "node-enrollment" / value["nodeId"]
        self.mkdir(workspace)
        p.deadline = time.monotonic() + 120
        self.w.validate_csr(p, value)
        chain = p.sign(value, workspace, hashlib.sha256(value["csrPem"].encode()).hexdigest(), root)
        emit(success=True, phase=self.phase, certificateLifetimeHours=24, nodeSignCommands=self.sign_count)
        self.phase = "worker-process"
        request_hash = hashlib.sha256(self.w.regular(self.folder / "request.json")).hexdigest()
        p.deadline = time.monotonic() + 120
        try:
            configuration = self.w.process(p, value, request_hash)
        except Exception:
            # Independently distinguish the image's self-readable-password default.
            # No ACL modifications to manufacture a passing result.
            failed_operation = self.last_operation
            self.reader_diagnostic(p, value)
            self.last_operation = failed_operation
            raise
        need(configuration["certificatePem"] == chain, "staged-certificate-not-reused")
        self.put(self.folder / "configuration.json", configuration)
        first_receipt = self.w.regular(self.state / "node-enrollment" / (value["nodeId"] + ".json"), 131072)
        p.deadline = time.monotonic() + 120
        need(self.w.process(p, value, request_hash) == configuration, "replay-result-changed")
        need(self.w.regular(self.state / "node-enrollment" / (value["nodeId"] + ".json"), 131072) == first_receipt,
             "replay-receipt-changed")
        p.deadline = time.monotonic() + 120
        renewal = {**value, "expiresAt": (datetime.datetime.now(UTC) + datetime.timedelta(minutes=11)).isoformat()}
        self.w.request(renewal, value["nodeId"])
        renewal_hash = hashlib.sha256(json.dumps(renewal).encode()).hexdigest()
        need(self.w.process(p, renewal, renewal_hash) == configuration and self.sign_count == 1,
             "unnecessary-node-certificate-renewal")
        self.phase = "reader-acl"
        self.acl_check(p, configuration)
        self.verify_owner(p)
        self.phase = "complete"
        emit(success=True, phase="complete", realDefaultCA=True, realLDAP=True, nodeReaderPolicyPrepared=True,
             certificateLifetimeHours=24, nodeSignCommands=self.sign_count, replayStable=True,
             groupRead=True, passwordReadDenied=True, groupWriteDenied=True, ownEntryWriteDenied=True,
             ownerAuthenticationPreserved=True, publishedHostPorts=0)

    def verify_owner(self, p):
        dn = "uid=qualification-owner,ou=Users," + BASE
        result = p.run(["docker", "exec", "-i", "-e", "LDAPTLS_CACERT=/trust/lucia-root-ca.crt",
                        "-e", "LDAPTLS_REQCERT=demand", "lucia-identity-ldap", "ldapwhoami", "-x",
                        "-H", "ldaps://identity-gateway:8636", "-D", dn, "-y", "/dev/stdin"],
                       input_text=self.w.regular(self.folder / "owner-password").decode())
        need(result.stdout.strip().lower() == ("dn:" + dn).lower(), "existing-owner-authentication-changed")

    def reader_diagnostic(self, p, value):
        stage = "receipt-read"
        try:
            receipt = self.w.read_json(self.state / "node-enrollment" / (value["nodeId"] + ".json"))
            dn = "uid=node-" + value["nodeId"].replace("-", "") + ",ou=Services," + BASE
            password = receipt["ldapBindPassword"]
            stage = "reader-self-search"
            command = self.reader_command(dn, "ldapsearch") + ["-LLL", "-b", dn, "-s", "base", "userPassword"]
            rows = self.command(command, input_text=password, check=False)
            emit(phase="reader-diagnostic", **ldap_summary(command, rows))
            self_read = rows.returncode == 0 and any(key.split(";")[0] == "userpassword"
                                                    for entry in self.w.ldif_entries(rows.stdout) for key in entry)
            owner_read, group_denied = False, False
            try:
                self.w.reader_search(p, dn, password, "uid=qualification-owner,ou=Users," + BASE,
                                     "base", "(objectClass=*)", "uid", "userPassword")
                owner_read = True
            except Exception as error:
                emit(phase="reader-diagnostic", success=False, code="owner-visibility-probe-failed",
                     errorType=type(error).__name__)
            try:
                self.acl_check(p, {"ldapBindDn": dn, "ldapBindPassword": password,
                                   "ownerGroupDn": "cn=lucia-owners,ou=Groups," + BASE})
                group_denied = True
            except Exception as error:
                emit(phase="reader-diagnostic", success=False, code="group-ACL-probe-failed",
                     errorType=type(error).__name__)
            emit(success=False, phase="reader-diagnostic",
                 code="default-LDAP-allows-reader-own-userPassword" if self_read else "reader-verification-failed",
                 readerOwnPasswordVisible=self_read, ownerReadWithPasswordHidden=owner_read,
                 groupReadAndWriteDenied=group_denied, productionModified=False, aclRelaxed=False)
        except Exception as error:
            emit(success=False, phase="reader-diagnostic", operation=stage, code="diagnostic-probe-failed",
                 errorType=type(error).__name__)

    def reader_command(self, dn, tool):
        return ["docker", "exec", "-i", "-e", "LDAPTLS_CACERT=/trust/lucia-root-ca.crt",
                "-e", "LDAPTLS_REQCERT=demand", self.containers["ldap"], tool, "-x",
                "-H", "ldaps://identity-gateway:8636", "-D", dn, "-y", "/dev/stdin"]

    def acl_check(self, p, configuration):
        dn, password = configuration["ldapBindDn"], configuration["ldapBindPassword"]
        group = configuration["ownerGroupDn"]
        rows = self.w.reader_search(p, dn, password, group, "base", "(objectClass=*)", "cn", "uniqueMember", "gidNumber", "userPassword")
        need(rows[0].get("uniquemember") == ["uid=qualification-owner,ou=Users," + BASE], "owner-membership-read-failed")
        # LDAP password and LDIF both need stdin: store ONLY this disposable reader
        # password in a pre-mounted private fixture file for the explicit deny probe.
        self.put(self.state / "secrets" / "reader-password", password)
        container = self.containers["ldap"]
        self.command(["docker", "cp", str(self.state / "secrets" / "reader-password"),
                      container + ":/run/qualification-reader-password"])
        command = self.reader_command(dn, "ldapmodify")
        command[-1] = "/run/qualification-reader-password"
        denied = self.command(command, input_text=(
            f"dn: {group}\nchangetype: modify\nadd: uniqueMember\nuniqueMember: {dn}\n"), check=False)
        need(denied.returncode == 50, "reader-group-write-not-denied-by-ACL")
        after = self.w.owner.ldap_search(p, group, "(cn=lucia-owners)", "uniqueMember")
        need(len(after) == 1 and after[0].get("uniqueMember") == ["uid=qualification-owner,ou=Users," + BASE],
             "group-membership-changed")
        denied = self.command(command, input_text=(
            f"dn: {dn}\nchangetype: modify\nreplace: description\ndescription: Must not be writable by node reader\n"),
            check=False)
        need(denied.returncode == 50, "reader-own-entry-write-not-denied-by-ACL")

    def manifest(self):
        if self.folder.exists():
            self.put(self.folder / "fixture.json", {
                "schemaVersion": 1, "purpose": "disposable-node-enrollment-qualification",
                "label": self.token, "containers": self.containers, "network": self.network,
                "stateDirectory": str(self.state), "configurationFile": str(self.folder / "configuration.json"),
                "caFile": str(self.state / "trust" / "lucia-root-ca.crt"),
                "ldapUri": "ldaps://identity-gateway:8636", "publishedHostPorts": [],
            })

    def cleanup(self):
        ok = True
        for identifier in reversed(list(self.containers.values())):
            try:
                label = self.command(["docker", "inspect", "--format", '{{index .Config.Labels "' + LABEL + '"}}',
                                      identifier], check=False, cleanup=True)
                if label.returncode == 0:
                    need(label.stdout.strip() == self.token, "cleanup-label-mismatch")
                    if identifier == self.containers.get("ldap"):
                        # Restore ownership of ONLY the two fixture bind mounts,
                        # so ordinary-user teardown can remove generated files.
                        self.command(["docker", "exec", "--user", "0", identifier, "chown", "-R",
                                      f"{os.getuid()}:{os.getgid()}", "/var/lib/ldap", "/etc/ldap/slapd.d"],
                                     check=False, cleanup=True)
                    self.command(["docker", "rm", "--force", "--volumes", identifier], cleanup=True)
            except Exception:
                ok = False
        if self.network:
            try:
                self.command(["docker", "network", "rm", self.network], cleanup=True)
            except Exception:
                ok = False
        if not self.preserve and self.folder.exists():
            try:
                shutil.rmtree(self.folder)
            except OSError:
                # A failed container may leave service-owned files. Never use a
                # broad host deletion/elevation to conceal cleanup failures.
                ok = False
        emit(phase="teardown", success=ok, fixturePreserved=self.preserve or self.folder.exists())
        return ok


def run(args):
    need(sys.platform == "linux" and os.getuid() != 0, "nonroot-linux-required")
    need(shutil.which("docker") is not None and shutil.which("openssl") is not None, "docker-and-openssl-required")
    need(not args.keep_containers or bool(args.output_dir), "keep-containers-requires-explicit-output-dir")
    check()
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location("enrollment_worker", pathlib.Path(__file__).with_name("enrollment_worker.py"))
    worker = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(worker)
    folder = output_path(args.output_dir or ".node-native-qualification-" + uuid.uuid4().hex)
    need(not folder.exists() and not folder.is_symlink(), "fixture-output-must-not-exist")
    descriptor = worker.open_directory(folder.parent)
    os.close(descriptor)
    os.umask(0o077)
    harness = Run(worker, folder, bool(args.output_dir))
    def stop(*_):
        harness.stopping = True
    previous = {sig: signal.signal(sig, stop) for sig in (signal.SIGINT, signal.SIGTERM)}
    success = False
    try:
        harness.prepare()
        harness.start_services()
        harness.qualify()
        success = True
        harness.manifest()
        if args.keep_containers:
            emit(success=True, phase="holding", maximumSeconds=3600, cleanupOnExit=True)
            until = time.monotonic() + 3600
            while not harness.stopping and time.monotonic() < until:
                time.sleep(1)
    except Exception as error:
        emit(success=False, phase=harness.phase,
             code=error.code if isinstance(error, QualificationError) else "native-qualification-failed",
             operation=harness.last_operation, errorType=type(error).__name__, productionModified=False)
    finally:
        with contextlib.suppress(Exception):
            harness.manifest()
        success = harness.cleanup() and success
        for sig, handler in previous.items():
            signal.signal(sig, handler)
    return 0 if success else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="action", required=True)
    sub.add_parser("check", help="Offline safety checks, no Docker calls")
    live = sub.add_parser("run", help="Linux-only disposable live qualification")
    live.add_argument("--output-dir", help="New relative private directory to preserve after teardown")
    live.add_argument("--keep-containers", action="store_true", help="Hold foreground after success; cleanup on exit, at most 1h")
    args = parser.parse_args()
    try:
        if args.action == "check":
            check()
        else:
            raise SystemExit(run(args))
    except QualificationError as error:
        emit(success=False, phase="preflight", code=error.code, productionModified=False)
        raise SystemExit(1)
    except Exception as error:
        emit(success=False, phase="preflight", code="qualification-preflight-failed",
             errorType=type(error).__name__, productionModified=False)
        raise SystemExit(1)
