#!/usr/bin/env python3
"""Lucia desktop protocol v1; all commands also run as `python3 -c SOURCE ...`.

inspect is read-only and needs no payload or sibling imports. start accepts a
reviewed source archive and private JSON on stdin. status never returns secrets.
The Linux worker inherits an exclusive flock and starts a new session, so closing
SSH only stops monitoring. Jobs and pinned tools live under ~/.local/share/lucia/
bootstrap; the AppHost always deploys from bootstrap/app, never an upload path.
Docker/OS repair and local CA trust are deliberately not automated here.

Schema-1 host additions: configure_host (legacy default false), model_directory,
host_package {archive_path, manifest_path, sha256, size}. The external runtime
manifest uses top-level sha256/size and per-file path/sha256/size records.
Only private upload paths enter jobs; runtime archives are streamed up to 8 GiB,
independently of the 16 MiB source archive. Stages are retained for the worker.
inspect returns host_ready/application_ready/host_package_required/model_directory
and active_job_configure_host. Results require both readiness flags when requested;
sso_verified remains false because no browser authorization-code exchange occurs.
"""

import sys

sys.dont_write_bytecode = True

import argparse
import contextlib
import hashlib
import hmac
import io
import ipaddress
import json
import os
import pathlib
import platform
import re
import shutil
import socket
import ssl
import stat
import subprocess
import tarfile
import time
import urllib.parse
import urllib.request
import urllib.error
import uuid

SCHEMA = 1
VERSION = "0.1.0"
DOTNET_VERSION = "10.0.401"
ASPIRE_VERSION = "13.5.4"
APPHOST = "src/Lucia.Homelab.Identity.AppHost/Lucia.Homelab.Identity.AppHost.csproj"
FILES = frozenset((
    "tools/desktop/bootstrap.py", "tools/identity/provision.py", "tools/identity/owner.py",
    "tools/identity/application.py", "tools/host/provision_host.py", "tools/host/package.py",
    "tools/boot/prepare.py", "tools/boot/provision_boot.py", "tools/boot/Dockerfile",
    "tools/domains/activation_worker.py", "tools/nodes/enrollment_worker.py", "tools/nodes/prepare_directory.py",
    "tools/packages/package_worker.py",
    "deployment/boot/Dockerfile", "deployment/boot/serve.py", "deployment/boot/discover-and-wait",
    "deployment/boot/partitioner-guard", "deployment/boot/grub.cfg.in", "deployment/boot/finish-install",
    "deployment/boot/screen.sh", "deployment/boot/grub-theme.txt",
    "deployment/host/Dockerfile",
    "deployment/identity/start-ca.sh", "deployment/identity/renew-certificate.sh",
    "deployment/identity/install-node-trust.sh", "deployment/identity/init_org_tree.ldif",
    "deployment/identity/init_org_entries.ldif", APPHOST,
    "src/Lucia.Homelab.Identity.AppHost/AppHost.cs",
    "src/Lucia.Homelab.Identity.AppHost/aspire.config.json",
))
SECRET_NAMES = (
    "identity-db-password", "authentik-secret-key", "authentik-admin-password",
    "authentik-bootstrap-token", "ldap-admin-password", "authentik-ldap-password", "ca-password",
)
CONTAINERS = tuple("lucia-identity-" + name for name in ("db", "ca", "ldap", "server", "worker", "gateway", "renewer"))
MAX_PAYLOAD = 16 * 1024 * 1024
MAX_FILE = 2 * 1024 * 1024
MAX_HOST_ARCHIVE = 8 * 1024 ** 3
MAX_HOST_MANIFEST = 8 * 1024 ** 2
MAX_EVENTS = 80
JOB_ID = re.compile(r"[a-f0-9]{32}")
REVIEW_FIELDS = frozenset(("schema_version", "public_host", "owner_username", "install_prerequisites", "verify_only"))
HOST_FIELDS = frozenset(("configure_host", "model_directory", "host_package"))


class SafeError(Exception):
    """Only static, credential-free operator messages may cross the protocol."""


def locations():
    home = pathlib.Path.home()
    return home / ".local/share/lucia/bootstrap", home / ".local/share/lucia/identity"


def no_symlinks(path):
    if any(item.is_symlink() or (hasattr(item, "is_junction") and item.is_junction()) for item in (path, *path.parents)):
        raise SafeError("A managed path is a symbolic link. Restore a real, user-owned directory before continuing.")


def owned_path(path, private=False):
    no_symlinks(path)
    info = path.stat()
    if ((hasattr(os, "getuid") and info.st_uid != os.getuid())
            or (private and os.name != "nt" and stat.S_IMODE(info.st_mode) & 0o077)
            or (stat.S_ISREG(info.st_mode) and info.st_nlink != 1)):
        raise SafeError("Managed storage must be owned by the SSH account, without links or unsafe permissions.")
    return info


def private_directory(path):
    no_symlinks(path)
    path.mkdir(parents=True, exist_ok=True, mode=0o700)
    if not path.is_dir() or (hasattr(os, "getuid") and path.stat().st_uid != os.getuid()):
        raise SafeError("A managed directory is not owned by the SSH account.")
    os.chmod(path, 0o700)


def read_bytes(path, limit=MAX_FILE):
    if not stat.S_ISREG(owned_path(path).st_mode):
        raise SafeError("A managed file is not a regular file.")
    with path.open("rb") as stream:
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise SafeError("A managed file exceeded its size limit.")
    return data


def read_json(path):
    data = json.loads(read_bytes(path))
    if not isinstance(data, dict):
        raise SafeError("A managed state record is malformed.")
    return data


