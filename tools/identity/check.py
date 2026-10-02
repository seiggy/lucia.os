"""Offline identity regressions: fixture state stays beneath this checkout.

Docker/LDAP/API transports are mocked. The CLI subprocess uses an explicit,
uninitialized fixture --state and fails before any service access.
"""

import argparse
import contextlib
import hashlib
import http.cookiejar
import io
import json
import os
import pathlib
import ssl
import subprocess
import sys
import tempfile
from unittest.mock import MagicMock, Mock, patch

from provision import BASE_DN, ROOT, IdentityReadinessError, Provisioner, SECRET_NAMES, authority, command_failure_message, configure_authentik, configure_browser_session, service_hosts, settings_for, validate_host
from owner import enroll_ldap_owner, finish_owner, owner_identity, validate_login_password, validate_password, validate_username, verify_login

for host in ("192.168.0.222", "spark-9423", "auth.lucia.home.arpa", "2001:db8::1"):
    assert validate_host(host) == host
for host in ("", "https://spark", "spark:9443", "bad/host", "bad host", "-bad.local", "bad..local", "fe80::1%eth0"):
    try:
        validate_host(host)
        raise AssertionError("Invalid host accepted: " + host)
    except ValueError:
        pass
assert authority("2001:db8::1", 9443) == "[2001:db8::1]:9443"
for username in ("zackw", "lucia-admin", "owner_1"):
    assert validate_username(username) == username
for username in ("", "root", "admin", "akadmin", "authentik", "../owner", "user,cn=admin", "user\ncn: admin", "A" * 33):
    try:
        validate_username(username)
        raise AssertionError("Unsafe or reserved username accepted.")
    except ValueError:
        pass
for password in ("short", "long-enough-but\nunsafe", "long-enough-but\0unsafe", "a" * 1025, None):
    try:
        validate_password(password)
        raise AssertionError("An invalid request password was accepted.")
    except ValueError:
        pass
assert validate_password("exactly14chars!") == "exactly14chars!"
for password in ("old", "x\n", "x\0"):
    assert validate_login_password(password) == password
for password in ("", None):
    try:
        validate_login_password(password)
        raise AssertionError("An empty existing-owner password was accepted.")
    except ValueError:
        pass
assert "ldapwhoami" in command_failure_message(["docker", "exec", "container", "ldapwhoami", "-w", "private-argument"], 81)
assert "81" in command_failure_message(["docker", "exec", "container", "ldapwhoami"], 81)
assert "private-argument" not in command_failure_message(["docker", "exec", "container", "ldapwhoami", "-w", "private-argument"], 81)

