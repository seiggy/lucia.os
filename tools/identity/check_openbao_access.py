"""Offline OpenBao access checks; no live identities, credentials, DNS or containers."""

import copy
import io
import json
import pathlib
import tempfile
import unittest
import urllib.error
from types import SimpleNamespace
from unittest.mock import patch

import application as app
import openbao_access as access
from check_application import FakeProvisioner, identifier
from provision import write_file

ORIGIN = "https://openbao.lab.example.test"
PROFILE = {"verifiedZone": "example.test", "canonicalAuthentikOrigin": "https://auth.example.test",
           "canonicalLuciaOrigin": "https://lucia.lab.example.test", "sparkOrigin": "https://spark.lab.example.test"}


def evaluate(expression, groups):
    query = SimpleNamespace(filter=lambda **kw: SimpleNamespace(exists=lambda: kw["pk"] in groups))
    user = SimpleNamespace(all_groups=lambda: query, attributes={"lucia_openbao_operator": "owner", "is_superuser": True})
    namespace = {"request": SimpleNamespace(user=user)}
    exec("def evaluate():\n" + "".join("    " + line + "\n" for line in expression.splitlines()), namespace)
    return namespace["evaluate"]()


class FakeIdentity(FakeProvisioner):
    def assert_closed_registration(self):
        for application in self.tables[app.APPLICATIONS]:
            if application["provider"] is not None:
                bindings = [item for item in self.tables[app.BINDINGS] if item["target"] == application["pk"]]
                assert len(bindings) == 1
                assert bindings[0]["enabled"] and not bindings[0]["negate"] and bindings[0]["group"] == self.owner_group

    def api(self, method, path, body=None, allow_missing=False):
        if path.startswith(app.APPLICATIONS + access.SLUG + "/check_access/"):
            self.calls.append((method, path, copy.deepcopy(body)))
            return {"passing": not self.deny_owner}
        if path.startswith(app.PROVIDERS) and "/preview_user/" in path:
            self.calls.append((method, path, copy.deepcopy(body)))
            mapping = self.tables[app.MAPPINGS][-1]
            return {"preview": {} if self.bad_claim else evaluate(mapping["expression"], [self.owner_group])}
        return super().api(method, path, body, allow_missing)


