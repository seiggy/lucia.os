"""Offline stdlib regressions. Run: python tools\\identity\\check_application.py.

No live transports, credentials, accounts, or registrations are used.
"""

import copy
import datetime
import hashlib
import json
import os
import pathlib
import tempfile
import unittest
import urllib.parse
import uuid
from types import SimpleNamespace
from unittest.mock import patch

import application as app


ORIGIN = "https://192.0.2.10"


def identifier():
    return str(uuid.uuid4())


class FakeProvisioner:
    def __init__(self, state):
        self.state = pathlib.Path(state)
        self.values = {}
        self.settings = {"public_host": "192.0.2.10", "ports": {"authentik": 9443}}
        self.calls = []
        self.failure = None
        self.owner_pk = 173
        self.owner_group = identifier()
        self.signer = identifier()
        self.tables = {
            app.APPLICATIONS: [], app.PROVIDERS: [], app.BINDINGS: [],
            app.GROUPS: [{"pk": self.owner_group, "name": "lucia-owners", "is_superuser": True,
                          "users": [self.owner_pk], "parents": [], "roles": [], "attributes": {}}],
            app.MAPPINGS: [
                {"pk": identifier(), "name": "Default " + scope, "scope_name": scope,
                 "managed": "goauthentik.io/providers/oauth2/scope-" + scope, "expression": "return {}"}
                for scope in app.SCOPES[:3]],
            app.CERTIFICATES: [
                {"pk": identifier(), "name": "lucia-ldap-ca", "private_key_available": False,
                 "key_type": "rsa", "cert_expiry": "2099-01-01T00:00:00Z"},
                {"pk": self.signer, "name": "authentik Self-signed Certificate", "private_key_available": True,
                 "key_type": "rsa", "cert_expiry": "2099-01-01T00:00:00Z"}],
            "/api/v3/flows/instances/": [
                {"pk": identifier(), "slug": slug, "designation": designation} for slug, designation in (
                    ("default-authentication-flow", "authentication"),
                    ("default-provider-authorization-implicit-consent", "authorization"),
                    ("default-provider-invalidation-flow", "invalidation"))],
            "/api/v3/sources/group_connections/ldap/": [],
            "/api/v3/stages/user_login/": [
                {"pk": identifier(), "name": "default-authentication-login", "session_duration": "seconds=0"}],
        }
        self.tables["/api/v3/flows/bindings/"] = [{
            "pk": identifier(), "target": self.tables["/api/v3/flows/instances/"][0]["pk"],
            "stage": self.tables["/api/v3/stages/user_login/"][0]["pk"],
        }]
        self.deny_owner = False
        self.hide_owner = False
        self.bad_claim = False
        self.paginate = False

    @property
    def mutations(self):
        return [call for call in self.calls if call[0] != "GET"]

    def delete(self, endpoint, key):
        self.tables[endpoint] = [item for item in self.tables[endpoint] if item["pk"] != key]
        if endpoint == app.APPLICATIONS:
            self.tables[app.BINDINGS] = [item for item in self.tables[app.BINDINGS] if item["target"] != key]
        elif endpoint == app.PROVIDERS:
            for item in self.tables[app.APPLICATIONS]:
                if item["provider"] == key:
                    item["provider"] = None
        elif endpoint == app.GROUPS:
            self.tables[app.BINDINGS] = [item for item in self.tables[app.BINDINGS] if item["group"] != key]
        elif endpoint == app.MAPPINGS:
            for item in self.tables[app.PROVIDERS]:
                item["property_mappings"] = [pk for pk in item["property_mappings"] if pk != key]

    def role(self, provider, groups):
        mapping = next(item for item in self.tables[app.MAPPINGS]
                       if item["pk"] in provider["property_mappings"] and item["scope_name"] == "lucia_api")
        query = SimpleNamespace(filter=lambda **kw: SimpleNamespace(exists=lambda: kw["pk"] in groups))
        request = SimpleNamespace(user=SimpleNamespace(all_groups=lambda: query,
                                  attributes={"lucia_role": "Owner", "is_superuser": True}))
        namespace = {"request": request}
        exec("def evaluate():\n" + "".join("    " + line + "\n" for line in mapping["expression"].splitlines()), namespace)
        return namespace["evaluate"]()

    def api(self, method, path, body=None, allow_missing=False):
        self.calls.append((method, path, copy.deepcopy(body)))
        if body and "expression" in body:
            body = {**body, "expression": body["expression"].strip()}
        if self.failure and self.failure(method, path, body):
            raise RuntimeError("Transport returned credential-rich-response")
        route, _, query = path.partition("?")
        filters = {key: values[0] for key, values in urllib.parse.parse_qs(query).items()}
        if route == "/api/v3/core/users/me/":
            return {"user": {"pk": 1, "is_superuser": True}}
        if route == app.APPLICATIONS + "lucia/check_access/":
            assert int(filters["for_user"]) == self.owner_pk
            bindings = self.tables[app.BINDINGS]
            allowed = any(item["group"] == self.owner_group and item["enabled"] and not item["negate"]
                          for item in bindings)
            return {"passing": allowed and not self.deny_owner}
        if route.endswith("/preview_user/"):
            assert int(filters["for_user"]) == self.owner_pk
            return {"preview": {} if self.bad_claim else self.role(self.tables[app.PROVIDERS][0], [self.owner_group])}
        endpoint = next((key for key in self.tables if route.startswith(key)), None)
        if endpoint is None:
            raise AssertionError("Unexpected fake endpoint: " + path)
        key = route[len(endpoint):].strip("/")
        table = self.tables[endpoint]
        if method == "GET" and not key:
            rows = list(table)
            for field in ("target", "slug", "group", "name"):
                if field in filters:
                    rows = [item for item in rows if str(item[field]) == filters[field]]
            if filters.get("for_user"):
                assert filters["superuser_full_list"] == "false"
                assert int(filters["for_user"]) == self.owner_pk
                rows = [] if self.hide_owner else [item for item in rows if item["meta_launch_url"]]
            if endpoint == app.PROVIDERS:
                rows = copy.deepcopy(rows)
                for row in rows:
                    assigned = next((item["slug"] for item in self.tables[app.APPLICATIONS]
                                     if item["provider"] == row["pk"]), None)
                    row.update(assigned_application_slug=assigned, assigned_backchannel_application_slug=None)
            page = int(filters.get("page", "1"))
            size = 1 if self.paginate else 100
            return copy.deepcopy({"results": rows[(page - 1) * size:page * size],
                                  "pagination": {"next": page + 1 if page * size < len(rows) else 0}})
        if method == "POST":
            assert not key
            item = copy.deepcopy(body)
            item["pk"] = len(self.calls) if endpoint == app.PROVIDERS else identifier()
            table.append(item)
            self.assert_closed_registration()
            return copy.deepcopy(item)
        current = next((item for item in table if str(item["slug"] if endpoint == app.APPLICATIONS else item["pk"]) == key), None)
        if current is None:
            assert method == "GET" and allow_missing
            return None
        if method == "PATCH":
            current.update(copy.deepcopy(body))
            self.assert_closed_registration()
        else:
            assert method == "GET"
        return copy.deepcopy(current)

    def assert_closed_registration(self):
        for application in self.tables[app.APPLICATIONS]:
            if application["provider"] is not None:
                bindings = [item for item in self.tables[app.BINDINGS] if item["target"] == application["pk"]]
                assert len(bindings) == 2, "Provider exposed before both access bindings existed."
                assert all(item["enabled"] and not item["negate"] for item in bindings)


class ApplicationChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix=".lucia-application-check-", dir=pathlib.Path.cwd())
        self.addCleanup(self.temp.cleanup)
        self.p = FakeProvisioner(pathlib.Path(self.temp.name) / "identity")
        self.p.state.mkdir()
        self.owner_loader = patch.object(app, "load_owner", return_value={"complete": True})
        self.owner_identity = patch.object(app, "owner_identity", side_effect=lambda p, record, grant=False: p.owner_pk)
        self.owner_loader.start()
        self.owner_identity.start()
        self.addCleanup(self.owner_loader.stop)
        self.addCleanup(self.owner_identity.stop)

    def ready(self):
        return app.reconcile_application(self.p, ORIGIN)

    def test_reconciliation_preserves_browser_sign_in(self):
        self.ready()
        stage = self.p.tables["/api/v3/stages/user_login/"][0]
        self.assertEqual(stage["session_duration"], "hours=8")
        stage["session_duration"] = "minutes=30"
        self.ready()
        self.assertEqual(stage["session_duration"], "minutes=30")

    def record(self):
        return json.loads((self.p.state / "host-auth.json").read_text())

    def secret(self):
        return (self.p.state / "secrets" / app.SECRET_NAME).read_bytes()

    def profile(self):
        return {
            "schemaVersion": 1, "verifiedZone": "example.com",
            "canonicalLuciaOrigin": "https://lucia.homelab.example.com",
            "canonicalAuthentikOrigin": "https://auth.homelab.example.com",
            "sparkOrigin": "https://atlas.homelab.example.com",
            "legacyLuciaOrigin": ORIGIN, "legacyAuthority": ORIGIN + ":9443/application/o/lucia/",
            "profileId": identifier(), "activatedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        }

    def activate(self, profile):
        root = self.p.state.parent / "host"
        data = root / "data"
        app.write_file(self.p.state / "host-settings.json", json.dumps({
            "schema_version": 1, "component": "managedhost",
            "installation_id": hashlib.sha256(str(self.p.state).encode()).hexdigest(),
            "host_state": str(root), "data_directory": str(data),
            "authentication": {**self.record(), "client_secret_file": str(self.p.state / "secrets" / app.SECRET_NAME)},
        }))
        path = data / "domains" / "active.json"
        app.write_file(path, json.dumps(profile))
        return path

    def test_reviewed_domain_preparation_changes_only_exact_callbacks_and_launch(self):
        self.ready()
        profile = self.profile()
        record, secret = self.record(), self.secret()
        before = copy.deepcopy(self.p.tables)
        self.p.calls.clear()
        result = app.prepare_domain_application(self.p, ORIGIN, profile)
        self.assertTrue(result["prepared"])
        self.assertFalse(result["sso_verified"])
        self.assertEqual(self.record(), record)
        self.assertEqual(self.secret(), secret)
        self.assertEqual(len(self.p.mutations), 2)
        self.assertEqual(self.p.mutations[0][0:2], ("PATCH", app.PROVIDERS + str(record["resources"]["provider"]) + "/"))
        self.assertEqual(set(self.p.mutations[0][2]), {"redirect_uris"})
        self.assertEqual(set(self.p.mutations[1][2]), {"meta_launch_url"})
        before[app.PROVIDERS][0]["redirect_uris"] = app._redirects([ORIGIN, profile["canonicalLuciaOrigin"]])
        before[app.APPLICATIONS][0]["meta_launch_url"] = profile["canonicalLuciaOrigin"] + "/"
        self.assertEqual(self.p.tables, before)
        self.p.calls.clear()
        self.assertEqual(app.prepare_domain_application(self.p, ORIGIN, profile), result)
        self.assertEqual(self.p.mutations, [])
        self.activate(profile)
        self.p.calls.clear()
        self.assertTrue(self.ready()["ready"])
        self.assertTrue(app.application_status(self.p, ORIGIN)["ready"])
        self.assertEqual(self.p.mutations, [])
        self.assertEqual(self.p.tables, before)
        self.assertEqual(self.secret(), secret)
        self.assertEqual(self.record(), record)
        self.assertEqual(app.prepare_application(self.p, ORIGIN)["public_origin"], ORIGIN)

    def test_domain_preparation_retries_partial_write_without_rotation(self):
        self.ready()
        profile = self.profile()
        secret = self.secret()
        self.p.failure = lambda method, path, body: method == "PATCH" and path == app.APPLICATIONS + "lucia/"
        with self.assertRaises(RuntimeError):
            app.prepare_domain_application(self.p, ORIGIN, profile)
        self.p.failure = None
        self.assertTrue(app.prepare_domain_application(self.p, ORIGIN, profile)["prepared"])
        self.assertEqual(self.secret(), secret)

    def test_domain_preparation_refuses_collisions_and_unrelated_repairs(self):
        self.ready()
        profile = self.profile()
        expected = copy.deepcopy(self.p.tables)
        for endpoint, field, value in (
            (app.PROVIDERS, "client_secret", "different"),
            (app.PROVIDERS, "redirect_uris", [{"matching_mode": "regex", "url": ".*", "redirect_uri_type": "authorization"}]),
            (app.PROVIDERS, "property_mappings", []),
            (app.APPLICATIONS, "meta_launch_url", "https://unexpected.example/"),
            (app.APPLICATIONS, "meta_description", "not owned"),
            (app.BINDINGS, "enabled", False),
        ):
            with self.subTest(field=field):
                self.p.tables = copy.deepcopy(expected)
                self.p.tables[endpoint][0][field] = value
                self.p.calls.clear()
                with self.assertRaises(app.ApplicationDrift):
                    app.prepare_domain_application(self.p, ORIGIN, profile)
                self.assertEqual(self.p.mutations, [])
        self.p.tables = expected
        self.activate(profile)
        self.p.calls.clear()
        with self.assertRaises(app.ApplicationDrift):
            app.prepare_domain_application(self.p, ORIGIN, {**profile, "profileId": identifier()})
        self.assertEqual(self.p.mutations, [])

    def test_invalid_domain_profiles_fail_before_remote_calls(self):
        self.ready()
        profile = self.profile()
        for key, value in (
            ("schemaVersion", True), ("schemaVersion", 2), ("profileId", "not-a-guid"),
            ("clientSecret", "not-config"), ("verifiedZone", "*.example.com"),
            ("canonicalLuciaOrigin", "https://lucia.outside.example"),
            ("canonicalLuciaOrigin", "https://lucia.homelab.example.com:443"),
            ("canonicalLuciaOrigin", "https://lucia.homelab.example.com/path"),
            ("canonicalLuciaOrigin", "https://lucia.homelab.example.com?"),
            ("sparkOrigin", profile["canonicalLuciaOrigin"]),
            ("legacyLuciaOrigin", "https://other.example"), ("legacyAuthority", "https://auth.example/application/o/lucia/"),
            ("activatedAt", "2999-01-01T00:00:00Z"), ("activatedAt", "2026-01-01T00:00:00"),
        ):
            with self.subTest(key=key, value=value):
                self.activate({**profile, key: value})
                self.p.calls.clear()
                with self.assertRaises(app.ApplicationDrift):
                    self.ready()
                self.assertEqual(self.p.calls, [])
                self.assertFalse(app.application_status(self.p, ORIGIN)["ready"])
        path = self.activate(profile)
        for data in ('{"schemaVersion":1,"schemaVersion":1}', " " * 16385):
            path.write_text(data)
            self.p.calls.clear()
            with self.assertRaises(app.ApplicationDrift):
                self.ready()
            self.assertEqual(self.p.calls, [])
        if os.name != "nt":
            path = self.activate(profile)
            path.chmod(0o644)
            with self.assertRaises(app.ApplicationDrift):
                self.ready()

    def test_public_service_names_cannot_replace_private_recovery_hosts(self):
        legacy_origin = "https://recovery-api.homelab.example.com:8443"
        self.p.settings["public_host"] = "recovery-auth.homelab.example.com"
        app.reconcile_application(self.p, legacy_origin)
        record = self.record()
        profile = {**self.profile(), "legacyLuciaOrigin": legacy_origin, "legacyAuthority": record["authority"]}
        self.assertEqual(app._domain_profile(profile, record), profile)
        expected = "Use distinct public hostnames to preserve private recovery addresses."
        for service in ("canonicalLuciaOrigin", "canonicalAuthentikOrigin", "sparkOrigin"):
            for legacy in ("legacyLuciaOrigin", "legacyAuthority"):
                with self.subTest(service=service, legacy=legacy):
                    collision = {**profile, service: "https://" + urllib.parse.urlsplit(profile[legacy]).hostname}
                    self.p.calls.clear()
                    with self.assertRaisesRegex(app.ApplicationDrift, expected):
                        app.prepare_domain_application(self.p, legacy_origin, collision)
                    self.assertEqual(self.p.calls, [])
                    self.activate(collision)
                    with self.assertRaisesRegex(app.ApplicationDrift, expected):
                        app.reconcile_application(self.p, legacy_origin)
                    self.assertEqual(self.p.calls, [])
                    self.assertEqual(self.record(), record)
                    alias = urllib.parse.urlsplit(profile[legacy]).hostname.upper() + "."
                    modified = {
                        **collision,
                        legacy: "https://" + alias + (":8443" if legacy == "legacyLuciaOrigin"
                                                     else ":9443/application/o/lucia/"),
                    }
                    modified_record = {**record, "public_origin": modified["legacyLuciaOrigin"],
                                       "authority": modified["legacyAuthority"]}
                    with self.assertRaisesRegex(app.ApplicationDrift, expected):
                        app._domain_profile(modified, modified_record)

    def test_active_domain_profile_links_and_host_path_drift_fail_closed(self):
        self.ready()
        profile = self.profile()
        path = self.activate(profile)
        host_settings = self.p.state / "host-settings.json"
        settings = json.loads(host_settings.read_text())
        settings["data_directory"] = str(self.p.state)
        app.write_file(host_settings, json.dumps(settings))
        self.p.calls.clear()
        with self.assertRaises(app.ApplicationDrift):
            self.ready()
        self.assertEqual(self.p.calls, [])
        self.activate(profile)
        target = path.with_name("linked.json")
        path.rename(target)
        try:
            path.symlink_to(target)
        except OSError:
            self.skipTest("Creating symlinks is unavailable without Windows developer privileges.")
        self.p.calls.clear()
        with self.assertRaises(app.ApplicationDrift):
            self.ready()
        self.assertEqual(self.p.calls, [])

    def test_prepare_is_local_and_stable(self):
        first = app.prepare_application(self.p, ORIGIN + "/")
        secret = self.secret()
        second = app.prepare_application(self.p, ORIGIN + ":443")
        self.assertEqual(first, second)
        self.assertEqual(self.secret(), secret)
        self.assertEqual(self.p.calls, [])
        self.assertEqual(first["authority"], ORIGIN + ":9443/application/o/lucia/")
        self.assertNotIn(secret.decode(), json.dumps(first))
        if os.name != "nt":
            self.assertEqual((self.p.state / "secrets" / app.SECRET_NAME).stat().st_mode & 0o777, 0o600)

    def test_invalid_and_changed_origins(self):
        for origin in (None, "", "http://a", "https://a/path", "https://a?", "https://a#",
                       "https://a:0", "https://a:", "https://u:p@a", "https://*.a", "https://a\\b",
                       "https://a\n", "https://[fe80::1%eth0]", "https://a:65536"):
            with self.subTest(origin=origin), self.assertRaises(ValueError):
                app.prepare_application(self.p, origin)
        self.assertEqual(app._origin("https://[2001:db8::1]:443"), "https://[2001:db8::1]")
        app.prepare_application(self.p, ORIGIN)
        with self.assertRaises(app.ApplicationDrift):
            app.prepare_application(self.p, "https://other.example")
        self.assertEqual(self.p.calls, [])

    def test_interrupted_prepare_preserves_secret(self):
        original = app._save
        def interrupt(p, record):
            if record["secret_initialized"]:
                raise OSError("Simulated interruption before final record")
            original(p, record)
        with patch.object(app, "_save", side_effect=interrupt), self.assertRaises(OSError):
            app.prepare_application(self.p, ORIGIN)
        secret = self.secret()
        installation = self.record()["installation_id"]
        resumed = app.prepare_application(self.p, ORIGIN)
        self.assertEqual(secret, self.secret())
        self.assertEqual(installation, resumed["installation_id"])
        self.assertTrue(resumed["secret_initialized"])

    def test_interrupted_atomic_publication_recovers_staged_files(self):
        saved = app.prepare_application(self.p, ORIGIN)
        record = self.record()
        record["secret_initialized"] = False
        path = self.p.state / "host-auth.json"
        path.write_text(json.dumps(record))
        path.rename(path.with_name(path.name + ".new"))
        secret_path = self.p.state / "secrets" / app.SECRET_NAME
        secret = self.secret()
        secret_path.rename(secret_path.with_name(secret_path.name + ".new"))
        self.assertEqual(app.prepare_application(self.p, ORIGIN), saved)
        self.assertEqual(secret, self.secret())
        self.assertEqual(self.p.calls, [])

    def test_incomplete_staged_secret_is_never_regenerated(self):
        app.prepare_application(self.p, ORIGIN)
        record = self.record()
        record["secret_initialized"] = False
        app._save(self.p, record)
        path = self.p.state / "secrets" / app.SECRET_NAME
        path.unlink()
        staged = path.with_name(path.name + ".new")
        staged.write_text("partial-write")
        with self.assertRaises(app.ApplicationDrift):
            app.prepare_application(self.p, ORIGIN)
        self.assertEqual(staged.read_text(), "partial-write")
        self.assertFalse(path.exists())

    def test_missing_record_and_secret_fail_closed(self):
        app.prepare_application(self.p, ORIGIN)
        record_path = self.p.state / "host-auth.json"
        record = record_path.read_bytes()
        record_path.unlink()
        with self.assertRaises(app.ApplicationDrift):
            app.prepare_application(self.p, ORIGIN)
        record_path.write_bytes(record)
        (self.p.state / "secrets" / app.SECRET_NAME).unlink()
        with self.assertRaises(app.ApplicationDrift):
            app.prepare_application(self.p, ORIGIN)
        self.assertFalse(app.application_status(self.p, ORIGIN)["ready"])
        self.assertEqual(self.p.calls, [])

    def test_fresh_create_exact_oauth_configuration_and_roles(self):
        result = self.ready()
        self.assertTrue(result["ready"])
        self.assertFalse(result["sso_verified"])
        provider = self.p.tables[app.PROVIDERS][0]
        self.assertEqual(provider["grant_types"], ["authorization_code"])
        self.assertEqual(provider["client_type"], "confidential")
        self.assertEqual(provider["signing_key"], self.p.signer)
        self.assertEqual(provider["redirect_uris"], [
            {"matching_mode": "strict", "url": ORIGIN + "/signin-oidc", "redirect_uri_type": "authorization"},
            {"matching_mode": "strict", "url": ORIGIN + "/signout-callback-oidc", "redirect_uri_type": "logout"},
        ])
        self.assertEqual(provider["logout_uri"], "")
        self.assertTrue(provider["include_claims_in_id_token"])
        group = next(item for item in self.p.tables[app.GROUPS] if item["name"] == "lucia-users")
        self.assertFalse(group["is_superuser"])
        self.assertEqual(group["users"], [])
        self.assertEqual(self.p.role(provider, [self.p.owner_group, group["pk"]]), {"lucia_role": "Owner"})
        self.assertEqual(self.p.role(provider, [group["pk"]]), {"lucia_role": "Inference"})
        self.assertEqual(self.p.role(provider, []), {}, "Caller attributes must not grant roles.")
        self.assertEqual(self.p.tables[app.APPLICATIONS][0]["policy_engine_mode"], "any")
        self.assertEqual({item["group"] for item in self.p.tables[app.BINDINGS]}, {group["pk"], self.p.owner_group})
        self.assertNotIn(self.secret().decode(), json.dumps(result))

    def test_rerun_and_status_preserve_everything(self):
        first = self.ready()
        group = self.p.tables[app.GROUPS][-1]
        group["users"].append(299)
        group["attributes"]["custom"] = "preserve"
        expected = copy.deepcopy(self.p.tables)
        secret = self.secret()
        self.p.calls.clear()
        self.assertEqual(self.ready(), first)
        self.assertEqual(self.p.mutations, [])
        self.assertEqual(self.p.tables, expected)
        self.assertEqual(secret, self.secret())
        contents = {path: path.read_bytes() for path in self.p.state.rglob("*") if path.is_file()}
        self.assertTrue(app.application_status(self.p, ORIGIN)["ready"])
        self.assertEqual(contents, {path: path.read_bytes() for path in self.p.state.rglob("*") if path.is_file()})

    def test_deleted_resources_repaired_without_rotation(self):
        initial = self.ready()
        secret = self.secret()
        for endpoint, kind in ((app.APPLICATIONS, "application"), (app.PROVIDERS, "provider"),
                               (app.MAPPINGS, "scope_mapping"), (app.GROUPS, "access_group")):
            with self.subTest(kind=kind):
                before = self.record()["resources"][kind]
                self.p.delete(endpoint, before)
                self.assertFalse(app.application_status(self.p, ORIGIN)["ready"])
                result = self.ready()
                self.assertNotEqual(result["resources"][kind], before)
                self.assertEqual(result["client_id"], initial["client_id"])
                self.assertEqual(self.secret(), secret)
        for role in ("owner", "inference"):
            binding = self.record()["resources"]["bindings"][role]["id"]
            self.p.delete(app.BINDINGS, binding)
            self.assertFalse(app.application_status(self.p, ORIGIN)["ready"])
            self.assertNotEqual(self.ready()["resources"]["bindings"][role]["id"], binding)

    def test_owned_drift_reconciled_and_regular_members_preserved(self):
        self.ready()
        self.p.tables[app.PROVIDERS][0]["redirect_uris"].append(
            {"matching_mode": "regex", "url": ".*", "redirect_uri_type": "authorization"})
        self.p.tables[app.BINDINGS][0]["enabled"] = False
        self.p.tables[app.APPLICATIONS][0]["policy_engine_mode"] = "all"
        self.assertFalse(app.application_status(self.p, ORIGIN)["ready"])
        self.assertTrue(self.ready()["ready"])
        self.assertEqual(len(self.p.tables[app.PROVIDERS][0]["redirect_uris"]), 2)
        self.assertNotIn("client_secret", next(body for method, path, body in self.p.mutations
                                             if method == "PATCH" and path.startswith(app.PROVIDERS)))

    def test_collisions_and_unmanaged_bindings_never_mutated(self):
        result = self.ready()
        scenarios = [
            (app.APPLICATIONS, "meta_description", "manual"),
            (app.PROVIDERS, "client_id", "different-client"),
            (app.PROVIDERS, "client_secret", "different-secret"),
            (app.PROVIDERS, "name", "manual"),
            (app.MAPPINGS, "managed", "manual"),
            (app.GROUPS, "attributes", {}),
            (app.GROUPS, "is_superuser", True),
            (app.GROUPS, "roles", [identifier()]),
            (app.GROUPS, "parents", [identifier()]),
            (app.BINDINGS, "group", identifier()),
            (app.BINDINGS, "target", identifier()),
        ]
        for endpoint, field, value in scenarios:
            with self.subTest(endpoint=endpoint, field=field):
                item = self.p.tables[endpoint][-1]
                old = copy.deepcopy(item.get(field))
                item[field] = value
                self.p.calls.clear()
                with self.assertRaises(app.ApplicationDrift):
                    self.ready()
                self.assertEqual(self.p.mutations, [])
                self.assertFalse(app.application_status(self.p, ORIGIN)["ready"])
                item[field] = old
        foreign = {**self.p.tables[app.BINDINGS][0], "pk": identifier(), "group": identifier()}
        self.p.tables[app.BINDINGS].append(foreign)
        self.p.calls.clear()
        with self.assertRaises(app.ApplicationDrift):
            self.ready()
        self.assertEqual(self.p.mutations, [])
        self.assertEqual(self.record()["client_id"], result["client_id"])

    def test_fresh_same_name_collision_not_adopted(self):
        self.p.tables[app.GROUPS].append({"pk": identifier(), "name": "lucia-users", "attributes": {}})
        with self.assertRaises(app.ApplicationDrift):
            self.ready()
        self.assertEqual(self.p.mutations, [])

    def test_shared_mapping_and_foreign_application_are_untouched(self):
        self.ready()
        foreign = copy.deepcopy(self.p.tables[app.PROVIDERS][0])
        foreign.update(pk=5001, name="Manual provider", client_id="manual-client", client_secret="not-ours")
        self.p.tables[app.PROVIDERS].append(foreign)
        self.p.calls.clear()
        with self.assertRaisesRegex(app.ApplicationDrift, "shared"):
            self.ready()
        self.assertEqual(self.p.mutations, [])
        self.assertEqual(self.p.tables[app.PROVIDERS][-1], foreign)

    def test_no_private_rsa_key_is_not_replaced_with_public_ca(self):
        self.p.tables[app.CERTIFICATES] = self.p.tables[app.CERTIFICATES][:1]
        with self.assertRaisesRegex(app.ApplicationDrift, "private RSA"):
            self.ready()
        self.assertEqual(self.p.mutations, [])
        self.p.tables[app.CERTIFICATES][0]["private_key_available"] = True
        with self.assertRaisesRegex(app.ApplicationDrift, "private RSA"):
            self.ready()
        self.assertEqual(self.p.mutations, [])

    def test_signer_expiry_and_replacement_need_review(self):
        self.ready()
        certificate = self.p.tables[app.CERTIFICATES][-1]
        for field, value in (("private_key_available", False), ("key_type", "ec"),
                             ("cert_expiry", "2000-01-01T00:00:00Z")):
            old = certificate[field]
            certificate[field] = value
            self.p.calls.clear()
            with self.assertRaises(app.ApplicationDrift):
                self.ready()
            self.assertEqual(self.p.mutations, [])
            certificate[field] = old

    def test_lost_create_response_recovers_owned_resources_and_bindings(self):
        original = self.p.api
        for endpoint in (app.GROUPS, app.MAPPINGS, app.PROVIDERS, app.APPLICATIONS, app.BINDINGS):
            with self.subTest(endpoint=endpoint):
                def interrupted(method, path, body=None, allow_missing=False):
                    response = original(method, path, body, allow_missing)
                    if method == "POST" and path == endpoint:
                        raise OSError("Lost create response")
                    return response
                self.p.api = interrupted
                with self.assertRaises(RuntimeError):
                    self.ready()
                self.p.api = original
        self.assertTrue(self.ready()["ready"])
        self.assertEqual(len(self.p.tables[app.PROVIDERS]), 1)
        self.assertEqual(len(self.p.tables[app.APPLICATIONS]), 1)
        self.assertEqual(len(self.p.tables[app.BINDINGS]), 2)

    def test_failure_propagates_without_response_body_or_secret(self):
        self.ready()
        self.p.failure = lambda method, path, body: path.startswith(app.PROVIDERS)
        for callback in (app.application_status, app.reconcile_application):
            with self.assertRaises(RuntimeError) as error:
                callback(self.p, ORIGIN)
            self.assertNotIn("credential-rich-response", str(error.exception))
            self.assertNotIn(self.secret().decode(), str(error.exception))

    def test_failed_binding_creation_keeps_application_inert(self):
        self.p.failure = lambda method, path, body: (
            method == "POST" and path == app.BINDINGS and body["group"] != self.p.owner_group)
        with self.assertRaises(RuntimeError):
            self.ready()
        application = self.p.tables[app.APPLICATIONS][0]
        self.assertIsNone(application["provider"])
        self.assertTrue(application["meta_hide"])
        self.assertEqual(application["meta_launch_url"], "")
        self.p.failure = None
        self.assertTrue(self.ready()["ready"])

    def test_policy_visibility_and_claim_evidence_required(self):
        self.ready()
        for flag in ("deny_owner", "hide_owner", "bad_claim"):
            setattr(self.p, flag, True)
            self.assertFalse(app.application_status(self.p, ORIGIN)["ready"])
            with self.assertRaises(app.ApplicationDrift):
                self.ready()
            setattr(self.p, flag, False)

    def test_pagination_and_local_record_not_enough(self):
        app.prepare_application(self.p, ORIGIN)
        status = app.application_status(self.p, ORIGIN)
        self.assertFalse(status["ready"])
        self.assertIn("Missing managed application.", status["reasons"])
        self.p.paginate = True
        self.assertTrue(self.ready()["ready"])


class AppClientChecks(unittest.TestCase):
    PROFILE = {"verifiedZone": "example.com", "canonicalLuciaOrigin": "https://lucia.homelab.example.com",
               "canonicalAuthentikOrigin": "https://auth.homelab.example.com", "sparkOrigin": "https://spark.homelab.example.com"}

    def request(self, **changes):
        return {"schemaVersion": 1, "stack": "observability", "name": "Grafana", "clientId": "lucia-app-observability",
                "clientSecret": "s" * 43, "redirectUri": "https://grafana.homelab.example.com/login/generic_oauth",
                "launchUrl": "https://grafana.homelab.example.com/", **changes}

    def test_requests_grant_only_exact_callbacks_under_the_domain(self):
        import app_clients
        self.assertEqual(app_clients._checked(self.request(), "observability", self.PROFILE), self.request())
        for changes in ({"clientId": "grafana"}, {"stack": "other"}, {"clientSecret": "short"}, {"extra": 1},
                        {"redirectUri": "http://grafana.homelab.example.com/login/generic_oauth"},
                        {"redirectUri": "https://grafana.example.org/login/generic_oauth"},
                        {"redirectUri": "https://lucia.homelab.example.com/login/generic_oauth"},
                        {"redirectUri": "https://grafana.homelab.example.com:8443/login/generic_oauth"},
                        {"redirectUri": "https://grafana.homelab.example.com/login?next=x"},
                        {"redirectUri": "https://x@grafana.homelab.example.com/login"},
                        {"launchUrl": "https://evil.homelab.example.com/"}, {"name": "a\nb"}):
            with self.assertRaises((ValueError, app.ApplicationDrift), msg=str(changes)):
                app_clients._checked(self.request(**changes), "observability", self.PROFILE)
        with self.assertRaises(app.ApplicationDrift):
            app_clients._checked(self.request(), "observability", None)


if __name__ == "__main__":
    unittest.main()
