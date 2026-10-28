"""What the Spark's platform update worker may do, kept apart so it can be checked anywhere.

Mirrors src/Lucia.Homelab.Server/Platform/PlatformImages.cs: the services, their policy classes and the version rules. The
worker trusts none of the web host's input: it re-reads every digest from the registry here and checks the policy again.
"""

import datetime
import json
import os
import pathlib
import re
import urllib.error
import urllib.parse
import urllib.request

UTC = datetime.timezone.utc
# Keep in step with PlatformImages.BuiltIn: service -> policy class.
SERVICES = {
    "authentik": "authentik", "gateway": "minor", "ca": "minor", "database": "patch", "directory": "patch",
    "telemetry-relay": "minor", "node-exporter": "minor", "gpu-exporter": "minor",
}
REQUEST_FIELDS = {"schemaVersion", "generation", "requestedAt", "updates"}
UPDATE_FIELDS = {"service", "tag", "digest"}
TAG = re.compile(r"\A[A-Za-z0-9_][A-Za-z0-9._-]{0,127}\Z")
DIGEST = re.compile(r"\Asha256:[a-f0-9]{64}\Z")
REGISTRIES = {"docker.io": "registry-1.docker.io", "ghcr.io": "ghcr.io"}
REPOSITORY = re.compile(r"\A[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*\Z")
MANIFEST_TYPES = ("application/vnd.oci.image.index.v1+json", "application/vnd.docker.distribution.manifest.list.v2+json",
                  "application/vnd.oci.image.manifest.v1+json", "application/vnd.docker.distribution.manifest.v2+json")
CDI = pathlib.Path("/var/run/cdi/nvidia.yaml")
HOST_PATH = re.compile(r"""\A\s*(?:-\s*)?["']?hostPath["']?\s*:\s*["']?([^"'\s,]+)["']?\s*,?\s*\Z""")
MAX_LONG = 2 ** 63 - 1


def now():
    return datetime.datetime.now(UTC).isoformat()


# ---------------------------------------------------------------- versions and policy

def version(tag):
    """A tag's numbers and its shape with each number replaced, as StackImages.Version reads it; None without numbers."""
    shape, numbers = [], []
    for part in re.split(r"([._-])", tag):
        if not part:
            continue
        if part in (".", "-", "_"):
            shape.append(part)
            continue
        if (len(part) >= 7 and all(c in "0123456789abcdef" for c in part) and any(c in "abcdef" for c in part)
                and any(c in "0123456789" for c in part)):
            shape.append("#")
            continue
        for token in re.findall(r"\d+|\D+", part):
            if token[0] not in "0123456789":
                shape.append(token)
                continue
            if not token.isascii() or int(token) > MAX_LONG:
                return None
            numbers.append(int(token))
            shape.append("0")
    return ("".join(shape), numbers) if numbers else None


def compare(current, candidate):
    """Positive when candidate is a newer tag of the same shape as current."""
    a, b = version(current), version(candidate)
    if not a or not b or a[0] != b[0]:
        return 0
    for x, y in zip(a[1], b[1]):
        if x != y:
            return 1 if y > x else -1
    return 0


def next_line(current, newer):
    """The first release line (year.minor) after the current one, among newer versions' numbers."""
    if len(current) < 3:
        return None
    lines = sorted({tuple(numbers[:2]) for numbers in newer if numbers[:2] != current[:2]})
    return list(lines[0]) if lines else None


def allowed(policy, current, candidate, tags=None):
    """Whether a policy lets current move to the newer candidate; for Authentik with tags, only to the next release line."""
    if compare(current, candidate) <= 0:
        return False
    now_numbers, next_numbers = version(current)[1], version(candidate)[1]
    if policy == "patch":
        return next_numbers[:-1] == now_numbers[:-1]
    if policy == "minor":
        return next_numbers[0] == now_numbers[0]
    if policy == "authentik":
        if len(now_numbers) < 3:
            return False
        if next_numbers[:2] == now_numbers[:2] or tags is None:
            return True
        line = next_line(now_numbers, [version(tag)[1] for tag in tags if compare(current, tag) > 0])
        return line is not None and next_numbers[:2] == line
    return False


