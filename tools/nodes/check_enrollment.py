#!/usr/bin/env python3
"""Offline checks: disposable test PKI, fake LDAP/step; NEVER touches native state.

Run python tools/nodes/check_enrollment.py from a private, trusted checkout.
Linux additionally exercises real descriptor-relative I/O and nonblocking locks.
Windows runs protocol/crypto/transport checks with filesystem primitives mocked.
Only an explicitly created, random directory UNDER THE CHECKOUT is removed.
"""

import argparse
import base64
import contextlib
import copy
import datetime
import hashlib
import importlib.util
import io
import json
import os
import pathlib
import shutil
import subprocess
import sys
import uuid
from types import SimpleNamespace
from unittest.mock import Mock, patch

if sys.platform != "linux":
    sys.modules["fcntl"] = SimpleNamespace(LOCK_EX=2, LOCK_NB=4, flock=Mock())
spec = importlib.util.spec_from_file_location("node_enrollment", pathlib.Path(__file__).with_name("enrollment_worker.py"))
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)

NODE = "11111111-1111-4111-8111-111111111111"
TASK = "22222222-2222-4222-8222-222222222222"
ENTRY = "33333333-3333-4333-8333-333333333333"


def rejected(action):
    try:
        action()
    except (ValueError, OSError, w.Unsupported):
        return
    raise AssertionError("Unsafe enrollment input was accepted.")


def put(path, data):
    with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), "wb") as stream:
        stream.write(data.encode() if isinstance(data, str) else data)


def openssl(*args, input_text=None, check=True):
    result = subprocess.run(["openssl", *map(str, args)], input=input_text, capture_output=True,
                            text=True, encoding="utf-8", timeout=30)
    if check and result.returncode:
        raise AssertionError("Offline OpenSSL fixture failed: " + result.stderr)
    return result


def result(text="", code=0):
    return SimpleNamespace(stdout=text, stderr="", returncode=code)


class Fake(w.Native):
    def __init__(self, state, root_pem, chain):
        super().__init__(argparse.Namespace(state=str(state)))
        self.settings = {"ldap_base_dn": w.BASE_DN, "public_host": "original.example", "ports": {"ldaps": 636}}
        self.root_pem, self.chain = root_pem, chain
        self.entry = None
        self.adds, self.signs, self.deletes = 0, 0, 0
        self.calls = []
        self.membership = False
        self.leaks_password = False
        self.crash_after_add = False
        self.verify_containers = Mock()
        self.signing_policy = Mock()

    def trust(self):
        return self.root_pem

    def load_scoped(self):
        return self.state.parent / "nodes"

    def sign(self, value, workspace, csr_hash, ca_pem):
        self.signs += 1
        self.verify_chain(self.chain, value, workspace, ca_pem, fresh=True)
        return self.chain

    def run(self, command, *, check=True, input_text=None, cwd=None):
        self.calls.append((command, input_text))
        if command[0] == "openssl":
            executed = openssl(*command[1:], input_text=input_text, check=False)
            if check and executed.returncode:
                raise w.Unsupported(w.UNSUPPORTED)
            return executed
        assert command[:3] == ["docker", "exec", "-i"]
        if "ldapdelete" in command:
            assert self.entry is not None and command[-1] == self.entry["dn"][0]
            self.entry = None
            self.deletes += 1
            return result()
        if "ldapadd" in command:
            assert self.entry is None
            self.entry = w.ldif_entries(input_text)[0]
            self.entry["entryuuid"] = [ENTRY]
            self.adds += 1
            if self.crash_after_add:
                raise RuntimeError("A secret-rich error: " + input_text)
            return result()
        assert "ldapsearch" in command
        if "-e" not in command:
            query = command[command.index("-b") + 2]
            if query.startswith("(|"):
                return result("dn: cn=unrelated,ou=Groups," + w.BASE_DN + "\n") if self.membership else result()
            if self.entry is None:
                return result()
            return result("".join(f"{k}: {v}\n" for k, values in self.entry.items() if k != "userpassword" for v in values))
        assert command[command.index("-y") + 1] == "/dev/stdin"
        stored = base64.b64decode(self.entry["userpassword"][0][6:])
        assert hashlib.sha1(input_text.encode() + stored[20:]).digest() == stored[:20]
        base = command[command.index("-b") + 1]
        if command[command.index("-s") + 1] == "one":
            text = f"dn: uid=owner,ou=Users,{w.BASE_DN}\nuid: owner\n"
        elif base.startswith("ou="):
            text = f"dn: {base}\nou: {base.split(',')[0][3:]}\n"
        else:
            text = f"dn: {base}\nuid: node-{NODE.replace('-', '')}\n"
        if self.leaks_password:
            text += "userPassword: must-not-be-readable\n"
        return result(text)


def fixture(folder):
    openssl("ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", folder / "node.key")
    openssl("req", "-new", "-key", folder / "node.key", "-subj", "/CN=" + NODE,
            "-addext", "subjectAltName=DNS:node-one", "-out", folder / "node.csr")
    public = subprocess.run(["openssl", "pkey", "-in", str(folder / "node.key"), "-pubout", "-outform", "DER"],
                            capture_output=True, check=True, timeout=30).stdout
    openssl("req", "-x509", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:P-256", "-nodes",
            "-subj", "/CN=Offline test root", "-days", "30", "-keyout", folder / "root.key",
            "-out", folder / "root.crt", "-addext", "basicConstraints=critical,CA:TRUE",
            "-addext", "keyUsage=critical,keyCertSign,cRLSign")
    openssl("req", "-new", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:P-256", "-nodes",
            "-subj", "/CN=Offline intermediate", "-keyout", folder / "int.key", "-out", folder / "int.csr")
    put(folder / "int.ext", "basicConstraints=critical,CA:TRUE,pathlen:0\nkeyUsage=critical,keyCertSign,cRLSign\n")
    openssl("x509", "-req", "-in", folder / "int.csr", "-CA", folder / "root.crt",
            "-CAkey", folder / "root.key", "-set_serial", "2", "-days", "20",
            "-extfile", folder / "int.ext", "-out", folder / "int.crt")
    put(folder / "leaf.ext", "basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature\n"
        "extendedKeyUsage=clientAuth,serverAuth\nsubjectAltName=DNS:node-one\n")
    openssl("x509", "-req", "-in", folder / "node.csr", "-CA", folder / "int.crt",
            "-CAkey", folder / "int.key", "-set_serial", "3", "-days", "7",
            "-extfile", folder / "leaf.ext", "-out", folder / "leaf.crt")
    value = {"schemaVersion": 1, "nodeId": NODE, "taskId": TASK, "hostname": "node-one",
             "publicKeyFingerprint": hashlib.sha256(public).hexdigest(),
             "csrPem": (folder / "node.csr").read_text(),
             "expiresAt": (w.now() + datetime.timedelta(minutes=10)).isoformat()}
    root = (folder / "root.crt").read_text()
    chain = (folder / "leaf.crt").read_text() + (folder / "int.crt").read_text()
    return value, root, chain