def write_bytes(path, data):
    no_symlinks(path)
    private_directory(path.parent)
    staging = path.with_name(path.name + "." + uuid.uuid4().hex + ".new")
    descriptor = os.open(staging, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(staging, path)
    finally:
        staging.unlink(missing_ok=True)


def write_json(path, data):
    write_bytes(path, (json.dumps(data, ensure_ascii=True) + "\n").encode())


def run_read(command, timeout=20, env=None):
    try:
        return subprocess.run(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                              stderr=subprocess.PIPE, text=True, encoding="utf-8",
                              errors="replace", timeout=timeout, env=env, close_fds=True)
    except (OSError, subprocess.SubprocessError):
        return subprocess.CompletedProcess(command, 1, "", "")


def lock_held(path):
    if not path.exists():
        return False
    no_symlinks(path)
    import fcntl
    with path.open("rb") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            return True
        fcntl.flock(lock, fcntl.LOCK_UN)
    return False


def process_identity(pid):
    if type(pid) is not int or pid <= 0:
        return None
    try:
        fields = pathlib.Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
        return fields[19] if fields[0] not in ("Z", "X") else None
    except (OSError, IndexError):
        return None


def host_value(value):
    if not isinstance(value, str) or not value or len(value) > 253 or "%" in value:
        raise SafeError("Public host must be a DNS name or IP address, without a scheme or port.")
    try:
        ipaddress.ip_address(value)
    except ValueError:
        if not re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?", value):
            raise SafeError("Public host must be a DNS name or IP address, without a scheme or port.") from None
        if any(not label or len(label) > 63 or label.startswith("-") or label.endswith("-") for label in value.split(".")):
            raise SafeError("Public host contains an invalid DNS label.")
    return value.lower()


def username_value(value):
    if not isinstance(value, str) or not re.fullmatch(r"[a-z][a-z0-9_-]{0,31}", value) or value in (
            "root", "admin", "akadmin", "authentik", "nobody"):
        raise SafeError("Choose a nonreserved owner username: 1-32 lowercase letters, digits, underscores or hyphens, starting with a letter.")
    return value


def password_value(value):
    if not isinstance(value, str) or not 14 <= len(value) <= 1024 or any(c in value for c in "\r\n\0"):
        raise SafeError("Owner password must contain 14-1024 characters, without newline or NUL characters.")
    return value


def login_password_value(value):
    if not isinstance(value, str) or not value:
        raise SafeError("The existing owner password must not be empty.")
    return value


def url(host, port, scheme="https"):
    return f"{scheme}://[{host}]:{port}" if ":" in host else f"{scheme}://{host}:{port}"


def installation_state(state, app):
    no_symlinks(state)
    if state.exists():
        owned_path(state)
    installed = state.exists() and any(state.iterdir())
    settings = read_json(state / "settings.json") if (state / "settings.json").exists() else None
    owner = read_json(state / "owner.json") if (state / "owner.json").exists() else None
    preparing = not settings and (state / "desktop-prepare.json").exists()
    if preparing:
        settings = read_json(state / "desktop-prepare.json")
        for name in ("ca", "ldap/data", "ldap/config", "trust"):
            path = state / name
            no_symlinks(path)
            if path.exists() and any(path.iterdir()):
                raise SafeError("Unrecorded identity storage is not empty. Restore its settings before continuing.")
    if settings:
        if (type(settings.get("schema_version")) is not int or settings.get("schema_version") != SCHEMA
                or settings.get("certificate_mode") != "private-ca"
                or settings.get("ldap_base_dn") != "dc=lucia,dc=home,dc=arpa"
                or settings.get("uid") != os.getuid() or settings.get("gid") != os.getgid()):
            raise SafeError("Existing identity settings require reviewed recovery or migration, not a new installation.")
        host_value(settings.get("public_host"))
        ports = settings.get("ports")
        if (not isinstance(ports, dict) or set(ports) != {"authentik", "ldaps", "ca"}
                or any(type(port) is not int or not 1 <= port <= 65535 for port in ports.values())
                or len(set(ports.values())) != 3):
            raise SafeError("Existing identity ports are malformed. Restore the installation settings.")
        for name in SECRET_NAMES:
            path = state / "secrets" / name
            if preparing and not path.exists() and not path.is_symlink():
                continue
            if not path.is_file() or len(read_bytes(path, 4096).rstrip(b"\r\n")) < 32:
                raise SafeError("Existing identity secrets are missing or incomplete. Restore them; they will not be regenerated.")
    elif installed and any(item.name != ".provision.lock" for item in state.iterdir()):
        raise SafeError("Partial identity data has no valid settings. Recover the original settings before installing.")
    if owner:
        username_value(owner.get("username"))
        if (type(owner.get("schema_version")) is not int or owner.get("schema_version") != SCHEMA
                or type(owner.get("complete")) is not bool or not settings or preparing):
            raise SafeError("Existing owner state is malformed. Restore its enrollment record.")
    legacy = False
    marker = state / "apphost-path.txt"
    if marker.exists():
        legacy = read_bytes(marker, 8192).decode().strip() != str(app / APPHOST)
        if legacy and not (owner and owner["complete"]):
            raise SafeError("A legacy installation is incomplete. Resume it from its original AppHost path; desktop will not relocate its volumes.")
    return installed, settings, owner, legacy


def listening_ports():
    if not pathlib.Path("/proc/net/tcp").exists():
        raise OSError("Socket inventory unavailable.")
    ports = set()
    for name in ("tcp", "tcp6"):
        path = pathlib.Path("/proc/net") / name
        if not path.exists():
            continue
        for line in path.read_text().splitlines()[1:]:
            fields = line.split()
            if len(fields) > 3 and fields[3] == "0A":
                ports.add(int(fields[1].split(":")[1], 16))
    return ports


def operating_system():
    values = {}
    if sys.platform == "linux":
        try:
            lines = pathlib.Path("/etc/os-release").read_text().splitlines()
        except OSError:
            lines = []
        for line in lines:
            if "=" in line:
                key, value = line.split("=", 1)
                values[key] = value.strip('"')
    return values


def tools_environment(root):
    env = os.environ.copy()
    dotnet = root / "runtimes" / ("dotnet-" + DOTNET_VERSION)
    aspire = root / "runtimes" / ("aspire-" + ASPIRE_VERSION)
    paths = [str(dotnet), str(aspire), str(pathlib.Path.home() / ".dotnet"),
             str(pathlib.Path.home() / ".dotnet/tools"), str(pathlib.Path.home() / ".aspire/bin"),
             str(pathlib.Path.home() / ".local/bin"), env.get("PATH", "")]
    env["PATH"] = os.pathsep.join(paths)
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    return env


def host_origin(host):
    return f"https://[{host}]" if ":" in host else f"https://{host}"


def model_directory_value(value):
    if value is None:
        return None
    if (not isinstance(value, str) or not value.startswith("/") or value.startswith("//")
            or len(value) > 4096 or "\\" in value or any(ord(c) < 32 or ord(c) == 127 for c in value)
            or any(part in (".", "..") for part in value.split("/"))):
        raise SafeError("Model directory must be an absolute Linux path without traversal or control characters.")
    root, state = locations()
    models = pathlib.Path(value)
    host = state.parent / "host"
    if (models in (pathlib.Path("/"), pathlib.Path.home()) or models.is_relative_to(state)
            or state.is_relative_to(models) or models.is_relative_to(root) or root.is_relative_to(models)
            or (models != host / "models" and (models.is_relative_to(host) or host.is_relative_to(models)))):
        raise SafeError("Use a dedicated model directory separate from identity, bootstrap, and host configuration.")
    no_symlinks(models)
    if models.exists():
        info = owned_path(models)
        if not stat.S_ISDIR(info.st_mode) or not os.access(models, os.R_OK | os.W_OK | os.X_OK):
            raise SafeError("The model directory must be owned and writable by the SSH account.")
    return models.as_posix()


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        raise SafeError("A verified service unexpectedly redirected its health or registration check.")


def service_json(state, endpoint, token=None, allow_missing=False):
    ca = state / "trust/lucia-root-ca.crt"
    read_bytes(ca, 32768)
    context = ssl.create_default_context(cafile=str(ca))
    opener = urllib.request.build_opener(NoRedirect(), urllib.request.HTTPSHandler(context=context),
                                        urllib.request.ProxyHandler({}))
    headers = {"Accept": "application/json"}
    if token is not None:
        headers["Authorization"] = "Bearer " + token
    try:
        with opener.open(urllib.request.Request(endpoint, headers=headers), timeout=5) as response:
            data = response.read(MAX_FILE + 1)
        if len(data) > MAX_FILE:
            raise SafeError("A service check returned an oversized response.")
        return json.loads(data)
    except urllib.error.HTTPError as error:
        if error.code == 404 and allow_missing:
            return None
        raise SafeError("A CA-verified service check failed. Review service health and access before retrying.") from None
    except (OSError, ValueError):
        raise SafeError("A CA-verified service check failed. Review service health, name resolution, and the existing CA.") from None


def application_probe(state, settings):
    """Standalone read-only probe; the full reconciler proves policy/mapping readiness after approval."""
    path = state / "host-auth.json"
    if not path.exists():
        return False, "Create the owned Lucia OIDC application and access bindings."
    record = read_json(path)
    origin = host_origin(settings["public_host"])
    if (record.get("schema_version") != 1 or record.get("public_origin") != origin
            or record.get("authority") != url(settings["public_host"], settings["ports"]["authentik"]) + "/application/o/lucia/"):
        raise SafeError("Managed application settings disagree with this identity. Restore them before repairing registration.")
    resources, markers = record.get("resources", {}), record.get("markers", {})
    token = read_bytes(state / "secrets/authentik-bootstrap-token", 4096).decode().rstrip("\r\n")
    base = url(settings["public_host"], settings["ports"]["authentik"])

    def get(path):
        return service_json(state, base + path, token, allow_missing=True)

    app = get("/api/v3/core/applications/lucia/")
    if not app:
        return False, "Repair the missing managed Lucia application and its owner/access bindings."
    if app.get("meta_description") != markers.get("application") or not markers.get("application"):
        raise SafeError("The Lucia application is not owned by this installation. Review the collision.")
    provider_id = resources.get("provider")
    if type(provider_id) is not int or provider_id < 1:
        return False, "Complete the managed Lucia OIDC provider registration."
    provider = get(f"/api/v3/providers/oauth2/{provider_id}/")
    if not provider:
        return False, "Repair the missing managed Lucia OIDC provider."
    if provider.get("name") != markers.get("provider") or provider.get("client_id") != record.get("client_id"):
        raise SafeError("The recorded OIDC provider is owned by another configuration. Review it before continuing.")
    secret_path = state / "secrets/host-oidc-client-secret"
    owned_path(secret_path, private=True)
    secret = read_bytes(secret_path, 4096).decode().rstrip("\r\n")
    if not secret or not isinstance(provider.get("client_secret"), str) or not hmac.compare_digest(provider["client_secret"], secret):
        raise SafeError("The managed OIDC client secret differs from its preserved credential. Review recovery; it will not be rotated.")
    expected_redirects = [
        {"matching_mode": "strict", "url": origin + "/signin-oidc", "redirect_uri_type": "authorization"},
        {"matching_mode": "strict", "url": origin + "/signout-callback-oidc", "redirect_uri_type": "logout"},
    ]
    if (app.get("pk") != resources.get("application") or app.get("provider") != provider_id or app.get("meta_launch_url") != origin + "/"
            or app.get("meta_hide") is not False or app.get("policy_engine_mode") != "any"
            or provider.get("client_type") != "confidential" or provider.get("redirect_uris") != expected_redirects
            or provider.get("grant_types") != ["authorization_code"] or provider.get("issuer_mode") != "per_provider"
            or provider.get("signing_key") != resources.get("signing_key")
            or not resources.get("signing_key") or resources.get("scope_mapping") not in provider.get("property_mappings", [])):
        return False, "Reconcile Lucia's managed OIDC settings, signed claims, and launch address."
    bindings = get("/api/v3/policies/bindings/?" + urllib.parse.urlencode({"target": app["pk"], "page_size": 100}))
    entries = bindings.get("results", []) if isinstance(bindings, dict) else []
    if len(entries) != 2 or bindings.get("pagination", {}).get("next"):
        return False, "Reconcile and verify the managed owner and inference access bindings."
    for role, group in (("owner", resources.get("owner_group")), ("inference", resources.get("access_group"))):
        saved = resources.get("bindings", {}).get(role, {})
        binding = next((entry for entry in entries if entry.get("pk") == saved.get("id")), None)
        if (not binding or not group or binding.get("group") != group or binding.get("enabled") is not True
                or binding.get("negate") is not False or binding.get("policy") or binding.get("user")
                or binding.get("target") != app["pk"] or binding.get("expires") or binding.get("expiring")):
            return False, "Repair the managed owner or inference application access binding."
    owner = resources.get("owner_user")
    if type(owner) is not int or owner < 1:
        return False, "Verify the enrolled owner's Lucia application access."
    access = get(f"/api/v3/core/applications/lucia/check_access/?for_user={owner}")
    preview = get(f"/api/v3/providers/oauth2/{provider_id}/preview_user/?for_user={owner}")
    ready = bool(access and access.get("passing") is True and preview
                 and preview.get("preview", {}).get("lucia_role") == "Owner")
    return ready, ("Owned registration and owner access checked; browser SSO has not been tested." if ready
                   else "Reconcile and verify the enrolled owner's application access and Owner claim.")


def host_probe(state, settings, docker, env):
    path = state / "host-settings.json"
    root = state.parent / "host"
    no_symlinks(root)
    if not path.exists():
        pending_models = None
        if root.exists():
            marker = read_json(root / "owner.json")
            if (marker.get("component") != "managedhost" or marker.get("identity_state") != str(state)
                    or marker.get("configured") is not False):
                raise SafeError("Managed host storage has no matching settings. Restore its record rather than adopting it.")
            pending_models = model_directory_value(marker.get("model_directory"))
        return False, True, None, pending_models
    record = read_json(path)
    installation = hashlib.sha256(str(state).encode()).hexdigest()
    if (not settings or record.get("schema_version") != 1 or record.get("component") != "managedhost"
            or record.get("installation_id") != installation or record.get("host_state") != str(root)
            or record.get("authentication", {}).get("public_origin") != host_origin(settings["public_host"])
            or not re.fullmatch(r"sha256:[a-f0-9]{64}", record.get("image_id", ""))):
        raise SafeError("Managed host settings do not match this installation. Restore them before deploying.")
    owned_path(root, private=True)
    marker = read_json(root / "owner.json")
    if (marker.get("component") != "managedhost" or marker.get("identity_state") != str(state)
            or marker.get("model_directory") != record.get("model_directory")):
        raise SafeError("The managed host storage ownership record does not match its settings.")
    models = model_directory_value(record.get("model_directory"))
    image = run_read([docker, "image", "inspect", record["image_id"], "--format", "{{json .Config.Labels}}"], env=env)
    available = image.returncode == 0
    if available:
        labels = json.loads(image.stdout)
        if (labels.get("io.lucia.component") != "managedhost"
                or labels.get("io.lucia.host-installation") != installation
                or labels.get("io.lucia.artifact-sha256") != record.get("artifact_sha256")):
            raise SafeError("The recorded host image is not owned by this installation.")
    found = run_read([docker, "inspect", "lucia-homelab-host", "--format",
                      '{"image":{{json .Image}},"state":{{json .State}},"labels":{{json .Config.Labels}}}'], env=env)
    ready = False
    if found.returncode == 0:
        container = json.loads(found.stdout)
        labels = container.get("labels") or {}
        if labels.get("io.lucia.component") != "managedhost" or labels.get("io.lucia.host-installation") != installation:
            raise SafeError("The host container name is owned by another deployment.")
        ready = (available and container["image"] == record["image_id"] and container["state"].get("Running") is True
                 and container["state"].get("Health", {}).get("Status") == "healthy")
        if ready:
            try:
                session = service_json(state, host_origin(settings["public_host"]) + "/api/auth/session")
                ready = session.get("enabled") is True and session.get("authenticated") is False
            except SafeError:
                ready = False
    return ready, not available, host_origin(settings["public_host"]), models


def inspect(configure_host=True):
    root, state = locations()
    checks = []

    def check(name, ready, message, action=False):
        checks.append({"name": name, "status": "ready" if ready else "action" if action else "blocked", "message": message})

    os_info = operating_system()
    arch = platform.machine().lower()
    supported = sys.platform == "linux" and arch in ("aarch64", "arm64", "x86_64", "amd64")
    supported = supported and os_info.get("ID") == "ubuntu" and os_info.get("VERSION_ID") in ("22.04", "24.04")
    check("Operating system", supported, "Supported Ubuntu Linux host." if supported
          else "This payload supports Ubuntu 22.04/24.04 on arm64 or x64, after NVIDIA OS first-run and SSH setup.")
    installed, settings, owner, legacy = False, None, None, False
    try:
        installed = state.exists() and any(state.iterdir())
        installed, settings, owner, legacy = installation_state(state, root / "app")
        check("Identity state", True, "Existing users, passwords, trust and volumes will be preserved." if installed else "No prior identity installation was found.")
    except (OSError, ValueError, KeyError, AttributeError, SafeError) as error:
        check("Identity state", False, str(error) if isinstance(error, SafeError) else "Identity state is unreadable or malformed. Recover it before continuing.")
    owner_ready = bool(owner and owner.get("complete"))
    verify_only = legacy or owner_ready
    host_ready, application_ready, host_package_required, host_url, model_directory = False, False, True, None, None
    env = tools_environment(root)
    docker = shutil.which("docker", path=env["PATH"])
    docker_info = run_read([docker, "info", "--format", "{{.ServerVersion}}"]) if docker else None
    docker_ready = bool(docker_info and docker_info.returncode == 0)
    check("Docker", docker_ready, "Docker daemon is accessible to the SSH account." if docker_ready
          else "Docker is missing, stopped, or inaccessible. Configure the existing NVIDIA Docker engine/account access first; desktop will not replace it.")
    own_ports, own_containers, rows = set(), [], []
    if docker_ready:
        inventory = run_read([docker, "ps", "-a", "--format", "{{json .}}"])
        try:
            if inventory.returncode:
                raise ValueError()
            rows = [json.loads(line) for line in inventory.stdout.splitlines() if line]
            collisions = [row for row in rows if row["Names"] in CONTAINERS
                          and "io.lucia.component=identity" not in row.get("Labels", "").split(",")]
            own_containers = [row["Names"] for row in rows if row["Names"] in CONTAINERS and row not in collisions]
            check("Container names", not collisions and (not own_containers or bool(settings)),
                  "Identity container names are available or owned by this installation." if not collisions and (not own_containers or settings)
                  else "Identity container names already exist without matching installation records. Recover the original deployment.")
            if own_containers:
                bindings = run_read([docker, "inspect", "--format", "{{json .NetworkSettings.Ports}}", *own_containers])
                mounts = run_read([docker, "inspect", "--format", "{{json .Mounts}}", *own_containers])
                if bindings.returncode or mounts.returncode:
                    raise ValueError()
                for line in bindings.stdout.splitlines():
                    for entries in (json.loads(line) or {}).values():
                        own_ports.update(int(entry["HostPort"]) for entry in entries or [])
                for line in mounts.stdout.splitlines():
                    for mount in json.loads(line):
                        if mount["Type"] == "bind" and not pathlib.Path(mount["Source"]).is_relative_to(state):
                            raise SafeError("Identity containers use another installation's storage. Refusing to adopt them.")
            volumes = run_read([docker, "volume", "ls", "--format", "{{.Name}}"])
            if volumes.returncode:
                raise ValueError()
            orphaned = not settings and any("lucia-identity-postgres" in name or "lucia-authentik-data" in name
                                            for name in volumes.stdout.splitlines())
            check("Persistent volumes", not orphaned, "No unrecorded identity volumes were found." if not orphaned
                  else "Identity volumes exist without settings. Restore their installation record before continuing.")
        except (ValueError, KeyError, TypeError, SafeError) as error:
            check("Container inventory", False, str(error) if isinstance(error, SafeError) else "Docker inventory could not be verified.")
        if configure_host:
            try:
                host_ready, host_package_required, host_url, model_directory = host_probe(state, settings, docker, env)
                host_rows = [row for row in rows if row["Names"] == "lucia-homelab-host"]
                if host_rows and not (state / "host-settings.json").exists():
                    raise SafeError("A host container exists without its installation record. It will not be adopted.")
                check("Lucia host", host_ready, "Managed host health and HTTPS session endpoint verified." if host_ready
                      else "Deploy or repair the managed Linux ARM64 host and dashboard.", action=True)
                if settings:
                    application_ready, message = application_probe(state, settings)
                else:
                    message = "Create the owned Lucia OIDC application after identity and host health are verified."
                check("Lucia sign-in", application_ready, message, action=True)
            except (OSError, ValueError, KeyError, TypeError, SafeError) as error:
                check("Managed host state", False, str(error) if isinstance(error, SafeError) else "Managed host state could not be safely verified.")
        if not verify_only or (configure_host and not host_ready):
            version = re.match(r"(\d+)\.", docker_info.stdout.strip())
            check("Docker version", bool(version and int(version[1]) >= 28),
                  "Docker meets Aspire's version requirement." if version and int(version[1]) >= 28
                  else "Aspire requires Docker 28 or newer. Review an NVIDIA-compatible engine upgrade before installing.")
            compose = run_read([docker, "compose", "version", "--short"])
            check("Docker Compose", compose.returncode == 0, "Docker Compose is available." if not compose.returncode
                  else "Install the official Docker Compose plugin for the existing engine before continuing.")
    for name in ("openssl",):
        available = shutil.which(name, path=env["PATH"]) is not None
        check(name, available, "OpenSSL is available." if available else "OpenSSL is required; install it using the OS package manager.")
    if configure_host:
        check("Host architecture", arch in ("aarch64", "arm64"), "The managed host package requires Linux ARM64.")
        check("Python runtime", sys.version_info >= (3, 11), "Managed host packaging requires Python 3.11 or newer on Spark.")
        if not host_ready:
            cdi = run_read(["nvidia-ctk", "cdi", "list"], env=env)
            check("NVIDIA CDI", cdi.returncode == 0 and "nvidia.com/gpu=all" in cdi.stdout.split(),
                  "Host deployment requires the existing nvidia.com/gpu=all CDI device; no driver changes will be made.")
    if not verify_only or (configure_host and not host_ready):
        for name, version in (("dotnet", DOTNET_VERSION), ("aspire", ASPIRE_VERSION)):
            available = shutil.which(name, path=env["PATH"]) is not None
            check(name, False, f"Check {name} {version}; install a verified, pinned user-local copy if needed."
                  if available else f"Install {name} {version} user-locally from official checksummed distribution.", action=True)
    try:
        wanted_ports = set(settings["ports"].values()) if settings else {9443, 636, 9444}
        if configure_host:
            if 443 in wanted_ports:
                check("Lucia HTTPS port", False, "Port 443 is already assigned to an identity endpoint. Review a migration.")
            wanted_ports.add(443)
        occupied = (listening_ports() & wanted_ports) - own_ports if sys.platform == "linux" else set()
        check("Network ports", not occupied, "Required ports are free or belong to the existing identity services." if not occupied
              else "An unrelated service occupies a required identity port. Resolve the collision before installing.")
        free = shutil.disk_usage(state if state.exists() else pathlib.Path.home()).free
        enough = free >= (256 * 1024 * 1024 if verify_only and (not configure_host or host_ready) else 12 * 1024 ** 3)
        check("Disk space", enough, "Sufficient available disk space." if enough else "At least 12 GiB free is required for a fresh installation (256 MiB for verification).")
    except (OSError, ValueError, KeyError):
        check("Host resources", False, "Port or disk availability could not be verified.")
    active_job = None
    active_job_configure_host = False
    public_host = settings["public_host"] if settings else None
    owner_username = owner["username"] if owner else None
    try:
        no_symlinks(root)
        if sys.platform == "linux":
            if (root / "active.json").exists():
                candidate = read_json(root / "active.json").get("job_id", "")
                if not JOB_ID.fullmatch(candidate):
                    raise ValueError()
                active = job_status(candidate, persist=False)
                if active["status"] == "running":
                    reviewed = validate_review(read_json(job_directory(candidate) / "request.json"))
                    if ((public_host and reviewed["public_host"] != public_host)
                            or (owner_username and reviewed["owner_username"] != owner_username)):
                        raise SafeError("The active job's reviewed identity disagrees with the existing installation.")
                    public_host = public_host or reviewed["public_host"]
                    owner_username = owner_username or reviewed["owner_username"]
                    model_directory = reviewed.get("model_directory") or model_directory
                    active_job = candidate
                    active_job_configure_host = reviewed.get("configure_host", False)
                    check("Setup job", False, "A durable setup job is active. Reconnect to its progress; do not start another.", action=True)
            if lock_held(state / ".provision.lock") and not active_job:
                check("Provisioner", False, "The identity command-line provisioner is active. Wait before starting desktop setup.")
    except (OSError, ValueError, TypeError, SafeError):
        check("Setup jobs", False, "Desktop job state is unsafe or malformed. Review it before continuing.")
    return {
        "schema_version": SCHEMA, "hostname": socket.gethostname(), "architecture": arch,
        "operating_system": os_info.get("PRETTY_NAME", platform.system()),
        "state_directory": str(state), "is_installed": installed, "owner_ready": owner_ready,
        "owner_username": owner_username,
        "public_host": public_host,
        "can_install": all(item["status"] != "blocked" for item in checks), "requires_sudo": False,
        "active_job_id": active_job, "active_job_configure_host": active_job_configure_host, "checks": checks,
        "host_ready": host_ready, "application_ready": application_ready, "host_url": host_url,
        "host_package_required": host_package_required, "model_directory": model_directory,
        "planned_changes": (["Verify existing identity services and supplied owner login; preserve accounts, credentials, and storage."]
                            if verify_only else [
                                "Install pinned, checksummed user-local .NET/Aspire tools if approved and needed.",
                                "Publish and deploy the existing Identity AppHost from a stable user-local path.",
                                "Preserve existing CA, LDAP, Authentik data and enroll the requested LDAP owner.",
                                "Verify real LDAP-backed Authentik sign-in; return the public CA without changing client trust.",
                            ]) + ([
                                "Deploy or repair Lucia on HTTPS port 443 using the existing identity gateway and NVIDIA CDI; preserve identity data.",
                                "Reconcile the owned Lucia OIDC application and owner access, even if a prior registration was deleted.",
                                "Use the reviewed existing model directory or an empty managed directory; no model weights are downloaded.",
                            ] if configure_host else []),
        "authentik_url": url(settings["public_host"], settings["ports"]["authentik"]) if settings else None,
    }


def validate_review(request):
    if (not isinstance(request, dict) or not REVIEW_FIELDS <= set(request) or set(request) - (REVIEW_FIELDS | HOST_FIELDS)
            or type(request["schema_version"]) is not int or request["schema_version"] != SCHEMA):
        raise SafeError("Setup request does not match protocol schema 1.")
    if any(type(request[name]) is not bool for name in ("install_prerequisites", "verify_only")):
        raise SafeError("Setup approval flags must be booleans.")
    request["public_host"] = host_value(request["public_host"])
    username_value(request["owner_username"])
    if type(request.get("configure_host", False)) is not bool:
        raise SafeError("Host setup approval must be a boolean.")
    if "model_directory" in request:
        request["model_directory"] = model_directory_value(request["model_directory"])
    package = request.get("host_package")
    if package is not None and (not isinstance(package, dict) or set(package) != {"archive_path", "manifest_path", "sha256", "size"}
            or any(not isinstance(package[key], str) for key in ("archive_path", "manifest_path", "sha256"))
            or not re.fullmatch(r"[a-f0-9]{64}", package["sha256"])
            or type(package["size"]) is not int or not 0 < package["size"] <= MAX_HOST_ARCHIVE):
        raise SafeError("Host package metadata is invalid.")
    if package is not None:
        cache = pathlib.Path.home() / ".cache/lucia-desktop"
        paths = [pathlib.Path(package[key]) for key in ("archive_path", "manifest_path")]
        if (any(not path.is_absolute() or ".." in path.parts or not path.is_relative_to(cache)
                or len(path.relative_to(cache).parts) != 2 for path in paths)
                or paths[0].parent != paths[1].parent or paths[0] == paths[1]):
            raise SafeError("Host package metadata must name distinct files in one private upload stage.")
    if not request.get("configure_host") and (package is not None or request.get("model_directory") is not None):
        raise SafeError("Host package and model options require explicit host setup approval.")
    return request


def validate_request(request):
    if (not isinstance(request, dict) or not REVIEW_FIELDS | {"owner_password", "sudo_password"} <= set(request)
            or set(request) - (REVIEW_FIELDS | HOST_FIELDS | {"owner_password", "sudo_password"})):
        raise SafeError("Setup request does not match protocol schema 1.")
    request.update(validate_review({key: value for key, value in request.items() if key not in ("owner_password", "sudo_password")}))
    login_password_value(request["owner_password"])
    if request["sudo_password"] is not None and not isinstance(request["sudo_password"], str):
        raise SafeError("Invalid privileged prerequisite credential.")
    if request["sudo_password"]:
        raise SafeError("This version does not perform privileged prerequisite installation. Do not supply a sudo password.")
    return request


def validated_payload(archive, expected_hash):
    if not re.fullmatch(r"[a-fA-F0-9]{64}", expected_hash or ""):
        raise SafeError("Payload archive SHA-256 is missing or malformed.")
    packed = read_bytes(archive, MAX_PAYLOAD)
    if hashlib.sha256(packed).hexdigest() != expected_hash.lower():
        raise SafeError("Payload archive SHA-256 does not match.")
    files, total, seen = {}, 0, set()
    directories = {str(parent) for path in FILES for parent in pathlib.PurePosixPath(path).parents if str(parent) != "."}
    try:
        with tarfile.open(fileobj=io.BytesIO(packed), mode="r|gz") as tar:
            for member in tar:
                name = member.name.rstrip("/") if member.isdir() else member.name
                if name in seen or len(seen) >= 64 or member.size < 0 or member.size > MAX_FILE:
                    raise SafeError("Payload archive contains duplicate or oversized entries.")
                seen.add(name)
                if member.isdir() and name in directories and member.size == 0:
                    continue
                if not member.isfile() or name not in FILES | {"manifest.json"} or member.issparse():
                    raise SafeError("Payload contains an unapproved path, link, or special file.")
                total += member.size
                if total > MAX_PAYLOAD:
                    raise SafeError("Expanded payload exceeded its size limit.")
                files[name] = tar.extractfile(member).read(member.size + 1)
    except (tarfile.TarError, EOFError, OSError):
        raise SafeError("Payload archive is not a valid bounded tar.gz file.") from None
    if set(files) != FILES | {"manifest.json"}:
        raise SafeError("Payload is missing required source files or its manifest.")
    try:
        manifest = json.loads(files.pop("manifest.json"))
        if (set(manifest) != {"schema_version", "version", "files"} or type(manifest["schema_version"]) is not int
                or manifest["schema_version"] != SCHEMA or manifest["version"] != VERSION):
            raise ValueError()
        entries = manifest["files"]
        if not isinstance(entries, list) or len(entries) != len(FILES):
            raise ValueError()
        mapped = {}
        for item in entries:
            if set(item) != {"path", "sha256"} or item["path"] in mapped:
                raise ValueError()
            mapped[item["path"]] = item["sha256"]
        if set(mapped) != FILES:
            raise ValueError()
        for name, data in files.items():
            if not isinstance(mapped[name], str) or mapped[name].lower() != hashlib.sha256(data).hexdigest():
                raise ValueError()
            if re.search(rb"-----BEGIN " + rb"(?:RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----", data):
                raise SafeError("Private key material is forbidden in the source payload.")
    except (ValueError, TypeError, KeyError, AttributeError):
        raise SafeError("Payload manifest schema or file hashes are invalid.") from None
    return files, manifest


def upload_file(path, expected_stage=None, limit=MAX_HOST_ARCHIVE):
    path = pathlib.Path(path)
    cache = pathlib.Path.home() / ".cache/lucia-desktop"
    if (not path.is_absolute() or ".." in path.parts or not path.is_relative_to(cache)
            or len(path.relative_to(cache).parts) != 2
            or (expected_stage is not None and path.parent != expected_stage)):
        raise SafeError("Packages must come from the same private ~/.cache/lucia-desktop upload stage.")
    owned_path(cache, private=True)
    owned_path(path.parent, private=True)
    info = owned_path(path, private=True)
    if not stat.S_ISREG(info.st_mode) or not 0 < info.st_size <= limit:
        raise SafeError("The uploaded package is not a bounded regular file.")
    return path


def validated_host_package(package, stage=None):
    archive = upload_file(package["archive_path"], stage)
    manifest_path = upload_file(package["manifest_path"], archive.parent, MAX_HOST_MANIFEST)
    if archive == manifest_path or archive.stat().st_size != package["size"]:
        raise SafeError("Host archive and manifest paths or archive size are invalid.")
    manifest = json.loads(read_bytes(manifest_path, MAX_HOST_MANIFEST))
    if (not isinstance(manifest, dict) or manifest.get("schema_version") != 1
            or type(manifest.get("schema_version")) is not int
            or manifest.get("version") != VERSION or manifest.get("rid") != "linux-arm64"
            or manifest.get("entrypoint") != "Lucia.Homelab.Server.dll"
            or manifest.get("sha256") != package["sha256"] or manifest.get("size") != package["size"]):
        raise SafeError("The host manifest does not match the reviewed Linux ARM64 package.")
    names = set()
    total = 0
    entries = manifest.get("files")
    if not isinstance(entries, list) or not 2 <= len(entries) <= 20000:
        raise SafeError("The host manifest file list is invalid.")
    for entry in entries:
        name = entry.get("path") if isinstance(entry, dict) else None
        if (not isinstance(name, str) or not re.fullmatch(r"[A-Za-z0-9_./@+-]{1,240}", name)
                or any(part in ("", ".", "..") or part.endswith(".") for part in name.split("/"))
                or name.casefold() in names or type(entry.get("size")) is not int or not 0 <= entry["size"] <= 1024 ** 3
                or not re.fullmatch(r"[a-f0-9]{64}", entry.get("sha256", ""))):
            raise SafeError("The host manifest contains unsafe paths, duplicates, or invalid file metadata.")
        names.add(name.casefold())
        total += entry["size"]
    if total > MAX_HOST_ARCHIVE or not {"lucia.homelab.server.dll", "wwwroot/index.html"} <= names:
        raise SafeError("The host manifest is oversized or missing the host/dashboard entrypoint.")
    with archive.open("rb") as stream:
        digest = hashlib.sha256()
        while block := stream.read(1024 * 1024):
            digest.update(block)
    if digest.hexdigest() != package["sha256"]:
        raise SafeError("The uploaded host archive failed SHA-256 verification.")
    return archive, manifest_path


def job_directory(job_id):
    if not isinstance(job_id, str) or not JOB_ID.fullmatch(job_id):
        raise SafeError("Invalid setup job identifier.")
    root, _ = locations()
    path = root / "jobs" / job_id
    no_symlinks(path)
    return path


def failed_status(job_id, message):
    return {"schema_version": SCHEMA, "job_id": job_id, "status": "failed", "events": [],
            "error": message, "result": None}


def validate_result(result, configure_host=False):
    required = {"authentik_url", "ldap_url", "owner_username", "root_certificate_pem", "root_fingerprint", "owner_login_verified"}
    if (not isinstance(result, dict) or not required <= set(result)
            or set(result) - (required | {"host_url", "host_ready", "application_ready", "sso_verified"})):
        raise SafeError("The setup result is incomplete.")
    username_value(result["owner_username"])
    pem = result["root_certificate_pem"]
    if not isinstance(pem, str) or len(pem) > 32768 or "PRIVATE KEY" in pem or pem.count("BEGIN CERTIFICATE") != 1:
        raise SafeError("The setup public certificate is invalid.")
    fingerprint = hashlib.sha256(ssl.PEM_cert_to_DER_cert(pem)).hexdigest()
    ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT).load_verify_locations(cadata=pem)
    if result["root_fingerprint"] != fingerprint or result["owner_login_verified"] is not True:
        raise SafeError("The setup result does not prove owner login and public certificate identity.")
    for name, scheme in (("authentik_url", "https"), ("ldap_url", "ldaps")):
        parsed = urllib.parse.urlsplit(result[name])
        if parsed.scheme != scheme or parsed.username or parsed.password or not parsed.hostname or not parsed.port or parsed.path or parsed.query or parsed.fragment:
            raise SafeError("The setup endpoint is malformed.")
        host_value(parsed.hostname)
    if any(type(result.get(key, False)) is not bool for key in ("host_ready", "application_ready", "sso_verified")):
        raise SafeError("The managed host readiness flags are invalid.")
    if configure_host and (result.get("host_ready") is not True or result.get("application_ready") is not True or not result.get("host_url")):
        raise SafeError("Managed host health and owned application access must both be verified before completion.")
    if result.get("host_url") is not None:
        parsed = urllib.parse.urlsplit(result["host_url"])
        if (parsed.scheme != "https" or parsed.port not in (None, 443) or parsed.username or parsed.password
                or parsed.path not in ("", "/") or parsed.query or parsed.fragment
                or parsed.hostname != urllib.parse.urlsplit(result["authentik_url"]).hostname):
            raise SafeError("The managed host URL does not match the reviewed HTTPS identity host.")
    if result.get("sso_verified"):
        raise SafeError("This bootstrap does not perform a browser OIDC exchange.")


