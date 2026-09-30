"""Offline stdlib regressions for people.py. Run: python tools\\identity\\check_people.py.

No live directory, Authentik, credentials or accounts are used.
"""

import base64
import unittest
from types import SimpleNamespace
from unittest.mock import patch

import people

BASE = "dc=lucia,dc=home,dc=arpa"


def dn(username):
    return f"uid={username},ou=Users,{BASE}"


def state():
    return {
        "base": BASE, "owner": "zackw",
        "people": {
            "zackw": {"uid": ["zackw"], "cn": ["zackw"], "entryUUID": ["u-zackw"]},
            "sam": {"uid": ["sam"], "cn": ["Sam Lee"], "mail": ["sam@example.com"], "entryUUID": ["u-sam"],
                    "pwdAccountLockedTime": [people.LOCKED]},
        },
        "groups": {
            "lucia-owners": {"cn": ["lucia-owners"], "uniqueMember": [dn("zackw")], "entryUUID": ["g-owners"]},
            "ldap-admins": {"cn": ["ldap-admins"], "uniqueMember": ["uid=admin," + BASE, dn("zackw")], "entryUUID": ["g-admins"]},
            "family": {"cn": ["family"], "description": ["Home"], "uniqueMember": [dn("sam"), "uid=node-1,ou=Services," + BASE],
                       "entryUUID": ["g-family"]},
        },
        "ak_users": [
            {"pk": 1, "username": "akadmin", "is_active": True, "attributes": {}},
            {"pk": 7, "username": "zackw", "email": "z@example.com", "is_active": True, "attributes": {"ldap_uniq": "u-zackw"}},
            {"pk": 8, "username": "sam", "email": "sam@example.com", "is_active": False, "attributes": {"ldap_uniq": "u-sam"}},
        ],
        "ak_groups": [
            {"pk": "a-owners", "name": "lucia-owners", "users": [7], "attributes": {"ldap_uniq": "g-owners"}},
            {"pk": "a-admins", "name": "ldap-admins", "users": [7], "attributes": {"ldap_uniq": "g-admins"}},
            {"pk": "a-family", "name": "family", "users": [8], "attributes": {"ldap_uniq": "g-family"}},
            {"pk": "a-users", "name": "lucia-users", "users": [8], "attributes": {"lucia_host_installation": "i"}},
            {"pk": "a-builtin", "name": "authentik Admins", "users": [1], "attributes": {}},
        ],
        "apps": [{"pk": "app-1", "slug": "lucia-app-immich", "name": "Immich", "meta_launch_url": "https://photos",
                  "bindings": [
                      {"pk": "b-owners", "group": "a-owners", "enabled": True, "negate": False, "order": 0},
                      {"pk": "b-admins", "group": "a-admins", "enabled": True, "negate": False, "order": 1},
                      {"pk": "b-family", "group": "a-family", "enabled": True, "negate": False, "order": 2},
                  ]}],
    }


def request(action, **fields):
    return {"schemaVersion": 1, "id": "20260101000000000-" + "0" * 32, "action": action,
            "requestedBy": "zackw", "requestedAt": "2026-01-01T00:00:00Z", **fields}


class Fake:
    def __init__(self):
        self.settings = {"ldap_base_dn": BASE}
        self.ldif, self.api = [], []
        self.s = state()

    def patches(self):
        def ldap(p, tool, *args, text=None):
            self.ldif.append((tool, args, text))
            if tool == "ldapadd" and text.startswith("dn: uid="):
                username = text.split("uid=")[1].split(",")[0]
                self.s["people"][username] = {"uid": [username], "cn": [username], "entryUUID": ["u-" + username]}
            return SimpleNamespace(returncode=0, stdout="")

        def api(p, method, path, body=None, allow_missing=False):
            self.api.append((method, path, body))
            return {"sync_users_password": True}

        return [patch.object(people, "_state", lambda p, i: self.s), patch.object(people, "_ldap", ldap),
                patch.object(people, "_api", api), patch.object(people, "_sync", lambda p: None)]

    def run(self, value):
        stack = self.patches()
        for item in stack:
            item.start()
        try:
            people.apply(self, value, value["id"], "i")
        finally:
            for item in stack:
                item.stop()


class Validation(unittest.TestCase):
    def test_passwords_follow_directory_policy(self):
        people.check_password("Correct-Horse-9-battery")
        for bad in ("Short-1a", "no-upper-case-9-here", "NO-LOWER-CASE-9-HERE", "No-Digits-Anywhere-", "NoSymbols123Here45",
                    "Tab\tInside-Password-9", None):
            with self.assertRaises(people.Refused):
                people.check_password(bad)

    def test_names_refuse_system_and_reserved(self):
        self.assertEqual(people._name("sam_2", people.RESERVED_USERS, "Usernames"), "sam_2")
        for bad in ("root", "sudo", "Sam", "2sam", "lucia-x", "node-abc", "a" * 33, "sam lee", ""):
            with self.assertRaises(people.Refused):
                people._name(bad, people.RESERVED_GROUPS, "Group names")

    def test_email_is_optional_but_valid(self):
        self.assertEqual(people._email(" "), "")
        self.assertEqual(people._email("a.b@home.example"), "a.b@home.example")
        with self.assertRaises(people.Refused):
            people._email("not an email")

    def test_request_shape_is_exact(self):
        fake = Fake()
        for bad in (None, request("dropTables"), {**request("deleteUser", username="sam"), "extra": 1},
                    {**request("deleteUser", username="sam"), "schemaVersion": True}):
            with self.assertRaises(people.Refused):
                fake.run(bad if isinstance(bad, dict) else {"id": "x"})
        with self.assertRaises(people.Refused):
            people.apply(fake, request("deleteUser", username="sam"), "20260101000000000-" + "1" * 32, "i")
        self.assertEqual(fake.ldif, [])

    def test_duplicate_names_are_owner_messages(self):
        with patch.object(people, "_ldap", lambda *a, **k: SimpleNamespace(returncode=19, stdout="")):
            with self.assertRaises(people.Refused):
                people._change(Fake(), "dn: x\n")


