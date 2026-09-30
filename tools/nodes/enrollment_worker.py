#!/usr/bin/env python3
"""Fixed-purpose, setup-user CA/LDAP bridge; never run inside the web container.

install|run, from ~/.local/share/lucia/bootstrap/app/tools/nodes/enrollment_worker.py.
Private 0600 requests in host/data/nodes/enrollment-requests/<nodeId>.json have
EXACT keys: schemaVersion (integer 1), nodeId (nonzero lowercase GUID D), taskId
(nonzero lowercase GUID D), hostname (one lowercase DNS label),
publicKeyFingerprint (lowercase SHA256 of P-256 DER SPKI), csrPem (PKCS#10 PEM),
expiresAt (UTC ISO8601, future, at most 15 minutes). CN is nodeId; the ONLY
requested extension is one DNS SAN equal to hostname. No other CSR attributes.
Before native processing, the private, bounded host/data/onboarding/state.json
journal must independently authorize the same node, task, fingerprint, hostname
and inventory with a nonzero grant ID and AwaitingEnrollment/Managed phases.
The journal is read-only; a CSR queue file alone is never installation authority.

Responses in enrollment-responses/<nodeId>.json acknowledge the immutable input
byte hash. configuration contains certificatePem (leaf + intermediate), caPem,
hostname, ldapUri, ldapBaseDn, ldapBindDn, ldapBindPassword, ownerGroupDn. These
are PRIVATE node-delivery artifacts, never public assets or UI payloads.
Fresh renewal requests must retain taskId, hostname, key and identical CSR bytes.
Certificates are issued for 24h and reused until <6h remain. Receipts survive partial operations;
there is no automatic deletion, password reset, trust rotation or migration.

Prerequisites: existing owned identity stack, approved user linger, default
lucia-installer JWK leaf template and its existing default 24h maximum lifetime.
step's default leaf template supplies digitalSignature and clientAuth/serverAuth:
github.com/smallstep/crypto/blob/master/x509util/templates.go (DefaultLeafTemplate).
No CA policy is changed here. LDAP ACLs must permit authenticated directory reads
but not userPassword reads. Unsupported policy fails closed without relaxing it.
"""

import argparse
import base64
import contextlib
import datetime
import fcntl
import hashlib
import json
import os
import pathlib
import re
import secrets
import shutil
import signal
import stat
import subprocess
import sys
import time
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "domains"))
sys.path.insert(0, str(ROOT / "tools" / "identity"))
from activation_worker import open_directory, parse_json, private_directory, read_json, regular, write_file
from provision import BASE_DN, Provisioner, authority, validate_host
import owner

UTC = datetime.timezone.utc
GUID = re.compile(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}")
SUCCESS = "Node certificate and directory reader verified."
FAILURE = "Node enrollment failed verification. Native state was preserved; review the scoped worker before retrying."
UNSUPPORTED = "Native CA trust, 24-hour leaf policy, or read-only LDAP policy is unavailable. Review native identity configuration."
FIELDS = {"schemaVersion", "nodeId", "taskId", "hostname", "publicKeyFingerprint", "csrPem", "expiresAt"}


class Unsupported(RuntimeError):
    pass


def require(condition):
    if not condition:
        raise ValueError("Invalid scoped enrollment data.")


def now():
    return datetime.datetime.now(UTC)


def guid(value):
    return isinstance(value, str) and GUID.fullmatch(value) is not None and uuid.UUID(value).int != 0


