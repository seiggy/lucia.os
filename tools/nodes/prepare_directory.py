#!/usr/bin/env python3
"""Explicit, one-time native policy preparation; NEVER called by the web/worker.

After maintainer review, from the stable bootstrap checkout, before worker start:
  python3 tools/nodes/prepare_directory.py apply --reviewed-node-reader-policy
Offline checks (no Docker/LDAP access):
  python tools/nodes/prepare_directory.py check

Only the one owned MDB suffix dc=lucia,dc=home,dc=arpa is changed. Two leading
olcAccess rules deny ALL userPassword access and restrict ALL other access to
read for normalized uid=node-<32 lowercase hex>,ou=services,<suffix> DNs.
Everyone else uses "by * break", preserving the pre-existing ACLs in order.
Anonymous password authentication remains governed by the existing auth ACL.
The GUID-shaped namespace is reserved for managed-node readers, not human users.

Access: fixed container-root SASL EXTERNAL over ldapi:/// first; if that context
cannot read/write config, the existing config-admin mount is the only fallback.
No secret bytes enter Python or arguments, no password resets, no offline edits.
The private node-directory-acl.json receipt contains the original ACL backup,
database UUID, expected prefix, and verified canonical result. One ADD-only
LDAPModify inserts ordered values {0}/{1}; no existing ACL is replaced/deleted.
Identity is rechecked before insertion and after. Concurrent changes cause a
read-back failure; they are preserved, never blindly rolled back.
No rollback, ACL adoption, drift repair, or CA policy mutation is automatic.

OpenLDAP 2.6 slapd.access matches normalized DNs; POSIX ERE {32} is supported.
aclparse.c renders bare "break" as "+0 break" and may omit default "stop".
These equivalent renderings are accepted ONLY for the two exact owned rules.
"""

import argparse
import importlib.util
import json
import os
import pathlib
import re
import sys
import time
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[2]
BASE = "dc=lucia,dc=home,dc=arpa"
NODE_DN = r"^uid=node-[0-9a-f]{32},ou=services,dc=lucia,dc=home,dc=arpa$"
RULES = [
    f'to attrs=userPassword by dn.regex="{NODE_DN}" none stop by * break',
    f'to * by dn.regex="{NODE_DN}" read stop by * break',
]


class PolicyError(RuntimeError):
    def __init__(self, code):
        self.code = code
        super().__init__(code)


def need(condition, code):
    if not condition:
        raise PolicyError(code)


def command(tool, method, *args):
    prefix = ["docker", "exec", "-i"]
    if method == "external":
        return [*prefix, "--user", "0", "lucia-identity-ldap", tool, "-Q", "-Y", "EXTERNAL", "-H", "ldapi:///", *args]
    need(method == "config-admin", "unknown-native-config-context")
    return [*prefix, "lucia-identity-ldap", tool, "-x", "-H", "ldap://127.0.0.1:389",
            "-D", "uid=admin," + BASE, "-y", "/run/secrets/ldap-admin-password", *args]


def acl_bodies(values):
    need(isinstance(values, list) and 1 <= len(values) <= 256 and len(json.dumps(values)) <= 65536,
         "bounded-existing-ACL-required")
    indexed = {}
    for value in values:
        need(isinstance(value, str) and len(value) <= 8192, "invalid-existing-ACL")
        match = re.fullmatch(r"\{(\d+)\}([\s\S]+)", value)
        need(match is not None and int(match[1]) not in indexed, "invalid-ACL-ordering")
        indexed[int(match[1])] = match[2].strip()
    need(set(indexed) == set(range(len(indexed))), "noncontiguous-ACL-ordering")
    return [indexed[index] for index in range(len(indexed))]


def normalized_prefix(value):
    # Our two quoted regexes contain no whitespace; no normalization of other ACLs.
    return re.sub(r"\s+", " ", value).strip().replace(" stop by *", " by *").replace("by * break", "by * +0 break")


def has_prefix(values):
    return len(values) >= 2 and [normalized_prefix(item) for item in values[:2]] == [normalized_prefix(item) for item in RULES]


