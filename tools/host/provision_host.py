"""Prepare a managed host; deployment remains exclusively the Identity AppHost's job.

Call prepare_host(p, archive, manifest, authentication_record, model_directory=None)
after identity exists and prepare_application has returned authority, client_id,
client_secret_file and public_origin. `p` is the identity Provisioner (state,
settings, values, run). Return/save a credential-free schema-1 host-settings.json.
Then the caller may use p.publish()/p.deploy(); this module never deploys, stops
services, downloads models, or migrates legacy storage.

Payload dependencies: this file, tools/host/package.py, deployment/host/Dockerfile.
Target requirements: Linux ARM64, Docker with NVIDIA CDI, existing identity/Aspire.
"""

import contextlib
import hashlib
import ipaddress
import json
import os
import pathlib
import re
import secrets
import stat
import tarfile
import tempfile

if __package__:
    from .package import (HostError, RID, SCHEMA, VERSION, file_mode, json_bytes, load_manifest,
                          no_links, regular_file, sha256_file, valid_hash)
else:
    from package import (HostError, RID, SCHEMA, VERSION, file_mode, json_bytes, load_manifest,
                         no_links, regular_file, sha256_file, valid_hash)

ROOT = pathlib.Path(__file__).resolve().parents[2]
HOST_ROOT = pathlib.Path.home() / ".local/share/lucia/host"
CONTAINER = "lucia-homelab-host"
COMPONENT = "managedhost"
KEY_NAMES = ("host-owner-api-key", "host-inference-api-key")
IMAGE_ID = re.compile(r"sha256:[a-f0-9]{64}")
SETTINGS_FIELDS = frozenset((
    "schema_version", "component", "version", "rid", "installation_id", "artifact_sha256",
    "dockerfile_sha256", "image_tag", "image_id", "host_state", "data_directory",
    "model_directory", "context_tokens", "voice_reserve_gib", "authentication",
    "trusted_proxy_networks", "identity_network",
))


def absolute_path(value):
    if not isinstance(value, (str, pathlib.Path)) or not str(value) or any(c in str(value) for c in "\r\n\0"):
        raise HostError("Host configuration requires an absolute local path.")
    path = pathlib.Path(value)
    if not path.is_absolute() or ".." in path.parts:
        raise HostError("Host configuration requires an absolute local path without traversal.")
    no_links(path)
    return path


def owned(path):
    no_links(path)
    info = path.stat()
    if hasattr(os, "getuid") and info.st_uid != os.getuid():
        raise HostError("Managed state must be owned by the provisioning account.")
    return info


def private_directory(path):
    no_links(path)
    path.mkdir(parents=True, exist_ok=True, mode=0o700)
    info = owned(path)
    if not stat.S_ISDIR(info.st_mode) or (os.name != "nt" and info.st_mode & 0o077):
        raise HostError("Managed state must be a private, user-owned directory.")


def read_json(path, limit=65536):
    info = regular_file(path)
    owned(path)
    if info.st_size > limit:
        raise HostError("Managed state record exceeds its size limit.")
    try:
        value = json.loads(path.read_bytes())
    except (json.JSONDecodeError, UnicodeDecodeError):
        raise HostError("Managed state record is not valid JSON.") from None
    if not isinstance(value, dict):
        raise HostError("Managed state record must be a JSON object.")
    return value


