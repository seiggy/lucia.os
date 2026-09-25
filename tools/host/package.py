#!/usr/bin/env python3
"""Package a maintainer-built ARM64 Server/SPA and optional x64 discovery payload.

The nested node runtime is a boot payload, never executed by the ARM64 host.
Source, installation state, node credentials, and model weights are excluded.

CLI: python tools/host/package.py PUBLISH_DIRECTORY HostPayload/host-linux-arm64.tar.gz
     --manifest HostPayload/manifest.json
The external manifest is schema 1:
{schema_version, version, rid, entrypoint, sha256, size,
 files: [{path, sha256, size}]}.
SHA-256 and size describe the compressed archive. File sizes are uncompressed bytes.
Paths use canonical POSIX separators. Modes are inferred from the path allowlist:
0755 for the apphost/native libraries, 0644 for managed assemblies and static assets.
Archive and manifest must arrive through the installer's trusted artifact transport;
hashes detect corruption, not the authenticity of an untrusted manifest.
"""

import argparse
import gzip
import hashlib
import json
import os
import pathlib
import re
import shutil
import stat
import tarfile
import tempfile

SCHEMA = 1
VERSION = "0.1.0"
RID = "linux-arm64"
ENTRYPOINT = "Lucia.Homelab.Server.dll"
REQUIRED = frozenset((ENTRYPOINT, "wwwroot/index.html"))
NODE_AGENT_PREFIX = "boot/node-agent-linux-x64/"
NODE_AGENT_REQUIRED = frozenset(NODE_AGENT_PREFIX + name for name in (
    "lucia-node-agent", "lucia-node-agent.dll", "lucia-node-agent.deps.json", "lucia-node-agent.runtimeconfig.json",
))
MAX_FILE = 1024 * 1024 * 1024
MAX_TOTAL = 4 * MAX_FILE
MAX_FILES = 20000
MAX_MANIFEST = 8 * 1024 * 1024
EXCLUDED_PARTS = frozenset((
    "bin", "obj", "node_modules", "models", "data", "secrets", "credentials",
    "config", "configuration", "appsettings", "source", "src", "logs",
))
WEB_EXTENSIONS = frozenset((
    ".html", ".js", ".mjs", ".css", ".svg", ".png", ".jpg", ".jpeg", ".gif",
    ".webp", ".avif", ".ico", ".woff", ".woff2", ".ttf", ".otf", ".webmanifest",
))


class HostError(ValueError):
    """Credential-free packaging/provisioning failure."""


def canonical_path(value):
    if (not isinstance(value, str) or not value or len(value) > 240
            or not re.fullmatch(r"[A-Za-z0-9_./@+-]+", value)
            or any(part in ("", ".", "..") or part.endswith(".") for part in value.split("/"))
            or value.startswith("/")):
        raise HostError("Artifact contains a noncanonical relative path.")
    return pathlib.PurePosixPath(value)


def file_mode(name):
    path = canonical_path(name)
    parts = path.parts
    if any(part.startswith(".") or part.lower() in EXCLUDED_PARTS for part in parts):
        return None
    lower = path.name.lower()
    if lower.startswith(("appsettings", "secrets", "credentials", "passwords", "tokens")):
        return None
    if parts[:2] == ("boot", "node-agent-linux-x64"):
        relative = parts[2:]
        if len(relative) == 1:
            if path.name in ("lucia-node-agent", "createdump") or re.fullmatch(r"lib[A-Za-z0-9_+.-]+\.so(?:\.[0-9]+)*", path.name):
                return 0o755
            if path.suffix == ".dll" or path.name in ("lucia-node-agent.deps.json", "lucia-node-agent.runtimeconfig.json", "lucia-node-agent.pdb"):
                return 0o644
            if lower.startswith(("license", "notice", "third-party-notices", "thirdpartynotices")) and path.suffix.lower() == ".txt":
                return 0o644
        if len(relative) == 2 and re.fullmatch(r"[a-z]{2,3}(?:-[A-Za-z]{2,4})?", relative[0]) and lower.endswith(".resources.dll"):
            return 0o644
        return None
    if lower.startswith(("license", "notice", "third-party-notices", "thirdpartynotices")):
        return 0o644 if (len(parts) == 1 or parts[0].lower() == "licenses") and path.suffix.lower() in ("", ".txt", ".md", ".html") else None
    if parts[0] == "licenses" and path.suffix.lower() in (".txt", ".md"):
        return 0o644
    if parts[0] == "wwwroot":
        return 0o644 if path.suffix.lower() in WEB_EXTENSIONS else None
    if len(parts) == 1:
        if path.name == "Lucia.Homelab.Server":
            return 0o755
        if path.suffix == ".dll" or path.name in (
                "Lucia.Homelab.Server.deps.json", "Lucia.Homelab.Server.runtimeconfig.json"):
            return 0o644
    if len(parts) == 2 and re.fullmatch(r"[a-z]{2,3}(?:-[A-Za-z]{2,4})?", parts[0]):
        return 0o644 if lower.endswith(".resources.dll") else None
    if len(parts) == 1 or (len(parts) == 4 and parts[:3] == ("runtimes", RID, "native")):
        if re.fullmatch(r"lib[A-Za-z0-9_+.-]+\.so(?:\.[0-9]+)*", path.name) or path.name == "Magick.Native-Q8-arm64.dll.so":
            return 0o755
    return None