def release_line(current, candidate):
    """Whether an Authentik update moves to another release line, which migrates its database for good."""
    a, b = version(current), version(candidate)
    return bool(a and b and a[1][:2] != b[1][:2])


# ---------------------------------------------------------------- the web host's request

def validate_request(value):
    """The web host's request.json, with exact keys; returns the updates. Raises ValueError for anything else."""
    if not isinstance(value, dict) or set(value) != REQUEST_FIELDS or type(value["schemaVersion"]) is not int \
            or value["schemaVersion"] != 1 or type(value["generation"]) is not int or value["generation"] < 0 \
            or not isinstance(value["requestedAt"], str) or len(value["requestedAt"]) > 64:
        raise ValueError("Invalid platform update request.")
    updates = value["updates"]
    if not isinstance(updates, list) or not 1 <= len(updates) <= len(SERVICES):
        raise ValueError("Invalid platform update request.")
    seen = set()
    for update in updates:
        if (not isinstance(update, dict) or set(update) != UPDATE_FIELDS or update["service"] not in SERVICES
                or update["service"] in seen or not isinstance(update["tag"], str) or not TAG.match(update["tag"])
                or not isinstance(update["digest"], str) or not DIGEST.match(update["digest"])):
            raise ValueError("Invalid platform update request.")
        seen.add(update["service"])
    return [dict(update) for update in updates]


def validate_manifest(value):
    """The AppHost's platform-images.json from the identity state: service -> image entry."""
    if not isinstance(value, dict) or value.get("schemaVersion") != 1 or not isinstance(value.get("images"), list):
        raise ValueError("The platform image manifest is invalid.")
    images = {}
    for image in value["images"]:
        if (not isinstance(image, dict) or image.get("service") not in SERVICES or image.get("policy") != SERVICES[image["service"]]
                or image.get("registry") not in REGISTRIES or not isinstance(image.get("repository"), str)
                or not REPOSITORY.match(image["repository"]) or not isinstance(image.get("tag"), str) or not TAG.match(image["tag"])
                or not isinstance(image.get("digest"), str) or not DIGEST.match(image["digest"])
                or not isinstance(image.get("builtIn"), dict) or not TAG.match(str(image["builtIn"].get("tag")))
                or not DIGEST.match(str(image["builtIn"].get("digest")))
                or not isinstance(image.get("containers"), list) or not image["containers"]
                or any(not isinstance(name, str) or not re.fullmatch(r"[a-z][a-z0-9-]{0,62}", name) for name in image["containers"])
                or image["service"] in images):
            raise ValueError("The platform image manifest is invalid.")
        images[image["service"]] = image
    if set(images) != set(SERVICES):
        raise ValueError("The platform image manifest doesn't list every platform image.")
    return images


def override_entry(image, tag, digest):
    """An images.override.json entry: the new pin, and the built-in pin it replaces (see PlatformImages.Effective)."""
    reference = image["repository"] if image["registry"] == "docker.io" else image["registry"] + "/" + image["repository"]
    return {"repository": reference, "tag": tag, "digest": digest,
            "replaces": {"tag": image["builtIn"]["tag"], "digest": image["builtIn"]["digest"]}}


# ---------------------------------------------------------------- the NVIDIA device list (CDI)

def gpu_devices(path=CDI, maximum=8 * 1024 * 1024):
    """Whether every hostPath in the NVIDIA device list exists. It's world-readable; anything else is reported, not assumed."""
    result = {"present": False, "current": False, "missing": [], "missingCount": 0, "paths": 0, "message": None, "checkedAt": now()}
    try:
        with open(path, "rb") as stream:
            data = stream.read(maximum + 1)
    except FileNotFoundError:
        result["message"] = "The NVIDIA device list doesn't exist."
        return result
    except OSError:
        result["message"] = "The NVIDIA device list couldn't be read."
        return result
    if len(data) > maximum:
        result["message"] = "The NVIDIA device list is unexpectedly large."
        return result
    paths = []
    for line in data.decode("utf-8", "replace").splitlines():
        match = HOST_PATH.match(line)
        if match and match.group(1).startswith("/"):
            paths.append(match.group(1))
    missing = sorted({item for item in paths if not os.path.lexists(item)})
    result.update(present=True, current=not missing, missing=missing[:50], missingCount=len(missing), paths=len(paths))
    if missing:
        result["message"] = "The NVIDIA device list names {} file{} that no longer exist{}.".format(
            len(missing), "" if len(missing) == 1 else "s", "s" if len(missing) == 1 else "")
    return result


