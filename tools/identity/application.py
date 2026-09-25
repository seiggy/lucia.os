"""Repeat-safe managed host registration against Authentik 2026.8.3.

Public entry points take a loaded Provisioner and an HTTPS origin. prepare is
local-only; status uses read-only LDAP/API checks; reconcile mutates owned
application resources and upgrades the default session-only browser login.
Callers must serialize identity work with owner enrollment.

State: host-auth.json (v1, public identifiers/ownership journal), host-auth.lock,
and secrets/host-oidc-client-secret (0600). Never delete the record to repair a
registration: its installation marker and client credentials must survive.

Verified against the installed 2026.8.3 OpenAPI schema and OAuth2 source:
redirect_uris has separate authorization/logout entries; logout_uri instead
means an OP-initiated logout notification endpoint. PKCE has no provider toggle:
the BFF must send S256 challenges, and retain id_token_hint for RP logout.
Readiness proves registration/policy/mapping, NOT a browser code exchange.

Reviewed domain preparation: prepare_domain_application(loaded_provisioner,
legacy_public_origin, reviewed_profile) adds only exact callbacks and changes
Lucia's launch URL. Run in the native bootstrap administrative context; never
mount that principal's credentials into the web host. It does not activate a
profile, modify DNS/trust, or prove browser SSO. Publish active.json only after
the owner-reviewed end-to-end checks. New browser origins need fresh SSO; old
issuer JWTs are not valid after activation, while legacy-origin API keys remain.
"""

import contextlib
import datetime
import hmac
import hashlib
import json
import os
import pathlib
import re
import secrets
import stat
import urllib.parse
import uuid

from owner import load_owner, owner_identity
from provision import authority, configure_browser_session, exactly_one, validate_host, write_file


APPLICATIONS = "/api/v3/core/applications/"
PROVIDERS = "/api/v3/providers/oauth2/"
GROUPS = "/api/v3/core/groups/"
MAPPINGS = "/api/v3/propertymappings/provider/scope/"
BINDINGS = "/api/v3/policies/bindings/"
CERTIFICATES = "/api/v3/crypto/certificatekeypairs/"
SECRET_NAME = "host-oidc-client-secret"
SCOPES = ["openid", "profile", "email", "lucia_api"]
PROFILE_FIELDS = {
    "schemaVersion", "verifiedZone", "canonicalLuciaOrigin", "canonicalAuthentikOrigin",
    "sparkOrigin", "legacyLuciaOrigin", "legacyAuthority", "profileId", "activatedAt",
}


class ApplicationDrift(RuntimeError):
    """Credential-free missing/unsafe configuration; requires repair or review."""


def _origin(value):
    if not isinstance(value, str) or not value or any(c.isspace() or ord(c) < 32 for c in value):
        raise ValueError("Managed host origin must be an HTTPS origin.")
    parsed = urllib.parse.urlsplit(value)
    if (parsed.scheme != "https" or not parsed.hostname or parsed.username is not None
            or parsed.password is not None or parsed.path not in ("", "/")
            or "?" in value or "#" in value or "\\" in value or "%" in value):
        raise ValueError("Managed host origin must use HTTPS without credentials, path, query, or fragment.")
    host = validate_host(parsed.hostname)
    port = parsed.port
    if (port is not None and not 1 <= port <= 65535) or parsed.netloc.endswith(":"):
        raise ValueError("Managed host origin has an invalid port.")
    host = f"[{host}]" if ":" in host else host
    return "https://" + host + (f":{port}" if port not in (None, 443) else "")