def checks(folder):
    value, root_pem, chain = fixture(folder)
    state = folder / "identity"
    state.mkdir(mode=0o700)
    (state / "certificates").mkdir(mode=0o700)
    p = Fake(state, root_pem, chain)
    onboarding = folder / "onboarding"
    onboarding.mkdir(mode=0o700)
    journal_path = onboarding / "state.json"
    journal = {
        "version": 3,
        "devices": [{"device": {"id": NODE, "taskId": TASK, "phase": "AwaitingEnrollment", "inventoryRevision": 1},
                     "sessionKeyFingerprint": value["publicKeyFingerprint"].upper(), "inventoryHash": "A" * 64}],
        "tasks": [{"task": {"id": TASK, "deviceId": NODE, "hostname": "node-one",
                           "phase": "AwaitingEnrollment", "inventoryRevision": 1},
                   "sessionKeyFingerprint": value["publicKeyFingerprint"].upper(), "inventoryHash": "A" * 64,
                   "grantRequestId": ENTRY}],
    }
    put(journal_path, json.dumps(journal))
    w.authorize_journal(p, value)
    # Journal authorization precedes every native command, even receipt replay.
    for collection, field, replacement in (
        ("devices", "id", TASK), ("devices", "taskId", NODE),
        ("devices", "phase", "Discovered"), ("devices", "phase", "Approved"),
        ("devices", "phase", "Installing"), ("devices", "phase", "Failed"),
        ("tasks", "id", NODE), ("tasks", "deviceId", TASK), ("tasks", "hostname", "other"),
        ("tasks", "phase", "Approved"), ("tasks", "phase", "GrantIssued"),
        ("tasks", "phase", "Installing"), ("tasks", "phase", "Invalidated"),
        ("tasks", "inventoryRevision", 2),
    ):
        bad = copy.deepcopy(journal)
        bad[collection][0]["device" if collection == "devices" else "task"][field] = replacement
        put(journal_path, json.dumps(bad))
        rejected(lambda: w.process(p, value, "a" * 64))
        assert not p.calls and not (state / "node-enrollment").exists()
    for collection, field, replacement in (
        ("devices", "sessionKeyFingerprint", "B" * 64),
        ("tasks", "sessionKeyFingerprint", "B" * 64),
        ("tasks", "inventoryHash", "B" * 64),
        ("tasks", "grantRequestId", None), ("tasks", "grantRequestId", str(uuid.UUID(int=0))),
    ):
        bad = copy.deepcopy(journal)
        bad[collection][0][field] = replacement
        put(journal_path, json.dumps(bad))
        rejected(lambda: w.process(p, value, "a" * 64))
        assert not p.calls
    for collection in ("devices", "tasks"):
        bad = copy.deepcopy(journal)
        bad[collection].append(copy.deepcopy(bad[collection][0]))
        put(journal_path, json.dumps(bad))
        rejected(lambda: w.authorize_journal(p, value))
        bad[collection] = []
        put(journal_path, json.dumps(bad))
        rejected(lambda: w.authorize_journal(p, value))
    journal_path.unlink()
    rejected(lambda: w.process(p, value, "a" * 64))
    put(journal_path, '{"version":3,"version":3}')
    rejected(lambda: w.authorize_journal(p, value))
    managed = copy.deepcopy(journal)
    managed["devices"][0]["device"]["phase"] = "Managed"
    managed["tasks"][0]["task"]["phase"] = "Managed"
    put(journal_path, json.dumps(managed))
    w.authorize_journal(p, value)
    put(journal_path, json.dumps(journal))
    journal_bytes = journal_path.read_bytes()
    assert w.request(value, NODE) == value
    w.validate_csr(p, value)
    assert w.inspect_certificate(chain, value) > w.now() + datetime.timedelta(days=6, hours=23)
    for changes in (
        {"schemaVersion": True}, {"schemaVersion": 2}, {"nodeId": str(uuid.UUID(int=0))},
        {"nodeId": "../escape"}, {"taskId": str(uuid.UUID(int=0))}, {"taskId": NODE.upper()},
        {"hostname": "bad.example"}, {"hostname": "-bad"}, {"hostname": "bad\nhost"},
        {"hostname": "UPPER"}, {"hostname": "x" * 64}, {"publicKeyFingerprint": "A" * 64},
        {"command": "sh"}, {"ldapUri": "ldap://attacker"}, {"privateKey": "never"},
        {"expiresAt": "2020-01-01T00:00:00Z"}, {"expiresAt": (w.now() + datetime.timedelta(hours=1)).isoformat()},
        {"expiresAt": (w.now() + datetime.timedelta(minutes=5)).replace(tzinfo=None).isoformat()},
        {"expiresAt": []}, {"csrPem": "a" * 16385},
    ):
        # Numeric-only test GUID has no upper/lower distinction.
        if changes == {"taskId": NODE.upper()}:
            changes = {"taskId": "AAAAAAAA-aaaa-4aaa-8aaa-aaaaaaaaaaaa"}
        rejected(lambda: w.request({**value, **changes}, NODE))
    rejected(lambda: w.parse_json('{"schemaVersion":1,"schemaVersion":1}'))
    rejected(lambda: w.validate_csr(p, {**value, "publicKeyFingerprint": "0" * 64}))
    rejected(lambda: w.validate_csr(p, {**value, "hostname": "different"}))
    rejected(lambda: w.validate_csr(p, {**value, "nodeId": TASK}))
    rejected(lambda: w.validate_csr(p, {**value, "csrPem": value["csrPem"] + value["csrPem"]}))
    for extension in ("basicConstraints=CA:TRUE", "extendedKeyUsage=clientAuth", "keyUsage=keyCertSign"):
        bad = openssl("req", "-new", "-key", folder / "node.key", "-subj", "/CN=" + NODE,
                      "-addext", "subjectAltName=DNS:node-one", "-addext", extension).stdout
        rejected(lambda: w.validate_csr(p, {**value, "csrPem": bad}))
    for name in ("DNS:node-one,DNS:other", "IP:127.0.0.1", "URI:https://node-one"):
        bad = openssl("req", "-new", "-key", folder / "node.key", "-subj", "/CN=" + NODE,
                      "-addext", "subjectAltName=" + name).stdout
        rejected(lambda: w.validate_csr(p, {**value, "csrPem": bad}))
    bad = openssl("req", "-new", "-key", folder / "node.key", "-subj", "/CN=" + NODE + "/O=extra",
                  "-addext", "subjectAltName=DNS:node-one").stdout
    rejected(lambda: w.validate_csr(p, {**value, "csrPem": bad}))
    der = bytearray(w.pem_blocks(value["csrPem"], "CERTIFICATE REQUEST")[0])
    der[-1] ^= 1
    bad = "-----BEGIN CERTIFICATE REQUEST-----\n" + base64.b64encode(der).decode() + "\n-----END CERTIFICATE REQUEST-----\n"
    rejected(lambda: w.validate_csr(p, {**value, "csrPem": bad}))
    rejected(lambda: w.tlvs(b"\x30\x80"))  # BER/indefinite and non-minimal lengths
    rejected(lambda: w.tlvs(b"\x30\x81\x01\x00"))

    # Certificate extensions are checked independently of successful chain validation.
    for extension in ("basicConstraints=critical,CA:TRUE", "keyUsage=critical,keyCertSign",
                      "extendedKeyUsage=serverAuth", "extendedKeyUsage=anyExtendedKeyUsage",
                      "subjectAltName=DNS:node-one,DNS:other"):
        lines = (folder / "leaf.ext").read_text().splitlines()
        lines = [extension if line.split("=")[0] == extension.split("=")[0] else line for line in lines]
        put(folder / "bad.ext", "\n".join(lines) + "\n")
        bad = openssl("x509", "-req", "-in", folder / "node.csr", "-CA", folder / "int.crt",
                      "-CAkey", folder / "int.key", "-set_serial", "4", "-days", "1",
                      "-extfile", folder / "bad.ext").stdout + (folder / "int.crt").read_text()
        rejected(lambda: w.inspect_certificate(bad, value))
    rejected(lambda: w.inspect_certificate((folder / "leaf.crt").read_text(), value))
    overlong = openssl("x509", "-req", "-in", folder / "node.csr", "-CA", folder / "int.crt",
                       "-CAkey", folder / "int.key", "-set_serial", "6", "-days", "9",
                       "-extfile", folder / "leaf.ext").stdout + (folder / "int.crt").read_text()
    rejected(lambda: w.inspect_certificate(overlong, value))

    fingerprint = hashlib.sha256(json.dumps(value).encode()).hexdigest()
    configuration = w.process(p, value, fingerprint)
    assert p.adds == p.signs == 1
    assert configuration["ldapUri"] == "ldaps://original.example:636"
    assert configuration["ldapBindDn"] == f"uid=node-{NODE.replace('-', '')},ou=Services,{w.BASE_DN}"
    assert set(configuration) == {"hostname", "certificatePem", "caPem", "ldapUri", "ldapBaseDn",
                                  "ldapBindDn", "ldapBindPassword", "ownerGroupDn"}
    assert len(configuration["ldapBindPassword"]) == 48
    assert w.process(p, value, fingerprint) == configuration
    assert p.adds == p.signs == 1
    revoked = copy.deepcopy(journal)
    revoked["devices"][0]["device"]["phase"] = "Failed"
    put(journal_path, json.dumps(revoked))
    rejected(lambda: w.process(p, value, fingerprint))
    put(journal_path, journal_bytes)
    assert w.process(p, value, "b" * 64) == configuration  # fresh envelope, same owned CSR
    assert p.adds == p.signs == 1
    # Same subject/key with a freshly signed CSR is a replacement, not a renewal.
    replacement = openssl("req", "-new", "-key", folder / "node.key", "-subj", "/CN=" + NODE,
                          "-addext", "subjectAltName=DNS:node-one").stdout
    rejected(lambda: w.process(p, {**value, "csrPem": replacement}, "c" * 64))
    rejected(lambda: w.process(p, {**value, "taskId": NODE}, "c" * 64))
    p.entry["description"] = ["Not owned"]
    rejected(lambda: w.process(p, value, "c" * 64))
    p.entry["description"] = ["Lucia managed node " + NODE]
    p.entry["entryuuid"] = [TASK]
    rejected(lambda: w.process(p, value, "c" * 64))
    p.entry["entryuuid"] = [ENTRY]
    p.membership = True
    rejected(lambda: w.process(p, value, "c" * 64))
    p.membership = False
    p.leaks_password = True
    rejected(lambda: w.process(p, value, "c" * 64))
    p.leaks_password = False
    for command, stdin in p.calls:
        assert configuration["ldapBindPassword"] not in " ".join(command)
        assert not any(tool in command for tool in ("sh", "bash", "ldapmodify", "ldappasswd", "rm"))
        if "ldapadd" in command:
            assert "userPassword: {SSHA}" in stdin and configuration["ldapBindPassword"] not in stdin

    # Partial LDAP create is resumed with the originally saved password, never reset.
    other_state = folder / "partial"
    other_state.mkdir(mode=0o700)
    (other_state / "certificates").mkdir(mode=0o700)
    partial = Fake(other_state, root_pem, chain)
    partial.crash_after_add = True
    try:
        w.process(partial, value, fingerprint)
        raise AssertionError("Expected partial failure.")
    except RuntimeError:
        pass
    saved = w.read_json(other_state / "node-enrollment" / (NODE + ".json"))
    assert partial.adds == 1 and saved["ldapEntryUuid"] is None
    partial.crash_after_add = False
    assert w.process(partial, value, fingerprint)["ldapBindPassword"] == saved["ldapBindPassword"]
    assert partial.adds == partial.signs == 1
    assert journal_path.read_bytes() == journal_bytes

    # Real 7-day test certificates with explicit start/end dates exercise the 6-day renewal threshold.
    receipt_path = state / "node-enrollment" / (NODE + ".json")
    saved = w.read_json(receipt_path)
    put(folder / "offline-ca.cnf",
        "[ca]\ndefault_ca=local\n[local]\n"
        "database=offline-index\nserial=offline-serial\nnew_certs_dir=.\n"
        "certificate=int.crt\nprivate_key=int.key\ndefault_md=sha256\npolicy=identity\n"
        "[identity]\ncommonName=supplied\n")
    put(folder / "offline-index", "")
    put(folder / "offline-serial", "10\n")
    def remaining(hours):
        start = (w.now() - (w.LIFETIME - datetime.timedelta(hours=hours))).strftime("%Y%m%d%H%M%SZ")
        end = (w.now() + datetime.timedelta(hours=hours)).strftime("%Y%m%d%H%M%SZ")
        issued = subprocess.run(
            ["openssl", "ca", "-batch", "-notext", "-config", "offline-ca.cnf", "-in", "node.csr",
             "-extfile", "leaf.ext", "-startdate", start, "-enddate", end],
            cwd=folder, capture_output=True, text=True, timeout=30)
        assert issued.returncode == 0, "Offline dated certificate fixture failed."
        put(folder / "offline-index", "")
        return issued.stdout + (folder / "int.crt").read_text()
    six = remaining(6 * 24 + 0.5)
    saved["certificatePem"] = six
    w.write_file(receipt_path, json.dumps(saved))
    assert w.process(p, value, "e" * 64)["certificatePem"] == six and p.signs == 1
    short = remaining(6 * 24 - 0.5)
    workspace = state / "certificates" / "node-enrollment" / NODE
    rejected(lambda: p.verify_chain(short, value, workspace, root_pem, fresh=True))
    saved["certificatePem"] = short
    w.write_file(receipt_path, json.dumps(saved))
    assert w.process(p, value, "d" * 64)["certificatePem"] == chain and p.signs == 2
    assert w.process(p, value, "d" * 64)["certificatePem"] == chain and p.signs == 2

    # An offline Managed node can renew an expired OWNED chain, not a substituted
    # key/CSR or a tampered CA signature. OpenSSL verifies at the old expiry - 1s.
    before_expiry_test = w.read_json(receipt_path)
    managed = copy.deepcopy(journal)
    managed["devices"][0]["device"]["phase"] = "Managed"
    managed["devices"][0]["scope"] = 2
    managed["tasks"][0]["task"]["phase"] = "Managed"
    put(journal_path, json.dumps(managed))
    future = w.now() + datetime.timedelta(days=8)
    with patch.object(w, "now", return_value=future):
        renewal = {**value, "expiresAt": (future + datetime.timedelta(minutes=10)).isoformat()}
        w.request(renewal, NODE)
        renewed_chain = remaining(7 * 24)
        p.chain = renewed_chain
        renewal_hash = hashlib.sha256(json.dumps(renewal).encode()).hexdigest()
        expired = p.verify_chain(chain, renewal, workspace, root_pem)
        assert expired < future
        rejected(lambda: p.verify_chain(chain, renewal, workspace, root_pem, fresh=True))
        verifications = [args for args, _ in p.calls if args[:2] == ["openssl", "verify"]]
        assert any(args[args.index("-attime") + 1] == str(int(expired.timestamp()) - 1) for args in verifications)
        rejected(lambda: w.process(p, {**renewal, "publicKeyFingerprint": "0" * 64}, renewal_hash))
        rejected(lambda: w.process(p, {**renewal, "csrPem": replacement}, renewal_hash))
        corrupted = bytearray(w.pem_blocks(chain, "CERTIFICATE")[0])
        corrupted[-1] ^= 1
        bad_chain = ("-----BEGIN CERTIFICATE-----\n" + base64.b64encode(corrupted).decode()
                     + "\n-----END CERTIFICATE-----\n" + (folder / "int.crt").read_text())
        corrupted_receipt = {**before_expiry_test, "certificatePem": bad_chain}
        w.write_file(receipt_path, json.dumps(corrupted_receipt))
        rejected(lambda: w.process(p, renewal, renewal_hash))
        assert p.signs == 2
        w.write_file(receipt_path, json.dumps(before_expiry_test))
        renewed = w.process(p, renewal, renewal_hash)
        assert renewed["certificatePem"] == renewed_chain and p.signs == 3 and p.adds == 1
        assert future + w.RENEW_BELOW < w.inspect_certificate(renewed_chain, renewal) <= future + w.MAX_LIFETIME
        assert w.process(p, renewal, renewal_hash) == renewed and p.signs == 3
    p.chain = chain
    w.write_file(receipt_path, json.dumps(before_expiry_test))
    put(journal_path, journal_bytes)

    # Sign transport uses only native mount paths, fixed provisioner, never a private key.
    workspace = state / "certificates" / "node-enrollment" / NODE
    native = w.Native(argparse.Namespace(state=str(state)))
    native.signing_policy = Mock()
    native.verify_chain = Mock(return_value=w.now() + w.LIFETIME)
    csr_hash = hashlib.sha256(value["csrPem"].encode()).hexdigest()
    def sign_command(command, **kwargs):
        assert command == ["docker", "exec", "lucia-identity-ca", "step", "ca", "sign",
                           "/certificates/node-enrollment/" + NODE + "/" + csr_hash + ".csr",
                           "/certificates/node-enrollment/" + NODE + "/" + csr_hash + ".crt",
                           "--provisioner", "lucia-installer", "--provisioner-password-file",
                           "/run/secrets/ca-password", "--ca-url", "https://localhost:9000",
                           "--root", "/home/step/certs/root_ca.crt", "--not-after", "168h", "--force"]
        w.write_file(workspace / (csr_hash + ".crt"), chain)
        return result()
    native.run = Mock(side_effect=sign_command)
    assert native.sign(value, workspace, csr_hash, root_pem) == chain
    assert native.sign(value, workspace, csr_hash, root_pem) == chain and native.run.call_count == 1

    # A 168h claim is required; the 24h default, narrower limits and custom templates are refused.
    native.signing_policy = w.Native.signing_policy.__get__(native)
    config = {"authority": {"provisioners": [{"type": "JWK", "name": "lucia-installer"}]}}
    with patch.object(w, "safe_bytes", return_value=json.dumps(config).encode()):
        rejected(native.signing_policy)
    config["authority"]["claims"] = {"maxTLSCertDuration": "24h"}
    with patch.object(w, "safe_bytes", return_value=json.dumps(config).encode()):
        rejected(native.signing_policy)
    config["authority"]["claims"] = {"maxTLSCertDuration": "168h"}
    with patch.object(w, "safe_bytes", return_value=json.dumps(config).encode()):
        native.signing_policy()
    config["authority"]["claims"] = {"maxTLSCertDuration": "167h"}
    with patch.object(w, "safe_bytes", return_value=json.dumps(config).encode()):
        rejected(native.signing_policy)
    config["authority"]["claims"] = {"maxTLSCertDuration": "168h"}
    config["authority"]["provisioners"][0]["options"] = {"x509": {"template": "unsafe"}}
    with patch.object(w, "safe_bytes", return_value=json.dumps(config).encode()):
        rejected(native.signing_policy)

    # Endpoint and data paths belong to native settings, never to a request.
    settings = {"schema_version": 1, "certificate_mode": "private-ca", "ldap_base_dn": w.BASE_DN,
                "public_host": "original.example", "ports": {"authentik": 9443, "ldaps": 636, "ca": 9444},
                "uid": 1000, "gid": 1000}
    expected = pathlib.Path.home() / ".local" / "share" / "lucia" / "host" / "data"
    host = {"data_directory": str(expected)}
    with patch.object(w.os, "getuid", return_value=1000, create=True), \
            patch.object(w.os, "getgid", return_value=1000, create=True), \
            patch.object(w, "read_json", side_effect=lambda path: host if path.name == "host-settings.json" else settings):
        assert native.load_scoped() == expected / "nodes"
        for changes in ({"public_host": "https://attacker"}, {"public_host": "host:636"},
                        {"public_host": "host\nargument"}, {"ldap_base_dn": "dc=arbitrary"},
                        {"uid": 0}, {"ports": {"authentik": 9443, "ldaps": True, "ca": 9444}}):
            original = settings.copy()
            settings.update(changes)
            rejected(native.load_scoped)
            settings.clear()
            settings.update(original)
        host["data_directory"] = str(folder / "arbitrary")
        rejected(native.load_scoped)

    # Install only the user unit and restart after updates; never enable linger.
    home = folder / "fake-home"
    stable = home / ".local" / "share" / "lucia" / "bootstrap" / "app" / "tools" / "nodes" / "enrollment_worker.py"
    with patch.object(w, "platform_check", return_value=stable), \
            patch.object(w.pathlib.Path, "home", return_value=home), \
            patch.object(w.os, "getuid", return_value=1000, create=True), \
            patch.object(w.subprocess, "run", return_value=result("yes\n")) as execute:
        w.install()
        unit = home / ".config" / "systemd" / "user" / "lucia-node-enrollment.service"
        assert "UMask=0077" in unit.read_text() and "NoNewPrivileges=yes" in unit.read_text()
        commands = [call.args[0] for call in execute.call_args_list]
        assert commands == [
            ["loginctl", "show-user", "1000", "-p", "Linger", "--value"],
            ["systemctl", "--user", "daemon-reload"],
            ["systemctl", "--user", "enable", "lucia-node-enrollment.service"],
            ["systemctl", "--user", "restart", "lucia-node-enrollment.service"],
        ]
        execute.reset_mock()
        execute.return_value = result("no\n")
        try:
            w.install()
            raise AssertionError("Installation without approved linger succeeded.")
        except RuntimeError:
            pass
        assert execute.call_count == 1

    # Runner never logs command stderr/LDIF, enforces deadlines and output bounds.
    native = w.Native(argparse.Namespace(state=str(state)))
    secret = configuration["ldapBindPassword"]
    with patch.object(w.subprocess, "run", return_value=SimpleNamespace(returncode=1, stdout=secret, stderr=secret)) as execute:
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            rejected(lambda: native.run(["openssl", "req", "-verify"]))
        assert secret not in output.getvalue()
        assert execute.call_args.kwargs["timeout"] <= 30
    native.deadline = 1
    try:
        native.run(["openssl", "req", "-verify"])
        raise AssertionError("An expired operation launched a command.")
    except TimeoutError:
        pass

    # Queue responses survive restart; errors never contain secret-bearing exceptions.
    directory = folder / "nodes"
    directory.mkdir(mode=0o700)
    for name in ("enrollment-requests", "enrollment-responses"):
        (directory / name).mkdir(mode=0o700)
    path = directory / "enrollment-requests" / (NODE + ".json")
    put(path, json.dumps(value))
    p.load_scoped = Mock(return_value=directory)
    response = directory / "enrollment-responses" / path.name
    with patch.object(w, "lease", side_effect=lambda _: contextlib.nullcontext()), \
            patch.object(w, "process", return_value=configuration) as process:
        w.handle(p, directory, path)
        first = response.read_bytes()
        w.handle(p, directory, path)
        assert process.call_count == 1 and response.read_bytes() == first
        assert json.loads(first)["requestHash"] == hashlib.sha256(path.read_bytes()).hexdigest()
        response.unlink()
        process.side_effect = RuntimeError(secret)
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            w.handle(p, directory, path)
        assert secret not in response.read_text() + output.getvalue()
        assert json.loads(response.read_text())["configuration"] is None
        response.unlink()
    with patch.object(w, "lease", side_effect=BlockingIOError()):
        w.handle(p, directory, path)
        assert not response.exists()
    put(path, '{"schemaVersion":1,"schemaVersion":1}')
    output = io.StringIO()
    with contextlib.redirect_stdout(output):
        w.handle(p, directory, path)
    assert json.loads(response.read_text())["success"] is False

    # A removed machine's reader and receipt go only once Lucia's journal and queues no longer hold it.
    (directory / "removals").mkdir(mode=0o700)
    (directory / "identities").mkdir(mode=0o700)
    removal = directory / "removals" / (NODE + ".json")
    receipt = state / "node-enrollment" / (NODE + ".json")
    node_certificates = state / "certificates" / "node-enrollment" / NODE
    assert p.entry is not None and receipt.exists() and node_certificates.exists()
    with patch.object(w, "lease", side_effect=lambda _: contextlib.nullcontext()):
        put(removal, json.dumps({"schemaVersion": 1, "nodeId": NODE}))
        rejected(lambda: w.remove(p, directory, removal))  # still in the journal, and its request is queued
        path.unlink()
        rejected(lambda: w.remove(p, directory, removal))
        put(journal_path, json.dumps({**journal, "devices": [], "tasks": []}))
        put(directory / "identities" / removal.name, "{}")
        rejected(lambda: w.remove(p, directory, removal))
        (directory / "identities" / removal.name).unlink()
        for bad in ({"schemaVersion": 1, "nodeId": TASK}, {"schemaVersion": 1, "nodeId": NODE, "dn": "uid=admin"}):
            put(removal, json.dumps(bad))
            rejected(lambda: w.remove(p, directory, removal))
        p.entry["description"] = ["Not owned"]
        put(removal, json.dumps({"schemaVersion": 1, "nodeId": NODE}))
        rejected(lambda: w.remove(p, directory, removal))
        assert p.deletes == 0 and receipt.exists() and node_certificates.exists()
        p.entry["description"] = ["Lucia managed node " + NODE]
        w.remove(p, directory, removal)
        assert p.entry is None and p.deletes == 1 and not receipt.exists() and not node_certificates.exists() and not removal.exists()
        put(removal, json.dumps({"schemaVersion": 1, "nodeId": NODE}))
        w.remove(p, directory, removal)  # already gone: just acknowledged
        assert p.deletes == 1 and not removal.exists()
    put(journal_path, journal_bytes)
    app_checks(folder, root_pem)
    if sys.platform == "linux":
        filesystem_checks(folder, directory, p, value)
    else:
        print("SKIP: Linux dirfd ownership/symlink/FIFO/lock/queue-bound checks (run on Linux).")