def job_status(job_id, persist=True):
    if not isinstance(job_id, str) or not JOB_ID.fullmatch(job_id):
        raise SafeError("Invalid setup job identifier.")
    root, _ = locations()
    try:
        directory = job_directory(job_id)
        record = read_json(directory / "status.json")
        if (set(record) != {"schema_version", "job_id", "status", "events", "error", "result"}
                or type(record["schema_version"]) is not int or record["schema_version"] != SCHEMA or record["job_id"] != job_id
                or record["status"] not in ("running", "succeeded", "failed")
                or not isinstance(record["events"], list) or len(record["events"]) > MAX_EVENTS):
            raise ValueError()
        for event in record["events"]:
            if (set(event) != {"phase", "message", "level"} or event["level"] not in ("info", "error")
                    or not isinstance(event["phase"], str) or not re.fullmatch(r"[a-z-]{1,32}", event["phase"])
                    or not isinstance(event["message"], str) or len(event["message"]) > 500):
                raise ValueError()
        if record["error"] is not None and (not isinstance(record["error"], str) or len(record["error"]) > 500):
            raise ValueError()
        if record["status"] == "failed" and not record["error"]:
            raise ValueError()
        if record["status"] == "running":
            active = read_json(root / "active.json").get("job_id")
            identity = read_json(directory / "worker.json")
            alive = identity.get("start_ticks") and process_identity(identity.get("pid")) == identity["start_ticks"]
            if active != job_id or not alive or not lock_held(root / ".run.lock"):
                record["status"], record["error"], record["result"] = "failed", "The remote worker stopped before completing. Review the last phase and retry; existing identity data was preserved.", None
                if persist:
                    write_json(directory / "status.json", record)
                    remove_job_password(directory)
        if record["status"] == "succeeded":
            if record["error"] is not None:
                raise ValueError()
            reviewed = validate_review(read_json(directory / "request.json"))
            validate_result(record["result"], reviewed.get("configure_host", False))
        elif record["result"] is not None:
            raise ValueError()
        return record
    except (OSError, ValueError, KeyError, TypeError, SafeError):
        return failed_status(job_id, "Setup job state is missing or malformed. Success could not be verified.")