with tempfile.TemporaryDirectory(prefix=".lucia-identity-check-", dir=ROOT / "tools/identity") as temporary:
    args = argparse.Namespace(command="prepare", state=str(pathlib.Path(temporary) / "identity"), host="192.0.2.10",
                              certificate_mode="private-ca", auth_port=None, ldap_port=None, ca_port=None)
    templates = pathlib.Path(temporary) / "checkout" / "deployment" / "identity"
    templates.mkdir(parents=True)
    for source in (ROOT / "deployment" / "identity").iterdir():
        if source.suffix in (".sh", ".ldif"):
            (templates / source.name).write_bytes(source.read_text().replace("\n", "\r\n").encode())
    first = Provisioner(args)
    with patch("provision.ROOT", templates.parents[1]), contextlib.redirect_stdout(io.StringIO()):
        first.prepare()
    for script in (first.state / "config").glob("*.sh"):
        assert b"\r" not in script.read_bytes(), "POSIX scripts must be staged with LF even from a CRLF checkout."
    assert b"\r" not in (first.state / "trust" / "install-node-trust.sh").read_bytes()
    hashes = {name: hashlib.sha256((first.state / "secrets" / name).read_bytes()).hexdigest() for name in SECRET_NAMES}
    second = Provisioner(args)
    with patch("provision.os.chmod", wraps=os.chmod) as chmod, contextlib.redirect_stdout(io.StringIO()):
        second.prepare()
    assert not any(call.args[0] in (first.state / "ldap/data", first.state / "ldap/config", first.state / "ca")
                   for call in chmod.call_args_list), "Reruns must preserve container-owned storage permissions."
    assert second.settings == first.settings
    assert second.settings["ldap_base_dn"] == BASE_DN
    assert hashes == {name: hashlib.sha256((second.state / "secrets" / name).read_bytes()).hexdigest() for name in SECRET_NAMES}
    entries = (second.state / "config" / "init_org_entries.ldif").read_text()
    assert "employee1" not in entries and "changeit" not in entries and "userPassword:" not in entries
    certificate = second.state / "certificates/identity.crt"
    key = second.state / "certificates/identity.key"
    marker = second.state / "certificates/ready.sha256"
    issuance = []

    def certificate_command(command, **kwargs):
        issuance.append(command)
        output, code = "", 0
        if command[:6] == ["docker", "exec", "lucia-identity-ca", "step", "certificate", "fingerprint"]:
            output = "a" * 64 + "\n"
        elif command[:2] == ["docker", "cp"]:
            pathlib.Path(command[-1]).write_text("test-public-root")
        elif command[:6] == ["docker", "exec", "lucia-identity-ca", "step", "ca", "certificate"]:
            assert not certificate.exists() and not key.exists() and not marker.exists()
            assert command[7:9] == ["/certificates/issuing.crt", "/certificates/issuing.key"]
            certificate.with_name("issuing.crt").write_text("test-complete-certificate")
            key.with_name("issuing.key").write_text("test-existing-key")
        elif command[:2] == ["openssl", "req"]:
            assert key.read_text() == "test-existing-key"
            pathlib.Path(command[command.index("-out") + 1]).write_text("test-csr")
        elif command[:6] == ["docker", "exec", "lucia-identity-ca", "step", "ca", "sign"]:
            certificate.with_name("issuing.crt").write_text("test-complete-certificate")
        elif command[:2] == ["openssl", "x509"] and "-checkend" in command:
            code = 1
        else:
            raise AssertionError(f"Unexpected certificate command: {command}")
        return subprocess.CompletedProcess(command, code, output, "")

    def valid_pair(crt, private_key, root):
        assert crt.read_text() == "test-complete-certificate"
        assert private_key.read_text() == "test-existing-key"

    with patch.object(second, "wait_container"), patch.object(second, "run", side_effect=certificate_command), \
            patch.object(second, "validate_certificate_pair", side_effect=valid_pair), \
            patch.object(second, "certificate_publication_lock", side_effect=contextlib.nullcontext), \
            contextlib.redirect_stdout(io.StringIO()):
        second.certificates()
        expected = "".join(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n" for path in (certificate, key))
        assert marker.read_text() == expected
        certificate.unlink()
        marker.unlink()
        issuance.clear()
        second.certificates()
        assert any(command[3:6] == ["step", "ca", "sign"] for command in issuance)
        assert not any(command[3:6] == ["step", "ca", "certificate"] for command in issuance)
        assert key.read_text() == "test-existing-key", "Recovery must keep an existing private key."
        assert not certificate.with_name("issuing.crt").exists()
        assert not key.with_name("issuing.key").exists()

    resolv = certificate.with_name("resolv.conf")
    resolv.write_text("nameserver 192.0.2.1\nsearch lan.example. bad_domain lan.example\n")
    assert service_hosts("spark-1", str(resolv)) == ["spark-1", "spark-1.lan.example"]
    assert service_hosts("spark.example", str(resolv)) == ["spark.example"]
    assert service_hosts("192.168.0.2", str(resolv)) == ["192.168.0.2"]
    assert service_hosts("spark-1", str(resolv) + ".missing") == ["spark-1"]
    resolv.unlink()

    tls = MagicMock()
    with patch("provision.ssl.create_default_context", return_value=tls), \
            patch("provision.socket.create_connection", side_effect=[ConnectionRefusedError(), MagicMock()]) as connect, \
            patch("provision.time.sleep"), patch("provision.time.monotonic", side_effect=[0, 0, 1]):
        second.wait_gateway_tls(timeout=2)
        assert connect.call_count == 2
        assert tls.wrap_socket.call_args.kwargs["server_hostname"] == "identity-gateway"
    with patch("provision.ssl.create_default_context"), \
            patch("provision.socket.create_connection", side_effect=ConnectionRefusedError("private-diagnostic")), \
            patch("provision.time.sleep"), patch("provision.time.monotonic", side_effect=[0, 0, 2]):
        try:
            second.wait_gateway_tls(timeout=2)
            raise AssertionError("An unavailable gateway was accepted as ready.")
        except IdentityReadinessError as error:
            assert "2 seconds" in str(error) and "private-diagnostic" not in str(error)
    untrusted = MagicMock()
    untrusted.wrap_socket.side_effect = ssl.SSLCertVerificationError("private-certificate-diagnostic")
    with patch("provision.ssl.create_default_context", return_value=untrusted), \
            patch("provision.socket.create_connection", return_value=MagicMock()), \
            patch("provision.time.sleep"), patch("provision.time.monotonic", side_effect=[0, 0, 2]):
        try:
            second.wait_gateway_tls(timeout=2)
            raise AssertionError("Untrusted TLS was accepted during readiness checks.")
        except IdentityReadinessError as error:
            assert "did not validate" in str(error) and "private-certificate-diagnostic" not in str(error)
    with patch.object(second, "wait_gateway_tls"), \
            patch.object(second, "run", return_value=subprocess.CompletedProcess(["ldapwhoami"], 49, "", "")) as bind:
        try:
            second.check_ldap_tls()
            raise AssertionError("An invalid LDAP credential was accepted.")
        except IdentityReadinessError as error:
            assert "49" in str(error)
        assert bind.call_count == 1, "Credential failures must not be retried into an LDAP lockout."
    secret = second.values["ldap-admin-password"]
    with patch("provision.subprocess.run", return_value=subprocess.CompletedProcess(["check"], 1, "failure " + secret, "")):
        try:
            second.run(["check"])
            raise AssertionError("Subprocess failure was ignored.")
        except RuntimeError as error:
            assert secret not in str(error) and "last-command-error.log" in str(error)
    log = (second.state / "last-command-error.log").read_text()
    assert secret not in log and "[redacted]" in log
    (second.state / "apphost-path.txt").write_text("different-checkout")
    try:
        second.publish()
        raise AssertionError("A checkout relocation could silently replace persistent volumes.")
    except RuntimeError as error:
        assert "location changed" in str(error)
    (second.state / "trust" / "lucia-root-ca.crt").write_text("public-test-certificate")
    requests = []
    login_stage = {"pk": "login-stage", "name": "default-authentication-login", "session_duration": "seconds=0",
                   "remember_me_offset": "seconds=0", "terminate_other_sessions": False}

    def api(method, path, body=None, allow_missing=False):
        requests.append((method, path, body))
        if path.startswith("/api/v3/crypto/"):
            return {"results": [{"pk": "ca", "name": "lucia-ldap-ca",
                                 "fingerprint_sha256": hashlib.sha256(b"test-ca").hexdigest()}]}
        if path.startswith("/api/v3/propertymappings/"):
            from urllib.parse import parse_qs, urlparse
            managed = parse_qs(urlparse(path).query)["managed"][0]
            return {"results": [{"pk": managed, "managed": managed}]}
        if path == "/api/v3/stages/user_login/?name=default-authentication-login":
            return {"results": [dict(login_stage)]}
        if path == "/api/v3/stages/user_login/login-stage/" and method == "PATCH":
            login_stage.update(body)
            return dict(login_stage)
        if path == "/api/v3/flows/instances/?slug=default-authentication-flow":
            return {"results": [{"pk": "authentication-flow", "slug": "default-authentication-flow", "designation": "authentication"}]}
        if path == "/api/v3/flows/bindings/?target=authentication-flow":
            return {"results": [{"stage": "login-stage"}]}
        if path.startswith("/api/v3/stages/password/"):
            return {"results": [{"pk": "stage", "name": "default-authentication-password",
                                 "backends": ["authentik.sources.ldap.auth.LDAPBackend"]}]}
        if path.startswith("/api/v3/tasks/schedules/?"):
            return {"results": [{"id": task, "actor_name": "authentik.sources.ldap.tasks." + task,
                                 "uid": "authentik.sources.ldap.tasks." + task + ":lucia-ldap"}
                                for task in ("ldap_sync", "ldap_connectivity_check")]}
        if path.endswith("/sync/status/"):
            return {"is_running": False, "last_successful_sync": "2999-01-01T00:00:00Z", "last_sync_status": "info"}
        if path == "/api/v3/sources/ldap/lucia-ldap/":
            return {"pk": "source", "base_dn": BASE_DN, "server_uri": "ldaps://identity-gateway:8636",
                    "connectivity": {"__all__": {"status": "ok"}, "identity-gateway": {"status": "ok"}}}
        if path == "/api/v3/core/users/me/" or method == "POST" and path.endswith("/send/"):
            return {}
        raise AssertionError(f"Unexpected Authentik API call: {method} {path}")

    with patch.object(second, "api", side_effect=api), patch("provision.ssl.PEM_cert_to_DER_cert", return_value=b"test-ca"), contextlib.redirect_stdout(io.StringIO()):
        configure_authentik(second)
    assert sum(method == "POST" and path.endswith("/send/") for method, path, _ in requests) == 2
    source = next(body for method, path, body in requests if method == "PATCH" and path.endswith("/lucia-ldap/"))
    assert source["peer_certificate"] == "ca" and source["sni"]
    assert source["sync_users_password"] and not source["password_login_update_internal_password"]
    assert login_stage["session_duration"] == "hours=8"
    assert login_stage["remember_me_offset"] == "seconds=0" and not login_stage["terminate_other_sessions"]
    writes = [call for call in requests if call[0] != "GET"]
    with patch.object(second, "api", side_effect=api):
        configure_browser_session(second)
        login_stage["session_duration"] = "minutes=30"
        configure_browser_session(second)
    assert [call for call in requests if call[0] != "GET"] == writes, "Reruns must preserve explicit session durations."
    for responses in (
        [{"results": []}],
        [{"results": [dict(login_stage), dict(login_stage)]}],
        [{"results": [{**login_stage, "session_duration": None}]}],
        [{"results": [{**login_stage, "session_duration": "seconds=0"}]}, {"results": []}],
        [{"results": [{**login_stage, "session_duration": "seconds=0"}]},
         {"results": [{"pk": "flow", "slug": "default-authentication-flow", "designation": "authentication"}]},
         {"results": []}],
    ):
        with patch.object(second, "api", side_effect=responses) as transport:
            try:
                configure_browser_session(second)
                raise AssertionError("Missing, ambiguous, or unbound browser login was accepted.")
            except RuntimeError:
                pass
            assert all(call.args[0] == "GET" for call in transport.call_args_list)
    with patch("owner.ldap_search", return_value=[{"uid": ["zackw"]}]):
        try:
            enroll_ldap_owner(second, "zackw")
            raise AssertionError("Existing LDAP account was silently elevated.")
        except RuntimeError as error:
            assert "already exists" in str(error)
    with patch("owner.ldap_search", return_value=[]), patch.object(second, "api", return_value={"results": [{"username": "zackw"}]}):
        try:
            enroll_ldap_owner(second, "zackw")
            raise AssertionError("Existing Authentik account was silently adopted.")
        except RuntimeError as error:
            assert "already exists" in str(error)
    record = {"schema_version": 1, "username": "zackw", "uid_number": 10000, "gid_number": 10000,
              "marker": "test-owner-marker", "user_uuid": "ldap-user", "group_uuid": "ldap-group", "complete": True}
    (second.state / "owner.json").write_text(json.dumps(record))
    owner_dn = "uid=zackw,ou=Users," + BASE_DN
    ldap_user = {"entryUUID": ["ldap-user"], "description": ["test-owner-marker"], "uidNumber": ["10000"],
                 "gidNumber": ["10000"], "userPassword": ["changed-by-owner"]}
    ldap_group = {"entryUUID": ["ldap-group"], "description": ["test-owner-marker"], "gidNumber": ["10000"],
                  "uniqueMember": [owner_dn]}
    with patch("owner.ldap_search", side_effect=[[ldap_user], [ldap_group], [{"uniqueMember": [owner_dn]}]]), \
            patch.object(second, "run") as run, contextlib.redirect_stdout(io.StringIO()):
        assert enroll_ldap_owner(second, "zackw", password="old") == record
        run.assert_not_called()
    assert not (second.state / "secrets/owner-initial-password").exists(), "Completed enrollment must not recreate credentials."
    with patch("owner.ldap_search", side_effect=[[ldap_user], [ldap_group], [{"uniqueMember": []}]]), \
            patch.object(second, "run") as run:
        try:
            enroll_ldap_owner(second, "zackw", password="supplied-password-aA1!")
            raise AssertionError("Completed owner LDAP admin rights were silently restored.")
        except RuntimeError as error:
            assert "removed" in str(error)
        run.assert_not_called()
    with patch("owner.ldap_search", return_value=[{**ldap_user, "entryUUID": ["replacement-user"]}]):
        try:
            enroll_ldap_owner(second, "zackw")
            raise AssertionError("Replacement LDAP identity was adopted.")
        except RuntimeError as error:
            assert "identity drifted" in str(error)
    try:
        enroll_ldap_owner(second, "someone-else")
        raise AssertionError("Initial owner was renamed implicitly.")
    except RuntimeError as error:
        assert "migration" in str(error)
    with patch("owner.ldap_search", side_effect=[[ldap_user], [ldap_group], [{"uniqueMember": [owner_dn]}]]), patch.object(second, "api", side_effect=[
        {"pk": "source"}, {"results": [{"username": "zackw", "pk": 5}]},
        {"results": [{"source": "source", "user": 5, "identifier": "different-user"}]},
    ]) as api_call:
        try:
            owner_identity(second, record, grant=True)
            raise AssertionError("An unlinked account could receive administrator access.")
        except RuntimeError as error:
            assert "not linked" in str(error)
        assert all(call.args[0] == "GET" for call in api_call.call_args_list)
    with patch("owner.ldap_search", side_effect=[[ldap_user], [ldap_group], [{"uniqueMember": [owner_dn]}]]), patch.object(second, "api", side_effect=[
        {"pk": "source"}, {"results": [{"username": "zackw", "pk": 5, "is_active": True}]},
        {"results": [{"source": "source", "user": 5, "identifier": "ldap-user"}]},
        {"results": [{"name": "lucia-owners", "pk": "group", "users": [5], "is_superuser": False}]},
        {"results": [{"source": "source", "group": "group", "identifier": "ldap-group"}]},
    ]) as api_call:
        try:
            owner_identity(second, record, grant=True)
            raise AssertionError("Revoked Authentik administrator permission was silently restored.")
        except RuntimeError as error:
            assert "removed" in str(error)
        assert all(call.args[0] == "GET" for call in api_call.call_args_list)
    password_file = second.state / "test-login-password"
    password_file.write_text("test-only-owner-password-aA1!")
    challenge_responses = [
        {"component": "ak-stage-identification"}, {"component": "ak-stage-password"},
        {"component": "ak-stage-password", "response_errors": {"password": ["Invalid password"]}},
        {"component": "ak-stage-identification"}, {"component": "ak-stage-password"}, {"component": "xak-flow-redirect"},
        {"results": [{"current": True, "uuid": "verification-session"}]},
        {"user": {"pk": 5, "username": "zackw", "is_superuser": True}}, None,
    ]
    responses = iter(challenge_responses)
    login_requests = []

    def open_request(request, timeout):
        login_requests.append(request)
        assert "authorization" not in {key.lower() for key in request.headers}
        if request.method != "GET":
            assert {key.lower(): value for key, value in request.headers.items()}["x-authentik-csrf"] == "test-csrf"
        body = next(responses)
        return io.BytesIO(json.dumps(body).encode() if body is not None else b"")

    def build_client(https, cookies):
        cookies.cookiejar.set_cookie(http.cookiejar.Cookie(
            0, "authentik_csrf", "test-csrf", None, False, "192.0.2.10", False, False,
            "/", True, True, None, True, None, None, {}))
        return Mock(open=open_request)

    def login_audit(method, path):
        if path.endswith("/lucia-ldap/"):
            return {"pk": "ldap-source"}
        return {"results": [{"user": {"username": "zackw"}, "context": {
            "auth_method": "ldap", "auth_method_args": {"source": {"pk": "ldapsource"}},
            "http_request": {"user_agent": login_requests[-1].get_header("User-agent")},
        }}]}

    with patch("owner.urllib.request.build_opener", side_effect=build_client), \
            patch("owner.ssl.create_default_context"), patch.object(second, "api", side_effect=login_audit), \
            contextlib.redirect_stdout(io.StringIO()):
        verify_login(second, record, 5, password_file=password_file, reject_wrong_password=True)
    assert login_requests[-1].method == "DELETE" and login_requests[-1].full_url.endswith("/verification-session/")
    responses = iter(challenge_responses[3:])

    def incorrect_backend(method, path):
        result = login_audit(method, path)
        if "results" in result:
            result["results"][0]["context"]["auth_method"] = "password"
        return result

    with patch("owner.urllib.request.build_opener", side_effect=build_client), \
            patch("owner.ssl.create_default_context"), patch.object(second, "api", side_effect=incorrect_backend):
        try:
            verify_login(second, record, 5, password_file=password_file)
            raise AssertionError("Internal-password authentication was mistaken for LDAP authentication.")
        except RuntimeError as error:
            assert "different backend" in str(error)
    assert login_requests[-1].method == "DELETE", "Failed validation must still revoke its session."
    responses = iter(challenge_responses[3:])
    with patch("owner.urllib.request.build_opener", side_effect=build_client), \
            patch("owner.ssl.create_default_context"), patch.object(second, "api", side_effect=login_audit), \
            contextlib.redirect_stdout(io.StringIO()):
        verify_login(second, record, 5, password="supplied-owner-password-aA1!")
    assert any(request.data and b"supplied-owner-password-aA1!" in request.data for request in login_requests)
    responses = iter(challenge_responses[3:])
    legacy_password = "x\n"
    with patch("owner.urllib.request.build_opener", side_effect=build_client), \
            patch("owner.ssl.create_default_context"), patch.object(second, "api", side_effect=login_audit), \
            contextlib.redirect_stdout(io.StringIO()):
        verify_login(second, record, 5, password=legacy_password)
    assert any(request.data and json.loads(request.data).get("password") == legacy_password for request in login_requests)
    generated = second.state / "secrets/owner-initial-password"
    generated.write_text("pre-existing-generated-credential-do-not-delete")
    saved_record = (second.state / "owner.json").read_bytes()
    with patch("owner.owner_identity", return_value=5), patch("owner.verify_login") as login, \
            contextlib.redirect_stdout(io.StringIO()):
        finish_owner(second, record, password="supplied-owner-password-aA1!")
        login.assert_called_once_with(second, record, 5, password="supplied-owner-password-aA1!")
    assert generated.read_text() == "pre-existing-generated-credential-do-not-delete"
    assert (second.state / "owner.json").read_bytes() == saved_record
    generated.unlink()
    (second.state / "owner.json").unlink()
    supplied = "chosen-owner-password-aA1!"

    def created_owner_search(p, dn, query, *attributes):
        if not (p.state / "owner.json").exists():
            return []
        current = json.loads((p.state / "owner.json").read_text())
        if query.startswith("(uid="):
            return [{**ldap_user, "description": [current["marker"]]}]
        if query == "(cn=lucia-owners)":
            return [{**ldap_group, "description": [current["marker"]]}]
        if query == "(cn=ldap-admins)":
            return [{"uniqueMember": [owner_dn]}]
        raise AssertionError(query)

    with patch("owner.ldap_search", side_effect=created_owner_search), \
            patch.object(second, "api", return_value={"results": []}), patch.object(second, "run") as run, \
            contextlib.redirect_stdout(io.StringIO()):
        pending = enroll_ldap_owner(second, "zackw", password=supplied)
        assert pending["credential_source"] == "request" and not pending["complete"]
        credential = second.state / "secrets/owner-request-password"
        assert credential.read_text() == supplied and not generated.exists()
        assert supplied not in (second.state / "owner.json").read_text()
        assert all(supplied not in str(call.args) for call in run.call_args_list), "Passwords must use stdin, never argv."
        assert all("ldappasswd" not in call.args[0] for call in run.call_args_list), "An existing LDAP password must never be replaced."
        assert enroll_ldap_owner(second, "zackw", password=supplied) == pending
        run.reset_mock()
        try:
            enroll_ldap_owner(second, "zackw", password="different-long-password-aA1!")
            raise AssertionError("A saved enrollment password was replaced.")
        except RuntimeError as error:
            assert "original password" in str(error)
        run.assert_not_called()
        assert credential.read_text() == supplied
    with patch("owner.ldap_search", side_effect=lambda p, dn, query, *attrs:
               [{"uniqueMember": []}] if query == "(cn=ldap-admins)" else created_owner_search(p, dn, query, *attrs)), \
            patch.object(second, "run") as run:
        try:
            enroll_ldap_owner(second, "zackw", password=supplied)
            raise AssertionError("Interrupted enrollment restored explicitly removed LDAP administrator access.")
        except RuntimeError as error:
            assert "removed" in str(error)
        assert all("ldapmodify" not in call.args[0] or "uniqueMember" not in call.kwargs.get("input_text", "")
                   for call in run.call_args_list)
    with patch("owner.owner_identity", return_value=5), patch("owner.verify_login", side_effect=RuntimeError("denied")), \
            contextlib.redirect_stdout(io.StringIO()):
        try:
            finish_owner(second, pending, password=supplied)
            raise AssertionError("Failed login completed enrollment.")
        except RuntimeError:
            pass
    assert credential.read_text() == supplied and not json.loads((second.state / "owner.json").read_text())["complete"]
    generated.write_text("a-separate-pre-existing-generated-credential")
    with patch("owner.owner_identity", return_value=5), patch("owner.verify_login") as login, \
            contextlib.redirect_stdout(io.StringIO()):
        finish_owner(second, pending, password=supplied)
        login.assert_called_once_with(second, pending, 5, reject_wrong_password=True, password=supplied)
    assert not credential.exists() and generated.read_text() == "a-separate-pre-existing-generated-credential"
    assert json.loads((second.state / "owner.json").read_text())["complete"]
    assert supplied not in second.redact(supplied)
    import base64
    assert base64.b64encode(supplied.encode()).decode() not in second.redact(base64.b64encode(supplied.encode()).decode())
    fresh_args = argparse.Namespace(**vars(args))
    fresh_args.state = str(pathlib.Path(temporary) / "fresh-owner")
    fresh = Provisioner(fresh_args)
    with contextlib.redirect_stdout(io.StringIO()):
        fresh.prepare()
    with patch("owner.ldap_search") as search, patch.object(fresh, "api") as api:
        try:
            enroll_ldap_owner(fresh, "zackw", password="old")
            raise AssertionError("A new account accepted a password shorter than 14 characters.")
        except ValueError:
            pass
        search.assert_not_called()
        api.assert_not_called()
    assert not (fresh.state / "owner.json").exists()
    created = {"user": False, "group": False, "password": False}

    def new_search(p, dn, query, *attributes):
        if not (p.state / "owner.json").exists():
            return []
        current = json.loads((p.state / "owner.json").read_text())
        if query.startswith("(uid="):
            return [{**ldap_user, "description": [current["marker"]], **({} if created["password"] else {"userPassword": None})}] if created["user"] else []
        if query == "(cn=lucia-owners)":
            return [{**ldap_group, "description": [current["marker"]]}] if created["group"] else []
        if query == "(cn=ldap-admins)":
            return [{"uniqueMember": [owner_dn]}]
        raise AssertionError(query)

    def create_command(command, *, input_text=None, **kwargs):
        if "ldapadd" in command:
            created["group" if "cn=lucia-owners" in input_text else "user"] = True
        if "ldappasswd" in command:
            assert input_text == supplied and supplied not in command
            created["password"] = True
        return subprocess.CompletedProcess(command, 0, "", "")

    def new_search_without_password(*args, **kwargs):
        result = new_search(*args, **kwargs)
        for entry in result:
            if entry.get("userPassword") is None:
                entry.pop("userPassword", None)
        return result

    with patch("owner.ldap_search", side_effect=new_search_without_password), \
            patch.object(fresh, "api", return_value={"results": []}), \
            patch.object(fresh, "run", side_effect=create_command), contextlib.redirect_stdout(io.StringIO()):
        enroll_ldap_owner(fresh, "zackw", password=supplied)
    assert created == {"user": True, "group": True, "password": True}
    assert (fresh.state / "secrets/owner-request-password").read_text() == supplied
    assert not (fresh.state / "secrets/owner-initial-password").exists()
    private_cli = subprocess.run([
        sys.executable, str(ROOT / "tools/identity/provision.py"), "verify-owner-login",
        "--state", str(pathlib.Path(temporary) / "uninitialized-cli"), "--request-stdin",
    ], input=json.dumps({"schema_version": 1, "owner_password": "old"}),
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    assert private_cli.returncode == 1, "Uninitialized verification must fail before any remote access."
    assert supplied not in private_cli.stdout + private_cli.stderr
    assert "Owner password must contain" not in private_cli.stdout, "Existing sign-in must not apply the creation minimum."
    changed = argparse.Namespace(**vars(args))
    changed.host = "auth.example.test"
    try:
        settings_for(changed, second.settings)
        raise AssertionError("An unreviewed endpoint/identity migration was accepted.")
    except ValueError:
        pass
    for port in (0, -1, 65536, 636):
        changed = argparse.Namespace(**vars(args))
        changed.auth_port = port
        try:
            settings_for(changed)
            raise AssertionError("An invalid or duplicate port was accepted.")
        except ValueError:
            pass
    (second.state / "secrets" / SECRET_NAMES[0]).unlink()
    try:
        with contextlib.redirect_stdout(io.StringIO()):
            second.prepare()
        raise AssertionError("A missing persistent credential was regenerated.")
    except RuntimeError:
        pass
print("Identity checks passed: repeat-safe storage/credentials, private supplied passwords, interrupted enrollment, owner drift guards, LDAP-backed login, CSRF, and credential/session cleanup.")
