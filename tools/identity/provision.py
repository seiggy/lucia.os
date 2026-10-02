#!/usr/bin/env python3
"""Idempotent Spark identity provisioning; all mutable state stays outside the checkout."""

import argparse
import contextlib
import base64
import datetime
import hashlib
import ipaddress
import json
import os
import pathlib
import re
import secrets
import shutil
import socket
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[2]
APPHOST = ROOT / "src/Lucia.Homelab.Identity.AppHost/Lucia.Homelab.Identity.AppHost.csproj"
SECRET_NAMES = (
    "identity-db-password", "authentik-secret-key", "authentik-admin-password",
    "authentik-bootstrap-token", "ldap-admin-password", "authentik-ldap-password", "ca-password",
)
CONTAINERS = (
    "lucia-identity-db", "lucia-identity-ca", "lucia-identity-ldap", "lucia-identity-server",
    "lucia-identity-worker", "lucia-identity-gateway", "lucia-identity-renewer",
)
BASE_DN = "dc=lucia,dc=home,dc=arpa"


class IdentityReadinessError(RuntimeError):
    """Credential-free readiness failure safe to display in the desktop."""


def command_failure_message(command, exit_code):
    known = {"ldapwhoami", "ldapsearch", "ldappasswd", "ldapmodify", "ldapadd", "openssl", "step"}
    operation = next((part for part in command if part in known), command[0] if command[0] in ("aspire", "docker") else "identity tool")
    return f"{operation} failed (exit code {exit_code}). Existing data was preserved; check this operation's service health before retrying."


def emit(phase, message, **fields):
    print(json.dumps({"phase": phase, "message": message, **fields}), flush=True)


def validate_host(host):
    if not host or len(host) > 253 or "%" in host:
        raise ValueError("Public host must be a DNS name or IP address, without a scheme, port, or zone suffix.")
    try:
        ipaddress.ip_address(host)
    except ValueError:
        if not re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?", host):
            raise ValueError("Invalid public host.")
        if any(not label or len(label) > 63 or label.startswith("-") or label.endswith("-") for label in host.split(".")):
            raise ValueError("Invalid DNS label.")
    return host.lower()


def service_hosts(host, resolv_conf="/etc/resolv.conf"):
    """The public host plus its LAN-qualified names. The Debian installer appends the
    DHCP domain to a dotless preseed host, so the service certificate must match it."""
    hosts = [host]
    if "." in host or ":" in host:
        return hosts
    try:
        lines = pathlib.Path(resolv_conf).read_text().splitlines()
    except OSError:
        return hosts
    for line in lines:
        parts = line.split()
        if parts and parts[0] in ("search", "domain"):
            for domain in parts[1:]:
                try:
                    name = validate_host(host + "." + domain.rstrip("."))
                except ValueError:
                    continue
                if name not in hosts:
                    hosts.append(name)
    return hosts


def authority(host, port):
    return f"[{host}]:{port}" if ":" in host else f"{host}:{port}"


def write_file(path, content, mode=0o600):
    path.parent.mkdir(parents=True, exist_ok=True)
    data = content.encode() if isinstance(content, str) else content
    if path.exists() and path.read_bytes() == data:
        return
    temporary = path.with_name(path.name + ".new")
    with open(temporary, "wb") as output:
        os.chmod(temporary, mode)
        output.write(data)
        output.flush()
        os.fsync(output.fileno())
    os.replace(temporary, path)


def settings_for(args, existing=None):
    old = existing or {}
    host = validate_host(args.host or old.get("public_host", ""))
    ports = {
        "authentik": args.auth_port if args.auth_port is not None else old.get("ports", {}).get("authentik", 9443),
        "ldaps": args.ldap_port if args.ldap_port is not None else old.get("ports", {}).get("ldaps", 636),
        "ca": args.ca_port if args.ca_port is not None else old.get("ports", {}).get("ca", 9444),
    }
    if any(port < 1 or port > 65535 for port in ports.values()) or len(set(ports.values())) != 3:
        raise ValueError("Identity ports must be distinct and between 1 and 65535.")
    if args.certificate_mode != "private-ca":
        raise ValueError("DNS-backed certificate migration is the next phase; this command currently accepts private-ca only.")
    settings = {
        "schema_version": 1, "certificate_mode": "private-ca", "public_host": host,
        "ldap_base_dn": BASE_DN, "ports": ports,
        "uid": os.getuid() if hasattr(os, "getuid") else 1000,
        "gid": os.getgid() if hasattr(os, "getgid") else 1000,
    }
    if old and old != settings:
        raise ValueError("This installation already has different identity settings. Use a reviewed migration; reruns never rename a directory, rotate trust, or change endpoints implicitly.")
    return settings