def remove_job_password(directory):
    path = directory / "owner-password"
    no_symlinks(path)
    path.unlink(missing_ok=True)


def check_review(request, inspection):
    if not inspection["can_install"]:
        blocked = [check for check in inspection.get("checks", []) if check["status"] == "blocked"]
        if blocked:
            raise SafeError("Preflight blocked: " + blocked[0]["message"])
        raise SafeError("Read-only preflight found a blocker. Inspect this host again and resolve the blocked checks.")
    if inspection["public_host"] and request["public_host"] != inspection["public_host"]:
        raise SafeError("The existing public host is immutable. Review an explicit migration instead.")
    if inspection["owner_username"] and request["owner_username"] != inspection["owner_username"]:
        raise SafeError("The existing owner cannot be changed by setup. Use the enrolled owner's username.")
    if request["verify_only"] and not inspection["owner_ready"]:
        raise SafeError("Verify-only requires a completed owner enrollment; partial desktop installs must resume setup.")
    if inspection["owner_ready"] is True:
        login_password_value(request["owner_password"])
    else:
        password_value(request["owner_password"])
    if request.get("configure_host"):
        if (inspection.get("model_directory") and request.get("model_directory") is not None
                and request["model_directory"] != inspection["model_directory"]):
            raise SafeError("The existing model directory cannot be changed implicitly. Review an explicit migration.")
        if inspection.get("host_package_required", True) and not request.get("host_package"):
            raise SafeError("A complete Linux ARM64 host bundle is required before this installation can proceed.")