def no_links(path):
    if any(item.is_symlink() or (hasattr(item, "is_junction") and item.is_junction())
           for item in (path, *path.parents)):
        raise HostError("Managed paths must not contain symbolic links or junctions.")


def regular_file(path):
    no_links(path)
    info = path.stat()
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
        raise HostError("Managed files must be regular files, not links or special files.")
    return info


def sha256_file(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def json_bytes(value):
    return (json.dumps(value, sort_keys=True, indent=2) + "\n").encode("utf-8")


def validate_manifest(value):
    if not isinstance(value, dict) or set(value) != {
            "schema_version", "version", "rid", "entrypoint", "sha256", "size", "files"}:
        raise HostError("Artifact manifest has invalid fields.")
    if (type(value["schema_version"]) is not int or value["schema_version"] != SCHEMA
            or value["version"] != VERSION or value["rid"] != RID or value["entrypoint"] != ENTRYPOINT):
        raise HostError("Unsupported host artifact schema, version, RID, or entrypoint.")
    if (not valid_hash(value["sha256"]) or type(value["size"]) is not int
            or not 0 < value["size"] <= MAX_TOTAL):
        raise HostError("Invalid archive size or SHA-256.")
    files = value["files"]
    if not isinstance(files, list) or not 2 <= len(files) <= MAX_FILES:
        raise HostError("Invalid artifact file count.")
    seen = set()
    total = 0
    for entry in files:
        if not isinstance(entry, dict) or set(entry) != {"path", "sha256", "size"}:
            raise HostError("Invalid artifact file record.")
        name = str(canonical_path(entry["path"]))
        if file_mode(name) is None:
            raise HostError("Artifact contains an excluded file.")
        if name.casefold() in seen:
            raise HostError("Artifact contains duplicate or case-colliding paths.")
        seen.add(name.casefold())
        if (type(entry["size"]) is not int or not 0 <= entry["size"] <= MAX_FILE
                or not valid_hash(entry["sha256"])):
            raise HostError("Invalid artifact file size or SHA-256.")
        total += entry["size"]
    names = {entry["path"] for entry in files}
    prefixes = {}
    for name in names:
        for path in (pathlib.PurePosixPath(name), *pathlib.PurePosixPath(name).parents):
            text = str(path)
            if prefixes.setdefault(text.casefold(), text) != text:
                raise HostError("Artifact contains case-colliding directory paths.")
    if total > MAX_TOTAL or not REQUIRED <= names:
        raise HostError("Artifact is oversized or missing the Server/SPA entrypoint.")
    if any(name.startswith(NODE_AGENT_PREFIX) for name in names) and not NODE_AGENT_REQUIRED <= names:
        raise HostError("The optional x64 discovery payload is missing its executable or runtime metadata.")
    if any(entry["size"] == 0 for entry in files if entry["path"] in REQUIRED):
        raise HostError("Server/SPA entrypoint must not be empty.")
    if any(entry["size"] == 0 for entry in files if entry["path"] in NODE_AGENT_REQUIRED):
        raise HostError("Discovery executable/runtime metadata must not be empty.")
    for name in names:
        if any(str(parent) in names for parent in pathlib.PurePosixPath(name).parents):
            raise HostError("Artifact file and directory paths collide.")
    return value


def valid_hash(value):
    return isinstance(value, str) and re.fullmatch(r"[a-f0-9]{64}", value) is not None


def load_manifest(path):
    path = pathlib.Path(path)
    if regular_file(path).st_size > MAX_MANIFEST:
        raise HostError("Artifact manifest is oversized.")
    try:
        return validate_manifest(json.loads(path.read_bytes()))
    except (json.JSONDecodeError, UnicodeDecodeError):
        raise HostError("Artifact manifest is not valid UTF-8 JSON.") from None


def package(publish_directory, archive_path, manifest_path=None):
    root = pathlib.Path(publish_directory).absolute()
    archive = pathlib.Path(archive_path).absolute()
    manifest_path = pathlib.Path(manifest_path or (str(archive) + ".manifest.json")).absolute()
    no_links(root)
    if not root.is_dir():
        raise HostError("Supply an existing Server publish directory with built SPA wwwroot.")
    for output in (archive, manifest_path):
        no_links(output)
        if output.is_relative_to(root) or output.exists():
            raise HostError("Artifact outputs must be new paths outside the publish directory.")
    if archive == manifest_path:
        raise HostError("Archive and manifest must have different paths.")
    entries = []
    total = 0
    for directory, dirs, files in os.walk(root, followlinks=False):
        for name in dirs + files:
            candidate = pathlib.Path(directory) / name
            no_links(candidate)
        dirs[:] = sorted(name for name in dirs if not name.startswith(".")
                         and name.lower() not in EXCLUDED_PARTS)
        for name in sorted(files):
            source = pathlib.Path(directory) / name
            info = regular_file(source)
            relative = source.relative_to(root).as_posix()
            mode = file_mode(relative)
            if mode is None:
                continue
            if info.st_size > MAX_FILE:
                raise HostError("An allowed artifact file exceeds the size limit.")
            total += info.st_size
            if total > MAX_TOTAL or len(entries) >= MAX_FILES:
                raise HostError("Artifact exceeds its total size or file-count limit.")
            entries.append({"path": relative, "size": info.st_size, "sha256": sha256_file(source)})
    entries.sort(key=lambda entry: entry["path"])
    # Validate before writing; the archive hash is filled in after compression.
    manifest = validate_manifest({
        "schema_version": SCHEMA, "version": VERSION, "rid": RID, "entrypoint": ENTRYPOINT,
        "sha256": "0" * 64, "size": 1, "files": entries,
    })
    archive.parent.mkdir(parents=True, exist_ok=True)
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".lucia-package-", dir=archive.parent) as temporary:
        staged = pathlib.Path(temporary) / "artifact.tar.gz"
        with staged.open("wb") as raw, gzip.GzipFile(filename="", fileobj=raw, mode="wb", mtime=0) as compressed:
            with tarfile.open(fileobj=compressed, mode="w", format=tarfile.USTAR_FORMAT) as tar:
                for entry in entries:
                    source = root / entry["path"]
                    if regular_file(source).st_size != entry["size"] or sha256_file(source) != entry["sha256"]:
                        raise HostError("Publish directory changed during packaging.")
                    header = tarfile.TarInfo(entry["path"])
                    header.size = entry["size"]
                    header.mode = file_mode(entry["path"])
                    with source.open("rb") as stream:
                        tar.addfile(header, stream)
        manifest.update(sha256=sha256_file(staged), size=staged.stat().st_size)
        validate_manifest(manifest)
        # Exclusive creation never overwrites an existing release artifact.
        with archive.open("xb") as target, staged.open("rb") as source:
            shutil.copyfileobj(source, target)
        with manifest_path.open("xb") as target:
            target.write(json_bytes(manifest))
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("publish_directory")
    parser.add_argument("archive_path")
    parser.add_argument("--manifest")
    args = parser.parse_args()
    try:
        result = package(args.publish_directory, args.archive_path, args.manifest)
        print(json.dumps({"sha256": result["sha256"], "size": result["size"], "files": len(result["files"])}))
    except (HostError, OSError) as error:
        parser.exit(1, f"Host packaging failed: {error}\n")