class Provisioner:
    def __init__(self, args):
        self.args = args
        requested = pathlib.Path(args.state).expanduser().absolute()
        if requested.is_symlink():
            raise ValueError("Identity state must not be a symbolic link.")
        self.state = requested.resolve()
        if self.state == pathlib.Path(self.state.anchor) or self.state == pathlib.Path.home().resolve():
            raise ValueError("Identity state must be a dedicated directory, not a filesystem or home root.")
        self.values = {}
        self.settings = None
        self.environment = os.environ.copy()
        self.environment["LUCIA_IDENTITY_STATE"] = str(self.state)
        self.environment["PATH"] = os.pathsep.join([
            str(pathlib.Path.home() / ".dotnet"), str(pathlib.Path.home() / ".dotnet/tools"),
            str(pathlib.Path.home() / ".local/bin"), self.environment.get("PATH", ""),
        ])
        if (pathlib.Path.home() / ".dotnet/dotnet").is_file():
            self.environment.setdefault("DOTNET_ROOT", str(pathlib.Path.home() / ".dotnet"))
            self.environment.setdefault("DOTNET_ROOT_ARM64", str(pathlib.Path.home() / ".dotnet"))

    def redact(self, text):
        for value in self.values.values():
            for representation in (value, json.dumps(value)[1:-1], urllib.parse.quote(value, safe=""),
                                   base64.b64encode(value.encode()).decode()):
                if representation:
                    text = text.replace(representation, "[redacted]")
        return text

    def run(self, command, *, check=True, input_text=None, cwd=None):
        result = subprocess.run(command, cwd=cwd or ROOT, env=self.environment, text=True, encoding="utf-8",
                                input=input_text, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        if check and result.returncode:
            output = self.redact(result.stdout + "\n" + result.stderr).strip()
            log = self.state / "last-command-error.log"
            write_file(log, output + "\n")
            tail = "\n".join(output.splitlines()[-12:])[-2000:]
            raise RuntimeError(f"{command[0]} failed with exit code {result.returncode}. Full sanitized log: {log}\n{tail}")
        return result

    def prepare(self):
        self.state.mkdir(parents=True, exist_ok=True)
        os.chmod(self.state, 0o700)
        settings_path = self.state / "settings.json"
        existing = json.loads(settings_path.read_text()) if settings_path.exists() else None
        self.settings = settings_for(self.args, existing)
        if not existing:
            for path in ("ca", "ldap/data", "ldap/config"):
                directory = self.state / path
                if directory.exists() and any(directory.iterdir()):
                    raise RuntimeError(f"Existing {path} data has no installation record. Restore its settings and secrets; refusing to initialize over it.")
        for path in ("secrets", "ca", "certificates", "trust", "config", "gateway", "ldap/data", "ldap/config", "deployment"):
            directory = self.state / path
            if not directory.exists():
                directory.mkdir(parents=True, mode=0o700)
        for name in SECRET_NAMES:
            path = self.state / "secrets" / name
            if path.is_symlink():
                raise RuntimeError(f"Secret file is a symbolic link: {name}")
            if not path.exists():
                if existing:
                    raise RuntimeError(f"Existing installation is missing {name}. Restore it; credentials are never regenerated on rerun.")
                with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "w") as output:
                    output.write(secrets.token_urlsafe(48) + "aA1!")
            value = path.read_text().rstrip("\r\n")
            if len(value) < 32:
                raise RuntimeError(f"Secret {name} is unexpectedly short.")
            os.chmod(path, 0o600)
            self.values[name] = value
        write_file(settings_path, json.dumps(self.settings, indent=2) + "\n")
        for name in ("start-ca.sh", "renew-certificate.sh", "init_org_tree.ldif", "init_org_entries.ldif"):
            write_file(self.state / "config" / name, (ROOT / "deployment/identity" / name).read_text(encoding="utf-8"))
        write_file(self.state / "trust" / "install-node-trust.sh",
                   (ROOT / "deployment/identity/install-node-trust.sh").read_text(encoding="utf-8"), 0o644)
        routes = {
            "http": {
                "routers": {"authentik": {"entryPoints": ["authentik"], "rule": "PathPrefix(`/`)", "service": "authentik", "tls": {}}},
                "services": {"authentik": {"loadBalancer": {"servers": [{"url": "http://identity-server:9000"}]}}},
            },
            "tcp": {
                "routers": {"ldap": {"entryPoints": ["ldaps"], "rule": "HostSNI(`*`)", "service": "ldap", "tls": {}}},
                "services": {"ldap": {"loadBalancer": {"servers": [{"address": "identity-ldap:389"}]}}},
            },
        }
        tls = {"tls": {
            "options": {"default": {"minVersion": "VersionTLS12"}},
            "certificates": [{"certFile": "/certificates/identity.crt", "keyFile": "/certificates/identity.key"}],
            "stores": {"default": {"defaultCertificate": {"certFile": "/certificates/identity.crt", "keyFile": "/certificates/identity.key"}}},
        }}
        write_file(self.state / "gateway" / "routes.yml", json.dumps(routes, indent=2) + "\n")
        write_file(self.state / "gateway" / "tls.yml", json.dumps(tls, indent=2) + "\n")
        emit("prepare", "Identity inputs are ready. Existing trust and credentials were preserved.", state=str(self.state))

    def load_existing(self):
        settings = json.loads((self.state / "settings.json").read_text())
        self.settings = settings_for(self.args, settings)
        for name in SECRET_NAMES:
            path = self.state / "secrets" / name
            if path.is_symlink():
                raise RuntimeError(f"Secret file is a symbolic link: {name}")
            self.values[name] = path.read_text().rstrip("\r\n")
            if len(self.values[name]) < 32:
                raise RuntimeError(f"Secret {name} is unexpectedly short.")

    def collision_check(self):
        for name in CONTAINERS:
            result = self.run(["docker", "inspect", name], check=False)
            if result.returncode == 0:
                data = json.loads(result.stdout)[0]
                labels = data["Config"].get("Labels", {})
                if labels.get("io.lucia.component") != "identity":
                    raise RuntimeError(f"Container name {name} is already owned by another deployment.")

    def publish(self):
        # ponytail: Aspire hashes the AppHost path into volume names; relocations need a migration.
        location = self.state / "apphost-path.txt"
        if location.exists() and location.read_text().strip() != str(APPHOST):
            raise RuntimeError("Identity AppHost location changed. Restore the original path or migrate the Compose project and volumes explicitly; refusing to deploy an empty replacement directory.")
        write_file(location, str(APPHOST) + "\n")
        emit("publish", "Generating Docker Compose artifacts from the identity AppHost.")
        result = self.run(["aspire", "publish", "--apphost", str(APPHOST), "--output-path",
                           str(self.state / "deployment"), "--environment", "Production", "--non-interactive"], cwd=self.state)
        write_file(self.state / "last-publish.log", self.redact(result.stdout + result.stderr))
        emit("publish", "Aspire-generated Compose artifacts are ready for inspection.", output=str(self.state / "deployment"))

    def deploy(self):
        if sys.platform != "linux":
            raise RuntimeError("Apply identity provisioning on the Linux Spark over SSH; local prepare/publish do not deploy.")
        self.collision_check()
        emit("deploy", "Applying the isolated persistent identity stack; existing Lucia development services are not stopped.")
        result = self.run(["aspire", "deploy", "--apphost", str(APPHOST), "--output-path",
                           str(self.state / "deployment"), "--environment", "Production", "--non-interactive"], cwd=self.state)
        write_file(self.state / "last-deploy.log", self.redact(result.stdout + result.stderr))
        for path in (self.state / "deployment").glob(".env*"):
            os.chmod(path, 0o600)
        emit("deploy", "Identity containers were deployed by Aspire. Waiting for persistent services.")

    def wait_container(self, name, timeout=300):
        deadline = time.monotonic() + timeout
        last = "not found"
        while time.monotonic() < deadline:
            result = self.run(["docker", "inspect", name], check=False)
            if result.returncode == 0:
                state = json.loads(result.stdout)[0]["State"]
                last = state.get("Health", {}).get("Status", state.get("Status"))
                if last in ("healthy", "running"):
                    return
                if state.get("Status") in ("exited", "dead"):
                    break
            time.sleep(2)
        logs = self.run(["docker", "logs", "--tail", "40", name], check=False)
        raise RuntimeError(self.redact(f"{name} is not ready ({last}).\n{logs.stdout}\n{logs.stderr}"))

    def certificates(self):
        self.wait_container("lucia-identity-ca")
        root = self.state / "trust" / "lucia-root-ca.crt"
        fingerprint = self.run(["docker", "exec", "lucia-identity-ca", "step", "certificate", "fingerprint",
                                "/home/step/certs/root_ca.crt"]).stdout.strip()
        if not re.fullmatch(r"[0-9a-fA-F]{64}", fingerprint):
            raise RuntimeError("The CA did not return a SHA-256 certificate fingerprint.")
        fingerprint_file = self.state / "trust" / "fingerprint.txt"
        if fingerprint_file.exists() and fingerprint_file.read_text().strip() != fingerprint:
            raise RuntimeError("The CA identity changed. Refusing to replace trusted root material.")
        self.run(["docker", "cp", "lucia-identity-ca:/home/step/certs/root_ca.crt", str(root)])
        os.chmod(root, 0o644)
        write_file(fingerprint_file, fingerprint + "\n", 0o644)
        certificate = self.state / "certificates" / "identity.crt"
        key = self.state / "certificates" / "identity.key"
        with self.certificate_publication_lock():
            if certificate.is_symlink() or key.is_symlink():
                raise RuntimeError("Service certificate files must not be symbolic links.")
            if certificate.exists() and not key.exists():
                raise RuntimeError("The service private key is missing. Restore it; existing keys are not silently replaced.")
            hosts = service_hosts(self.settings["public_host"])
            valid = certificate.exists() and self.run(
                ["openssl", "x509", "-in", str(certificate), "-checkend", "28800", "-noout"], check=False).returncode == 0
            if valid:
                names = self.run(["openssl", "x509", "-in", str(certificate), "-noout", "-ext", "subjectAltName"]).stdout
                valid = all(re.search(r"(?:DNS|IP Address):" + re.escape(name) + r"(?:,|\s|$)", names) for name in hosts)
            if not valid:
                staged = certificate.with_name("issuing.crt")
                staged_key = key.with_name("issuing.key")
                csr = certificate.with_name("issuing.csr")
                common = ["--provisioner", "lucia-installer", "--provisioner-password-file", "/run/secrets/ca-password",
                          "--ca-url", "https://localhost:9000", "--root", "/home/step/certs/root_ca.crt", "--force"]
                try:
                    if key.exists():
                        sans = []
                        for host in hosts:
                            try:
                                ipaddress.ip_address(host)
                                sans.append("IP:" + host)
                            except ValueError:
                                sans.append("DNS:" + host)
                        self.run(["openssl", "req", "-new", "-key", str(key), "-out", str(csr),
                                  "-subj", "/CN=" + hosts[0], "-addext",
                                  "subjectAltName=" + ",".join(sans) + ",DNS:identity-gateway,DNS:localhost"])
                        self.run(["docker", "exec", "lucia-identity-ca", "step", "ca", "sign",
                                  "/certificates/issuing.csr", "/certificates/issuing.crt", *common])
                        signing_key = key
                    else:
                        self.run(["docker", "exec", "lucia-identity-ca", "step", "ca", "certificate",
                                  self.settings["public_host"], "/certificates/issuing.crt", "/certificates/issuing.key",
                                  *[arg for host in hosts for arg in ("--san", host)],
                                  "--san", "identity-gateway", "--san", "localhost", *common])
                        signing_key = staged_key
                    self.validate_certificate_pair(staged, signing_key, root)
                    if signing_key == staged_key:
                        os.replace(staged_key, key)
                    os.replace(staged, certificate)
                finally:
                    for temporary in (staged, staged_key, csr):
                        temporary.unlink(missing_ok=True)
            self.validate_certificate_pair(certificate, key, root)
            os.chmod(key, 0o600)
            marker = "".join(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n" for path in (certificate, key))
            write_file(certificate.parent / "ready.sha256", marker)
            (self.state / "gateway" / "tls.yml").touch()
        emit("certificates", "Private-CA service certificate is ready; no client trust store was changed.", fingerprint=fingerprint)

    @contextlib.contextmanager
    def certificate_publication_lock(self):
        import fcntl
        lock_path = self.state / "certificates" / ".publish.lock"
        if lock_path.is_symlink():
            raise RuntimeError("Certificate publication lock must not be a symbolic link.")
        with lock_path.open("a+b") as lock:
            os.chmod(lock_path, 0o600)
            fcntl.flock(lock, fcntl.LOCK_EX)
            yield

    def validate_certificate_pair(self, certificate, key, root):
        self.run(["openssl", "verify", "-CAfile", str(root), "-untrusted", str(certificate), "-purpose", "sslserver",
                  "-verify_hostname", "identity-gateway", str(certificate)])
        certificate_key = self.run(["openssl", "x509", "-in", str(certificate), "-pubkey", "-noout"]).stdout.strip()
        private_key = self.run(["openssl", "pkey", "-in", str(key), "-pubout"]).stdout.strip()
        if not certificate_key or certificate_key != private_key:
            raise RuntimeError("The service certificate and private key do not match; refusing to publish them.")

    def ldap(self):
        self.wait_container("lucia-identity-ldap")
        base = self.settings["ldap_base_dn"]
        reader = "uid=authentik,ou=Services," + base
        root = "uid=admin," + base
        bind = ["docker", "exec", "lucia-identity-ldap", "ldapwhoami", "-x", "-H", "ldap://127.0.0.1:389",
                "-D", reader, "-y", "/run/secrets/authentik-ldap-password"]
        if self.run(bind, check=False).returncode:
            search = self.run(["docker", "exec", "lucia-identity-ldap", "ldapsearch", "-LLL", "-x",
                               "-H", "ldap://127.0.0.1:389", "-D", root, "-y", "/run/secrets/ldap-admin-password",
                               "-b", reader, "-s", "base", "userPassword"]).stdout
            if any(line.startswith("userPassword:") for line in search.splitlines()):
                raise RuntimeError("Existing LDAP reader password does not match stored credentials. Refusing to reset it.")
            self.run(["docker", "exec", "lucia-identity-ldap", "ldappasswd", "-x", "-H", "ldap://127.0.0.1:389",
                      "-D", root, "-y", "/run/secrets/ldap-admin-password", "-T", "/run/secrets/authentik-ldap-password", reader])
            self.run(["docker", "exec", "-i", "lucia-identity-ldap", "ldapmodify", "-x", "-H", "ldap://127.0.0.1:389",
                      "-D", root, "-y", "/run/secrets/ldap-admin-password"],
                     input_text=f"dn: {reader}\nchangetype: modify\nreplace: pwdReset\npwdReset: FALSE\n")
            self.run(bind)
        self.check_ldap_tls()
        emit("ldap", "Directory reader authenticated over verified LDAPS. No demonstration users were installed.")

    def check_ldap_tls(self):
        self.wait_gateway_tls()
        reader = "uid=authentik,ou=Services," + self.settings["ldap_base_dn"]
        result = self.run(["docker", "exec", "-e", "LDAPTLS_CACERT=/trust/lucia-root-ca.crt",
                  "-e", "LDAPTLS_REQCERT=demand", "lucia-identity-ldap", "ldapwhoami", "-x",
                  "-H", "ldaps://identity-gateway:8636", "-D", reader, "-y", "/run/secrets/authentik-ldap-password"], check=False)
        if result.returncode:
            raise IdentityReadinessError(
                f"The directory service-reader bind over verified LDAPS failed (LDAP exit code {result.returncode}). "
                "Stored credentials were not reset. Check LDAP availability and the existing reader account.")

    def wait_gateway_tls(self, timeout=60):
        context = ssl.create_default_context(cafile=str(self.state / "trust" / "lucia-root-ca.crt"))
        deadline = time.monotonic() + timeout
        reason = "the gateway was not reachable"
        while time.monotonic() < deadline:
            try:
                with socket.create_connection(("127.0.0.1", self.settings["ports"]["ldaps"]), timeout=3) as connection:
                    with context.wrap_socket(connection, server_hostname="identity-gateway"):
                        return
            except ssl.SSLCertVerificationError:
                reason = "its certificate did not validate against the installed CA"
            except OSError:
                reason = "the TLS listener was not ready"
            time.sleep(1)
        raise IdentityReadinessError(
            f"The identity TLS gateway did not become ready within {timeout} seconds: {reason}. "
            "Check the gateway and CA containers; certificate verification was not bypassed.")

    def api(self, method, path, body=None, allow_missing=False):
        context = ssl.create_default_context(cafile=str(self.state / "trust" / "lucia-root-ca.crt"))
        url = "https://" + authority(self.settings["public_host"], self.settings["ports"]["authentik"]) + path
        request = urllib.request.Request(url, data=None if body is None else json.dumps(body).encode(),
                                         method=method, headers={
            "Authorization": "Bearer " + self.values["authentik-bootstrap-token"], "Content-Type": "application/json",
        })
        try:
            with urllib.request.urlopen(request, context=context, timeout=30) as response:
                data = response.read()
                return json.loads(data) if data else None
        except urllib.error.HTTPError as error:
            if error.code == 404 and allow_missing:
                return None
            raise RuntimeError(self.redact(f"Authentik {method} {path} returned {error.code}: {error.read().decode()[:3000]}")) from error

    def summary(self, persist=True):
        from owner import load_owner

        owner = load_owner(self)
        if not owner["complete"]:
            raise RuntimeError("Owner enrollment has not completed sign-in verification.")
        host = self.settings["public_host"]
        result = {
            "schema_version": 1, "certificate_mode": self.settings["certificate_mode"],
            "authentik_url": "https://" + authority(host, self.settings["ports"]["authentik"]),
            "ldap_url": "ldaps://" + authority(host, self.settings["ports"]["ldaps"]),
            "ca_url": "https://" + authority(host, self.settings["ports"]["ca"]),
            "ldap_base_dn": self.settings["ldap_base_dn"],
            "trust_certificate": str(self.state / "trust" / "lucia-root-ca.crt"),
            "trust_fingerprint": (self.state / "trust" / "fingerprint.txt").read_text().strip(),
            "client_trust_installed_by_provisioner": False,
            "owner_username": owner["username"],
            "owner_login_ready": True,
        }
        if persist:
            write_file(self.state / "installation.json", json.dumps(result, indent=2) + "\n")
        emit("complete", "Identity services are provisioned. Install the exported public CA on clients before browser sign-in.", **result)

    def verify(self):
        from owner import load_owner, owner_identity

        for name in CONTAINERS:
            self.wait_container(name, timeout=30)
        self.check_ldap_tls()
        source = self.api("GET", "/api/v3/sources/ldap/lucia-ldap/")
        if (source["base_dn"] != self.settings["ldap_base_dn"] or not source.get("peer_certificate")
                or source.get("password_login_update_internal_password")
                or source["server_uri"] != "ldaps://identity-gateway:8636" or not source.get("sni")):
            raise RuntimeError("LDAP source drifted from the verified directory/TLS/password policy.")
        certificate = self.api("GET", f"/api/v3/crypto/certificatekeypairs/{source['peer_certificate']}/")
        fingerprint = hashlib.sha256(ssl.PEM_cert_to_DER_cert(
            (self.state / "trust" / "lucia-root-ca.crt").read_text())).hexdigest()
        if certificate.get("private_key_available") or certificate.get("fingerprint_sha256", "").replace(":", "").lower() != fingerprint:
            raise RuntimeError("LDAP source no longer trusts the installed public CA.")
        self.api("GET", "/api/v3/sources/ldap/lucia-ldap/debug/")
        sync = self.api("GET", "/api/v3/sources/ldap/lucia-ldap/sync/status/")
        if not sync.get("last_successful_sync") or sync.get("last_sync_status") not in ("done", "info"):
            raise RuntimeError("LDAP source has no successful synchronization or its latest sync failed.")
        owner = load_owner(self)
        if not owner["complete"]:
            raise RuntimeError("Owner enrollment has not completed sign-in verification.")
        owner_identity(self, owner)
        emit("verify", "Read-only service, TLS, LDAP-source, and owner-access checks succeeded.")


def exactly_one(items, description):
    if len(items) != 1:
        raise RuntimeError(f"Expected exactly one {description}; found {len(items)}.")
    return items[0]


def configure_browser_session(provisioner):
    stages = provisioner.api("GET", "/api/v3/stages/user_login/?name=default-authentication-login")["results"]
    stage = exactly_one([item for item in stages if item["name"] == "default-authentication-login"], "default login stage")
    duration = stage.get("session_duration")
    if not isinstance(duration, str) or not duration.strip():
        raise RuntimeError("Authentik did not report a valid browser session duration.")
    if duration != "seconds=0":
        return  # Preserve an operator's explicit duration, including a shorter lifetime.
    flows = provisioner.api("GET", "/api/v3/flows/instances/?slug=default-authentication-flow")["results"]
    flow = exactly_one([item for item in flows if item["slug"] == "default-authentication-flow"
                        and item["designation"] == "authentication"], "default authentication flow")
    bindings = provisioner.api("GET", f"/api/v3/flows/bindings/?target={flow['pk']}")["results"]
    exactly_one([item for item in bindings if item["stage"] == stage["pk"]], "bound default login stage")
    saved = provisioner.api("PATCH", f"/api/v3/stages/user_login/{stage['pk']}/", {"session_duration": "hours=8"})
    if saved.get("session_duration") != "hours=8":
        raise RuntimeError("Authentik did not save the persistent eight-hour browser session.")
    emit("authentik-session", "Default browser sign-in now survives browser restarts for eight hours; existing sessions and credentials are unchanged.")


def configure_authentik(provisioner):
    deadline = time.monotonic() + 300
    last_error = None
    while time.monotonic() < deadline:
        try:
            provisioner.api("GET", "/api/v3/core/users/me/")
            break
        except (OSError, RuntimeError, urllib.error.URLError) as error:
            last_error = error
            time.sleep(3)
    else:
        raise RuntimeError(f"Authentik bootstrap did not become ready over verified TLS: {last_error}")

    started = datetime.datetime.now(datetime.timezone.utc)
    ca_name = "lucia-ldap-ca"
    certificates = provisioner.api("GET", "/api/v3/crypto/certificatekeypairs/?name=" + ca_name)["results"]
    certificates = [item for item in certificates if item["name"] == ca_name]
    ca_data = (provisioner.state / "trust" / "lucia-root-ca.crt").read_text()
    ca_fingerprint = hashlib.sha256(ssl.PEM_cert_to_DER_cert(ca_data)).hexdigest()
    if certificates:
        certificate = exactly_one(certificates, "Lucia LDAP CA")
        if certificate.get("private_key_available"):
            raise RuntimeError("The managed public CA record unexpectedly contains a private key. Inspect it instead of overwriting it.")
        if certificate.get("fingerprint_sha256", "").replace(":", "").lower() != ca_fingerprint:
            raise RuntimeError("Authentik's existing Lucia CA differs from the installed CA. A trust migration is required.")
    else:
        certificate = provisioner.api("POST", "/api/v3/crypto/certificatekeypairs/",
                                     {"name": ca_name, "certificate_data": ca_data, "key_data": ""})

    mappings = {}
    for name in ("openldap-uid", "openldap-cn", "default-mail"):
        managed = "goauthentik.io/sources/ldap/" + name
        result = provisioner.api("GET", "/api/v3/propertymappings/source/ldap/?" + urllib.parse.urlencode({"managed": managed}))
        mappings[name] = exactly_one([item for item in result["results"] if item.get("managed") == managed],
                                     "LDAP mapping " + managed)["pk"]

    base_dn = provisioner.settings["ldap_base_dn"]
    source = {
        "name": "Lucia LDAP", "slug": "lucia-ldap", "enabled": True,
        "server_uri": "ldaps://identity-gateway:8636",
        "bind_cn": "uid=authentik,ou=Services," + base_dn,
        "bind_password": provisioner.values["authentik-ldap-password"],
        "base_dn": base_dn, "additional_user_dn": "ou=Users", "additional_group_dn": "ou=Groups",
        "user_object_filter": "(objectClass=posixAccount)",
        "group_object_filter": "(objectClass=groupOfUniqueNames)",
        "group_membership_field": "uniqueMember", "user_membership_attribute": "distinguishedName",
        "object_uniqueness_field": "entryUUID",
        "user_property_mappings": [mappings["openldap-uid"], mappings["openldap-cn"], mappings["default-mail"]],
        "group_property_mappings": [mappings["openldap-cn"]],
        "peer_certificate": certificate["pk"], "client_certificate": None,
        "start_tls": False, "sni": True, "sync_users": True, "sync_groups": True,
        # Self-service password changes write back to LDAP; people.enable_password_changes grants the write.
        "sync_users_password": True, "password_login_update_internal_password": False,
        "delete_not_found_objects": False, "lookup_groups_from_user": False, "sync_group_hierarchy": False,
    }
    current = provisioner.api("GET", "/api/v3/sources/ldap/lucia-ldap/", allow_missing=True)
    if current and (current["base_dn"] != base_dn or current["server_uri"] != source["server_uri"]):
        raise RuntimeError("The managed LDAP source has different directory or endpoint settings; refusing an implicit authority migration.")
    saved = provisioner.api("PATCH" if current else "POST",
                            "/api/v3/sources/ldap/lucia-ldap/" if current else "/api/v3/sources/ldap/", source)
    password_stages = provisioner.api("GET", "/api/v3/stages/password/?name=default-authentication-password")["results"]
    stage = exactly_one([item for item in password_stages if item["name"] == "default-authentication-password"], "default password stage")
    backend = "authentik.sources.ldap.auth.LDAPBackend"
    if backend not in stage["backends"]:
        provisioner.api("PATCH", f"/api/v3/stages/password/{stage['pk']}/", {"backends": [*stage["backends"], backend]})
    configure_browser_session(provisioner)
    emit("authentik", "LDAP source reconciled with explicit CA verification, stable IDs, and LDAP password write-back.")
    sync_ldap(provisioner, saved["pk"], certificate["pk"], started)


def sync_ldap(provisioner, source_pk, ca_pk, started=None):
    started = started or datetime.datetime.now(datetime.timezone.utc)
    schedules = provisioner.api("GET", "/api/v3/tasks/schedules/?" + urllib.parse.urlencode({"rel_obj_id": source_pk}))["results"]
    for task in ("ldap_connectivity_check", "ldap_sync"):
        actor = "authentik.sources.ldap.tasks." + task
        candidates = [schedule for schedule in schedules
                      if schedule.get("actor_name") == actor and schedule.get("uid") == actor + ":lucia-ldap"]
        schedule = exactly_one(candidates, task + " schedule")
        provisioner.api("POST", f"/api/v3/tasks/schedules/{schedule['id']}/send/")

    deadline = time.monotonic() + 180
    while time.monotonic() < deadline:
        connection = provisioner.api("GET", "/api/v3/sources/ldap/lucia-ldap/").get("connectivity", {})
        sync = provisioner.api("GET", "/api/v3/sources/ldap/lucia-ldap/sync/status/")
        last = sync.get("last_successful_sync")
        timestamp = datetime.datetime.fromisoformat(last.replace("Z", "+00:00")) if last else None
        if (connection.get("__all__", {}).get("status") == "ok"
                and connection.get("identity-gateway", {}).get("status") == "ok"
                and not sync.get("is_running") and sync.get("last_sync_status") in ("done", "info")
                and timestamp and timestamp >= started):
            write_file(provisioner.state / "last-verification.json", json.dumps({
                "checked_at": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                "ldap_source_pk": source_pk, "ca_pk": ca_pk,
                "tls_verified": True, "sync": sync,
            }, indent=2) + "\n")
            emit("verify", "Fresh LDAP connectivity and synchronization succeeded through Authentik.")
            return
        time.sleep(3)
    raise RuntimeError("LDAP source did not complete a fresh successful connection/sync. Inspect Authentik task results; stale cached success was not accepted.")


def arguments():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("prepare", "publish", "apply", "verify", "owner", "verify-owner-login"))
    parser.add_argument("--state", default=str(pathlib.Path.home() / ".local/share/lucia/identity"))
    parser.add_argument("--host")
    parser.add_argument("--certificate-mode", default="private-ca")
    parser.add_argument("--auth-port", type=int)
    parser.add_argument("--ldap-port", type=int)
    parser.add_argument("--ca-port", type=int)
    parser.add_argument("--owner-username")
    parser.add_argument("--password-file", help="For verify-owner-login after changing the initial password; never pass a password as an argument.")
    parser.add_argument("--request-stdin", action="store_true",
                        help='Read {"schema_version":1,"owner_password":"..."} privately from stdin for apply, owner, or verify-owner-login.')
    return parser.parse_args()