def start(archive, expected_hash, request):
    if sys.platform != "linux":
        raise SafeError("Remote setup must run on the Linux host.")
    import fcntl
    request = validate_request(request)
    root, _ = locations()
    private_directory(root)
    no_symlinks(root / ".run.lock")
    with (root / ".run.lock").open("a+b") as lock:
        os.chmod(root / ".run.lock", 0o600)
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            for _ in range(50):
                try:
                    active_id = read_json(root / "active.json")["job_id"]
                    active = job_status(active_id, persist=False)
                    if active["status"] == "running":
                        return {"schema_version": SCHEMA, "job_id": active_id, "status": "running"}
                except (OSError, ValueError, KeyError, SafeError):
                    pass
                time.sleep(0.1)
            raise SafeError("Another setup process owns the installation lock. Reinspect before retrying.")
        inspection = inspect(request.get("configure_host", False))
        check_review(request, inspection)
        archive = upload_file(archive, limit=MAX_PAYLOAD)
        files, manifest = validated_payload(archive, expected_hash)
        if request.get("host_package"):
            # Runtime bytes stay in this private stage while the detached worker uses them.
            validated_host_package(request["host_package"], archive.parent)
        job_id = uuid.uuid4().hex
        directory = job_directory(job_id)
        private_directory(directory)
        for name, data in files.items():
            write_bytes(directory / "payload" / name, data)
        write_json(directory / "payload/manifest.json", manifest)
        metadata = {key: value for key, value in request.items() if key not in ("owner_password", "sudo_password")}
        write_json(directory / "request.json", metadata)
        record = {"schema_version": SCHEMA, "job_id": job_id, "status": "running",
                  "events": [{"phase": "queued", "message": "Reviewed setup accepted; remote work continues if you disconnect.", "level": "info"}],
                  "error": None, "result": None}
        write_json(directory / "status.json", record)
        write_json(directory / "worker.json", {"pid": os.getpid(), "start_ticks": process_identity(os.getpid())})
        write_json(root / "active.json", {"job_id": job_id})
        try:
            write_bytes(directory / "owner-password", request["owner_password"].encode())
            # The child inherits this exact flock, closing the handoff/duplicate-worker race.
            child = subprocess.Popen([sys.executable, str(directory / "payload/tools/desktop/bootstrap.py"),
                                      "_worker", "--job-id", job_id, "--lock-fd", str(lock.fileno())],
                                     stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                     start_new_session=True, close_fds=True, pass_fds=(lock.fileno(),),
                                     cwd=directory, env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"})
            identity = process_identity(child.pid)
            if identity:
                write_json(directory / "worker.json", {"pid": child.pid, "start_ticks": identity})
        except OSError:
            write_json(directory / "status.json", failed_status(job_id, "The durable worker could not be started. Retry after checking Python/process availability."))
            remove_job_password(directory)
            raise SafeError("The durable worker could not be started.") from None
        return {"schema_version": SCHEMA, "job_id": job_id, "status": "running"}


class SecureRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        approved_download_url(newurl)
        return super().redirect_request(request, fp, code, msg, headers, newurl)


def approved_download_url(value):
    parsed = urllib.parse.urlsplit(value)
    if parsed.scheme != "https" or parsed.username or parsed.password or parsed.hostname not in {
            "builds.dotnet.microsoft.com", "dotnetcli.blob.core.windows.net", "dotnetcli.azureedge.net",
            "github.com", "release-assets.githubusercontent.com"}:
        raise SafeError("A prerequisite download did not use an approved official HTTPS endpoint.")


def download(url_value, limit, destination=None):
    approved_download_url(url_value)
    opener = urllib.request.build_opener(SecureRedirect(), urllib.request.HTTPSHandler(context=ssl.create_default_context()))
    try:
        with opener.open(urllib.request.Request(url_value, headers={"User-Agent": "Lucia-Desktop-Setup/0.1"}), timeout=120) as response:
            approved_download_url(response.geturl())
            if destination is None:
                data = response.read(limit + 1)
                if len(data) > limit:
                    raise SafeError("Prerequisite metadata exceeded its size limit.")
                return data
            no_symlinks(destination)
            size = 0
            digest = hashlib.sha512()
            with destination.open("xb") as output:
                os.chmod(destination, 0o600)
                while block := response.read(1024 * 1024):
                    size += len(block)
                    if size > limit:
                        raise SafeError("A prerequisite download exceeded its size limit.")
                    digest.update(block)
                    output.write(block)
            return digest.hexdigest()
    except OSError:
        raise SafeError("The official prerequisite download failed. Check outbound HTTPS access, OS certificate trust, and available disk space; TLS verification was not disabled.") from None


def extract_distribution(archive, directory):
    private_directory(directory)
    total, names = 0, set()
    with tarfile.open(archive, "r|gz") as tar:
        for member in tar:
            name = member.name
            while name.startswith("./"):
                name = name[2:]
            if name in ("", ".") and member.isdir():
                continue
            relative = pathlib.PurePosixPath(name)
            if (relative.is_absolute() or ".." in relative.parts or "\\" in name or ":" in name
                    or name in names or len(names) >= 100000 or not (member.isdir() or member.isfile())
                    or member.issparse() or member.size < 0 or member.size > 512 * 1024 ** 2):
                raise SafeError("An official prerequisite archive contains an unsafe or oversized entry.")
            names.add(name)
            total += member.size
            if total > 4 * 1024 ** 3:
                raise SafeError("An expanded prerequisite exceeded its size limit.")
            target = directory / relative
            no_symlinks(target)
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True, mode=0o700)
            else:
                target.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
                with target.open("xb") as output:
                    shutil.copyfileobj(tar.extractfile(member), output, 1024 * 1024)
                os.chmod(target, 0o700 if member.mode & 0o111 else 0o600)