def _domain_profile(value, record):
    try:
        if (not isinstance(value, dict) or set(value) != PROFILE_FIELDS
                or type(value["schemaVersion"]) is not int or value["schemaVersion"] != 1
                or str(uuid.UUID(value["profileId"])) != value["profileId"]
                or uuid.UUID(value["profileId"]).int == 0):
            raise ValueError()
        activated = datetime.datetime.fromisoformat(value["activatedAt"].replace("Z", "+00:00"))
        if (activated.tzinfo is None or activated.year == 1
                or activated > datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(minutes=5)):
            raise ValueError()
        zone = value["verifiedZone"]
        def dns_name(name):
            return (isinstance(name, str) and len(name) <= 253 and "." in name
                    and all(re.fullmatch(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?", label)
                            for label in name.split("."))
                    and not re.fullmatch(r"[0-9.]+", name))
        if not dns_name(zone):
            raise ValueError()
        origins = [value[key] for key in ("canonicalLuciaOrigin", "canonicalAuthentikOrigin", "sparkOrigin")]
        for origin in origins:
            host = urllib.parse.urlsplit(origin).hostname
            if (not dns_name(host) or origin != "https://" + host or _origin(origin) != origin
                    or host != zone and not host.endswith("." + zone)):
                raise ValueError()
        if (len(set(origins)) != 3 or value["legacyLuciaOrigin"] != record["public_origin"]
                or value["legacyAuthority"] != record["authority"]):
            raise ValueError()
        legacy_hosts = {
            urllib.parse.urlsplit(value[key]).hostname.encode("idna").decode("ascii").rstrip(".").lower()
            for key in ("legacyLuciaOrigin", "legacyAuthority")
        }
        if any(urllib.parse.urlsplit(origin).hostname in legacy_hosts for origin in origins):
            raise ApplicationDrift("Use distinct public hostnames to preserve private recovery addresses.")
    except (KeyError, TypeError, ValueError, AttributeError, OverflowError):
        raise ApplicationDrift("Domain profile is invalid or disagrees with this installation's legacy ownership.") from None
    return dict(value)


def _no_links(path):
    for entry in (path, *path.parents):
        if entry.is_symlink() or getattr(entry, "is_junction", lambda: False)():
            raise ApplicationDrift("Domain profile paths must not contain links or reparse points.")


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON property.")
        result[key] = value
    return result


def _private_json(path, limit=16384, missing_ok=False):
    _no_links(path)
    try:
        info = path.stat()
        if not stat.S_ISREG(info.st_mode) or not 0 < info.st_size <= limit:
            raise ValueError()
        if os.name != "nt" and (stat.S_IMODE(info.st_mode) & 0o077):
            raise ValueError()
        with path.open("rb") as stream:
            data = stream.read(limit + 1)
        if len(data) > limit:
            raise ValueError()
        value = json.loads(data, object_pairs_hook=_unique_object)
        if not isinstance(value, dict):
            raise ValueError()
        return value
    except FileNotFoundError:
        if missing_ok:
            return None
        raise ApplicationDrift("Domain profile or host settings disappeared; restore the reviewed state.") from None
    except (OSError, ValueError, RecursionError):
        raise ApplicationDrift("Domain profile or host settings are not readable, private, bounded JSON.") from None


def _active_domain_profile(p, record):
    path = p.state / "host-settings.json"
    settings = _private_json(path, 65536, missing_ok=True)
    if settings is None:
        return None
    try:
        root = pathlib.Path(settings["host_state"])
        data = pathlib.Path(settings["data_directory"])
        auth = settings["authentication"]
        if (settings["schema_version"] != 1 or settings["component"] != "managedhost"
                or settings["installation_id"] != hashlib.sha256(str(p.state).encode()).hexdigest()
                or not root.is_absolute() or root in (pathlib.Path(root.anchor), pathlib.Path.home())
                or ".." in root.parts or data != root / "data" or not data.is_absolute()
                or any(auth[key] != record[key] for key in ("public_origin", "authority", "client_id"))
                or auth["client_secret_file"] != str(p.state / "secrets" / SECRET_NAME)):
            raise ValueError()
    except (KeyError, TypeError, ValueError):
        raise ApplicationDrift("Host settings do not match the existing managed application ownership.") from None
    path = data / "domains" / "active.json"
    profile = _private_json(path, missing_ok=True)
    return _domain_profile(profile, record) if profile is not None else None


def _redirects(origins):
    return [
        {"matching_mode": "strict", "url": origin + suffix, "redirect_uri_type": kind}
        for origin in dict.fromkeys(origins)
        for suffix, kind in (("/signin-oidc", "authorization"), ("/signout-callback-oidc", "logout"))
    ]


def _paths(p):
    record = p.state / "host-auth.json"
    secret = p.state / "secrets" / SECRET_NAME
    for path in (p.state, secret.parent, record, secret, record.with_name(record.name + ".new"),
                 secret.with_name(secret.name + ".new"), p.state / "host-auth.lock"):
        if path.is_symlink():
            raise ApplicationDrift("Managed host state or secret path is a symbolic link; review it.")
    return record, secret


@contextlib.contextmanager
def _lock(p):
    _paths(p)
    with os.fdopen(os.open(p.state / "host-auth.lock", os.O_RDWR | os.O_CREAT, 0o600), "r+b") as lock:
        if os.name == "nt":
            import msvcrt
            if os.fstat(lock.fileno()).st_size == 0:
                lock.write(b"\0")
                lock.flush()
            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            yield
        finally:
            if os.name == "nt":
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(lock.fileno(), fcntl.LOCK_UN)


def _save(p, record):
    path, _ = _paths(p)
    write_file(path, json.dumps(record, indent=2) + "\n")
    os.chmod(path, 0o600)


def _load(p, origin):
    path, _ = _paths(p)
    if not path.is_file():
        raise ApplicationDrift("Managed host registration record is missing; run prepare_application.")
    try:
        record = json.loads(path.read_text(encoding="utf-8"))
        installation = str(uuid.UUID(record["installation_id"]))
        expected_authority = "https://" + authority(
            p.settings["public_host"], p.settings["ports"]["authentik"]) + "/application/o/lucia/"
        if (record["schema_version"] != 1 or record["installation_id"] != installation
                or record["client_id"] != "lucia-host-" + uuid.UUID(installation).hex
                or record["markers"] != _markers(installation)
                or not isinstance(record["resources"], dict)
                or not isinstance(record["secret_initialized"], bool)):
            raise ValueError()
        resources = record["resources"]
        for key in ("application", "access_group", "scope_mapping", "signing_key", "owner_group"):
            if key in resources and str(uuid.UUID(resources[key])) != resources[key]:
                raise ValueError()
        for key in ("provider", "owner_user"):
            if key in resources and (type(resources[key]) is not int or resources[key] < 1):
                raise ValueError()
        for role, binding in resources.get("bindings", {}).items():
            if role not in ("owner", "inference") or not isinstance(binding["pending"], bool):
                raise ValueError()
            for key in ("id", "target", "group"):
                if key == "id" and binding[key] is None and binding["pending"]:
                    continue
                if str(uuid.UUID(binding[key])) != binding[key]:
                    raise ValueError()
        if record["public_origin"] != origin or record["authority"] != expected_authority:
            raise ApplicationDrift("Managed host origin or identity authority changed; an explicit migration is required.")
    except (KeyError, TypeError, ValueError, AttributeError):
        raise ApplicationDrift("Managed host registration record is invalid; restore it instead of adopting resources.") from None
    return record


def _markers(installation):
    return {
        "application": "Lucia managed host " + installation,
        "provider": "Lucia managed host OIDC " + installation,
        "access_group": installation,
        "scope_mapping": "lucia/host/" + installation + "/scope-lucia_api",
    }


def _secret(p):
    _, path = _paths(p)
    if not path.is_file():
        raise ApplicationDrift("Managed OIDC client secret is missing; restore it, never rotate it on rerun.")
    value = path.read_text(encoding="ascii").rstrip("\r\n")
    if not re.fullmatch(r"[A-Za-z0-9_-]{64}", value):
        raise ApplicationDrift("Managed OIDC client secret is incomplete; restore it instead of regenerating it.")
    p.values[SECRET_NAME] = value
    return value


def _public(p, record):
    return {
        **record,
        "application_slug": "lucia",
        "application_name": "Lucia",
        "launch_url": record["public_origin"] + "/",
        "redirect_uri": record["public_origin"] + "/signin-oidc",
        "post_logout_uri": record["public_origin"] + "/signout-callback-oidc",
        "scopes": list(SCOPES),
        "client_secret_file": str(p.state / "secrets" / SECRET_NAME),
    }


def _prepare(p, origin):
    path, secret = _paths(p)
    staged_record = path.with_name(path.name + ".new")
    if not path.exists() and staged_record.exists():
        # No API calls can precede the installation journal. Preserve its marker
        # if the first atomic publication was interrupted.
        os.replace(staged_record, path)
    if not path.exists():
        if secret.exists() or secret.with_name(secret.name + ".new").exists():
            raise ApplicationDrift("OIDC secret exists without its installation record; restore the record.")
        installation = str(uuid.uuid4())
        record = {
            "schema_version": 1, "installation_id": installation,
            "client_id": "lucia-host-" + uuid.UUID(installation).hex,
            "public_origin": origin,
            "authority": "https://" + authority(p.settings["public_host"], p.settings["ports"]["authentik"])
                         + "/application/o/lucia/",
            "secret_initialized": False, "markers": _markers(installation), "resources": {},
        }
        _save(p, record)
    record = _load(p, origin)
    if not secret.exists() and not record["secret_initialized"]:
        staged = secret.with_name(secret.name + ".new")
        if staged.exists():
            value = staged.read_text(encoding="ascii")
            if not re.fullmatch(r"[A-Za-z0-9_-]{64}", value):
                raise ApplicationDrift("Interrupted client secret write is incomplete; restore the staged secret.")
            os.replace(staged, secret)
        else:
            write_file(secret, secrets.token_urlsafe(48))
    _secret(p)
    os.chmod(secret, 0o600)
    record["secret_initialized"] = True
    _save(p, record)
    return record


def prepare_application(provisioner, public_origin):
    """Persist stable local configuration, without contacting Authentik."""
    origin = _origin(public_origin)
    with _lock(provisioner):
        return _public(provisioner, _prepare(provisioner, origin))


def _api(p, method, path, body=None, allow_missing=False):
    try:
        return p.api(method, path, body, allow_missing=allow_missing)
    except (RuntimeError, OSError, ValueError):
        # Provisioner includes response bodies in errors; OAuth responses may hold
        # other clients' secrets that cannot safely be added to its redaction set.
        raise RuntimeError(f"Authentik {method} {path.split('?')[0]} failed; registration was not verified.") from None


def _list(p, endpoint, **filters):
    result = []
    page = 1
    while page <= 100:
        data = _api(p, "GET", endpoint + "?" + urllib.parse.urlencode({**filters, "page_size": 100, "page": page}))
        result.extend(data["results"])
        following = data["pagination"]["next"]
        if not following:
            return result
        if not isinstance(following, int) or following <= page:
            raise RuntimeError("Authentik returned invalid pagination; registration was not verified.")
        page = following
    raise RuntimeError("Authentik listing exceeded 100 pages; narrow the installation before reconciling.")


def _owned(record, kind, items, candidate, owns):
    saved = record["resources"].get(kind)
    matches = [item for item in items if item["pk"] == saved or candidate(item) or owns(item)]
    if len(matches) > 1 or matches and not owns(matches[0]):
        raise ApplicationDrift(f"Managed {kind} has an ownership collision; refusing to adopt or overwrite it.")
    return matches[0] if matches else None


def _valid_signer(item):
    try:
        expiry = datetime.datetime.fromisoformat(item["cert_expiry"].replace("Z", "+00:00"))
        return (item["private_key_available"] is True and item["key_type"] == "rsa"
                and item["name"] != "lucia-ldap-ca"
                and expiry > datetime.datetime.now(datetime.timezone.utc))
    except (KeyError, TypeError, ValueError, AttributeError):
        return False


def _role_expression(owner_group, access_group):
    # all_groups matches Authentik's binding engine, including inherited membership.
    return (
        f'if request.user.all_groups().filter(pk="{uuid.UUID(owner_group)}").exists():\n'
        '    return {"lucia_role": "Owner"}\n'
        f'if request.user.all_groups().filter(pk="{uuid.UUID(access_group)}").exists():\n'
        '    return {"lucia_role": "Inference"}\n'
        "return {}"
    )


def _snapshot(p, record):
    domain_profile = _active_domain_profile(p, record)
    me = _api(p, "GET", "/api/v3/core/users/me/")["user"]
    if not me["is_superuser"]:
        raise ApplicationDrift("The bootstrap API principal cannot verify another user's application access.")
    owner = load_owner(p)
    if owner.get("complete") is not True:
        raise ApplicationDrift("LDAP owner enrollment and sign-in verification must finish first.")
    user_pk = owner_identity(p, owner, grant=False)
    groups = _list(p, GROUPS)
    owner_group = exactly_one([item for item in groups if item["name"] == "lucia-owners"], "synced owners group")
    markers = record["markers"]
    group = _owned(record, "access_group", groups, lambda item: item["name"] == "lucia-users",
                   lambda item: item.get("attributes", {}).get("lucia_host_installation") == markers["access_group"])
    if group:
        if group["is_superuser"] or group.get("parents") or group.get("roles"):
            raise ApplicationDrift("Managed lucia-users has administrative roles or parent groups; review without removing roles.")
        if _list(p, "/api/v3/sources/group_connections/ldap/", group=group["pk"]):
            raise ApplicationDrift("Managed lucia-users is linked to LDAP; it must remain Authentik-managed.")
    mappings = _list(p, MAPPINGS)
    mapping = _owned(record, "scope_mapping", mappings,
                     lambda item: item["name"] == markers["scope_mapping"],
                     lambda item: item.get("managed") == markers["scope_mapping"])
    defaults = []
    for scope in SCOPES[:3]:
        managed = "goauthentik.io/providers/oauth2/scope-" + scope
        choices = [item for item in mappings if item.get("managed") == managed and item["scope_name"] == scope]
        if len(choices) != 1:
            raise ApplicationDrift("Required Authentik default OIDC scope mapping is missing or ambiguous: " + scope)
        defaults.append(choices[0]["pk"])
    providers = _list(p, PROVIDERS)
    provider = _owned(record, "provider", providers,
                      lambda item: item["client_id"] == record["client_id"] or item["name"] == markers["provider"],
                      lambda item: item["name"] == markers["provider"] and item["client_id"] == record["client_id"])
    if provider:
        if not hmac.compare_digest(provider["client_secret"], _secret(p)):
            raise ApplicationDrift("Managed provider client secret drifted; restore it through explicit review, not rotation.")
        if (provider.get("assigned_application_slug") not in (None, "", "lucia")
                or provider.get("assigned_backchannel_application_slug")):
            raise ApplicationDrift("Managed provider is assigned to another application; refusing to modify it.")
    if mapping and any(item["pk"] != (provider["pk"] if provider else None)
                       and mapping["pk"] in item["property_mappings"] for item in providers):
        raise ApplicationDrift("Managed scope mapping is shared with another provider; refusing to change its claims.")
    apps = _list(p, APPLICATIONS, superuser_full_list="true")
    app = _owned(record, "application", apps,
                 lambda item: item["slug"] == "lucia" or item["name"] == "Lucia",
                 lambda item: item.get("meta_description") == markers["application"])
    if app and (app["slug"] != "lucia" or app.get("backchannel_providers")
                or app["provider"] is not None and (not provider or app["provider"] != provider["pk"])):
        raise ApplicationDrift("Managed application has an unexpected slug or provider assignment; review it.")
    certs = _list(p, CERTIFICATES)
    saved_signer = record["resources"].get("signing_key")
    if saved_signer:
        signer = next((item for item in certs if item["pk"] == saved_signer), None)
        if not signer or not _valid_signer(signer):
            raise ApplicationDrift("Persisted OIDC signing key is missing, expired, or not a private RSA key; review key migration.")
    else:
        choices = [item for item in certs if _valid_signer(item)]
        preferred = [item for item in choices if item["name"] == "authentik Self-signed Certificate"]
        choices = preferred or choices
        if len(choices) != 1:
            raise ApplicationDrift("No unambiguous available private RSA signing certificate; public LDAP CA is never a signer.")
        signer = choices[0]
    flows = _list(p, "/api/v3/flows/instances/")
    selected_flows = {}
    for field, slug, designation in (
        ("authentication_flow", "default-authentication-flow", "authentication"),
        ("authorization_flow", "default-provider-authorization-implicit-consent", "authorization"),
        ("invalidation_flow", "default-provider-invalidation-flow", "invalidation"),
    ):
        choices = [item for item in flows if item["slug"] == slug and item["designation"] == designation]
        if len(choices) != 1:
            raise ApplicationDrift("Required Authentik flow is missing or ambiguous: " + slug)
        selected_flows[field] = choices[0]["pk"]
    bindings = _inspect_bindings(p, record, app, owner_group, group)
    return dict(application=app, provider=provider, access_group=group, scope_mapping=mapping,
                signing_key=signer, defaults=defaults, flows=selected_flows, bindings=bindings,
                owner_group=owner_group, owner_user=user_pk, domain_profile=domain_profile)


def _binding_body(record, role, target, group):
    order = 1000000000 + uuid.UUID(record["installation_id"]).int % 1000000000
    return dict(target=target, group=group, policy=None, user=None, negate=False,
                enabled=True, order=order + (role == "inference"), timeout=30, failure_result=False)


def _inspect_bindings(p, record, app, owner_group, access_group):
    bindings = _list(p, BINDINGS, target=app["pk"]) if app else []
    saved = record["resources"].get("bindings", {})
    result = {}
    for role, group in (("owner", owner_group), ("inference", access_group)):
        journal = saved.get(role, {})
        current = _api(p, "GET", BINDINGS + journal["id"] + "/", allow_missing=True) if journal.get("id") else None
        if current and (not app or current["target"] != app["pk"]):
            raise ApplicationDrift("Managed binding was reassigned to another target; refusing to overwrite it.")
        desired = _binding_body(record, role, app["pk"], group["pk"]) if app and group else None
        if not current and journal.get("pending") and desired:
            candidates = [item for item in bindings if all(item.get(key) == value for key, value in desired.items())]
            if len(candidates) > 1:
                raise ApplicationDrift("Managed binding creation is ambiguous; review duplicate bindings.")
            current = candidates[0] if candidates else None
        if current and (not desired or current["order"] != desired["order"]
                        or current["group"] != desired["group"] or current["policy"] or current["user"]):
            raise ApplicationDrift("Managed binding group, policy, user, or ownership order changed; review it.")
        if current and (current.get("expires") or current.get("expiring")):
            raise ApplicationDrift("Managed binding has temporary access semantics; review its expiration.")
        result[role] = current
    known = {item["pk"] for item in result.values() if item}
    if any(item["pk"] not in known for item in bindings):
        raise ApplicationDrift("Unmanaged application bindings exist; refusing to remove or authorize unrelated access.")
    return result


def _desired(record, snapshot):
    group = snapshot["access_group"]
    mapping = snapshot["scope_mapping"]
    provider = snapshot["provider"]
    profile = snapshot.get("domain_profile")
    origin = profile["canonicalLuciaOrigin"] if profile else record["public_origin"]
    return {
        "access_group": {"name": "lucia-users", "is_superuser": False},
        "scope_mapping": {
            "name": record["markers"]["scope_mapping"], "managed": record["markers"]["scope_mapping"],
            "scope_name": "lucia_api", "description": "Access Lucia according to explicitly granted group membership.",
            "expression": _role_expression(snapshot["owner_group"]["pk"], group["pk"]) if group else "",
        },
        "provider": {
            "name": record["markers"]["provider"], **snapshot["flows"],
            "client_type": "confidential", "client_id": record["client_id"],
            "grant_types": ["authorization_code"], "issuer_mode": "per_provider", "sub_mode": "user_uuid",
            "signing_key": snapshot["signing_key"]["pk"], "encryption_key": None,
            "include_claims_in_id_token": True,
            "property_mappings": snapshot["defaults"] + ([mapping["pk"]] if mapping else []),
            "redirect_uris": _redirects([record["public_origin"], origin]),
            "logout_uri": "", "logout_method": "backchannel",
            "jwt_federation_sources": [], "jwt_federation_providers": [],
        },
        "application": {
            "slug": "lucia", "name": "Lucia", "provider": provider["pk"] if provider else None,
            "meta_launch_url": origin + "/", "meta_description": record["markers"]["application"],
            "meta_hide": False, "policy_engine_mode": "any",
        },
    }


def _changes(current, desired):
    differences = {}
    for key, value in desired.items():
        actual = current.get(key)
        equal = set(actual or []) == set(value) if key in ("property_mappings", "grant_types") else actual == value
        if not equal:
            differences[key] = value
    return differences


def _reasons(record, snapshot):
    reasons = []
    if not record["secret_initialized"]:
        reasons.append("Local client secret initialization needs completion.")
    desired = _desired(record, snapshot)
    for kind in ("access_group", "scope_mapping", "provider", "application"):
        current = snapshot[kind]
        if not current:
            reasons.append("Missing managed " + kind + ".")
        else:
            differences = _changes(current, desired[kind])
            if differences:
                reasons.append("Managed " + kind + " drift: " + ", ".join(sorted(differences)) + ".")
            if record["resources"].get(kind) != current["pk"]:
                reasons.append("Unjournaled managed " + kind + ".")
    for role, group in (("owner", snapshot["owner_group"]), ("inference", snapshot["access_group"])):
        current = snapshot["bindings"][role]
        if not current:
            reasons.append("Missing managed " + role + " binding.")
        elif group and _changes(current, _binding_body(record, role, snapshot["application"]["pk"], group["pk"])):
            reasons.append("Managed " + role + " binding drift.")
        elif record["resources"].get("bindings", {}).get(role, {}).get("id") != current["pk"]:
            reasons.append("Unjournaled managed " + role + " binding.")
    if record["resources"].get("owner_group") != snapshot["owner_group"]["pk"]:
        reasons.append("Owner group registration needs reconciliation.")
    if record["resources"].get("signing_key") != snapshot["signing_key"]["pk"]:
        reasons.append("Signing key registration needs reconciliation.")
    return reasons


def _evidence(p, snapshot):
    user = snapshot["owner_user"]
    access = _api(p, "GET", APPLICATIONS + "lucia/check_access/?for_user=" + str(user))
    visible = _list(p, APPLICATIONS, for_user=user, slug="lucia", only_with_launch_url="true",
                    superuser_full_list="false")
    preview = _api(p, "GET", PROVIDERS + str(snapshot["provider"]["pk"]) + "/preview_user/?for_user=" + str(user))
    reasons = []
    if access.get("passing") is not True:
        reasons.append("Authentik policy engine did not authorize the enrolled owner.")
    if not any(item["pk"] == snapshot["application"]["pk"] for item in visible):
        reasons.append("Managed application is not visible in the enrolled owner's application list.")
    if preview.get("preview", {}).get("lucia_role") != "Owner":
        reasons.append("Authentik mapping preview did not produce the enrolled owner's Owner claim.")
    return reasons


def application_status(provisioner, public_origin):
    """Read actual resources and owner policy evidence; never write local/remote state."""
    origin = _origin(public_origin)
    result = {"ready": False, "reasons": [], "sso_verified": False}
    try:
        record = _load(provisioner, origin)
        _secret(provisioner)
        result.update(_public(provisioner, record))
        snapshot = _snapshot(provisioner, record)
        result["reasons"] = _reasons(record, snapshot)
        if not result["reasons"]:
            result["reasons"] = _evidence(provisioner, snapshot)
        result["ready"] = not result["reasons"]
    except ApplicationDrift as error:
        result["reasons"] = [str(error)]
    return result


def prepare_domain_application(provisioner, public_origin, reviewed_profile):
    """Administrative, explicitly reviewed preparation, NOT activation or SSO evidence.

    Only exact authorization/logout callbacks and meta_launch_url may change.
    The existing registration must otherwise be healthy. Retry with the same
    profile after a partial transport failure; never rotate or repair credentials.
    Serialize this operation with domain activation and native bootstrap work.
    """
    p = provisioner
    with _lock(p):
        record = _load(p, _origin(public_origin))
        _secret(p)
        profile = _domain_profile(reviewed_profile, record)
        snapshot = _snapshot(p, record)
        active = snapshot["domain_profile"]
        if active is not None and active != profile:
            raise ApplicationDrift("A different domain profile is active; a further migration requires separate review.")
        snapshot["domain_profile"] = profile
        desired = _desired(record, snapshot)
        legacy_redirects = _redirects([record["public_origin"]])
        # Accept only the old or this exact reviewed state (including interrupted
        # preparation). Unrelated callbacks/launch URLs must never be adopted.
        provider, application = snapshot["provider"], snapshot["application"]
        if (not provider or not application
                or provider["redirect_uris"] not in (legacy_redirects, desired["provider"]["redirect_uris"])
                or application["meta_launch_url"] not in (record["public_origin"] + "/", desired["application"]["meta_launch_url"])):
            raise ApplicationDrift("Existing callbacks or launch URL conflict with the reviewed domain profile.")
        checked = {**snapshot, "provider": {**provider, "redirect_uris": desired["provider"]["redirect_uris"]},
                   "application": {**application, "meta_launch_url": desired["application"]["meta_launch_url"]}}
        reasons = _reasons(record, checked) or _evidence(p, snapshot)
        if reasons:
            raise ApplicationDrift("Repair the existing application before domain preparation: " + " ".join(reasons))
        for endpoint, key, current, body in (
            (PROVIDERS, str(provider["pk"]), provider, {"redirect_uris": desired["provider"]["redirect_uris"]}),
            (APPLICATIONS, "lucia", application, {"meta_launch_url": desired["application"]["meta_launch_url"]}),
        ):
            changes = _changes(current, body)
            if changes:
                _api(p, "PATCH", endpoint + key + "/", changes)
        verified = _snapshot(p, record)
        if verified["domain_profile"] != active:
            raise ApplicationDrift("The active domain profile changed during preparation; stop and review.")
        verified["domain_profile"] = profile
        reasons = _reasons(record, verified) or _evidence(p, verified)
        if reasons:
            raise ApplicationDrift("Domain callback preparation was not verified: " + " ".join(reasons))
        return {"prepared": True, "profile_id": profile["profileId"], "sso_verified": False,
                "launch_url": desired["application"]["meta_launch_url"],
                "redirect_uris": desired["provider"]["redirect_uris"]}


def reconcile_application(provisioner, public_origin):
    """Reconcile owned resources; never enroll accounts, reset LDAP, or rotate secrets."""
    p = provisioner
    origin = _origin(public_origin)
    with _lock(p):
        record = _prepare(p, origin)
        snapshot = _snapshot(p, record)
        configure_browser_session(p)
        resources = record["resources"]
        resources.update(owner_group=snapshot["owner_group"]["pk"], owner_user=snapshot["owner_user"],
                         signing_key=snapshot["signing_key"]["pk"])
        for kind in ("access_group", "scope_mapping", "provider", "application"):
            if snapshot[kind]:
                resources[kind] = snapshot[kind]["pk"]
        _save(p, record)
        # Keep an incomplete registration inert: no provider/launch URL is exposed
        # until both access bindings and the provider configuration are verified.
        if snapshot["application"] and _reasons(record, snapshot):
            app = snapshot["application"]
            patch = _changes(app, {"provider": None, "meta_hide": True, "meta_launch_url": ""})
            if patch:
                snapshot["application"] = _api(p, "PATCH", APPLICATIONS + "lucia/", patch)
        for kind, endpoint in (("access_group", GROUPS), ("scope_mapping", MAPPINGS),
                               ("provider", PROVIDERS), ("application", APPLICATIONS)):
            desired = _desired(record, snapshot)[kind]
            current = snapshot[kind]
            if kind == "application":
                desired = {**desired, "provider": None, "meta_hide": True, "meta_launch_url": ""}
                if current and not _reasons(record, snapshot):
                    continue
            if current:
                changes = _changes(current, desired)
                if changes:
                    key = current["slug"] if kind == "application" else str(current["pk"])
                    current = _api(p, "PATCH", endpoint + key + "/", changes)
            else:
                if kind == "access_group":
                    desired.update(users=[], parents=[], roles=[],
                                   attributes={"lucia_host_installation": record["markers"]["access_group"]})
                if kind == "provider":
                    desired["client_secret"] = _secret(p)
                current = _api(p, "POST", endpoint, desired)
            snapshot[kind] = current
            resources[kind] = current["pk"]
            _save(p, record)
        for role, group in (("owner", snapshot["owner_group"]), ("inference", snapshot["access_group"])):
            body = _binding_body(record, role, snapshot["application"]["pk"], group["pk"])
            current = snapshot["bindings"][role]
            journal = resources.setdefault("bindings", {})
            if current:
                changes = _changes(current, body)
                if changes:
                    current = _api(p, "PATCH", BINDINGS + current["pk"] + "/", changes)
            else:
                journal[role] = {"id": None, "pending": True, **body}
                _save(p, record)
                current = _api(p, "POST", BINDINGS, body)
            journal[role] = {"id": current["pk"], "pending": False, **body}
            _save(p, record)
        # Re-read before exposing a provider, including all unexpected bindings.
        snapshot = _snapshot(p, record)
        remaining = _reasons(record, snapshot)
        non_app = [reason for reason in remaining if not reason.startswith("Managed application drift:")]
        if non_app:
            raise ApplicationDrift("Registration remains incomplete: " + " ".join(non_app))
        desired = _desired(record, snapshot)["application"]
        changes = _changes(snapshot["application"], desired)
        if changes:
            _api(p, "PATCH", APPLICATIONS + "lucia/", changes)
        status = application_status(p, origin)
        if not status["ready"]:
            raise ApplicationDrift("Registration verification failed: " + " ".join(status["reasons"]))
        return status