class Guards(unittest.TestCase):
    def test_first_owner_is_protected(self):
        for value in (request("deleteUser", username="zackw"), request("setUserActive", username="zackw", active=False),
                      request("setUserGroups", username="zackw", groups=["family"]),
                      request("setGroupMembers", group="lucia-owners", members=["sam"]),
                      request("updateGroup", group="lucia-owners", description="")):
            fake = Fake()
            with self.assertRaises(people.Refused):
                fake.run(value)
            self.assertEqual((fake.ldif, fake.api), ([], []))

    def test_lucia_groups_cannot_be_deleted_and_hidden_groups_are_unknown(self):
        for group in ("lucia-owners", "lucia-users", "ldap-admins", "ldap-password-reset"):
            fake = Fake()
            with self.assertRaises(people.Refused):
                fake.run(request("deleteGroup", group=group))
            self.assertEqual(fake.ldif, [])

    def test_membership_write_keeps_service_members(self):
        fake = Fake()
        fake.run(request("setGroupMembers", group="family", members=["zackw"]))
        (tool, _, text), = fake.ldif
        self.assertIn("replace: uniqueMember", text)
        self.assertEqual(text.count("uniqueMember::"), 2)
        fake = Fake()
        fake.s["groups"]["family"]["uniqueMember"] = [dn("sam")]
        fake.run(request("setGroupMembers", group="family", members=[]))
        self.assertTrue(fake.ldif[0][2].endswith("replace: uniqueMember\nuniqueMember:\n"))

    def test_lucia_users_changes_go_to_authentik(self):
        fake = Fake()
        fake.run(request("setUserGroups", username="zackw", groups=["lucia-owners", "lucia-users"]))
        self.assertEqual(fake.ldif, [])
        self.assertEqual(fake.api, [("POST", people.GROUPS + "a-users/add_user/", {"pk": 7})])

    def test_app_access_keeps_owners_and_skips_hidden(self):
        fake = Fake()
        fake.run(request("setAppGroups", app="lucia-app-immich", groups=["lucia-users"]))
        self.assertEqual([(m, path) for m, path, _ in fake.api],
                         [("DELETE", people.BINDINGS + "b-family/"), ("POST", people.BINDINGS)])
        self.assertEqual(fake.api[1][2]["group"], "a-users")
        fake = Fake()
        with self.assertRaises(people.Refused):
            fake.run(request("setAppGroups", app="lucia", groups=[]))

    def test_create_user_sets_password_then_groups(self):
        fake = Fake()
        fake.run(request("createUser", username="kim", name="Kim Park", email="",
                         password="Correct horse 9 battery~", groups=["family"]))
        self.assertEqual([tool for tool, _, _ in fake.ldif if tool != "ldapsearch"], ["ldapadd", "ldappasswd", "ldapmodify", "ldapmodify"])
        self.assertIn(base64.b64encode(dn("kim").encode()).decode(), fake.ldif[-1][2])
        self.assertNotIn("battery", repr([text for tool, _, text in fake.ldif if tool != "ldappasswd"]))

    def test_disable_and_enable(self):
        fake = Fake()
        fake.run(request("setUserActive", username="sam", active=True))
        self.assertIn("delete: pwdAccountLockedTime", fake.ldif[0][2])
        self.assertEqual(fake.api, [("PATCH", people.USERS + "8/", {"is_active": True})])


class Snapshot(unittest.TestCase):
    def test_snapshot_holds_only_visible_names(self):
        fake = Fake()
        fake.s["groups"]["lucia-owners"]["description"] = ["Lucia initial owner 0"]
        with fake.patches()[2]:
            value = people.snapshot(fake, "i", fake.s)
        self.assertEqual([group["name"] for group in value["groups"]], ["family", "lucia-owners", "lucia-users"])
        self.assertEqual(value["groups"][2]["kind"], "lucia")
        self.assertNotIn("initial owner", value["groups"][1]["description"])
        sam, zack = value["users"]
        self.assertEqual((sam["active"], sam["groups"], zack["protected"], zack["email"]),
                         (False, ["family", "lucia-users"], True, "z@example.com"))
        self.assertEqual(value["apps"][1]["groups"], ["family", "lucia-owners"])
        self.assertNotIn("password", repr(value).lower().replace("passwordchange", ""))


if __name__ == "__main__":
    unittest.main()