def prerequisites(root, directory, approved, event):
    env = tools_environment(root)
    dotnet = shutil.which("dotnet", path=env["PATH"])
    aspire = shutil.which("aspire", path=env["PATH"])
    dotnet_ok = dotnet and any(line.startswith(DOTNET_VERSION + " ") for line in run_read([dotnet, "--list-sdks"], env=env).stdout.splitlines())
    aspire_version = run_read([aspire, "--version"], env=env) if aspire else None
    aspire_ok = aspire_version and aspire_version.returncode == 0 and re.search(r"(?<![\d.])" + re.escape(ASPIRE_VERSION) + r"(?![\d.])", aspire_version.stdout)
    if (not dotnet_ok or not aspire_ok) and not approved:
        raise SafeError("Pinned .NET/Aspire prerequisites are needed. Review and approve user-local prerequisite installation, then retry.")
    rid = "linux-arm64" if platform.machine().lower() in ("aarch64", "arm64") else "linux-x64"
    for name, version, ready in (("dotnet", DOTNET_VERSION, dotnet_ok), ("aspire", ASPIRE_VERSION, aspire_ok)):
        if ready:
            continue
        event("prerequisites", "Downloading and verifying pinned " + name + " " + version + " from official release metadata.")
        if name == "dotnet":
            metadata = json.loads(download("https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json", 8 * 1024 * 1024))
            sdks = [sdk for release in metadata["releases"] for sdk in release.get("sdks", [release.get("sdk", {})]) if sdk.get("version") == version]
            candidates = [item for sdk in sdks for item in sdk["files"] if item["rid"] == rid and item["name"].endswith(".tar.gz")]
            if not candidates or any(candidate != candidates[0] for candidate in candidates):
                raise SafeError("The pinned .NET SDK was not uniquely described by official release metadata.")
            asset_url, expected = candidates[0]["url"], candidates[0]["hash"].lower()
        else:
            asset_url = f"https://github.com/microsoft/aspire/releases/download/v{version}/aspire-cli-{rid}-{version}.tar.gz"
            expected = download(asset_url + ".sha512", 1024).decode().strip().lower()
        if not re.fullmatch(r"[a-f0-9]{128}", expected):
            raise SafeError("Official prerequisite SHA-512 metadata is malformed.")
        archive = directory / (name + "-" + uuid.uuid4().hex + ".tar.gz")
        if download(asset_url, 800 * 1024 * 1024, archive) != expected:
            raise SafeError("Prerequisite download failed SHA-512 verification.")
        runtime = root / "runtimes" / (name + "-" + version)
        if runtime.exists():
            raise SafeError("A pinned user-local runtime exists but failed verification. Review it before replacing any files.")
        staging = runtime.with_name(runtime.name + "." + uuid.uuid4().hex)
        extract_distribution(archive, staging)
        if not (staging / name).is_file():
            raise SafeError("The official prerequisite archive did not contain its expected executable.")
        write_json(staging / "lucia-distribution.json", {"name": name, "version": version, "sha512": expected})
        os.rename(staging, runtime)
        archive.unlink()
        env = tools_environment(root)
    dotnet = shutil.which("dotnet", path=env["PATH"])
    if not dotnet:
        raise SafeError("The installed .NET SDK could not be located.")
    env["DOTNET_ROOT"] = str(pathlib.Path(dotnet).resolve().parent)
    env["DOTNET_ROOT_ARM64"] = env["DOTNET_ROOT"]
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    if not any(line.startswith(DOTNET_VERSION + " ") for line in run_read([dotnet, "--list-sdks"], env=env).stdout.splitlines()):
        raise SafeError("The pinned .NET SDK cannot execute. Check the host's official OS runtime dependencies.")
    aspire = shutil.which("aspire", path=env["PATH"])
    result = run_read([aspire, "--version"], env=env) if aspire else None
    if not result or result.returncode or not re.search(r"(?<![\d.])" + re.escape(ASPIRE_VERSION) + r"(?![\d.])", result.stdout):
        raise SafeError("The pinned Aspire CLI cannot execute. Check the host's official OS runtime dependencies.")
    setup = run_read([aspire, "setup", "--non-interactive"], timeout=300, env=env)
    if setup.returncode:
        raise SafeError("Aspire CLI bundle setup failed. Check official distribution/network and OS runtime dependencies.")
    return env