def write_private(path, data):
    private_directory(path.parent)
    no_links(path)
    if path.exists():
        regular_file(path)
        owned(path)
        if path.read_bytes() == data:
            return
    descriptor, name = tempfile.mkstemp(prefix="." + path.name + "-", dir=path.parent)
    staging = pathlib.Path(name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            os.chmod(staging, 0o600)
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(staging, path)
    finally:
        staging.unlink(missing_ok=True)


@contextlib.contextmanager
def preparation_lock(root):
    path = root / "prepare.lock"
    no_links(path)
    descriptor = os.open(path, os.O_CREAT | os.O_RDWR, 0o600)
    try:
        owned(path)
        if os.name == "nt":
            import msvcrt
            msvcrt.locking(descriptor, msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield
    finally:
        os.close(descriptor)


def read_secret(path, minimum=1):
    info = regular_file(path)
    owned(path)
    if (not minimum <= info.st_size <= 16384
            or (os.name != "nt" and info.st_mode & 0o077)):
        raise HostError("A persistent host secret is empty, oversized, or not private.")
    try:
        value = path.read_text(encoding="utf-8").rstrip("\r\n")
    except UnicodeDecodeError:
        raise HostError("A persistent host secret is not UTF-8.") from None
    if not value.strip() or len(value) < minimum or any(c in value for c in "\r\n\0"):
        raise HostError("A persistent host secret is invalid.")
    return value


def validate_authentication(record, settings):
    if not isinstance(record, dict):
        raise HostError("Authentication registration must be an object.")
    required = ("authority", "client_id", "client_secret_file", "public_origin")
    if any(not isinstance(record.get(key), str) or not record[key]
           or any(ord(c) < 32 for c in record[key]) for key in required):
        raise HostError("Authentication registration is missing a required field.")
    host = settings["public_host"]
    host = f"[{host}]" if ":" in host else host
    expected_origin = f"https://{host}"
    expected_authority = f"https://{host}:{settings['ports']['authentik']}/application/o/lucia/"
    if record["public_origin"] not in (expected_origin, expected_origin + "/") or record["authority"] != expected_authority:
        raise HostError("Authentication URLs must match this identity's HTTPS origin and Lucia issuer.")
    if len(record["client_id"]) > 256 or any(c.isspace() for c in record["client_id"]):
        raise HostError("Authentication client ID is oversized.")
    secret = absolute_path(record["client_secret_file"])
    read_secret(secret)
    return {**{key: record[key] for key in required}, "public_origin": expected_origin}


def validate_settings(value, identity_settings, identity_state):
    if not isinstance(value, dict) or set(value) != SETTINGS_FIELDS:
        raise HostError("Managed host settings have invalid fields; restore the installation record.")
    if (value["schema_version"] != SCHEMA or type(value["schema_version"]) is not int
            or value["component"] != COMPONENT or value["version"] != VERSION or value["rid"] != RID):
        raise HostError("Unsupported managed host configuration.")
    installation = hashlib.sha256(str(identity_state).encode()).hexdigest()
    if value["installation_id"] != installation:
        raise HostError("Managed host settings belong to a different identity installation.")
    for key in ("artifact_sha256", "dockerfile_sha256"):
        if not valid_hash(value[key]):
            raise HostError("Invalid artifact or Dockerfile hash in host settings.")
    expected_tag = image_tag(value["artifact_sha256"], value["dockerfile_sha256"])
    if value["image_tag"] != expected_tag or not isinstance(value["image_id"], str) or not IMAGE_ID.fullmatch(value["image_id"]):
        raise HostError("Host image identity is invalid.")
    root = absolute_path(value["host_state"])
    if root in (pathlib.Path(root.anchor), pathlib.Path.home()) or root != HOST_ROOT.absolute():
        raise HostError("Host state must use the dedicated managed host directory.")
    if absolute_path(value["data_directory"]) != root / "data":
        raise HostError("Host persistent data must remain in its original managed directory.")
    validate_model_directory(absolute_path(value["model_directory"]), root, identity_state)
    if (type(value["context_tokens"]) is not int or not 256 <= value["context_tokens"] <= 1048576
            or type(value["voice_reserve_gib"]) not in (int, float)
            or not 8 <= value["voice_reserve_gib"] <= 1024):
        raise HostError("Invalid context limit or voice memory reserve.")
    validate_authentication(value["authentication"], identity_settings)
    validate_subnets(value["trusted_proxy_networks"])
    network = value["identity_network"]
    if (not isinstance(network, dict) or set(network) != {"id", "name"}
            or not valid_hash(network["id"]) or not isinstance(network["name"], str)
            or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]+", network["name"])
            or network["name"] in ("bridge", "host", "none")):
        raise HostError("Invalid isolated identity network.")
    return value


def validate_model_directory(models, root, state):
    if (models in (pathlib.Path(models.anchor), pathlib.Path.home())
            or models.is_relative_to(state) or state.is_relative_to(models)
            or (models != root / "models" and (models.is_relative_to(root) or root.is_relative_to(models)))):
        raise HostError("Models must use a dedicated directory separate from identity, host configuration, and data.")


def validate_subnets(values):
    if (not isinstance(values, list) or not values or len(values) > 8
            or any(not isinstance(value, str) for value in values) or len(set(values)) != len(values)):
        raise HostError("Explicit isolated identity network CIDRs are required.")
    try:
        networks = [ipaddress.ip_network(value, strict=True) for value in values]
    except (ValueError, TypeError):
        raise HostError("Invalid identity network CIDR.") from None
    allowed = [ipaddress.ip_network(value) for value in ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7")]
    if any(not any(network.version == private.version and network.subnet_of(private) for private in allowed)
           or network.prefixlen < (8 if network.version == 4 else 32) for network in networks):
        raise HostError("Default-route, public, or local-only proxy networks are not allowed.")
    return values


def verify_release(release, manifest):
    no_links(release)
    if os.name != "nt" and owned(release).st_mode & 0o222:
        raise HostError("Existing release directory is not immutable.")
    if read_json(release / "manifest.json", 8 * 1024 * 1024) != manifest:
        raise HostError("Existing release has a different manifest; refusing to overwrite it.")
    files = {entry["path"]: entry for entry in manifest["files"]}
    expected_dirs = {str(parent) for name in files for parent in pathlib.PurePosixPath(name).parents}
    expected_dirs.discard(".")
    actual = set()
    publish = release / "publish"
    for directory, dirs, names in os.walk(publish, followlinks=False):
        no_links(pathlib.Path(directory))
        for name in dirs:
            path = pathlib.Path(directory) / name
            no_links(path)
            if path.relative_to(publish).as_posix() not in expected_dirs:
                raise HostError("Existing release contains an unexpected directory.")
        for name in names:
            path = pathlib.Path(directory) / name
            relative = path.relative_to(publish).as_posix()
            entry = files.get(relative)
            if (entry is None or regular_file(path).st_size != entry["size"]
                    or sha256_file(path) != entry["sha256"]):
                raise HostError("Existing release content does not match its immutable manifest.")
            if os.name != "nt" and stat.S_IMODE(owned(path).st_mode) != (file_mode(relative) & ~0o222):
                raise HostError("Existing release file permissions do not match its immutable manifest.")
            actual.add(relative)
    if actual != files.keys() or {path.name for path in release.iterdir()} != {"publish", "manifest.json"}:
        raise HostError("Existing release contains missing or unexpected files.")


def extract_release(archive_path, manifest_path, root):
    archive = absolute_path(pathlib.Path(archive_path).absolute())
    manifest = load_manifest(manifest_path)
    if regular_file(archive).st_size != manifest["size"]:
        raise HostError("Artifact archive size does not match its manifest.")
    releases = root / "releases"
    private_directory(releases)
    release = releases / manifest["sha256"]
    no_links(release)
    with archive.open("rb") as source:
        if hashlib.file_digest(source, "sha256").hexdigest() != manifest["sha256"]:
            raise HostError("Artifact archive SHA-256 does not match its manifest.")
        source.seek(0)
        if release.exists():
            verify_release(release, manifest)
            return release, manifest
        with tempfile.TemporaryDirectory(prefix=".extract-", dir=releases) as temporary:
            staging = pathlib.Path(temporary)
            publish = staging / "publish"
            publish.mkdir(mode=0o700)
            expected = {entry["path"]: entry for entry in manifest["files"]}
            seen = set()
            try:
                with tarfile.open(fileobj=source, mode="r|gz") as tar:
                    for member in tar:
                        entry = expected.get(member.name)
                        if (entry is None or member.name in seen or not member.isfile()
                                or member.linkname or member.pax_headers or member.size != entry["size"]
                                or member.mode != file_mode(member.name) or member.offset_data - member.offset != 512):
                            raise HostError("Archive has an unexpected path, link, type, size, or mode.")
                        seen.add(member.name)
                        destination = publish / member.name
                        destination.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
                        digest = hashlib.sha256()
                        with tar.extractfile(member) as content, destination.open("xb") as output:
                            while block := content.read(1024 * 1024):
                                digest.update(block)
                                output.write(block)
                        if digest.hexdigest() != entry["sha256"]:
                            raise HostError("An archive member failed SHA-256 verification.")
                        os.chmod(destination, file_mode(member.name) & ~0o222)
            except (tarfile.TarError, EOFError):
                raise HostError("Artifact is not a complete regular-file tar.gz archive.") from None
            if seen != expected.keys():
                raise HostError("Archive is missing manifest files.")
            (staging / "manifest.json").write_bytes(json_bytes(manifest))
            os.chmod(staging / "manifest.json", 0o400)
            for directory, _, _ in os.walk(publish, topdown=False):
                os.chmod(directory, 0o555)
            os.chmod(staging, 0o500)
            os.rename(staging, release)
    return release, manifest


def image_tag(artifact_hash, dockerfile_hash):
    key = hashlib.sha256((artifact_hash + ":" + dockerfile_hash).encode()).hexdigest()
    return "lucia-managed-host:" + key


def command_json(provisioner, command):
    result = provisioner.run(command)
    try:
        return json.loads(result.stdout)
    except (json.JSONDecodeError, UnicodeDecodeError):
        raise HostError("Docker returned malformed metadata.") from None


def inspect_image(provisioner, reference, labels):
    value = command_json(provisioner, ["docker", "image", "inspect", reference, "--format",
        '{"id":{{json .Id}},"architecture":{{json .Architecture}},"os":{{json .Os}},"labels":{{json .Config.Labels}}}'])
    if (not isinstance(value, dict) or not isinstance(value.get("id"), str)
            or not IMAGE_ID.fullmatch(value["id"]) or value.get("architecture") != "arm64"
            or value.get("os") != "linux" or not isinstance(value.get("labels"), dict)
            or any(value["labels"].get(key) != expected for key, expected in labels.items())):
        raise HostError("Host image tag collides with an unowned or incompatible image.")
    return value["id"]


def prepare_image(provisioner, root, release, manifest, installation):
    dockerfile = ROOT / "deployment/host/Dockerfile"
    regular_file(dockerfile)
    dockerfile_hash = sha256_file(dockerfile)
    artifact_hash = manifest["sha256"]
    tag = image_tag(artifact_hash, dockerfile_hash)
    labels = {"io.lucia.component": COMPONENT, "io.lucia.host-installation": installation,
              "io.lucia.artifact-sha256": artifact_hash, "io.lucia.dockerfile-sha256": dockerfile_hash}
    private_directory(root / "images")
    record_path = root / "images" / (tag.split(":")[1] + ".json")
    record = read_json(record_path) if record_path.exists() else None
    if record is not None and (set(record) != {"image_id", "image_tag", "labels"}
            or record["image_tag"] != tag or record["labels"] != labels
            or not isinstance(record["image_id"], str) or not IMAGE_ID.fullmatch(record["image_id"])):
        raise HostError("Host image receipt is invalid or belongs to another installation.")
    listed = provisioner.run(["docker", "image", "ls", "--quiet", "--no-trunc", "--filter", "reference=" + tag]).stdout.split()
    if listed:
        image_id = inspect_image(provisioner, tag, labels)
        if record and image_id != record["image_id"]:
            raise HostError("Owned host image tag changed identity; restore the recorded image.")
    elif record and record["image_id"] in provisioner.run(["docker", "image", "ls", "--all", "--quiet", "--no-trunc"]).stdout.split():
        image_id = inspect_image(provisioner, record["image_id"], labels)
        if image_id != record["image_id"]:
            raise HostError("Docker image identity does not match its recorded receipt.")
    else:
        command = ["docker", "build", "--platform", "linux/arm64", "--tag", tag, "--file", str(dockerfile)]
        for key, value in labels.items():
            command.extend(("--label", key + "=" + value))
        provisioner.run([*command, str(release)])
        image_id = inspect_image(provisioner, tag, labels)
    write_private(record_path, json_bytes({"image_id": image_id, "image_tag": tag, "labels": labels}))
    return {"image_id": image_id, "image_tag": tag, "dockerfile_sha256": dockerfile_hash,
            "artifact_sha256": artifact_hash}


def identity_network(provisioner):
    gateway = command_json(provisioner, ["docker", "inspect", "lucia-identity-gateway", "--format",
        '{"labels":{{json .Config.Labels}},"networks":{{json .NetworkSettings.Networks}}}'])
    labels = gateway.get("labels") or {}
    project = labels.get("com.docker.compose.project")
    networks = gateway.get("networks", {})
    if labels.get("io.lucia.component") != "identity" or not project or len(networks) != 1:
        raise HostError("Existing identity gateway must own exactly one isolated Compose network.")
    name, attachment = next(iter(networks.items()))
    if name in ("bridge", "host", "none"):
        raise HostError("Identity gateway is not on an isolated Compose network.")
    network = command_json(provisioner, ["docker", "network", "inspect", name])[0]
    if (network["Id"] != attachment["NetworkID"] or network.get("Driver") != "bridge"
            or (network.get("Labels") or {}).get("com.docker.compose.project") != project):
        raise HostError("Identity network ownership does not match its gateway.")
    subnets = [entry["Subnet"] for entry in network.get("IPAM", {}).get("Config", []) if "Subnet" in entry]
    validate_subnets(subnets)
    return {"id": network["Id"], "name": name}, subnets


def container_metadata(provisioner):
    found = provisioner.run(["docker", "ps", "--all", "--quiet", "--filter", f"name=^/{CONTAINER}$"]).stdout.split()
    if not found:
        return None
    if len(found) != 1:
        raise HostError("Managed host container name is ambiguous.")
    return command_json(provisioner, ["docker", "inspect", CONTAINER, "--format",
        '{"image_id":{{json .Image}},"status":{{json .State.Status}},"running":{{json .State.Running}},'
        '"health":{{if .State.Health}}{{json .State.Health.Status}}{{else}}null{{end}},'
        '"labels":{{json .Config.Labels}}}'])


def check_container_owner(container, installation):
    if container is not None:
        labels = container.get("labels") or {}
        if labels.get("io.lucia.component") != COMPONENT or labels.get("io.lucia.host-installation") != installation:
            raise HostError("Managed host container name is owned by another deployment.")


def prepare_host(provisioner, archive_path, manifest_path, authentication_record, model_directory=None):
    state = absolute_path(provisioner.state)
    settings = provisioner.settings
    if not isinstance(settings, dict) or any(type(settings.get(key)) is not int or settings[key] <= 0 for key in ("uid", "gid")):
        raise HostError("Load an existing non-root identity installation before preparing the host.")
    if hasattr(os, "getuid") and (settings["uid"] != os.getuid() or settings["gid"] != os.getgid()):
        raise HostError("Provision as the identity UID/GID; host storage must not be chowned implicitly.")
    if 443 in settings["ports"].values():
        raise HostError("Managed host HTTPS port 443 conflicts with an existing identity endpoint.")
    authentication = validate_authentication(authentication_record, settings)
    root = HOST_ROOT.absolute()
    absolute_path(root)
    installation = hashlib.sha256(str(state).encode()).hexdigest()
    marker = {"schema_version": SCHEMA, "component": COMPONENT, "identity_state": str(state)}
    marker_path = root / "owner.json"
    existing = root.exists()
    configured = False
    pending_models = root / "models"
    if existing:
        owner = read_json(marker_path) if marker_path.is_file() else {}
        if (set(owner) != {*marker, "configured", "model_directory"} or any(owner.get(key) != value for key, value in marker.items())
                or type(owner.get("configured")) is not bool):
            raise HostError("Host state directory collides with unowned data; refusing to adopt it.")
        configured = owner["configured"]
        pending_models = absolute_path(owner["model_directory"])
    path = state / "host-settings.json"
    if configured and not path.exists():
        raise HostError("Persistent host settings are missing; restore them before preparing this installation.")
    previous = validate_settings(read_json(path), settings, state) if path.exists() else None
    models = absolute_path(model_directory) if model_directory is not None else (
        pathlib.Path(previous["model_directory"]) if previous else pending_models)
    if (previous and str(models) != previous["model_directory"]) or (existing and models != pending_models):
        raise HostError("Model directory changed; reruns never migrate or replace existing models.")
    validate_model_directory(models, root, state)
    marker["model_directory"] = str(models)
    if models.exists():
        info = owned(models)
        if not stat.S_ISDIR(info.st_mode) or not os.access(models, os.R_OK | os.W_OK | os.X_OK):
            raise HostError("Model directory must be owned and writable by the identity account.")
    if previous and (not (root / "data").is_dir() or not (root / "data/data-protection").is_dir()):
        raise HostError("Persistent host data is missing; restore it before rerunning preparation.")
    private_directory(root)
    with preparation_lock(root):
        if not existing:
            write_private(marker_path, json_bytes({**marker, "configured": False}))
        for name in KEY_NAMES:
            secret_path = state / "secrets" / name
            no_links(secret_path)
            if not secret_path.exists():
                if existing or previous:
                    raise HostError("Persistent host API key is missing; restore it, never regenerate it.")
                private_directory(secret_path.parent)
                with os.fdopen(os.open(secret_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "w") as output:
                    output.write(secrets.token_urlsafe(48))
            elif not existing:
                raise HostError("Unowned host API key collision; refusing to adopt a preexisting credential.")
            provisioner.values[name] = read_secret(secret_path, minimum=32)
        check_container_owner(container_metadata(provisioner), installation)
        network, subnets = identity_network(provisioner)
        trust = state / "trust" / "lucia-root-ca.crt"
        regular_file(trust)
        if b"PRIVATE KEY" in trust.read_bytes():
            raise HostError("Host trust mount must contain only the public identity CA.")
        for directory in (root / "data", root / "data/data-protection", root / "data/huggingface"):
            private_directory(directory)
        for directory in (root / "data/domains", root / "data/domains/certificates", state / "gateway/domains", root / "data/nodes"):
            private_directory(directory)
        if not models.exists():
            private_directory(models)
        release, manifest = extract_release(archive_path, manifest_path, root)
        image = prepare_image(provisioner, root, release, manifest, installation)
        record = {
            "schema_version": SCHEMA, "component": COMPONENT, "version": VERSION, "rid": RID,
            "installation_id": installation, **image, "host_state": str(root),
            "data_directory": str(root / "data"), "model_directory": str(models),
            "context_tokens": previous["context_tokens"] if previous else 8192,
            "voice_reserve_gib": previous["voice_reserve_gib"] if previous else 8,
            "authentication": authentication, "trusted_proxy_networks": subnets, "identity_network": network,
        }
        validate_settings(record, settings, state)
        route = {"http": {
            "routers": {"lucia-host": {"entryPoints": ["host"], "rule": "PathPrefix(`/`)", "service": "lucia-host", "tls": {}}},
            "services": {"lucia-host": {"loadBalancer": {"servers": [{"url": "http://lucia-host:8080"}]}}},
        }}
        route_path = state / "gateway/host.yml"
        if route_path.exists() and read_json(route_path) != route:
            raise HostError("Managed host gateway route collides with an unrelated configuration.")
        write_private(route_path, json_bytes(route))
        write_private(path, json_bytes(record))
        write_private(marker_path, json_bytes({**marker, "configured": True}))
        return record


def host_status(provisioner):
    """Read-only HTTP/container metadata. Model readiness requires an authenticated API check."""
    path = absolute_path(provisioner.state) / "host-settings.json"
    if not path.exists():
        return {"schema_version": SCHEMA, "configured": False, "model_readiness": "not_checked"}
    record = validate_settings(read_json(path), provisioner.settings, provisioner.state)
    container = container_metadata(provisioner)
    check_container_owner(container, record["installation_id"])
    image_ids = provisioner.run(["docker", "image", "ls", "--all", "--quiet", "--no-trunc"]).stdout.split()
    try:
        cdi = provisioner.run(["nvidia-ctk", "cdi", "list"], check=False)
        available = cdi.returncode == 0 and "nvidia.com/gpu=all" in cdi.stdout.split()
        gpu = {"available": available, "device": "nvidia.com/gpu=all",
               "status": "available" if available else "cdi_unavailable"}
    except FileNotFoundError:
        gpu = {"available": False, "device": "nvidia.com/gpu=all", "status": "nvidia_ctk_missing"}
    try:
        result = provisioner.run(["nvidia-smi", "--query-gpu=name,uuid,driver_version,memory.total",
                                  "--format=csv,noheader,nounits"], check=False)
        gpu["metadata"] = [line.strip() for line in result.stdout.splitlines()] if result.returncode == 0 else []
        gpu["metadata_status"] = "available" if result.returncode == 0 else "query_failed"
    except FileNotFoundError:
        gpu.update(metadata=[], metadata_status="nvidia_smi_missing")
    return {
        "schema_version": SCHEMA, "configured": True, "public_origin": record["authentication"]["public_origin"],
        "image_id": record["image_id"], "image_present": record["image_id"] in image_ids,
        "container": None if container is None else {
            key: container[key] for key in ("image_id", "running", "status", "health")},
        "expected_image_running": bool(container and container["running"] and container["image_id"] == record["image_id"]),
        "gpu": gpu, "model_readiness": "not_checked",
    }