def snapshot(p, w, method):
    result = p.run(command("ldapsearch", method, "-LLL", "-o", "ldif-wrap=no", "-b", "cn=config", "-s", "one",
                           "-l", "15", "-z", "2", "(olcSuffix=" + BASE + ")",
                           "olcSuffix", "olcAccess", "olcRootDN", "objectClass", "entryUUID"),
                   check=False)
    if result.returncode in (32, 48, 49, 50, 81, 91, 255) or result.returncode == 0 and not result.stdout.strip():
        return None
    need(result.returncode == 0, "config-search-failed")
    rows = w.ldif_entries(result.stdout)
    if not rows:
        return None
    need(len(rows) == 1, "exactly-one-suffix-database-required")
    row = rows[0]
    if "olcaccess" not in row:
        return None
    need(row.get("olcsuffix") == [BASE] and row.get("olcrootdn") == ["uid=admin," + BASE],
         "suffix-database-ownership-mismatch")
    need("olcmdbconfig" in [item.lower() for item in row.get("objectclass", [])], "owned-MDB-database-required")
    dn = row["dn"][0]
    need(re.fullmatch(r"olcDatabase=\{\d{1,3}\}mdb,cn=config", dn) is not None, "unexpected-config-database-DN")
    identifiers = row.get("entryuuid", [])
    need(len(identifiers) == 1 and re.fullmatch(r"[0-9a-f-]{36}", identifiers[0]) is not None
         and uuid.UUID(identifiers[0]).int != 0, "database-identity-missing")
    return {"dn": dn, "uuid": identifiers[0], "acl": acl_bodies(row["olcaccess"])}


def prepare(p, w):
    """p supplies a bounded native transport; w is the unchanged worker's secure I/O."""
    need(p.settings["ldap_base_dn"] == BASE, "unsupported-directory-suffix")
    method, current = "external", snapshot(p, w, "external")
    if current is None:
        method, current = "config-admin", snapshot(p, w, "config-admin")
    need(current is not None, "native-config-access-unavailable")
    path = p.state / "node-directory-acl.json"
    try:
        receipt = w.parse_json(w.regular(path, 262144))
    except FileNotFoundError:
        need(not any("uid=node-" in rule.lower() for rule in current["acl"]), "existing-node-ACL-without-owned-receipt")
        receipt = {"schemaVersion": 1, "suffix": BASE, "databaseDn": current["dn"], "databaseUuid": current["uuid"],
                   "originalAcl": current["acl"], "prefix": RULES, "appliedAcl": None, "complete": False}
        w.write_file(path, json.dumps(receipt) + "\n")  # Durable backup BEFORE the only directory write.
    need(receipt.get("schemaVersion") == 1 and receipt.get("suffix") == BASE and receipt.get("prefix") == RULES
         and receipt.get("databaseDn") == current["dn"] and receipt.get("databaseUuid") == current["uuid"],
         "ACL-receipt-identity-drift")
    original = receipt["originalAcl"]
    need(isinstance(original, list) and 1 <= len(original) <= 254 and all(isinstance(item, str) for item in original),
         "ACL-backup-invalid")
    if receipt["complete"]:
        need(has_prefix(current["acl"]) and current["acl"] == receipt["appliedAcl"]
             and current["acl"][2:] == original, "prepared-ACL-drift")
        return {"changed": False, "configurationContext": method, "verified": True}
    already_applied = has_prefix(current["acl"]) and current["acl"][2:] == original
    if not already_applied:
        need(current["acl"] == original, "pending-ACL-drift")
        checked = snapshot(p, w, method)
        need(checked == current, "config-changed-before-add")
        ldif = (f"dn: {current['dn']}\nchangetype: modify\nadd: olcAccess\n"
                f"olcAccess: {{0}}{RULES[0]}\nolcAccess: {{1}}{RULES[1]}\n")
        def modify():
            return p.run(command("ldapmodify", method), input_text=ldif, check=False)
        result = modify()
        if result.returncode == 50 and method == "external":
            method = "config-admin"
            fallback = snapshot(p, w, method)
            need(fallback is not None, "native-config-write-access-unavailable")
            need(fallback["dn"] == current["dn"] and fallback["uuid"] == current["uuid"] and fallback["acl"] == original,
                 "config-changed-before-admin-fallback")
            current = fallback
            result = modify()
        need(result.returncode != 50, "native-config-write-access-unavailable")
        need(result.returncode == 0, "ACL-ordered-add-refused")
        current = snapshot(p, w, method)
        need(current is not None and current["dn"] == receipt["databaseDn"] and current["uuid"] == receipt["databaseUuid"]
             and has_prefix(current["acl"]) and current["acl"][2:] == original, "ACL-readback-mismatch")
    receipt.update(appliedAcl=current["acl"], complete=True)
    w.write_file(path, json.dumps(receipt) + "\n")
    return {"changed": not already_applied, "configurationContext": method, "verified": True}