def stable_payload(root, directory):
    app = root / "app"
    no_symlinks(app)
    marker = app / ".desktop-managed.json"
    ownership = {"schema_version": SCHEMA, "stable_path": str(app)}
    if marker.exists() and read_json(marker) != ownership:
        raise SafeError("The stable install ownership record is inconsistent.")
    if app.exists() and not marker.exists() and not (app / "manifest.json").is_file() and any(app.iterdir()):
        raise SafeError("The stable install directory contains unrecorded files. Review it instead of overwriting it.")
    sdk = {"sdk": {"version": DOTNET_VERSION, "rollForward": "disable", "allowPrerelease": False}}
    pin = app / "src/Lucia.Homelab.Identity.AppHost/global.json"
    if pin.exists() and read_json(pin) != sdk:
        raise SafeError("The stable AppHost has different SDK settings. Review them before replacing any files.")
    write_json(marker, ownership)
    manifest = read_json(directory / "payload/manifest.json")
    for item in manifest["files"]:
        data = read_bytes(directory / "payload" / item["path"])
        if hashlib.sha256(data).hexdigest() != item["sha256"].lower():
            raise SafeError("A staged payload file changed before activation.")
        write_bytes(app / item["path"], data)
    write_json(app / "manifest.json", manifest)
    write_json(pin, sdk)
    return app


def execute_setup(directory, request, password, event):
    root, state = locations()
    inspection = inspect(request.get("configure_host", False))
    check_review({**request, "owner_password": password}, inspection)
    private_directory(state)
    no_symlinks(state / ".provision.lock")
    import fcntl
    with (state / ".provision.lock").open("a+b") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise SafeError("The identity command-line provisioner is active; retry when it has finished.") from None
        _, settings, owner_record, legacy = installation_state(state, root / "app")
        check_review({**request, "owner_password": password}, {
            "can_install": True, "public_host": settings["public_host"] if settings else None,
            "owner_username": owner_record["username"] if owner_record else None,
            "owner_ready": bool(owner_record and owner_record["complete"]),
            "host_package_required": inspection.get("host_package_required", True),
            "model_directory": inspection.get("model_directory"),
        })
        verify_only = request["verify_only"] or legacy or bool(owner_record and owner_record["complete"])
        return provision_locked(root, state, directory, request, password, verify_only, event,
                                host_ready=inspection.get("host_ready", False))


