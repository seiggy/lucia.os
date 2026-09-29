"""Owner-only Authentik OIDC clients for Lucia catalog apps, such as Grafana.

The web host asks for one through host/data/domains/app-sso-requests/<stack>.json; the native activation worker
passes the parsed requests here. reconcile keeps exactly one client per request and removes owned clients whose
request is gone. It creates only confidential authorization-code clients bound to lucia-owners, with an exact https
callback under the active domain that isn't one of Lucia's own names, and never changes the Lucia host registration.
"""

import re
import urllib.parse

import application
from application import APPLICATIONS, BINDINGS, MAPPINGS, PROVIDERS, ApplicationDrift, _api, _changes, _list

PREFIX = "lucia-app-"
STACK = re.compile(r"[a-z](?:[a-z0-9-]{0,38}[a-z0-9])?")
SECRET = re.compile(r"[A-Za-z0-9_-]{43,128}")
LABEL = re.compile(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?")
FIELDS = {"schemaVersion", "stack", "name", "clientId", "clientSecret", "redirectUri", "launchUrl"}


def _marker(stack, installation):
    return f"Lucia app {stack} {installation}"


def _checked(value, stack, profile):
    """The request, when it asks only for what this bridge may grant."""
    if (not isinstance(value, dict) or set(value) != FIELDS or type(value["schemaVersion"]) is not int
            or value["schemaVersion"] != 1 or value["stack"] != stack or not STACK.fullmatch(stack)
            or value["clientId"] != PREFIX + stack or not isinstance(value["name"], str)
            or not 0 < len(value["name"]) <= 64 or any(ord(c) < 32 for c in value["name"])
            or not isinstance(value["clientSecret"], str) or not SECRET.fullmatch(value["clientSecret"])
            or not isinstance(value["redirectUri"], str) or not isinstance(value["launchUrl"], str)):
        raise ValueError("Invalid app sign-in request.")
    if profile is None:
        raise ApplicationDrift("App sign-in needs an active domain.")
    redirect = urllib.parse.urlsplit(value["redirectUri"])
    host = redirect.hostname or ""
    own = {urllib.parse.urlsplit(profile[key]).hostname
           for key in ("canonicalLuciaOrigin", "canonicalAuthentikOrigin", "sparkOrigin")}
    if (redirect.scheme != "https" or value["redirectUri"] != f"https://{host}{redirect.path}"
            or not re.fullmatch(r"/[A-Za-z0-9/_-]{1,64}", redirect.path) or len(host) > 253
            or not host.endswith("." + profile["verifiedZone"]) or host in own
            or not all(LABEL.fullmatch(label) for label in host.split("."))
            or value["launchUrl"] != f"https://{host}/"):
        raise ValueError("App sign-in callbacks must be exact https addresses under the active domain.")
    return value


def _client(p, snapshot, offline, installation, value, providers, apps):
    stack, slug = value["stack"], value["clientId"]
    marker = _marker(stack, installation)
    found = [item for item in providers if item["client_id"] == slug or item["name"] == marker]
    if len(found) > 1 or found and (found[0]["name"] != marker or found[0]["client_id"] != slug):
        raise ApplicationDrift("An app sign-in provider collides with an unmanaged one.")
    provider = found[0] if found else None
    app = next((item for item in apps if item["slug"] == slug), None)
    if app and app.get("meta_description") != marker:
        raise ApplicationDrift("An app sign-in application collides with an unmanaged one.")
    body = {
        "name": marker, **snapshot["flows"], "client_type": "confidential", "client_id": slug,
        "client_secret": value["clientSecret"], "grant_types": ["authorization_code", "refresh_token"],
        "issuer_mode": "per_provider", "sub_mode": "user_uuid", "signing_key": snapshot["signing_key"]["pk"],
        "encryption_key": None, "include_claims_in_id_token": True,
        "property_mappings": snapshot["defaults"] + [offline["pk"]],
        "redirect_uris": [{"matching_mode": "strict", "url": value["redirectUri"], "redirect_uri_type": "authorization"}],
    }
    if provider is None:
        provider = _api(p, "POST", PROVIDERS, body)
    elif changes := _changes(provider, body):
        provider = _api(p, "PATCH", PROVIDERS + str(provider["pk"]) + "/", changes)
    body = {"slug": slug, "name": value["name"], "provider": provider["pk"], "meta_launch_url": value["launchUrl"],
            "meta_description": marker, "meta_hide": False, "policy_engine_mode": "any"}
    if app is None:
        app = _api(p, "POST", APPLICATIONS, body)
    elif changes := _changes(app, body):
        app = _api(p, "PATCH", APPLICATIONS + slug + "/", changes)
    owners = snapshot["owner_group"]["pk"]
    # Owners may bind more groups themselves, such as viewers; Lucia only keeps its own.
    if not any(item["group"] == owners and item["enabled"] and not item["negate"]
               for item in _list(p, BINDINGS, target=app["pk"])):
        _api(p, "POST", BINDINGS, {"target": app["pk"], "group": owners, "policy": None, "user": None, "negate": False,
                                   "enabled": True, "order": 0, "timeout": 30, "failure_result": False})
    user = str(snapshot["owner_user"])
    if _api(p, "GET", APPLICATIONS + slug + "/check_access/?for_user=" + user).get("passing") is not True:
        raise ApplicationDrift("Authentik did not authorize the owner for the app.")
    preview = _api(p, "GET", PROVIDERS + str(provider["pk"]) + "/preview_user/?for_user=" + user).get("preview", {})
    if "lucia-owners" not in (preview.get("groups") or []):
        raise ApplicationDrift("The app's tokens don't carry the owner's lucia-owners group.")


def reconcile(p, origin, requests):
    """Apply every request, remove unrequested owned clients, and return each request's error type or None."""
    record = application._load(p, origin)
    snapshot = application._snapshot(p, record)
    installation = record["installation_id"]
    offline = [item for item in _list(p, MAPPINGS)
               if item.get("managed") == "goauthentik.io/providers/oauth2/scope-offline_access"]
    if len(offline) != 1:
        raise ApplicationDrift("Authentik's default offline_access scope mapping is missing or ambiguous.")
    providers = _list(p, PROVIDERS)
    apps = _list(p, APPLICATIONS, superuser_full_list="true")
    for app in apps:
        stack = app["slug"][len(PREFIX):]
        if (not app["slug"].startswith(PREFIX) or stack in requests
                or app.get("meta_description") != _marker(stack, installation)):
            continue
        _api(p, "DELETE", APPLICATIONS + app["slug"] + "/")
        for provider in providers:
            if provider["pk"] == app["provider"] and provider["name"] == _marker(stack, installation):
                _api(p, "DELETE", PROVIDERS + str(provider["pk"]) + "/")
    results = {}
    for stack, value in requests.items():
        try:
            _client(p, snapshot, offline[0], installation, _checked(value, stack, snapshot["domain_profile"]), providers, apps)
            results[stack] = None
        except (ApplicationDrift, ValueError, KeyError, TypeError, RuntimeError) as error:
            results[stack] = type(error).__name__
    return results