STALE_GPU = "The NVIDIA device list is out of date, so GPU containers can't start. Use Refresh NVIDIA device list on the Spark updates page first."


# ---------------------------------------------------------------- the registry, read independently of the web host

def _source(registry, repository):
    api = REGISTRIES[registry]
    path = repository if registry != "docker.io" or "/" in repository else "library/" + repository
    if not REPOSITORY.match(path):
        raise ValueError("Invalid repository.")
    return api, path


def _send(method, url, path, token, accept=False):
    for attempt in range(2):
        headers = {"User-Agent": "lucia-platform-update"}
        if accept:
            headers["Accept"] = ", ".join(MANIFEST_TYPES)
        if token[0]:
            headers["Authorization"] = "Bearer " + token[0]
        try:
            return urllib.request.urlopen(urllib.request.Request(url, method=method, headers=headers), timeout=30)
        except urllib.error.HTTPError as error:
            challenge = error.headers.get("WWW-Authenticate", "") if error.headers else ""
            if error.code != 401 or attempt or not challenge.lower().startswith("bearer "):
                raise
            values = dict(re.findall(r'(\w+)="([^"]*)"', challenge))
            realm = urllib.parse.urlsplit(values.get("realm", ""))
            if realm.scheme != "https" or not realm.netloc:
                raise RuntimeError("The registry asked for a sign-in Lucia can't do.") from None
            query = urllib.parse.urlencode({key: value for key, value in (("scope", f"repository:{path}:pull"),
                                                                          ("service", values.get("service"))) if value})
            with urllib.request.urlopen(urllib.request.Request(values["realm"] + ("&" if realm.query else "?") + query,
                                                               headers={"User-Agent": "lucia-platform-update"}), timeout=30) as response:
                body = json.loads(response.read(1024 * 1024))
            token[0] = body.get("token") or body.get("access_token")
            if not isinstance(token[0], str):
                raise RuntimeError("The registry returned no pull token.") from None
    raise RuntimeError("The registry refused Lucia.")


def registry_digest(registry, repository, tag, token=None):
    """The digest the registry serves for a tag now (HEAD, like docker pull resolves it)."""
    api, path = _source(registry, repository)
    token = token if token is not None else [None]
    with _send("HEAD", f"https://{api}/v2/{path}/manifests/{urllib.parse.quote(tag, safe='')}", path, token, accept=True) as response:
        digest = response.headers.get("Docker-Content-Digest", "")
    if not DIGEST.match(digest):
        raise RuntimeError(f"The registry gave no digest for {repository}:{tag}.")
    return digest


def registry_tags(registry, repository, token=None, pages=50):
    api, path = _source(registry, repository)
    token = token if token is not None else [None]
    tags, following = [], f"/v2/{path}/tags/list?n=1000"
    for _ in range(pages):
        if not following:
            break
        with _send("GET", f"https://{api}{following}", path, token) as response:
            body = json.loads(response.read(8 * 1024 * 1024 + 1))
            link = response.headers.get("Link", "")
        tags.extend(item for item in body.get("tags") or [] if isinstance(item, str))
        match = re.search(r'<([^>]+)>\s*;\s*rel="?next"?', link)
        following = match.group(1) if match and match.group(1).startswith("/v2/") else None
    return tags


def moved(running, digest):
    """Aspire publishes pinned images by digest alone (repository@sha256:...), with no tag, so only the digest is compared."""
    return "@" in running and running.rsplit("@", 1)[1] == digest