def provision_locked(root, state, directory, request, password, verify_only, event, host_ready=False):
    configure_host = request.get("configure_host", False)
    if verify_only and not configure_host:
        source = directory / "payload"
        environment = tools_environment(root)
    else:
        environment = (tools_environment(root) if verify_only and host_ready
                       else prerequisites(root, directory, request["install_prerequisites"], event))
        source = stable_payload(root, directory)
    sys.path.insert(0, str(source / "tools/identity"))
    import provision
    import owner
    if configure_host:
        import application
        sys.path.insert(0, str(source / "tools/host"))
        import provision_host

    class DesktopProvisioner(provision.Provisioner):
        def run(self, command, *, check=True, input_text=None, cwd=None):
            # Never persist subprocess output: upstream diagnostics may contain derived credentials.
            result = subprocess.run(command, cwd=cwd or source, env=self.environment, text=True, encoding="utf-8",
                                    input=input_text, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                    timeout=1800, close_fds=True)
            if check and result.returncode:
                raise SafeError(provision.command_failure_message(command, result.returncode))
            if command[0] == "aspire":
                result.stdout, result.stderr = "", ""
            return result

        def check_ldap_tls(self):
            try:
                return super().check_ldap_tls()
            except provision.IdentityReadinessError as error:
                raise SafeError(str(error)) from None

    args = argparse.Namespace(state=str(state), host=request["public_host"], certificate_mode="private-ca",
                              auth_port=None, ldap_port=None, ca_port=None)
    p = DesktopProvisioner(args)
    p.environment.update(environment)
    p.environment["LUCIA_IDENTITY_STATE"] = str(state)
    p.values["owner-request-password"] = password
    with open(os.devnull, "w") as quiet, contextlib.redirect_stdout(quiet), contextlib.redirect_stderr(quiet):
        if verify_only:
            event("verify", "Verifying the existing deployment without redeploying, reconciling permissions, or changing trust.")
            p.load_existing()
            p.verify()
            record = owner.load_owner(p)
            event("owner-login", "Verifying the supplied owner password through real LDAP-backed Authentik sign-in.")
            owner.verify_login(p, record, owner.owner_identity(p, record), password=password)
            owner.remove_request_credential(p, record)
        else:
            event("prepare", "Preparing identity inputs while preserving existing credentials and trust.")
            journal = state / "desktop-prepare.json"
            if not (state / "settings.json").exists() and not journal.exists():
                write_json(journal, provision.settings_for(args))
            p.prepare()
            if journal.exists():
                if read_json(journal) != p.settings:
                    raise SafeError("The interrupted preparation settings differ from the installed identity.")
                journal.unlink()
            event("publish", "Publishing the Identity AppHost from its stable user-local path.")
            p.publish()
            event("deploy", "Deploying CA, LDAP and Authentik containers with their persistent storage.")
            p.deploy()
            event("certificates", "Verifying the private CA and service certificate; client trust is unchanged.")
            p.certificates()
            event("ldap", "Verifying directory service access over trusted LDAPS.")
            p.ldap()
            event("authentik", "Reconciling the verified LDAP source and waiting for actual synchronization.")
            provision.configure_authentik(p)
            event("owner", "Enrolling the directory owner without resetting any existing password.")
            record = owner.enroll_ldap_owner(p, request["owner_username"], password=password)
            ldap_source = p.api("GET", "/api/v3/sources/ldap/lucia-ldap/")
            provision.sync_ldap(p, ldap_source["pk"], ldap_source["peer_certificate"])
            event("owner-login", "Verifying real LDAP-backed owner sign-in and revoking the verification session.")
            owner.finish_owner(p, record, password=password)
            p.verify()
            p.summary()
        if configure_host:
            origin = host_origin(p.settings["public_host"])
            event("host-auth", "Preparing stable local OIDC credentials; existing client secrets and identity passwords are preserved.")
            authentication = application.prepare_application(p, origin)
            package = request.get("host_package")
            if package:
                event("host-package", "Verifying and staging the uploaded ARM64 runtime; no model weights are downloaded.")
                archive, manifest = validated_host_package(package)
                provision_host.prepare_host(p, archive, manifest, authentication, model_directory=request.get("model_directory"))
            else:
                installed_host = provision_host.host_status(p)
                if not installed_host.get("configured") or not installed_host.get("image_present"):
                    raise SafeError("The installed managed image is unavailable. Reinspect with a complete desktop host bundle.")
                saved = read_json(state / "host-settings.json")
                if any(saved["authentication"].get(key) != authentication.get(key)
                       for key in ("authority", "client_id", "client_secret_file", "public_origin")):
                    raise SafeError("Installed host authentication differs from its registration. Review an explicit migration.")
            if not host_probe(state, p.settings, "docker", p.environment)[0]:
                if verify_only and host_ready:
                    p.environment.update(prerequisites(root, directory, request["install_prerequisites"], event))
                retain_apphost_location(provision, state, source)
                event("host-publish", "Publishing the managed host and gateway on the existing identity network.")
                p.publish()
                event("host-deploy", "Deploying the managed host with NVIDIA CDI and HTTPS port 443; identity storage stays in place.")
                p.deploy()
            event("host-health", "Waiting for owned host/container health and the CA-verified HTTPS session endpoint.")
            wait_host(p)
            event("application", "Reconciling the owned Lucia OIDC application, claims, and owner access bindings.")
            status = application.reconcile_application(p, origin)
            if status.get("ready") is not True:
                raise SafeError("Lucia application reconciliation did not verify owner access. Review registration and retry.")
            wait_host(p)
            activation_worker = source / "tools/domains/activation_worker.py"
            if activation_worker.is_file():
                linger = p.run(["loginctl", "show-user", str(os.getuid()), "-p", "Linger", "--value"], check=False)
                if linger.returncode == 0 and linger.stdout.strip() == "yes":
                    event("domain-worker", "Installing the scoped domain activation service without exposing administrator credentials to the web host.")
                    p.run([sys.executable, str(activation_worker), "install"])
                    enrollment_worker = source / "tools/nodes/enrollment_worker.py"
                    if enrollment_worker.is_file():
                        sys.path.insert(0, str(source / "tools/nodes"))
                        import enrollment_worker as node_enrollment
                        import prepare_directory as node_directory
                        native = node_enrollment.Native(argparse.Namespace(state=str(state)))
                        native.deadline = time.monotonic() + 120
                        native.load_scoped()
                        native.verify_containers()
                        # execute_setup already holds the native setup/provision locks.
                        node_directory.prepare(native, node_enrollment)
                        event("node-worker", "Installing the scoped node CA and directory enrollment service; administrator credentials remain outside the web host.")
                        p.run([sys.executable, str(enrollment_worker), "install"])
                else:
                    event("domain-worker", "DNS activation service is deferred until persistent user services are explicitly approved; identity and host setup are otherwise unchanged.")
        pem = read_bytes(state / "trust/lucia-root-ca.crt", 32768).decode("ascii")
        fingerprint = hashlib.sha256(ssl.PEM_cert_to_DER_cert(pem)).hexdigest()
        if read_bytes(state / "trust/fingerprint.txt", 256).decode().strip().lower() != fingerprint:
            raise SafeError("The exported root certificate differs from the installation's pinned fingerprint.")
        result = {
            "authentik_url": url(p.settings["public_host"], p.settings["ports"]["authentik"]),
            "ldap_url": url(p.settings["public_host"], p.settings["ports"]["ldaps"], "ldaps"),
            "owner_username": record["username"], "root_certificate_pem": pem,
            "root_fingerprint": fingerprint, "owner_login_verified": True,
            "host_url": host_origin(p.settings["public_host"]) if configure_host else None,
            "host_ready": configure_host, "application_ready": configure_host, "sso_verified": False,
        }
        validate_result(result, configure_host)
        return result


def retain_apphost_location(provision, state, source):
    marker = state / "apphost-path.txt"
    if not marker.exists():
        return
    original = pathlib.Path(read_bytes(marker, 8192).decode().strip())
    if original == source / APPHOST:
        return
    if not original.is_absolute():
        raise SafeError("The original Identity AppHost path is invalid. Review its deployment location.")
    # Never move the AppHost (Aspire derives volume names from it), or overwrite a legacy checkout.
    for name in ("AppHost.cs", "Lucia.Homelab.Identity.AppHost.csproj", "aspire.config.json"):
        if read_bytes(original.parent / name) != read_bytes((source / APPHOST).parent / name):
            raise SafeError("The original Identity AppHost needs the reviewed managed-host update at its existing path. Update it without relocating its volumes, then retry.")
    provision.APPHOST = original


def wait_host(provisioner):
    deadline = time.monotonic() + 180
    while True:
        if host_probe(provisioner.state, provisioner.settings, "docker", provisioner.environment)[0]:
            return
        if time.monotonic() >= deadline:
            raise SafeError("The managed host did not become healthy over CA-verified HTTPS. Inspect the host and gateway; setup is not complete.")
        time.sleep(2)


def worker(job_id, lock_fd):
    directory = job_directory(job_id)
    root, _ = locations()
    os.umask(0o077)
    sys.dont_write_bytecode = True
    # Accept only the inherited installation lock, never an independently launched worker.
    inherited, expected = os.fstat(lock_fd), (root / ".run.lock").stat()
    if (inherited.st_dev, inherited.st_ino) != (expected.st_dev, expected.st_ino) or not lock_held(root / ".run.lock"):
        raise SafeError("The durable worker did not inherit the installation lock.")
    import fcntl
    try:
        fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        raise SafeError("Another durable worker owns the installation lock.") from None
    write_json(directory / "worker.json", {"pid": os.getpid(), "start_ticks": process_identity(os.getpid())})
    record = read_json(directory / "status.json")
    phase = "queued"

    def event(name, message):
        nonlocal phase
        phase = name
        record["events"] = (record["events"] + [{"phase": name, "message": message, "level": "info"}])[-MAX_EVENTS:]
        write_json(directory / "status.json", record)

    try:
        if read_json(root / "active.json")["job_id"] != job_id:
            raise SafeError("This worker is no longer the active setup job.")
        request = read_json(directory / "request.json")
        password = login_password_value(read_bytes(directory / "owner-password", 65536).decode())
        validate_request({**request, "owner_password": password, "sudo_password": None})
        event("preflight", "Rechecking reviewed host state before making changes.")
        result = execute_setup(directory, request, password, event)
        validate_result(result, request.get("configure_host", False))
        event("complete", "Managed host and owned application access are ready; browser SSO and client CA trust remain separate."
              if request.get("configure_host") else "Identity and LDAP-backed owner login verified; managed host readiness was not requested.")
        record.update(status="succeeded", result=result)
    except Exception as error:
        message = str(error) if isinstance(error, SafeError) else f"Setup failed during {phase}. No success was recorded; inspect the identity services and retry with the same settings and owner."
        record.update(status="failed", error=message, result=None)
        record["events"] = (record["events"] + [{"phase": phase, "message": message, "level": "error"}])[-MAX_EVENTS:]
    finally:
        remove_job_password(directory)
        write_json(directory / "status.json", record)
        os.close(lock_fd)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("inspect", "start", "status", "_worker"))
    parser.add_argument("--archive")
    parser.add_argument("--sha256")
    parser.add_argument("--job-id")
    parser.add_argument("--lock-fd", type=int)
    args = parser.parse_args()
    try:
        if args.command == "inspect":
            result = inspect()
        elif args.command == "status":
            result = job_status(args.job_id)
        elif args.command == "start":
            os.umask(0o077)
            private = sys.stdin.read(16385)
            if len(private) > 16384:
                raise SafeError("Setup request exceeded its size limit.")
            result = start(args.archive, args.sha256, json.loads(private))
        else:
            worker(args.job_id, args.lock_fd)
            return 0
        print(json.dumps(result, ensure_ascii=True), flush=True)
        return 0
    except Exception as error:
        message = str(error) if isinstance(error, SafeError) else "The bootstrap request or host state is invalid. No verified result is available."
        print(json.dumps({"schema_version": SCHEMA, "status": "failed", "error": message}), flush=True)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
