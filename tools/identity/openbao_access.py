"""Private OpenBao ingress and owner-only Authentik OIDC; never initializes or unseals a vault.

prepare registers a dedicated Authentik application without exposing OpenBao.
configure prompts privately for the existing scoped operator password, verifies
native auth, and then publishes a private-only route. It never grants app roles.
"""

import argparse
import getpass
import hmac
import json
import os
import pathlib
import re
import secrets
import socket
import ssl
import sys
import urllib.error
import urllib.parse
import urllib.request

import application as app
from openbao_backup import BackupError, NoRedirect, private_file
from provision import Provisioner, authority, write_file

SLUG = "lucia-openbao"
OIDC = "lucia-oidc"
JWT = "lucia-jwt"
SECRET = "openbao-oidc-client-secret"
PRIVATE_RANGES = ["127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "::1/128", "fc00::/7"]


class AccessError(RuntimeError):
    """Credential-free failure safe for an operator terminal."""


def request_json(url, method="GET", body=None, headers=None, trust=None, missing_ok=False):
    context = ssl.create_default_context(cafile=str(trust) if trust else None)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    opener = urllib.request.build_opener(
        urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=context), NoRedirect())
    request = urllib.request.Request(url, method=method,
                                     data=json.dumps(body).encode() if body is not None else None,
                                     headers={"Content-Type": "application/json", **(headers or {})})
    try:
        with opener.open(request, timeout=30) as response:
            data = response.read(1024 * 1024 + 1)
            if len(data) > 1024 * 1024:
                raise AccessError("Identity response exceeded its size limit.")
            return json.loads(data) if data else None
    except urllib.error.HTTPError as error:
        code = error.code
        error.close()
        if code == 404 and missing_ok:
            return None
        raise AccessError(f"Identity request returned HTTP {code}; configuration was not verified.") from None
    except urllib.error.URLError:
        raise AccessError("Identity service could not be reached over verified TLS.") from None
    except BackupError:
        raise AccessError("Identity service redirected the request; configure its direct verified endpoint.") from None


class AccessProvisioner(Provisioner):
    def api(self, method, path, body=None, allow_missing=False):
        return request_json(
            "https://" + authority(self.settings["public_host"], self.settings["ports"]["authentik"]) + path,
            method, body, {"Authorization": "Bearer " + self.values["authentik-bootstrap-token"]},
            self.state / "trust/lucia-root-ca.crt", allow_missing)


def callbacks(origin):
    return [origin + f"/ui/vault/auth/{OIDC}/oidc/callback",
            origin + f"/v1/auth/{OIDC}/oidc/callback"]


def operator_expression(group):
    # Bind the synchronized group ID, not user-editable attributes or a renamed group.
    import uuid
    return (f'if request.user.all_groups().filter(pk="{uuid.UUID(group)}").exists():\n'
            '    return {"lucia_openbao_operator": "owner"}\nreturn {}')