def main():
    from owner import enroll_ldap_owner, finish_owner, load_owner, owner_identity, validate_login_password, validate_password, validate_username, verify_login

    os.umask(0o077)
    provisioner = Provisioner(arguments())
    provisioner.state.mkdir(parents=True, exist_ok=True)
    lock = open(provisioner.state / ".provision.lock", "a")
    try:
        password = None
        if provisioner.args.request_stdin:
            if provisioner.args.command not in ("apply", "owner", "verify-owner-login") or provisioner.args.password_file:
                raise ValueError("Private request data is only supported for apply, owner, or verify-owner-login, without --password-file.")
            data = sys.stdin.read(16385)
            if len(data) > 16384:
                raise ValueError("Owner request is too large.")
            try:
                request = json.loads(data)
            except ValueError:
                raise ValueError("Owner request must be valid JSON.") from None
            if (not isinstance(request, dict) or set(request) != {"schema_version", "owner_password"}
                    or request["schema_version"] != 1):
                raise ValueError("Owner request must contain schema_version 1 and owner_password only.")
            password = validate_login_password(request["owner_password"])
            provisioner.values["owner-request-password"] = password
        if sys.platform == "linux":
            import fcntl
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if password is not None and provisioner.args.command in ("apply", "owner"):
            existing_owner = load_owner(provisioner) if (provisioner.state / "owner.json").exists() else None
            if not existing_owner or existing_owner.get("complete") is not True:
                validate_password(password)
        if provisioner.args.command in ("owner", "verify-owner-login"):
            provisioner.load_existing()
            if provisioner.args.command == "verify-owner-login":
                owner = load_owner(provisioner)
                verify_login(provisioner, owner, owner_identity(provisioner, owner), provisioner.args.password_file, password=password)
                return 0
            owner = enroll_ldap_owner(provisioner, provisioner.args.owner_username, password=password)
            configure_authentik(provisioner)
            finish_owner(provisioner, owner, password=password)
            provisioner.verify()
            provisioner.summary()
            return 0
        if provisioner.args.command == "verify":
            provisioner.load_existing()
            provisioner.verify()
            provisioner.summary(persist=False)
            return 0
        if provisioner.args.command == "apply" and not (provisioner.state / "owner.json").exists():
            validate_username(provisioner.args.owner_username)
        provisioner.prepare()
        if provisioner.args.command == "prepare":
            return 0
        for tool in ("dotnet", "aspire"):
            if shutil.which(tool, path=provisioner.environment["PATH"]) is None:
                raise RuntimeError(f"Required tool is missing: {tool}")
        if provisioner.args.command in ("publish", "apply"):
            provisioner.publish()
        if provisioner.args.command == "publish":
            return 0
        if provisioner.args.command == "apply":
            provisioner.deploy()
        provisioner.certificates()
        provisioner.ldap()
        configure_authentik(provisioner)
        owner = enroll_ldap_owner(provisioner, provisioner.args.owner_username, password=password)
        source = provisioner.api("GET", "/api/v3/sources/ldap/lucia-ldap/")
        sync_ldap(provisioner, source["pk"], source["peer_certificate"])
        finish_owner(provisioner, owner, password=password)
        provisioner.verify()
        provisioner.summary()
        return 0
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError, urllib.error.URLError) as error:
        emit("error", provisioner.redact(str(error)))
        return 1
    finally:
        lock.close()


if __name__ == "__main__":
    raise SystemExit(main())