def app_checks(folder, root_pem):
    """App certificates: request shape, the host's stack and node records as authority, CSR and chain rules, transport."""
    base = folder / "app"
    for name in ("", "identity", "identity/certificates", "nodes", "nodes/identities", "nodes/app-certificate-requests",
                 "nodes/app-certificate-responses", "stacks", "domains"):
        (base / name).mkdir(mode=0o700, exist_ok=True)
    address = "192.168.1.20"
    openssl("ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", base / "app.key")
    assert w.APP_TEMPLATES == frozenset({"crowdsec", "observability"})
    name = "crowdsec.lab.example.com"
    openssl("req", "-new", "-key", base / "app.key", "-subj", "/CN=lucia-app-crowdsec",
            "-addext", "subjectAltName=DNS:" + name, "-out", base / "app.csr")
    openssl("req", "-new", "-key", base / "app.key", "-subj", "/CN=lucia-app-crowdsec",
            "-addext", "subjectAltName=IP:" + address + ",DNS:" + name, "-out", base / "ip.csr")
    openssl("req", "-new", "-key", base / "app.key", "-subj", "/CN=lucia-app-crowdsec",
            "-addext", "subjectAltName=DNS:lucia.lab.example.com", "-out", base / "dns.csr")
    openssl("req", "-new", "-key", base / "app.key", "-subj", "/CN=lucia-app-crowdsec",
            "-addext", "subjectAltName=DNS:" + name + ",DNS:extra.lab.example.com", "-out", base / "extra.csr")
    openssl("req", "-new", "-key", base / "app.key", "-subj", "/CN=" + NODE,
            "-addext", "subjectAltName=DNS:" + name, "-out", base / "node.csr")
    put(base / "app.ext", "basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature\n"
        "extendedKeyUsage=serverAuth,clientAuth\nsubjectAltName=DNS:" + name + "\n")
    put(base / "ip.ext", "basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature\n"
        "extendedKeyUsage=serverAuth,clientAuth\nsubjectAltName=IP:" + address + ",DNS:" + name + "\n")
    openssl("x509", "-req", "-in", base / "ip.csr", "-CA", folder / "int.crt", "-CAkey", folder / "int.key",
            "-set_serial", "41", "-days", "7", "-extfile", base / "ip.ext", "-out", base / "ip.crt")
    openssl("x509", "-req", "-in", base / "app.csr", "-CA", folder / "int.crt", "-CAkey", folder / "int.key",
            "-set_serial", "40", "-days", "7", "-extfile", base / "app.ext", "-out", base / "app.crt")
    chain = (base / "app.crt").read_text() + (folder / "int.crt").read_text()
    value = {"schemaVersion": 1, "stack": "crowdsec", "nodeId": NODE, "address": address,
             "csrPem": (base / "app.csr").read_text(), "expiresAt": (w.now() + datetime.timedelta(minutes=10)).isoformat()}
    assert w.app_request(copy.deepcopy(value), "crowdsec") == value
    for changes, stack in (({"stack": "other"}, "crowdsec"), ({"address": "203.0.113.7"}, "crowdsec"), ({"address": "8.8.8.8"}, "crowdsec"), ({"address": "100.64.0.1"}, "crowdsec"),
                           ({"address": "127.0.0.1"}, "crowdsec"), ({"address": "169.254.1.1"}, "crowdsec"),
                           ({"address": "fd00::1"}, "crowdsec"), ({"address": "192.168.001.020"}, "crowdsec"),
                           ({"nodeId": "not-a-guid"}, "crowdsec"), ({"schemaVersion": 2}, "crowdsec"),
                           ({"expiresAt": (w.now() + datetime.timedelta(hours=2)).isoformat()}, "crowdsec"),
                           ({"extra": True}, "crowdsec"), ({"stack": "Bad_Name"}, "Bad_Name")):
        rejected(lambda: w.app_request({**value, **changes}, stack))

    p = Fake(base / "identity", root_pem, chain)
    stack = {"name": "crowdsec", "node": "node-one", "manifest": {"schemaVersion": 1, "placement": {},
             "template": {"id": "crowdsec", "version": 1}, "names": ["crowdsec"]}, "move": None, "restore": None}
    identity = {"nodeId": NODE, "hostname": "node-one", "address": address}
    profile = {"schemaVersion": 1, "profileId": ENTRY}
    job = {"id": ENTRY, "plan": {"naming": {"domain": "example.com", "namespace": "lab.example.com",
           "localHostnames": ["lucia.lab.example.com", "auth.lab.example.com", "spark.lab.example.com"]}}}

    def records(stacks=None, node=None, active=None, workflow=None):
        put(base / "stacks" / "stacks.json", json.dumps({"schemaVersion": 1, "stacks": [stack] if stacks is None else stacks}))
        put(base / "nodes" / "identities" / (NODE + ".json"), json.dumps(identity if node is None else node))
        put(base / "domains" / "active.json", json.dumps(profile if active is None else active))
        put(base / "domains" / "workflow.json", json.dumps({"version": 1, "plan": None, "job": job if workflow is None else workflow}))

    def named(*names):
        return [{**stack, "manifest": {**stack["manifest"], "names": list(names)}}]

    records()
    assert w.authorize_app(p, value) == name
    records(named("guard"))
    assert w.authorize_app(p, value) == "guard.lab.example.com"
    # Observability's alert intake for CrowdSec, under its own name from the stack's settings.
    records([{**stack, "manifest": {**stack["manifest"], "template": {"id": "observability", "version": 13}, "names": ["otlp-alerts"]}}])
    assert w.authorize_app(p, value) == "otlp-alerts.lab.example.com"
    naming = job["plan"]["naming"]
    for stacks, node, active, workflow in (
            ([], None, None, None), ([{**stack, "manifest": {**stack["manifest"], "template": {"id": "plex"}}}], None, None, None),
            ([{**stack, "move": {"from": "node-one", "to": "node-two"}}], None, None, None), ([{**stack, "restore": {"id": ENTRY}}], None, None, None),
            ([{**stack, "node": "node-two"}], None, None, None), ([stack, stack], None, None, None), (None, {**identity, "address": "192.168.1.21"}, None, None),
            (None, {**identity, "hostname": "node-two"}, None, None), (None, {**identity, "nodeId": TASK}, None, None),
            # The name comes from the stack's own manifest and the active domain, never from the request.
            (named(), None, None, None), (named("crowdsec", "other"), None, None, None), (named("Bad_Label"), None, None, None),
            (named("lucia"), None, None, None), (named("node-one"), None, None, None), (named("a.b"), None, None, None),
            ([{**stack, "manifest": {k: v for k, v in stack["manifest"].items() if k != "names"}}], None, None, None),
            (None, None, {**profile, "profileId": TASK}, None), (None, None, None, {**job, "id": TASK}),
            (None, None, None, {**job, "plan": {"naming": {**naming, "namespace": "Lab.Example.com"}}}),
            (None, None, None, {**job, "plan": {"naming": {**naming, "namespace": "localhost"}}}),
            (None, None, None, {**job, "plan": {"naming": {**naming, "localHostnames": None}}})):
        records(stacks, node, active, workflow)
        rejected(lambda: w.authorize_app(p, value))
    records(None, None, None, {**job, "plan": {"naming": {**naming, "localHostnames": [name]}}})
    rejected(lambda: w.authorize_app(p, value))
    (base / "domains" / "active.json").unlink()
    rejected(lambda: w.authorize_app(p, value))
    records()
    p.settings["public_host"] = address
    rejected(lambda: w.authorize_app(p, value))
    p.settings["public_host"] = "original.example"

    spki = w.validate_app_csr(p, value, name)
    for csr in ("dns.csr", "node.csr", "ip.csr", "extra.csr"):
        rejected(lambda: w.validate_app_csr(p, {**value, "csrPem": (base / csr).read_text()}, name))
    rejected(lambda: w.validate_app_csr(p, value, "other.lab.example.com"))
    assert w.inspect_app_certificate(chain, value, spki, name) <= w.now() + w.MAX_LIFETIME
    rejected(lambda: w.inspect_app_certificate(chain, value, spki, "other.lab.example.com"))
    rejected(lambda: w.inspect_app_certificate((base / "ip.crt").read_text() + (folder / "int.crt").read_text(), value, spki, name))
    rejected(lambda: w.inspect_app_certificate(chain, value, spki[:-1] + bytes([spki[-1] ^ 1]), name))
    rejected(lambda: w.inspect_app_certificate((base / "app.crt").read_text(), value, spki, name))

    # Signing uses the fixed provisioner and mount paths, verifies the chain for the name, and leaves no files behind.
    csr_hash = hashlib.sha256(value["csrPem"].encode()).hexdigest()
    workspace = base / "identity" / "certificates" / "app-certificates" / "crowdsec"
    commands = []

    def native(command, *, check=True, input_text=None, cwd=None):
        commands.append(command)
        if command[0] == "openssl":
            executed = openssl(*command[1:], input_text=input_text, check=False)
            if check and executed.returncode:
                raise w.Unsupported(w.UNSUPPORTED)
            return executed
        assert command == ["docker", "exec", "lucia-identity-ca", "step", "ca", "sign",
                           "/certificates/app-certificates/crowdsec/" + csr_hash + ".csr",
                           "/certificates/app-certificates/crowdsec/" + csr_hash + ".crt",
                           "--provisioner", "lucia-installer", "--provisioner-password-file", "/run/secrets/ca-password",
                           "--ca-url", "https://localhost:9000", "--root", "/home/step/certs/root_ca.crt",
                           "--not-after", "168h", "--force"]
        w.write_file(workspace / (csr_hash + ".crt"), p.chain)
        return result()

    p.run = native
    assert w.process_app(p, value) == chain
    assert any(command[:2] == ["openssl", "verify"] and command[command.index("-verify_hostname") + 1] == name for command in commands)
    assert not list(workspace.iterdir())
    p.chain = (folder / "leaf.crt").read_text() + (folder / "int.crt").read_text()
    rejected(lambda: w.process_app(p, value))
    p.chain = chain

    # The queue answers once per request snapshot; a refusal names no internals.
    path = base / "nodes" / "app-certificate-requests" / "crowdsec.json"
    response = base / "nodes" / "app-certificate-responses" / "crowdsec.json"
    put(path, json.dumps(value))
    p.load_scoped = Mock(return_value=base / "nodes")
    with patch.object(w, "lease", side_effect=lambda _: contextlib.nullcontext()):
        w.handle_app(p, base / "nodes", path)
        answer = json.loads(response.read_text())
        assert answer["success"] is True and answer["certificatePem"] == chain and answer["stack"] == "crowdsec"
        assert answer["requestHash"] == hashlib.sha256(path.read_bytes()).hexdigest()
        signed = len(commands)
        w.handle_app(p, base / "nodes", path)
        assert len(commands) == signed
        records([])
        put(path, json.dumps({**value, "expiresAt": (w.now() + datetime.timedelta(minutes=9)).isoformat()}))
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            w.handle_app(p, base / "nodes", path)
        answer = json.loads(response.read_text())
        assert answer["success"] is False and answer["certificatePem"] is None and answer["message"] == w.APP_FAILURE
        assert "app-certificate-failed" in output.getvalue()
    assert [item.name for item in w.app_candidates(base / "nodes" / "app-certificate-requests")] == ["crowdsec.json"]
    put(base / "nodes" / "app-certificate-requests" / "Bad.json", "{}")
    assert [item.name for item in w.app_candidates(base / "nodes" / "app-certificate-requests")] == ["crowdsec.json"]