def check():
    """In-memory directory and receipt checks, never contacts Docker or LDAP."""
    from types import SimpleNamespace
    import copy
    expression = re.compile(NODE_DN)
    assert expression.fullmatch("uid=node-" + "a" * 32 + ",ou=services," + BASE)
    for name in ("uid=authentik,ou=services," + BASE, "uid=admin," + BASE,
                 "uid=node-" + "a" * 32 + ",ou=users," + BASE,
                 "uid=node-" + "a" * 31 + ",ou=services," + BASE,
                 "uid=node-" + "a" * 32 + ",ou=services,dc=elsewhere"):
        assert not expression.fullmatch(name)
    original = ["to attrs=userPassword by self write by anonymous auth by * none", "to * by users read"]
    data = {
        "dn": ["olcDatabase={1}mdb,cn=config"], "olcsuffix": [BASE], "olcrootdn": ["uid=admin," + BASE],
        "objectclass": ["olcDatabaseConfig", "olcMdbConfig"], "entryuuid": ["11111111-1111-4111-8111-111111111111"],
        "olcaccess": ["{0}" + original[0], "{1}" + original[1]],
    }
    class Fake:
        def __init__(self):
            self.state, self.settings = pathlib.Path("fixture"), {"ldap_base_dn": BASE}
            self.row, self.files, self.writes = copy.deepcopy(data), {}, []
            self.external_read_denied, self.external_write_denied = False, False
            self.all_access_denied = False
            self.crash_after_write = False
            self.concurrent_tail_change = False
            self.concurrent_recreate = False
        def read(self, path, maximum):
            if path not in self.files:
                raise FileNotFoundError()
            return self.files[path]
        def save(self, path, content):
            self.files[path] = content.encode()
        def run(self, args, **kwargs):
            if "ldapsearch" in args:
                denied = self.all_access_denied or "EXTERNAL" in args and self.external_read_denied
                return SimpleNamespace(returncode=50 if denied else 0, stdout="" if denied else json.dumps([self.row]))
            assert "ldapmodify" in args and len(self.files) == 1
            self.writes.append(args)
            assert "-e" not in args and not any("assert=" in value for value in args)
            assert kwargs["input_text"] == ("dn: olcDatabase={1}mdb,cn=config\nchangetype: modify\nadd: olcAccess\n"
                                           f"olcAccess: {{0}}{RULES[0]}\nolcAccess: {{1}}{RULES[1]}\n")
            assert "replace:" not in kwargs["input_text"] and "delete:" not in kwargs["input_text"]
            if "EXTERNAL" in args and self.external_write_denied:
                return SimpleNamespace(returncode=50)
            canonical = [normalized_prefix(item) for item in RULES]
            tail = original + (["to dn.base=\"cn=operator\" by users read"] if self.concurrent_tail_change else [])
            self.row["olcaccess"] = ["{" + str(i) + "}" + value for i, value in enumerate(canonical + tail)]
            if self.concurrent_recreate:
                self.row["entryuuid"] = ["33333333-3333-4333-8333-333333333333"]
            if self.crash_after_write:
                self.crash_after_write = False
                raise TimeoutError()
            return SimpleNamespace(returncode=0)
        def io(self):
            return SimpleNamespace(ldif_entries=json.loads, parse_json=json.loads, regular=self.read, write_file=self.save)
    fake = Fake()
    assert prepare(fake, fake.io())["changed"] and len(fake.writes) == 1
    receipt = copy.deepcopy(fake.files)
    assert not prepare(fake, fake.io())["changed"] and fake.files == receipt and len(fake.writes) == 1
    for drift in ("prefix", "tail", "duplicate-prefix", "uuid", "suffix", "missing-backup"):
        candidate = copy.deepcopy(fake)
        if drift == "prefix":
            candidate.row["olcaccess"][0] = candidate.row["olcaccess"][0].replace(" none", " read", 1)
        elif drift == "tail":
            candidate.row["olcaccess"][-1] += " by anonymous read"
        elif drift == "duplicate-prefix":
            bodies = acl_bodies(candidate.row["olcaccess"])
            candidate.row["olcaccess"] = ["{" + str(i) + "}" + value for i, value in enumerate(bodies[:2] + bodies)]
        elif drift == "uuid":
            candidate.row["entryuuid"] = ["22222222-2222-4222-8222-222222222222"]
        elif drift == "suffix":
            candidate.row["olcsuffix"] = ["dc=other"]
        else:
            candidate.files.clear()
        before = len(candidate.writes)
        try:
            prepare(candidate, candidate.io())
            raise AssertionError("Directory ACL drift was accepted.")
        except PolicyError:
            pass
        assert len(candidate.writes) == before
    candidate = Fake()
    candidate.all_access_denied = True
    try:
        prepare(candidate, candidate.io())
        raise AssertionError("Missing config authority accepted.")
    except PolicyError as error:
        assert error.code == "native-config-access-unavailable"
    assert not candidate.files and not candidate.writes
    for read_denied, write_denied in ((True, False), (False, True)):
        candidate = Fake()
        candidate.external_read_denied, candidate.external_write_denied = read_denied, write_denied
        assert prepare(candidate, candidate.io())["configurationContext"] == "config-admin"
        assert "/run/secrets/ldap-admin-password" in candidate.writes[-1]
    candidate = Fake()
    candidate.crash_after_write = True
    try:
        prepare(candidate, candidate.io())
    except TimeoutError:
        pass
    assert not prepare(candidate, candidate.io())["changed"] and len(candidate.writes) == 1
    for change in ("concurrent_tail_change", "concurrent_recreate"):
        candidate = Fake()
        setattr(candidate, change, True)
        try:
            prepare(candidate, candidate.io())
            raise AssertionError("Concurrent config drift was ignored.")
        except PolicyError as error:
            assert error.code == "ACL-readback-mismatch"
        after = copy.deepcopy(candidate.row)
        assert len(candidate.files) == len(candidate.writes) == 1
        try:
            prepare(candidate, candidate.io())
            raise AssertionError("Concurrent drift was overwritten on retry.")
        except PolicyError:
            pass
        assert candidate.row == after and len(candidate.writes) == 1
    print(json.dumps({"success": True, "phase": "directory-policy-offline-check", "liveLDAPContacted": False,
                      "checks": ["namespace", "backup-before-write", "prefix-only", "idempotency", "drift",
                                 "native-config-fallback", "crash-recovery", "add-only", "concurrent-changes-preserved"]}))