class AccessChecks(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.p = FakeIdentity(pathlib.Path(self.temporary.name) / "identity")
        self.p.state.mkdir()
        self.host_record = {"installation_id": identifier()}
        self.snapshot = {
            "domain_profile": dict(PROFILE), "owner_group": self.p.tables[app.GROUPS][0],
            "signing_key": self.p.tables[app.CERTIFICATES][1], "owner_user": self.p.owner_pk,
            "defaults": [item["pk"] for item in self.p.tables[app.MAPPINGS]],
            "flows": {key: identifier() for key in ("authentication_flow", "authorization_flow", "invalidation_flow")},
        }
        write_file(self.p.state / "host-settings.json", json.dumps({"authentication": {"public_origin": "https://192.0.2.10"}}))
        write_file(self.p.state / "openbao-settings.json", '{"bind_address":"127.0.0.1","port":8200}')
        for target, result in (("_load", self.host_record), ("_snapshot", self.snapshot)):
            mock = patch.object(app, target, return_value=result)
            mock.start()
            self.addCleanup(mock.stop)
        certificate = patch.object(access, "verify_ingress_certificate")
        certificate.start()
        self.addCleanup(certificate.stop)

    def register(self):
        return access.register(self.p, ORIGIN)

    def test_registration_is_repeat_safe_and_owner_only(self):
        record = self.register()
        count = len(self.p.mutations)
        self.assertEqual(self.register(), record)
        self.assertEqual(len(self.p.mutations), count)
        provider = self.p.tables[app.PROVIDERS][0]
        self.assertEqual(provider["grant_types"], ["authorization_code"])
        self.assertEqual([item["url"] for item in provider["redirect_uris"]], access.callbacks(ORIGIN))
        self.assertTrue(all(item["matching_mode"] == "strict" for item in provider["redirect_uris"]))
        expression = self.p.tables[app.MAPPINGS][-1]["expression"]
        self.assertEqual(evaluate(expression, [self.p.owner_group]), {"lucia_openbao_operator": "owner"})
        self.assertEqual(evaluate(expression, []), {})
        self.assertEqual(evaluate(expression, [identifier()]), {})
        self.assertEqual(len(self.p.tables[app.BINDINGS]), 1)
        self.assertFalse((self.p.state / "gateway/openbao.yml").exists())

    def test_scope_and_credential_drift_are_refused(self):
        record = self.register()
        for table, field, value in ((app.PROVIDERS, "client_secret", "credential-rich-response"),
                                    (app.PROVIDERS, "redirect_uris", []),
                                    (app.MAPPINGS, "expression", "return {'lucia_openbao_operator': 'owner'}")):
            item = self.p.tables[table][-1]
            old = item[field]
            item[field] = value
            with self.assertRaises((access.AccessError, app.ApplicationDrift)):
                self.register()
            item[field] = old
        self.p.tables[app.BINDINGS].append({**self.p.tables[app.BINDINGS][0], "pk": identifier(), "group": identifier()})
        with self.assertRaises(app.ApplicationDrift):
            self.register()
        self.assertEqual(record["resources"]["owner_group"], self.p.owner_group)

    def test_interrupted_binding_creation_resumes_without_duplicates(self):
        original = self.p.api
        interrupted = False

        def fail(method, path, body=None, allow_missing=False):
            nonlocal interrupted
            result = original(method, path, body, allow_missing)
            if method == "POST" and path == app.BINDINGS and not interrupted:
                interrupted = True
                raise RuntimeError("credential-rich-response")
            return result

        with patch.object(self.p, "api", side_effect=fail):
            with self.assertRaisesRegex(RuntimeError, "registration was not verified"):
                self.register()
        self.register()
        self.assertEqual(len(self.p.tables[app.BINDINGS]), 1)
        self.assertEqual(len(self.p.tables[app.PROVIDERS]), 1)

    def test_origins_cannot_be_adopted_or_migrated(self):
        for origin in ("http://openbao.lab.example.test", "https://other.invalid",
                       PROFILE["canonicalAuthentikOrigin"], ORIGIN + ":8443", ORIGIN + "/path"):
            with self.assertRaises((ValueError, access.AccessError)):
                access.register(self.p, origin)
        self.register()
        with self.assertRaises(access.AccessError):
            access.register(self.p, "https://another.lab.example.test")
        (self.p.state / "secrets" / access.SECRET).unlink()
        with self.assertRaises(access.AccessError):
            self.register()

    def test_gateway_is_private_and_verifies_backend_tls(self):
        config = access.gateway_configuration(ORIGIN)["http"]
        router = config["routers"]["lucia-openbao"]
        self.assertEqual(router["entryPoints"], ["host"])
        self.assertEqual(router["rule"], "Host(`openbao.lab.example.test`)")
        self.assertEqual(router["middlewares"], ["openbao-private"])
        transport = config["serversTransports"]["openbao-tls"]
        self.assertEqual(transport, {"serverName": "identity-gateway", "rootCAs": ["/config/openbao-root-ca.crt"]})
        service = config["services"]["lucia-openbao"]["loadBalancer"]
        self.assertEqual(service["servers"], [{"url": "https://identity-openbao:8200"}])
        self.assertEqual(service["serversTransport"], "openbao-tls")
        self.assertNotIn("forwardAuth", json.dumps(config))
        self.assertNotIn("insecureSkipVerify", json.dumps(config))
        import ipaddress
        ranges = [ipaddress.ip_network(value) for value in config["middlewares"]["openbao-private"]["ipAllowList"]["sourceRange"]]
        for address, expected in (("192.168.10.8", True), ("10.2.3.4", True), ("fd00::8", True),
                                  ("203.0.113.8", False), ("2001:db8::8", False)):
            ip = ipaddress.ip_address(address)
            self.assertEqual(any(ip.version == network.version and ip in network for network in ranges), expected)

    def test_dns_registration_reuses_profile_bound_domain_state(self):
        record = self.register()
        data = self.p.state / "host/data"
        host = {"authentication": {"public_origin": "https://192.0.2.10"}, "data_directory": str(data)}
        write_file(self.p.state / "host-settings.json", json.dumps(host))
        profile = {**PROFILE, "profileId": identifier()}
        workflow = {"job": {"id": profile["profileId"], "plan": {"naming": {
            "namespace": "lab.example.test", "localHostnames": ["lucia.lab.example.test"]}}}}
        write_file(data / "domains/workflow.json", json.dumps(workflow))
        with patch.object(app, "_active_domain_profile", return_value=profile):
            access.publish_dns_registration(self.p, record)
            access.publish_dns_registration(self.p, record)
            path = data / "domains/openbao-access.json"
            self.assertEqual(json.loads(path.read_text()), {
                "schemaVersion": 1, "profileId": profile["profileId"], "origin": ORIGIN})
            write_file(path, json.dumps({"schemaVersion": 1, "profileId": identifier(), "origin": ORIGIN}))
            with self.assertRaises(access.AccessError):
                access.publish_dns_registration(self.p, record)
            path.unlink()
            with self.assertRaises(access.AccessError):
                access.publish_dns_registration(self.p, {**record, "origin": "https://openbao.example.test"})
            with self.assertRaises(access.AccessError):
                access.publish_dns_registration(self.p, {**record, "authority": "https://other.invalid/"})

    def test_configure_retries_and_revokes_before_route_publication(self):
        record = self.register()
        calls = []
        stored = {}
        mounts = {}

        def backend(p, method, route, body=None, token=None, missing_ok=False):
            calls.append((method, route, copy.deepcopy(body), token))
            if route == "auth/lucia-operator/login/operator":
                return {"auth": {"client_token": "synthetic-operator-token", "policies": ["default", "lucia-operator"],
                                 "lease_duration": 900}}
            if route == "auth/token/revoke-self":
                return None
            if route == "sys/policies/acl/lucia-operator":
                return {"data": {"policy": "synthetic"}}
            if route == "sys/auth":
                return {"data": mounts}
            if route.startswith("sys/auth/"):
                mounts[route.split("/")[-1] + "/"] = body
                return None
            if method == "LIST":
                role = route + "/owners"
                return {"data": {"keys": ["owners"]}} if role in stored else None
            if route.endswith("/oidc/auth_url"):
                return {"data": {"auth_url": "https://auth.example.test/authorize"}}
            if method == "POST":
                stored[route] = copy.deepcopy(body)
                return None
            return {"data": stored[route]} if route in stored else None

        def publish(p, registered):
            self.assertEqual(calls[-1][1], "auth/token/revoke-self")
            self.assertEqual(registered, record)

        with patch.object(access, "backend", side_effect=backend), patch.object(access, "publish_gateway", side_effect=publish):
            for attempt in range(2):
                result = access.configure(self.p, record, "synthetic-password")
                self.assertEqual(result["app_roles"], 0)
        role = stored[f"auth/{access.OIDC}/role/owners"]
        self.assertEqual(role["role_type"], "oidc")
        self.assertEqual(role["bound_claims"], {"lucia_openbao_operator": "owner"})
        self.assertEqual(role["bound_audiences"], [record["client_id"]])
        self.assertEqual(role["token_policies"], ["lucia-operator"])
        self.assertEqual(role["token_ttl"], 900)
        self.assertEqual(role["token_explicit_max_ttl"], 3600)
        self.assertFalse(any(route.startswith(f"auth/{access.JWT}/role/") for route in stored))
        self.assertEqual(stored[f"auth/{access.JWT}/config"]["default_role"], "")
        self.assertEqual(stored[f"auth/{access.JWT}/config"]["jwt_supported_algs"], ["RS256"])
        self.assertFalse(result["browser_login_verified"])

    def test_configuration_failure_revokes_and_never_publishes(self):
        record = self.register()
        calls = []

        def backend(p, method, route, body=None, token=None, missing_ok=False):
            calls.append(route)
            if route == "auth/lucia-operator/login/operator":
                return {"auth": {"client_token": "synthetic", "policies": ["root"], "lease_duration": 900}}
            if route == "auth/token/revoke-self":
                return None
            raise AssertionError(route)

        with patch.object(access, "backend", side_effect=backend), patch.object(access, "publish_gateway") as publish:
            with self.assertRaises(access.AccessError):
                access.configure(self.p, record, "synthetic")
            self.assertEqual(calls[-1], "auth/token/revoke-self")
            publish.assert_not_called()

    def test_transport_refuses_redirects_and_redacts_response_bodies(self):
        error = urllib.error.HTTPError("https://identity.example.test", 403, "", {}, io.BytesIO(b"credential-rich-response"))
        with patch.object(access.ssl, "create_default_context"), patch.object(access.urllib.request, "build_opener") as opener:
            opener.return_value.open.side_effect = error
            with self.assertRaisesRegex(access.AccessError, "HTTP 403") as failure:
                access.request_json("https://identity.example.test")
            self.assertNotIn("credential-rich-response", str(failure.exception))
            handlers = opener.call_args.args
            self.assertIsInstance(handlers[0], urllib.request.ProxyHandler)
            self.assertEqual(handlers[0].proxies, {})
            self.assertIsInstance(handlers[2], access.NoRedirect)


if __name__ == "__main__":
    unittest.main()