def _record(p, origin, host_record, snapshot):
    profile = snapshot["domain_profile"]
    if profile is None:
        raise AccessError("OpenBao routed access requires an active domain profile.")
    origin = app._origin(origin)
    host = urllib.parse.urlsplit(origin).hostname
    own = {urllib.parse.urlsplit(profile[key]).hostname
           for key in ("canonicalLuciaOrigin", "canonicalAuthentikOrigin", "sparkOrigin")}
    if (origin != "https://" + host or host in own or not host.endswith("." + profile["verifiedZone"])
            or not all(re.fullmatch(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?", label) for label in host.split("."))):
        raise AccessError("Use a distinct HTTPS hostname under the active domain, without a port.")
    installation = host_record["installation_id"]
    expected = {"schema_version": 1, "installation_id": installation, "origin": origin,
                "authority": profile["canonicalAuthentikOrigin"] + f"/application/o/{SLUG}/",
                "client_id": "lucia-openbao-" + installation.replace("-", "")}
    path = p.state / "openbao/authentik.json"
    secret_path = p.state / "secrets" / SECRET
    for candidate in (path, secret_path, path.with_name(path.name + ".new"),
                      secret_path.with_name(secret_path.name + ".new")):
        app._no_links(candidate)
    if path.exists():
        record = app._private_json(path)
        if (any(record.get(key) != value for key, value in expected.items())
                or not isinstance(record.get("resources"), dict) or type(record.get("secret_initialized")) is not bool):
            raise AccessError("OpenBao registration differs from the reviewed installation, origin or issuer; review before migration.")
    else:
        if secret_path.exists() or secret_path.with_name(secret_path.name + ".new").exists():
            raise AccessError("OpenBao client secret exists without its ownership journal; restore the journal.")
        record = {**expected, "resources": {}, "secret_initialized": False}
        write_file(path, json.dumps(record, indent=2) + "\n")
    if not secret_path.exists():
        if record["resources"] or record["secret_initialized"]:
            raise AccessError("OpenBao client secret is missing from an existing registration; restore it, do not rotate implicitly.")
        staged = secret_path.with_name(secret_path.name + ".new")
        if staged.exists():
            if not re.fullmatch(r"[A-Za-z0-9_-]{64}", private_file(staged)):
                raise AccessError("Interrupted OpenBao client secret write is invalid.")
            os.replace(staged, secret_path)
        else:
            write_file(secret_path, secrets.token_urlsafe(48))
    if not re.fullmatch(r"[A-Za-z0-9_-]{64}", private_file(secret_path)):
        raise AccessError("OpenBao client secret is invalid.")
    record["secret_initialized"] = True
    _save(p, record)
    return record


def _save(p, record):
    write_file(p.state / "openbao/authentik.json", json.dumps(record, indent=2) + "\n")


def register(p, origin):
    """Reconcile only this installation's OpenBao application; never alter host SSO."""
    with app._lock(p):
        host = app._private_json(p.state / "host-settings.json", 65536)
        host_record = app._load(p, host["authentication"]["public_origin"])
        snapshot = app._snapshot(p, host_record)
        record = _record(p, origin, host_record, snapshot)
        marker = "Lucia OpenBao " + record["installation_id"]
        resources = record["resources"]
        owner = snapshot["owner_group"]
        for key, value in (("owner_group", owner["pk"]), ("signing_key", snapshot["signing_key"]["pk"])):
            if resources.get(key, value) != value:
                raise AccessError("OpenBao's owner group or signing key changed; review the identity migration.")
        resources.update(owner_group=owner["pk"], signing_key=snapshot["signing_key"]["pk"])
        mapping = app._owned(record, "scope_mapping", app._list(p, app.MAPPINGS),
                             lambda item: item["name"] == marker,
                             lambda item: item.get("managed") == marker)
        providers = app._list(p, app.PROVIDERS)
        provider = app._owned(record, "provider", providers,
                              lambda item: item["client_id"] == record["client_id"] or item["name"] == marker,
                              lambda item: item["name"] == marker and item["client_id"] == record["client_id"])
        secret = private_file(p.state / "secrets" / SECRET)
        if provider and (not hmac.compare_digest(provider["client_secret"], secret)
                         or provider.get("assigned_application_slug") not in (None, "", SLUG)
                         or provider.get("assigned_backchannel_application_slug")):
            raise AccessError("OpenBao provider credentials or application ownership drifted.")
        if mapping and any(item["pk"] != (provider["pk"] if provider else None)
                           and mapping["pk"] in item["property_mappings"] for item in providers):
            raise AccessError("OpenBao's operator claim mapping is shared with another provider; refusing to change it.")
        application = app._owned(record, "application", app._list(p, app.APPLICATIONS, superuser_full_list="true"),
                                 lambda item: item["slug"] == SLUG,
                                 lambda item: item.get("meta_description") == marker)
        if application and (application["slug"] != SLUG or application.get("backchannel_providers")
                            or application["provider"] not in (None, provider["pk"] if provider else None)):
            raise AccessError("OpenBao application assignment drifted.")
        bindings = app._inspect_bindings(p, record, application, owner, None)
        desired_mapping = {"name": marker, "managed": marker, "scope_name": "lucia_openbao",
                           "description": "OpenBao scoped operator access for Lucia owners.",
                           "expression": operator_expression(owner["pk"])}
        if mapping and app._changes(mapping, desired_mapping):
            raise AccessError("OpenBao claim mapping drifted; refusing an implicit claim change.")
        if mapping is None:
            mapping = app._api(p, "POST", app.MAPPINGS, desired_mapping)
        resources["scope_mapping"] = mapping["pk"]
        _save(p, record)
        desired_provider = {
            "name": marker, **snapshot["flows"], "client_type": "confidential",
            "client_id": record["client_id"], "grant_types": ["authorization_code"],
            "issuer_mode": "per_provider", "sub_mode": "user_uuid",
            "signing_key": snapshot["signing_key"]["pk"], "encryption_key": None,
            "include_claims_in_id_token": True, "property_mappings": snapshot["defaults"] + [mapping["pk"]],
            "redirect_uris": [{"matching_mode": "strict", "url": uri, "redirect_uri_type": "authorization"}
                              for uri in callbacks(record["origin"])],
            "jwt_federation_sources": [], "jwt_federation_providers": [],
        }
        if provider and app._changes(provider, desired_provider):
            raise AccessError("OpenBao OIDC provider drifted; review callbacks, grants and signing configuration.")
        if provider is None:
            provider = app._api(p, "POST", app.PROVIDERS, {**desired_provider, "client_secret": secret})
        resources["provider"] = provider["pk"]
        _save(p, record)
        desired_app = {"slug": SLUG, "name": "OpenBao", "provider": provider["pk"],
                       "meta_launch_url": record["origin"] + "/ui/", "meta_description": marker,
                       "meta_hide": False, "policy_engine_mode": "any"}
        if application is None:
            application = app._api(p, "POST", app.APPLICATIONS,
                                   {**desired_app, "provider": None, "meta_hide": True, "meta_launch_url": ""})
        elif application["provider"] is not None and app._changes(application, desired_app):
            raise AccessError("OpenBao application settings drifted; review them before publishing access.")
        resources["application"] = application["pk"]
        _save(p, record)
        body = app._binding_body(record, "owner", application["pk"], owner["pk"])
        binding = bindings["owner"]
        if binding and app._changes(binding, body):
            raise AccessError("OpenBao owner binding drifted; refusing an implicit access change.")
        if binding is None:
            resources.setdefault("bindings", {})["owner"] = {"id": None, "pending": True, **body}
            _save(p, record)
            binding = app._api(p, "POST", app.BINDINGS, body)
        resources.setdefault("bindings", {})["owner"] = {"id": binding["pk"], "pending": False, **body}
        _save(p, record)
        app._inspect_bindings(p, record, application, owner, None)
        if app._changes(application, desired_app):
            app._api(p, "PATCH", app.APPLICATIONS + SLUG + "/", desired_app)
        user = str(snapshot["owner_user"])
        access = app._api(p, "GET", app.APPLICATIONS + SLUG + "/check_access/?for_user=" + user)
        preview = app._api(p, "GET", app.PROVIDERS + str(provider["pk"]) + "/preview_user/?for_user=" + user)
        if access.get("passing") is not True or preview.get("preview", {}).get("lucia_openbao_operator") != "owner":
            raise AccessError("Authentik did not verify the enrolled owner's OpenBao access and operator claim.")
        return record


def backend(p, method, route, body=None, token=None, missing_ok=False):
    config = json.loads(private_file(p.state / "openbao-settings.json"))
    if config["bind_address"] != "127.0.0.1" or type(config["port"]) is not int or not 1 <= config["port"] <= 65535:
        raise AccessError("This access rollout requires the existing loopback OpenBao endpoint.")
    return request_json("https://localhost:" + str(config["port"]) + "/v1/" + route, method, body,
                        {"X-Vault-Token": token} if token else {}, p.state / "trust/lucia-root-ca.crt", missing_ok)


def auth_configuration(record):
    base = {"oidc_discovery_url": record["authority"], "bound_issuer": record["authority"],
            "jwt_supported_algs": ["RS256"]}
    return {
        OIDC: {**base, "oidc_client_id": record["client_id"], "default_role": "owners",
               "oidc_response_mode": "query", "oidc_response_types": ["code"]},
        JWT: {**base, "oidc_client_id": "", "default_role": ""},
    }


def operator_role(record):
    return {"role_type": "oidc", "user_claim": "sub", "bound_audiences": [record["client_id"]],
            "bound_claims_type": "string", "bound_claims": {"lucia_openbao_operator": "owner"},
            "allowed_redirect_uris": callbacks(record["origin"]),
            "oidc_scopes": ["profile", "email", "lucia_openbao"], "verbose_oidc_logging": False,
            "token_policies": ["lucia-operator"], "token_ttl": 900, "token_max_ttl": 3600,
            "token_explicit_max_ttl": 3600}


def gateway_configuration(origin):
    origin = app._origin(origin)
    host = urllib.parse.urlsplit(origin).hostname
    if origin != "https://" + host:
        raise AccessError("OpenBao ingress must use a canonical HTTPS origin on port 443.")
    return {"http": {
        "routers": {"lucia-openbao": {"entryPoints": ["host"], "rule": f"Host(`{host}`)",
                                     "service": "lucia-openbao", "middlewares": ["openbao-private"], "tls": {}}},
        "middlewares": {"openbao-private": {"ipAllowList": {"sourceRange": PRIVATE_RANGES}}},
        "services": {"lucia-openbao": {"loadBalancer": {"serversTransport": "openbao-tls",
                                                      "servers": [{"url": "https://identity-openbao:8200"}]}}},
        "serversTransports": {"openbao-tls": {"serverName": "identity-gateway",
                                             "rootCAs": ["/config/openbao-root-ca.crt"]}},
    }}


def publish_gateway(p, record):
    root = p.state / "gateway"
    for path in (root, root / "openbao.yml", root / "openbao.yml.new",
                 root / "openbao-root-ca.crt", root / "openbao-root-ca.crt.new"):
        app._no_links(path)
    write_file(root / "openbao-root-ca.crt", (p.state / "trust/lucia-root-ca.crt").read_bytes())
    write_file(root / "openbao.yml", json.dumps(gateway_configuration(record["origin"]), indent=2) + "\n")
    publish_dns_registration(p, record)


def publish_dns_registration(p, record):
    host = app._private_json(p.state / "host-settings.json", 65536)
    host_record = app._load(p, host["authentication"]["public_origin"])
    profile = app._active_domain_profile(p, host_record)
    if profile is None or profile["canonicalAuthentikOrigin"] + f"/application/o/{SLUG}/" != record["authority"]:
        raise AccessError("The active domain changed before OpenBao DNS registration; stop and review it.")
    root = pathlib.Path(host["data_directory"]) / "domains"
    workflow = app._private_json(root / "workflow.json", 1024 * 1024)
    job = workflow["job"]
    namespace = job["plan"]["naming"]["namespace"]
    hostname = urllib.parse.urlsplit(record["origin"]).hostname
    if (job["id"] != profile["profileId"] or not hostname.endswith("." + namespace)
            or "." in hostname[:-(len(namespace) + 1)]
            or hostname in job["plan"]["naming"]["localHostnames"]):
        raise AccessError("OpenBao DNS requires a distinct hostname in the active private namespace.")
    path = root / "openbao-access.json"
    for candidate in (root, path, path.with_name(path.name + ".new")):
        app._no_links(candidate)
    desired = {"schemaVersion": 1, "profileId": profile["profileId"], "origin": record["origin"]}
    if path.exists() and app._private_json(path) != desired:
        raise AccessError("OpenBao DNS intent conflicts with existing domain state; refusing to overwrite it.")
    write_file(path, json.dumps(desired, indent=2) + "\n")


def verify_ingress_certificate(record):
    host = urllib.parse.urlsplit(record["origin"]).hostname
    context = ssl.create_default_context()
    with socket.create_connection(("127.0.0.1", 443), timeout=10) as connection:
        with context.wrap_socket(connection, server_hostname=host):
            pass


def configure(p, record, password):
    with app._lock(p):
        if app._private_json(p.state / "openbao/authentik.json") != record:
            raise AccessError("OpenBao registration changed before configuration; stop and review it.")
        return _configure(p, record, password)


def _configure(p, record, password):
    verify_ingress_certificate(record)
    login = backend(p, "POST", "auth/lucia-operator/login/operator", {"password": password})
    token = login["auth"]["client_token"]
    try:
        policies = set(login["auth"]["policies"])
        if policies != {"default", "lucia-operator"} or login["auth"]["lease_duration"] > 3600:
            raise AccessError("Expected the existing scoped operator login, never root or an expanded policy set.")
        if backend(p, "GET", "sys/policies/acl/lucia-operator", token=token) is None:
            raise AccessError("The existing operator policy must be present.")
        mounts = backend(p, "GET", "sys/auth", token=token)["data"]
        for mount in (OIDC, JWT):
            current = mounts.get(mount + "/")
            description = "Lucia OpenBao " + record["installation_id"] + " " + mount
            if current and (current["type"] not in ("jwt", "oidc") or current["description"] != description):
                raise AccessError("An OpenBao auth mount collides with unmanaged configuration.")
        for mount, desired in auth_configuration(record).items():
            description = "Lucia OpenBao " + record["installation_id"] + " " + mount
            if mount + "/" not in mounts:
                backend(p, "POST", "sys/auth/" + mount,
                        {"type": "oidc" if mount == OIDC else "jwt", "description": description,
                         "config": {"listing_visibility": "unauth" if mount == OIDC else "hidden"}}, token)
            roles = backend(p, "LIST", f"auth/{mount}/role", token=token, missing_ok=True)
            allowed = {"owners"} if mount == OIDC else set()
            if roles and set(roles.get("data", {}).get("keys", [])) - allowed:
                raise AccessError("Unexpected OpenBao roles exist; no additional operator or application grants were approved.")
            current = backend(p, "GET", f"auth/{mount}/config", token=token, missing_ok=True)
            if current:
                actual = current["data"]
                if any(actual.get(key) != value for key, value in desired.items()):
                    raise AccessError("OpenBao OIDC/JWT config drifted; refusing an implicit issuer or client change.")
            else:
                body = dict(desired)
                if mount == OIDC:
                    body["oidc_client_secret"] = private_file(p.state / "secrets" / SECRET)
                backend(p, "POST", f"auth/{mount}/config", body, token)
        current_role = backend(p, "GET", f"auth/{OIDC}/role/owners", token=token, missing_ok=True)
        desired_role = operator_role(record)
        if current_role and any(current_role["data"].get(key) != value for key, value in desired_role.items()):
            raise AccessError("OpenBao operator role drifted; refusing an implicit access change.")
        if current_role is None:
            backend(p, "POST", f"auth/{OIDC}/role/owners", desired_role, token)
        roles = backend(p, "LIST", f"auth/{JWT}/role", token=token, missing_ok=True)
        if roles and roles.get("data", {}).get("keys"):
            raise AccessError("Unexpected JWT application roles exist; no application grants were approved.")
        verified = backend(p, "GET", f"auth/{OIDC}/role/owners", token=token)["data"]
        if any(verified.get(key) != value for key, value in desired_role.items()):
            raise AccessError("OpenBao did not persist the expected owner-only role.")
        result = backend(p, "POST", f"auth/{OIDC}/oidc/auth_url",
                         {"role": "owners", "redirect_uri": callbacks(record["origin"])[0]})
        if not result.get("data", {}).get("auth_url"):
            raise AccessError("OpenBao did not generate an OIDC authorization request.")
    finally:
        backend(p, "POST", "auth/token/revoke-self", token=token)
    publish_gateway(p, record)
    return {"configured": True, "origin": record["origin"], "app_roles": 0, "browser_login_verified": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("prepare", "configure", "register-dns"))
    parser.add_argument("--state", default=str(pathlib.Path.home() / ".local/share/lucia/identity"))
    parser.add_argument("--origin", required=True)
    args = parser.parse_args()
    p = AccessProvisioner(argparse.Namespace(state=args.state, host=None, certificate_mode="private-ca",
                                            auth_port=None, ldap_port=None, ca_port=None))
    p.load_existing()
    if args.command == "configure" and not sys.stdin.isatty():
        raise AccessError("Configure requires a private interactive terminal; do not pipe an operator password.")
    if args.command == "register-dns":
        with app._lock(p):
            record = app._private_json(p.state / "openbao/authentik.json")
            if app._origin(args.origin) != record["origin"]:
                raise AccessError("Use the existing approved OpenBao origin for DNS registration.")
            if app._private_json(p.state / "gateway/openbao.yml") != gateway_configuration(record["origin"]):
                raise AccessError("Configure and verify the existing private OpenBao route before registering DNS.")
            publish_dns_registration(p, record)
        print(json.dumps({"dns_registered": True, "origin": record["origin"]}))
        return
    record = register(p, args.origin)
    if args.command == "prepare":
        print(json.dumps({"registered": True, "origin": record["origin"], "openbao_configured": False}))
    else:
        password = getpass.getpass("OpenBao scoped operator password: ")
        if not password:
            raise AccessError("An operator password is required; nothing was configured.")
        print(json.dumps(configure(p, record, password)))


if __name__ == "__main__":
    try:
        main()
    except (AccessError, app.ApplicationDrift) as error:
        print(f"OpenBao access setup failed: {error}", file=sys.stderr)
        sys.exit(1)
    except (OSError, ValueError, KeyError, RuntimeError) as error:
        print(f"OpenBao access setup failed ({type(error).__name__}); review private configuration before retrying. "
              "No storage was initialized or restored.", file=sys.stderr)
        sys.exit(1)