def apply():
    need(sys.platform == "linux" and os.getuid() != 0, "nonroot-linux-required")
    stable = pathlib.Path.home() / ".local/share/lucia/bootstrap/app/tools/nodes/prepare_directory.py"
    need(pathlib.Path(__file__).resolve() == stable, "stable-native-checkout-required")
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location("enrollment_worker", stable.with_name("enrollment_worker.py"))
    worker = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(worker)
    os.umask(0o077)
    state = pathlib.Path.home() / ".local/share/lucia/identity"
    p = worker.Native(argparse.Namespace(state=str(state)))
    with worker.lease(ROOT.parent / ".run.lock"), worker.lease(state / ".provision.lock"):
        p.deadline = time.monotonic() + 120
        p.load_scoped()
        p.verify_containers()
        result = prepare(p, worker)
    print(json.dumps({"success": True, "phase": "directory-policy", **result}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="action", required=True)
    sub.add_parser("check")
    apply_parser = sub.add_parser("apply")
    apply_parser.add_argument("--reviewed-node-reader-policy", required=True, action="store_true")
    args = parser.parse_args()
    try:
        check() if args.action == "check" else apply()
    except Exception as error:
        print(json.dumps({"success": False, "phase": "directory-policy",
                          "code": error.code if isinstance(error, PolicyError) else "native-directory-policy-failed",
                          "errorType": type(error).__name__}))
        raise SystemExit(1)