def filesystem_checks(folder, directory, p, value):
    private = folder / "private.json"
    w.write_file(private, "{}")
    assert stat_mode(private) == 0o600 and w.safe_bytes(private) == b"{}"
    private.chmod(0o644)
    rejected(lambda: w.safe_bytes(private))
    private.chmod(0o600)
    for name in ("linked.json", "hardlink.json", "fifo.json"):
        path = folder / name
        if name == "linked.json":
            path.symlink_to(private)
        elif name == "hardlink.json":
            os.link(private, path)
        else:
            os.mkfifo(path, 0o600)
        rejected(lambda: w.safe_bytes(path))
        path.unlink()
    linked = folder / "linked-parent"
    linked.symlink_to(directory, target_is_directory=True)
    rejected(lambda: w.write_file(linked / "escape.json", "{}"))
    private.write_bytes(b"x" * 32769)
    rejected(lambda: w.safe_bytes(private))
    journal = folder / "onboarding" / "state.json"
    original = journal.read_bytes()
    journal.chmod(0o644)
    rejected(lambda: w.authorize_journal(p, value))
    journal.chmod(0o600)
    journal.unlink()
    journal.symlink_to(private)
    rejected(lambda: w.authorize_journal(p, value))
    journal.unlink()
    put(journal, original)
    with journal.open("r+b") as output:
        output.truncate(32 * 1024 * 1024 + 1)
    rejected(lambda: w.authorize_journal(p, value))
    put(journal, original)
    lock = folder / ".lock"
    with w.lease(lock):
        try:
            with w.lease(lock):
                raise AssertionError("A held lease was acquired twice.")
        except BlockingIOError:
            pass
    for index in range(129):
        put(directory / "enrollment-requests" / f"noise-{index}", "")
    rejected(lambda: w.candidates(directory / "enrollment-requests"))


def stat_mode(path):
    return path.stat().st_mode & 0o777


if __name__ == "__main__":
    # Do not use OS temporary directories: all fixtures are confined to this checkout.
    folder = pathlib.Path.cwd() / (".node-enrollment-check-" + uuid.uuid4().hex)
    folder.mkdir(mode=0o700)
    try:
        with contextlib.ExitStack() as stack:
            if sys.platform != "linux":
                stack.enter_context(patch.object(w, "private_directory", side_effect=lambda p: p.mkdir(mode=0o700, exist_ok=True)))
                stack.enter_context(patch.object(w, "write_file", side_effect=put))
                stack.enter_context(patch.object(w, "read_json", side_effect=lambda p: w.parse_json(p.read_bytes())))
                stack.enter_context(patch.object(w, "regular", side_effect=lambda p, maximum=32768: p.read_bytes()))
                stack.enter_context(patch.object(w, "safe_bytes", side_effect=lambda p, maximum=32768, **kwargs: p.read_bytes()))
            checks(folder)
        print("PASS: scoped node enrollment protocol, PKI, LDAP ownership, renewal, recovery, dedup, redaction and app certificates. No live services.")
    finally:
        shutil.rmtree(folder)