def request(value, node_id):
    require(isinstance(value, dict) and set(value) == FIELDS)
    require(type(value["schemaVersion"]) is int and value["schemaVersion"] == 1)
    require(guid(node_id) and value["nodeId"] == node_id and guid(value["taskId"]))
    require(isinstance(value["hostname"], str)
            and re.fullmatch(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?", value["hostname"]) is not None)
    require(isinstance(value["publicKeyFingerprint"], str)
            and re.fullmatch(r"[0-9a-f]{64}", value["publicKeyFingerprint"]) is not None)
    require(isinstance(value["csrPem"], str) and len(value["csrPem"]) <= 16384)
    require(isinstance(value["expiresAt"], str)
            and re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?(?:Z|\+00:00)", value["expiresAt"]) is not None)
    expires = datetime.datetime.fromisoformat(value["expiresAt"].replace("Z", "+00:00"))
    require(now() < expires <= now() + datetime.timedelta(minutes=15))
    return value


def safe_bytes(path, maximum=32768, public=False):
    """Descriptor-relative read; public CA exports may be 0644, requests must be 0600."""
    parent = open_directory(path.parent)
    try:
        fd = os.open(path.name, os.O_RDONLY | os.O_NONBLOCK | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
        with os.fdopen(fd, "rb") as stream:
            info = os.fstat(stream.fileno())
            require(stat.S_ISREG(info.st_mode) and info.st_uid == os.getuid() and info.st_nlink == 1
                    and info.st_size <= maximum)
            require(not info.st_mode & 0o022 if public else stat.S_IMODE(info.st_mode) == 0o600)
            data = stream.read(maximum + 1)
            require(len(data) <= maximum)
            return data
    finally:
        os.close(parent)


# This is a bounded DER envelope reader, NOT a general ASN.1 implementation.
# Only the explicit PKCS#10/X.509 structures below are accepted; OpenSSL still
# verifies signatures, curve points, certificate chains, purpose and validity.
def tlvs(data):
    result = []
    offset = 0
    while offset < len(data):
        start = offset
        require(offset + 2 <= len(data))
        tag, length = data[offset:offset + 2]
        offset += 2
        require(tag & 31 != 31)
        if length & 128:
            count = length & 127
            require(1 <= count <= 3 and offset + count <= len(data) and data[offset] != 0)
            length = int.from_bytes(data[offset:offset + count], "big")
            require(length >= 128 and length.bit_length() > 8 * (count - 1))
            offset += count
        require(offset + length <= len(data))
        result.append((tag, data[offset:offset + length], data[start:offset + length]))
        require(len(result) <= 64)
        offset += length
    return result


def single(data, tag):
    items = tlvs(data)
    require(len(items) == 1 and items[0][0] == tag)
    return items[0][1]


def pem_blocks(text, label):
    require(isinstance(text, str) and len(text) <= 65536)
    pattern = r"-----BEGIN " + label + r"-----\s*([A-Za-z0-9+/=\r\n]+?)\s*-----END " + label + r"-----"
    matches = list(re.finditer(pattern, text))
    require(1 <= len(matches) <= 4 and not re.sub(pattern, "", text).strip())
    return [base64.b64decode(re.sub(r"\s", "", match[1]), validate=True) for match in matches]


def subject(data, node_id):
    rdn = single(single(single(data, 0x30), 0x31), 0x30)
    items = tlvs(rdn)
    require(len(items) == 2 and items[0][:2] == (6, bytes.fromhex("550403")))
    require(items[1][0] in (0x0c, 0x13) and items[1][1] == node_id.encode("ascii"))


def public_key(data, fingerprint):
    items = tlvs(single(data, 0x30))
    require(len(items) == 2)
    require(items[0][2] == bytes.fromhex("301306072a8648ce3d020106082a8648ce3d030107"))
    require(items[1][0] == 3 and len(items[1][1]) == 66 and items[1][1][:2] == b"\x00\x04")
    require(hashlib.sha256(data).hexdigest() == fingerprint)


def extensions(data):
    result = {}
    for tag, content, _ in tlvs(single(data, 0x30)):
        require(tag == 0x30)
        items = tlvs(content)
        require(len(items) in (2, 3) and items[0][0] == 6 and items[-1][0] == 4)
        oid = items[0][1].hex()
        require(oid not in result)
        critical = len(items) == 3
        if critical:
            require(items[1][:2] == (1, b"\xff"))
        result[oid] = (critical, items[-1][1])
    return result


def san(extension, hostname):
    require(tlvs(single(extension[1], 0x30)) == [(0x82, hostname.encode(), bytes([0x82, len(hostname)]) + hostname.encode())])


def validate_csr(p, value):
    blocks = pem_blocks(value["csrPem"], "CERTIFICATE REQUEST")
    require(len(blocks) == 1)
    items = tlvs(single(blocks[0], 0x30))
    require(len(items) == 3 and items[0][0] == 0x30)
    require(items[1][2] == bytes.fromhex("300a06082a8648ce3d040302") and items[2][0] == 3)
    info = tlvs(items[0][1])
    require(len(info) == 4 and info[0][:2] == (2, b"\x00") and info[3][0] == 0xa0)
    subject(info[1][2], value["nodeId"])
    public_key(info[2][2], value["publicKeyFingerprint"])
    attribute = tlvs(single(info[3][1], 0x30))
    require(len(attribute) == 2 and attribute[0][:2] == (6, bytes.fromhex("2a864886f70d01090e"))
            and attribute[1][0] == 0x31)
    exts = extensions(attribute[1][1])
    require(set(exts) == {"551d11"})
    san(exts["551d11"], value["hostname"])
    result = p.run(["openssl", "req", "-verify", "-noout"], input_text=value["csrPem"], check=False)
    verified = (result.stdout + result.stderr).strip()
    require(result.returncode == 0 and verified in ("verify OK", "Certificate request self-signature verify OK"))


def cert_time(item):
    tag, data, _ = item
    require(tag in (0x17, 0x18))
    text = data.decode("ascii")
    require(re.fullmatch(r"\d{12}Z" if tag == 0x17 else r"\d{14}Z", text) is not None)
    if tag == 0x17:
        year = int(text[:2])
        text = str(2000 + year if year < 50 else 1900 + year) + text[2:]
    return datetime.datetime.strptime(text, "%Y%m%d%H%M%SZ").replace(tzinfo=UTC)


def inspect_certificate(chain, value):
    blocks = pem_blocks(chain, "CERTIFICATE")
    require(len(blocks) >= 2)  # The node receives the intermediate, not just a leaf.
    outer = tlvs(single(blocks[0], 0x30))
    require(len(outer) == 3 and outer[0][0] == 0x30)
    fields = tlvs(outer[0][1])
    require(len(fields) == 8 and fields[0][:2] == (0xa0, b"\x02\x01\x02") and fields[7][0] == 0xa3)
    subject(fields[5][2], value["nodeId"])
    public_key(fields[6][2], value["publicKeyFingerprint"])
    validity = tlvs(single(fields[4][2], 0x30))
    require(len(validity) == 2)
    starts, ends = map(cert_time, validity)
    require(starts <= now() and ends - starts <= datetime.timedelta(hours=25))
    exts = extensions(fields[7][1])
    require({"551d11", "551d0f", "551d25"} <= set(exts))
    san(exts["551d11"], value["hostname"])
    # P-256 signing only: never keyCertSign, cRLSign, keyAgreement, or anyEKU.
    require(exts["551d0f"][1] == b"\x03\x02\x07\x80")
    if "551d13" in exts:
        require(exts["551d13"][1] == b"\x30\x00")  # CA=false, no pathLen
    eku = tlvs(single(exts["551d25"][1], 0x30))
    allowed = {bytes.fromhex("2b06010505070301"), bytes.fromhex("2b06010505070302")}
    require(1 <= len(eku) <= 2 and all(tag == 6 and oid in allowed for tag, oid, _ in eku)
            and len({oid for _, oid, _ in eku}) == len(eku)
            and bytes.fromhex("2b06010505070302") in {oid for _, oid, _ in eku})
    require(all(not critical or oid in {"551d11", "551d0f", "551d25", "551d13"}
                for oid, (critical, _) in exts.items()))
    return ends


class Native(Provisioner):
    deadline = None

    def run(self, command, *, check=True, input_text=None, cwd=None):
        remaining = self.deadline - time.monotonic() if self.deadline else 120
        if remaining <= 0:
            raise TimeoutError("Enrollment deadline exceeded.")
        result = subprocess.run(command, cwd=cwd or ROOT, env=self.environment, text=True, encoding="utf-8",
                                input=input_text, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                timeout=min(45 if "step" in command else 30, remaining))
        require(len(result.stdout) + len(result.stderr) <= 262144)
        if check and result.returncode:
            raise Unsupported(UNSUPPORTED)
        return result

    def load_scoped(self):
        self.settings = read_json(self.state / "settings.json")
        s = self.settings
        require(type(s["schema_version"]) is int and s["schema_version"] == 1
                and s["certificate_mode"] == "private-ca" and s["ldap_base_dn"] == BASE_DN
                and s["uid"] == os.getuid() and s["gid"] == os.getgid())
        require(validate_host(s["public_host"]) == s["public_host"])
        require(set(s["ports"]) == {"authentik", "ldaps", "ca"}
                and all(type(port) is int and 1 <= port <= 65535 for port in s["ports"].values())
                and len(set(s["ports"].values())) == 3)
        expected = pathlib.Path.home() / ".local" / "share" / "lucia" / "host" / "data"
        require(read_json(self.state / "host-settings.json")["data_directory"] == str(expected))
        return expected / "nodes"

    def trust(self):
        root = safe_bytes(self.state / "trust" / "lucia-root-ca.crt", public=True).decode("ascii")
        der = pem_blocks(root, "CERTIFICATE")
        require(len(der) == 1)
        fingerprint = safe_bytes(self.state / "trust" / "fingerprint.txt", public=True).decode("ascii").strip().lower()
        require(hashlib.sha256(der[0]).hexdigest() == fingerprint)
        native_root = safe_bytes(self.state / "ca" / "certs" / "root_ca.crt", public=True).decode("ascii")
        require(pem_blocks(native_root, "CERTIFICATE") == der)
        return root

    def verify_containers(self):
        expected = {
            "lucia-identity-ca": {"/home/step": "ca", "/certificates": "certificates",
                                  "/run/secrets/ca-password": "secrets/ca-password"},
            "lucia-identity-ldap": {"/var/lib/ldap": "ldap/data", "/etc/ldap/slapd.d": "ldap/config",
                                    "/trust": "trust", "/run/secrets/ldap-admin-password": "secrets/ldap-admin-password"},
        }
        for name, mounts in expected.items():
            # Inspect only ownership/mounts, not environment or admin credentials.
            result = self.run(["docker", "inspect", "--format",
                               '{"labels":{{json .Config.Labels}},"mounts":{{json .Mounts}}}', name])
            data = parse_json(result.stdout)
            require(data["labels"].get("io.lucia.component") == "identity")
            for destination, relative in mounts.items():
                matching = [m for m in data["mounts"] if m["Destination"] == destination]
                require(len(matching) == 1 and matching[0]["Type"] == "bind"
                        and matching[0]["Source"] == str(self.state / relative))

    def signing_policy(self):
        config = parse_json(safe_bytes(self.state / "ca" / "config" / "ca.json", 131072, public=True))
        auth = config["authority"]
        candidates = [p for p in auth["provisioners"] if p.get("name") == "lucia-installer"]
        if len(candidates) != 1 or candidates[0].get("type") != "JWK":
            raise Unsupported(UNSUPPORTED)
        provisioner = candidates[0]
        # Refuse custom templates rather than risk issuing a CA and rejecting it afterward.
        if auth.get("options") or provisioner.get("options"):
            raise Unsupported(UNSUPPORTED)
        claims = {**auth.get("claims", {}), **provisioner.get("claims", {})}
        duration = claims.get("maxTLSCertDuration", "24h")
        if not isinstance(duration, str) or not re.fullmatch(r"(?:\d+h)?(?:\d+m)?(?:\d+s)?", duration) or not duration:
            raise Unsupported(UNSUPPORTED)
        seconds = sum(int(n) * {"h": 3600, "m": 60, "s": 1}[unit] for n, unit in re.findall(r"(\d+)([hms])", duration))
        if seconds < 24 * 3600:
            raise Unsupported(UNSUPPORTED)

    def verify_chain(self, chain, value, workspace, ca_pem, fresh=False):
        ends = inspect_certificate(chain, value)
        require(pem_blocks(chain, "CERTIFICATE")[1] != pem_blocks(ca_pem, "CERTIFICATE")[0])
        if fresh:
            require(now() + datetime.timedelta(hours=12) < ends <= now() + datetime.timedelta(hours=25))
        write_file(workspace / "verify.crt", chain)
        write_file(workspace / "root.crt", ca_pem)
        # Historical receipts may have expired; structural validation is still
        # required before renewal, then fresh output is verified at current time.
        at = max(int(now().timestamp()), 0)
        if ends <= now():
            at = int((ends - datetime.timedelta(seconds=1)).timestamp())
        self.run(["openssl", "verify", "-CAfile", str(workspace / "root.crt"),
                  "-untrusted", str(workspace / "verify.crt"), "-purpose", "sslclient",
                  "-verify_hostname", value["hostname"], "-attime", str(at), str(workspace / "verify.crt")])
        return ends

    def sign(self, value, workspace, csr_hash, ca_pem):
        self.signing_policy()
        csr_path = workspace / (csr_hash + ".csr")
        certificate = workspace / (csr_hash + ".crt")
        try:
            staged = regular(certificate, 65536).decode("ascii")
        except FileNotFoundError:
            staged = ""
        if staged:
            ends = self.verify_chain(staged, value, workspace, ca_pem)
            if ends - now() >= datetime.timedelta(hours=6):
                return staged
        write_file(csr_path, value["csrPem"])
        # Never let --force follow a stale output link in a native directory.
        write_file(certificate, "")
        relative = "/certificates/node-enrollment/" + value["nodeId"] + "/" + csr_hash
        self.run(["docker", "exec", "lucia-identity-ca", "step", "ca", "sign",
                  relative + ".csr", relative + ".crt", "--provisioner", "lucia-installer",
                  "--provisioner-password-file", "/run/secrets/ca-password",
                  "--ca-url", "https://localhost:9000", "--root", "/home/step/certs/root_ca.crt",
                  "--not-after", "24h", "--force"])
        chain = regular(certificate, 65536).decode("ascii")
        self.verify_chain(chain, value, workspace, ca_pem, fresh=True)
        return chain


def ldif_entries(text):
    require(len(text) <= 262144)
    result = []
    for block in text.strip().split("\n\n"):
        entry = {}
        for line in block.splitlines():
            if not line or line.startswith("#"):
                continue
            require(not line.startswith(" ") and ":" in line)
            key, value = line.split(":", 1)
            require(not value.startswith("<"))
            value = base64.b64decode(value[1:].strip(), validate=True).decode() if value.startswith(":") else value.lstrip()
            entry.setdefault(key.lower(), []).append(value)
        if entry:
            require(len(entry.get("dn", [])) == 1)
            result.append(entry)
    return result


def reader_search(p, dn, password, base, scope, query, *attrs):
    result = p.run(["docker", "exec", "-i", "-e", "LDAPTLS_CACERT=/trust/lucia-root-ca.crt",
                    "-e", "LDAPTLS_REQCERT=demand", "lucia-identity-ldap",
                    "ldapsearch", "-LLL", "-o", "ldif-wrap=no", "-x", "-H", "ldaps://identity-gateway:8636",
                    "-D", dn, "-y", "/dev/stdin", "-b", base, "-s", scope,
                    "-l", "15", "-z", "1", query, *attrs], input_text=password, check=False)
    if result.returncode not in (0, 4):  # sizeLimitExceeded still permits the bounded first-entry probe.
        raise Unsupported(UNSUPPORTED)
    entries = ldif_entries(result.stdout)
    if not entries or any(key.split(";")[0] == "userpassword" for e in entries for key in e):
        raise Unsupported(UNSUPPORTED)
    return entries


def ldap_reader(p, receipt, save):
    uid = "node-" + receipt["nodeId"].replace("-", "")
    base = p.settings["ldap_base_dn"]
    dn = f"uid={uid},ou=Services,{base}"
    marker = "Lucia managed node " + receipt["nodeId"]

    def lookup():
        return owner.ldap_search(p, None, f"(uid={uid})", "uid", "cn", "sn", "objectClass",
                                 "description", "entryUUID", "uidNumber", "gidNumber")

    entries = lookup()
    if not entries:
        require(receipt["ldapEntryUuid"] is None)
        salt = secrets.token_bytes(16)
        digest = hashlib.sha1(receipt["ldapBindPassword"].encode() + salt).digest()
        hashed = "{SSHA}" + base64.b64encode(digest + salt).decode()
        # Receipt (including password) has already been fsynced before this ONLY LDAP write.
        owner.ldap_command(p, "ldapadd", input_text=(
            f"dn: {dn}\nobjectClass: top\nobjectClass: inetOrgPerson\n"
            f"uid: {uid}\ncn: {uid}\nsn: Service\n"
            f"description: {marker}\nuserPassword: {hashed}\n"))
        entries = lookup()
    require(len(entries) == 1)
    entry = {k.lower(): v for k, v in entries[0].items()}
    require(entry.get("dn") == [dn] and entry.get("uid") == [uid] and entry.get("cn") == [uid]
            and entry.get("sn") == ["Service"] and entry.get("description") == [marker]
            and "uidnumber" not in entry and "gidnumber" not in entry)
    require("inetorgperson" in {c.lower() for c in entry.get("objectclass", [])}
            and {c.lower() for c in entry.get("objectclass", [])} <= {"top", "person", "organizationalperson", "inetorgperson"})
    identifier = entry.get("entryuuid", [])
    require(len(identifier) == 1 and guid(identifier[0])
            and receipt["ldapEntryUuid"] in (None, identifier[0]))
    # Never grant memberships, including indirectly privileged non-owner groups.
    require(not owner.ldap_search(p, None, f"(|(uniqueMember={dn})(member={dn})(memberUid={uid}))", "dn"))
    if receipt["ldapEntryUuid"] is None:
        receipt["ldapEntryUuid"] = identifier[0]
        save()
    password = receipt["ldapBindPassword"]
    for ou in ("Users", "Groups"):
        rows = reader_search(p, dn, password, f"ou={ou},{base}", "base", "(objectClass=*)", "ou", "userPassword")
        require(rows[0].get("ou") == [ou])
    # Check a real owner/user's protected attribute and this service's password.
    reader_search(p, dn, password, f"ou=Users,{base}", "one", "(objectClass=inetOrgPerson)", "uid", "userPassword")
    reader_search(p, dn, password, dn, "base", "(objectClass=*)", "uid", "userPassword")
    return dn


def authorize_journal(p, value):
    path = p.load_scoped().parent / "onboarding" / "state.json"
    journal = parse_json(safe_bytes(path, 32 * 1024 * 1024))
    require(isinstance(journal, dict) and type(journal.get("version")) is int and journal["version"] == 3)
    devices, tasks = journal.get("devices"), journal.get("tasks")
    require(isinstance(devices, list) and len(devices) <= 128
            and isinstance(tasks, list) and len(tasks) <= 256)
    require(all(isinstance(row, dict) and isinstance(row.get("device"), dict) for row in devices)
            and all(isinstance(row, dict) and isinstance(row.get("task"), dict) for row in tasks))
    found_devices = [row for row in devices if row["device"].get("id") == value["nodeId"]]
    found_tasks = [row for row in tasks if row["task"].get("id") == value["taskId"]]
    require(len(found_devices) == len(found_tasks) == 1)
    stored_device, stored_task = found_devices[0], found_tasks[0]
    device, task = stored_device["device"], stored_task["task"]
    require(device.get("taskId") == value["taskId"] and task.get("deviceId") == value["nodeId"]
            and task.get("hostname") == value["hostname"]
            and device.get("phase") in ("AwaitingEnrollment", "Managed")
            and task.get("phase") in ("AwaitingEnrollment", "Managed"))
    for record in (stored_device, stored_task):
        fingerprint = record.get("sessionKeyFingerprint")
        require(isinstance(fingerprint, str) and re.fullmatch(r"[0-9a-fA-F]{64}", fingerprint) is not None
                and fingerprint.lower() == value["publicKeyFingerprint"])
    inventory = stored_device.get("inventoryHash")
    require(isinstance(inventory, str) and re.fullmatch(r"[0-9a-fA-F]{64}", inventory) is not None
            and stored_task.get("inventoryHash") == inventory
            and type(device.get("inventoryRevision")) is int and device["inventoryRevision"] >= 1
            and type(task.get("inventoryRevision")) is int
            and task["inventoryRevision"] == device["inventoryRevision"])
    grant = stored_task.get("grantRequestId")
    require(guid(grant) and sum(row.get("grantRequestId") == grant for row in tasks) == 1)


def process(p, value, request_hash):
    authorize_journal(p, value)
    validate_csr(p, value)
    p.verify_containers()
    ca_pem = p.trust()
    native = p.state / "node-enrollment"
    private_directory(native)
    receipt_path = native / (value["nodeId"] + ".json")
    identity = {key: value[key] for key in ("nodeId", "taskId", "hostname", "publicKeyFingerprint")}
    csr_hash = hashlib.sha256(value["csrPem"].encode()).hexdigest()
    identity["csrHash"] = csr_hash
    try:
        receipt = parse_json(regular(receipt_path, 131072))
    except FileNotFoundError:
        receipt = {"schemaVersion": 1, **identity, "ldapBindPassword": secrets.token_urlsafe(36),
                   "ldapEntryUuid": None, "certificatePem": None, "caPem": ca_pem,
                   "lastRequestHash": None, "lastResponse": None}
    require(receipt["schemaVersion"] == 1 and all(receipt.get(k) == v for k, v in identity.items())
            and receipt["caPem"] == ca_pem)
    require(isinstance(receipt["ldapBindPassword"], str) and re.fullmatch(r"[A-Za-z0-9_-]{48}", receipt["ldapBindPassword"]) is not None)
    if receipt["lastRequestHash"] == request_hash and receipt["lastResponse"] is not None:
        return receipt["lastResponse"]
    def save():
        write_file(receipt_path, json.dumps(receipt) + "\n")
    save()
    certificates = p.state / "certificates" / "node-enrollment"
    private_directory(certificates)
    workspace = certificates / value["nodeId"]
    private_directory(workspace)
    chain = receipt["certificatePem"]
    ends = p.verify_chain(chain, value, workspace, ca_pem) if chain else None
    if ends is None or ends - now() < datetime.timedelta(hours=6):
        # Policy unsupported? Fail BEFORE creating the reader.
        p.signing_policy()
    dn = ldap_reader(p, receipt, save)
    if ends is None or ends - now() < datetime.timedelta(hours=6):
        chain = p.sign(value, workspace, csr_hash, ca_pem)
        receipt["certificatePem"] = chain
        save()
    result = {
        "hostname": value["hostname"], "certificatePem": chain, "caPem": ca_pem,
        "ldapUri": "ldaps://" + authority(p.settings["public_host"], p.settings["ports"]["ldaps"]),
        "ldapBaseDn": p.settings["ldap_base_dn"], "ldapBindDn": dn,
        "ldapBindPassword": receipt["ldapBindPassword"],
        "ownerGroupDn": "cn=lucia-owners,ou=Groups," + p.settings["ldap_base_dn"],
    }
    receipt.update(lastRequestHash=request_hash, lastResponse=result)
    save()
    return result


@contextlib.contextmanager
def lease(path):
    parent = open_directory(path.parent)
    try:
        fd = os.open(path.name, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC, 0o600, dir_fd=parent)
        with os.fdopen(fd, "a+b") as lock:
            info = os.fstat(lock.fileno())
            require(stat.S_ISREG(info.st_mode) and info.st_uid == os.getuid() and info.st_nlink == 1
                    and not info.st_mode & 0o022)
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            yield
    finally:
        os.close(parent)


def handle(p, directory, path):
    # Snapshot once. No path, command, CA settings or credentials come from JSON.
    data = safe_bytes(path)
    fingerprint = hashlib.sha256(data).hexdigest()
    response_path = directory / "enrollment-responses" / path.name
    try:
        previous = parse_json(regular(response_path, 131072))
        if previous.get("requestHash") == fingerprint:
            return
    except FileNotFoundError:
        pass
    value = None
    try:
        value = request(parse_json(data), path.stem)
        with lease(ROOT.parent / ".run.lock"), lease(p.state / ".provision.lock"):
            p.deadline = time.monotonic() + 120
            require(p.load_scoped() == directory)
            configuration = process(p, value, fingerprint)
        success, message = True, SUCCESS
    except BlockingIOError:
        return  # Shared native jobs own the lease; retry next poll, no failed receipt.
    except Exception as error:
        success, configuration = False, None
        message = UNSUPPORTED if isinstance(error, Unsupported) else FAILURE
        print(json.dumps({"event": "node-enrollment-failed", "nodeId": path.stem,
                          "errorType": type(error).__name__}), flush=True)
    write_file(response_path, json.dumps({
        "schemaVersion": 1, "nodeId": path.stem, "taskId": value["taskId"] if value else None,
        "requestHash": fingerprint, "success": success, "message": message,
        "checkedAt": now().isoformat(), "configuration": configuration,
    }) + "\n")


def remove(p, directory, path):
    # Retire a removed machine's LDAP reader and enrollment receipt, only once Lucia no longer knows the machine.
    value = parse_json(safe_bytes(path, 4096))
    require(isinstance(value, dict) and set(value) == {"schemaVersion", "nodeId"} and value["schemaVersion"] == 1
            and isinstance(value["nodeId"], str) and value["nodeId"] == path.stem and guid(path.stem))
    node_id = path.stem
    journal = parse_json(safe_bytes(p.load_scoped().parent / "onboarding" / "state.json", 32 * 1024 * 1024))
    require(isinstance(journal, dict) and isinstance(journal.get("devices"), list)
            and all(isinstance(row, dict) and isinstance(row.get("device"), dict) for row in journal["devices"]))
    require(not any(row["device"].get("id") == node_id for row in journal["devices"]))
    for queue in ("identities", "enrollment-requests"):
        require(not os.path.lexists(directory / queue / path.name))
    with lease(ROOT.parent / ".run.lock"), lease(p.state / ".provision.lock"):
        p.deadline = time.monotonic() + 120
        require(p.load_scoped() == directory)
        uid = "node-" + node_id.replace("-", "")
        dn = f"uid={uid},ou=Services,{p.settings['ldap_base_dn']}"
        entries = owner.ldap_search(p, None, f"(uid={uid})", "description")
        if entries:
            entry = {k.lower(): v for k, v in entries[0].items()}
            require(len(entries) == 1 and entry.get("dn") == [dn]
                    and entry.get("description") == ["Lucia managed node " + node_id])
            owner.ldap_command(p, "ldapdelete", dn)
        receipt = p.state / "node-enrollment" / path.name
        if os.path.lexists(receipt):
            require(receipt.is_file() and not receipt.is_symlink())
            receipt.unlink()
        workspace = p.state / "certificates" / "node-enrollment" / node_id
        if os.path.lexists(workspace):
            require(workspace.is_dir() and not workspace.is_symlink())
            shutil.rmtree(workspace)
    path.unlink()
    print(json.dumps({"event": "node-removed", "nodeId": node_id}), flush=True)


def candidates(directory):
    fd = open_directory(directory)
    try:
        names = []
        with os.scandir(fd) as entries:
            for entry in entries:
                names.append(entry.name)
                require(len(names) <= 128)
        return [directory / name for name in sorted(names) if name.endswith(".json") and guid(name[:-5])]
    finally:
        os.close(fd)


def platform_check():
    if sys.platform != "linux" or os.getuid() == 0:
        raise RuntimeError("Run node enrollment as the non-root Linux setup user.")
    stable = pathlib.Path.home() / ".local" / "share" / "lucia" / "bootstrap" / "app" / "tools" / "nodes" / "enrollment_worker.py"
    require(pathlib.Path(__file__).resolve() == stable)
    # Also reject writable/symlinked parents of the stable implementation.
    fd = open_directory(stable.parent)
    os.close(fd)
    return stable


def run():
    platform_check()
    os.umask(0o077)
    state = pathlib.Path.home() / ".local" / "share" / "lucia" / "identity"
    p = Native(argparse.Namespace(state=str(state)))
    directory = p.load_scoped()
    private_directory(directory)
    for name in ("enrollment-requests", "enrollment-responses", "removals"):
        private_directory(directory / name)
    stopping = False
    def stop(*_):
        nonlocal stopping
        stopping = True
    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    heartbeat = directory / "enrollment-worker.json"
    try:
        while not stopping:
            write_file(heartbeat, json.dumps({"schemaVersion": 1, "ready": True, "checkedAt": now().isoformat()}) + "\n")
            for path in candidates(directory / "enrollment-requests"):
                if stopping:
                    break
                try:
                    handle(p, directory, path)
                except (OSError, ValueError):
                    # Unreadable/symlink/FIFO input has no safe snapshot to acknowledge.
                    print('{"event":"node-enrollment-unsafe-queue-file"}', flush=True)
                write_file(heartbeat, json.dumps({"schemaVersion": 1, "ready": True, "checkedAt": now().isoformat()}) + "\n")
            for path in candidates(directory / "removals"):
                if stopping:
                    break
                try:
                    remove(p, directory, path)
                except BlockingIOError:
                    pass  # Shared native jobs own the lease; retry next poll.
                except Exception as error:
                    print(json.dumps({"event": "node-removal-failed", "nodeId": path.stem,
                                      "errorType": type(error).__name__}), flush=True)
            time.sleep(5)
    finally:
        write_file(heartbeat, json.dumps({"schemaVersion": 1, "ready": False, "checkedAt": now().isoformat()}) + "\n")


def install():
    stable = platform_check()
    os.umask(0o077)
    linger = subprocess.run(["loginctl", "show-user", str(os.getuid()), "-p", "Linger", "--value"],
                            check=True, capture_output=True, text=True, timeout=30).stdout.strip()
    if linger != "yes":
        raise RuntimeError("Approve persistent user services before installing node enrollment.")
    unit = pathlib.Path.home() / ".config" / "systemd" / "user" / "lucia-node-enrollment.service"
    unit.parent.mkdir(parents=True, exist_ok=True)
    escaped = str(stable).replace("\\", "\\\\").replace('"', '\\"').replace("%", "%%")
    write_file(unit, "[Unit]\nDescription=Lucia scoped node enrollment\n"
               "[Service]\nType=simple\nExecStart=/usr/bin/python3 \"" + escaped + "\" run\n"
               "Restart=on-failure\nRestartSec=5\nUMask=0077\nNoNewPrivileges=yes\n"
               "[Install]\nWantedBy=default.target\n")
    for args in (["daemon-reload"], ["enable", unit.name], ["restart", unit.name]):
        subprocess.run(["systemctl", "--user", *args], check=True, capture_output=True, timeout=30)


if __name__ == "__main__":
    try:
        if sys.argv[1:] == ["install"]:
            install()
        elif sys.argv[1:] == ["run"]:
            run()
        else:
            raise ValueError("Usage: enrollment_worker.py install|run")
    except Exception:
        raise SystemExit("Scoped node enrollment stopped. Check native paths, identity policy and approved user linger.")
