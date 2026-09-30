"""People, groups and app access for Lucia's LDAP directory and Authentik.

The web host queues one owner-requested change per file in host/data/domains/directory-requests/; the native
activation worker passes each to apply() and publishes snapshot() as directory.json, which holds no secrets.
LDAP holds people, passwords and groups, since servers and NAS shares read it too. Authentik syncs them for
sign-in and also holds lucia-users (access to Lucia) and each app's group bindings.

Guards: the enrolled owner can't be deleted, disabled or removed from lucia-owners; lucia-owners stays bound to
every app; only onboarded Lucia apps (lucia-app-*) take bindings, never Lucia's own registration; and service
accounts, the LDAP admin groups and Authentik's built-ins are never shown or changed. Names that Linux servers
already use, such as sudo or docker, are refused so a directory group can't grant local rights.
"""

import base64
import datetime
import re
import string
import time

import app_clients
from application import APPLICATIONS, BINDINGS, GROUPS, _api, _list
from owner import load_owner

USERS = "/api/v3/core/users/"
SOURCE = "/api/v3/sources/ldap/lucia-ldap/"
SYNC = "authentik.sources.ldap.tasks.ldap_sync"
OWNERS, MEMBERS = "lucia-owners", "lucia-users"
HIDDEN = {"ldap-admins", "ldap-password-reset"}
REQUEST = re.compile(r"\d{17}-[0-9a-f]{32}")
NAME = re.compile(r"[a-z][a-z0-9_-]{0,31}")
EMAIL = re.compile(r"[^@\s]{1,64}@[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?\.[A-Za-z]{2,63}")
LOCKED = "000001010000Z"
PREFIXES = ("systemd-", "node-", "lucia-", "ldap-", "ak-", "authentik", "_")
RESERVED_USERS = {
    "root", "admin", "administrator", "akadmin", "authentik", "nobody", "daemon", "bin", "sys", "sync", "games", "man",
    "lp", "mail", "news", "uucp", "proxy", "www-data", "backup", "list", "irc", "gnats", "sshd", "messagebus", "debian",
    "ubuntu", "lucia", "docker", "polkitd", "avahi", "ntp", "postfix", "operator", "guest", "support", "nvidia",
}
RESERVED_GROUPS = RESERVED_USERS | {
    "sudo", "wheel", "adm", "admins", "administrators", "lxd", "libvirt", "kvm", "disk", "shadow", "staff", "users",
    "video", "render", "audio", "plugdev", "netdev", "input", "tty", "dialout", "cdrom", "floppy", "tape", "nogroup",
    "ssh", "utmp", "kmem", "src", "sasl", "voice", "fax", "gpio", "i2c", "spi", "crontab", "ssl-cert", "owners",
}
FIELDS = {
    "createUser": {"username", "name", "email", "password", "groups"},
    "updateUser": {"username", "name", "email"},
    "setUserGroups": {"username", "groups"},
    "setPassword": {"username", "password"},
    "setUserActive": {"username", "active"},
    "deleteUser": {"username"},
    "createGroup": {"group", "description"},
    "updateGroup": {"group", "description"},
    "setGroupMembers": {"group", "members"},
    "deleteGroup": {"group"},
    "setAppGroups": {"app", "groups"},
}
COMMON = {"schemaVersion", "id", "action", "requestedBy", "requestedAt"}


class Refused(ValueError):
    """A change the owner can fix; its message is shown to them."""

    def __init__(self, message):
        super().__init__(message)
        self.message = message


def _ldap(p, tool, *args, text=None):
    return p.run(["docker", "exec", "-i", "lucia-identity-ldap", tool, "-x", "-H", "ldap://127.0.0.1:389",
                  "-D", "uid=admin," + p.settings["ldap_base_dn"], "-y", "/run/secrets/ldap-admin-password", *args],
                 input_text=text, check=False)


def _search(p, base, query, *attributes):
    result = _ldap(p, "ldapsearch", "-LLL", "-o", "ldif-wrap=no", "-b", base, query, *attributes)
    if result.returncode == 32:
        return []
    if result.returncode:
        raise RuntimeError("The directory could not be read.")
    entries = []
    for block in result.stdout.strip().split("\n\n"):
        entry = {}
        for line in block.splitlines():
            if not line or line.startswith("#"):
                continue
            key, value = line.split(":", 1)
            value = base64.b64decode(value[1:].strip()).decode() if value.startswith(":") else value.strip()
            entry.setdefault(key, []).append(value)
        if entry:
            entries.append(entry)
    return entries


def _value(name, value):
    return f"{name}:: {base64.b64encode(value.encode()).decode()}\n"


def _change(p, ldif, tool="ldapmodify"):
    result = _ldap(p, tool, text=ldif)
    if result.returncode == 19:
        raise Refused("That name or email is already used by another person or group in the directory.")
    if result.returncode == 68:
        raise Refused("That name is already used in the directory.")
    if result.returncode:
        raise RuntimeError("The directory refused the change.")


def _text(value, limit, what, optional=False):
    if not isinstance(value, str) or any(ord(c) < 32 or ord(c) == 127 for c in value) or len(value.strip()) > limit \
            or not optional and not value.strip():
        raise Refused(f"Enter {'an' if what[0] in 'aeiou' else 'a'} {what} of up to {limit} characters.")
    return value.strip()


def _email(value):
    value = _text(value, 254, "email address", optional=True)
    if value and not EMAIL.fullmatch(value):
        raise Refused("Enter a valid email address, or leave it empty.")
    return value


def check_password(value):
    """The directory's password policy: 14+ characters with upper, lower, digit and symbol."""
    if (not isinstance(value, str) or not 14 <= len(value) <= 256 or any(ord(c) < 32 or ord(c) == 127 for c in value)
            or not any(c in string.ascii_uppercase for c in value) or not any(c in string.ascii_lowercase for c in value)
            or not any(c in string.digits for c in value) or not any(c in string.punctuation for c in value)):
        raise Refused("Passwords need 14 or more characters, with an uppercase letter, a lowercase letter, a digit and a symbol.")
    return value


def _name(value, reserved, what):
    if not isinstance(value, str) or not NAME.fullmatch(value) or value in reserved or value.startswith(PREFIXES):
        raise Refused(f"{what} use 1–32 lowercase letters, digits, - or _, start with a letter, and can't be a name "
                      "servers already use.")
    return value


def _state(p, installation):
    base = p.settings["ldap_base_dn"]
    people = {entry["uid"][0]: entry for entry in _search(
        p, "ou=Users," + base, "(objectClass=posixAccount)", "uid", "cn", "mail", "entryUUID", "pwdAccountLockedTime")
        if len(entry.get("uid", [])) == 1 and len(entry.get("entryUUID", [])) == 1}
    groups = {entry["cn"][0]: entry for entry in _search(
        p, "ou=Groups," + base, "(objectClass=groupOfUniqueNames)", "cn", "description", "uniqueMember", "entryUUID")
        if len(entry.get("cn", [])) == 1 and len(entry.get("entryUUID", [])) == 1}
    apps = []
    for app in _list(p, APPLICATIONS, superuser_full_list="true"):
        stack = app["slug"][len(app_clients.PREFIX):]
        if app["slug"].startswith(app_clients.PREFIX) and app.get("meta_description") == app_clients._marker(stack, installation):
            apps.append({**app, "bindings": _list(p, BINDINGS, target=app["pk"])})
    return {"base": base, "owner": load_owner(p)["username"], "people": people, "groups": groups,
            "ak_users": _list(p, USERS), "ak_groups": _list(p, GROUPS), "apps": apps}


def _dn(s, username):
    return f"uid={username},ou=Users,{s['base']}"


def _ak_user(s, username):
    uniq = s["people"][username]["entryUUID"][0]
    return next((user for user in s["ak_users"] if user.get("attributes", {}).get("ldap_uniq") == uniq), None)


def _ak_group(s, name):
    if name == MEMBERS:
        return next((group for group in s["ak_groups"] if group["name"] == MEMBERS
                     and "ldap_uniq" not in group.get("attributes", {})), None)
    uniq = s["groups"][name]["entryUUID"][0]
    return next((group for group in s["ak_groups"] if group.get("attributes", {}).get("ldap_uniq") == uniq), None)


def _visible(s):
    names = [name for name in s["groups"] if name not in HIDDEN]
    return names + ([MEMBERS] if MEMBERS not in s["groups"] and _ak_group(s, MEMBERS) else [])


def _members(s, name):
    if name == MEMBERS:
        group = _ak_group(s, MEMBERS)
        return {username for username in s["people"]
                if (user := _ak_user(s, username)) is not None and group and user["pk"] in group["users"]}
    dns = {member.lower() for member in s["groups"][name].get("uniqueMember", [])}
    return {username for username in s["people"] if _dn(s, username).lower() in dns}


def snapshot(p, installation, s=None):
    s = s or _state(p, installation)
    visible = _visible(s)
    members = {name: _members(s, name) for name in visible}
    by_pk = {}
    for name in visible:
        if group := _ak_group(s, name):
            by_pk[group["pk"]] = name
    users = []
    for username, entry in sorted(s["people"].items()):
        user = _ak_user(s, username)
        users.append({
            "username": username, "name": entry["cn"][0],
            "email": (entry.get("mail") or [user["email"] if user else ""])[0] or None,
            "active": "pwdAccountLockedTime" not in entry and (user is None or user["is_active"]),
            "protected": username == s["owner"], "synced": user is not None,
            "groups": sorted(name for name in visible if username in members[name]),
        })
    groups = [{
        "name": name, "description": "Full control of Lucia, every server and every app." if name == OWNERS
        else (s["groups"][name].get("description") or [""])[0] if name in s["groups"]
        else "Can open Lucia and use its local AI.", "kind": "directory" if name in s["groups"] else "lucia",
        "protected": name in (OWNERS, MEMBERS), "members": sorted(members[name]),
    } for name in sorted(visible)]
    apps = [{"slug": "lucia", "name": "Lucia", "launchUrl": None, "fixed": True, "groups": [OWNERS, MEMBERS]}]
    for app in sorted(s["apps"], key=lambda item: item["name"].lower()):
        bound = {by_pk[item["group"]] for item in app["bindings"]
                 if item.get("group") in by_pk and item["enabled"] and not item["negate"]}
        apps.append({"slug": app["slug"], "name": app["name"], "launchUrl": app.get("meta_launch_url") or None,
                     "fixed": False, "groups": sorted(bound)})
    source = _api(p, "GET", SOURCE)
    return {"schemaVersion": 1, "checkedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "passwordChange": bool(source.get("sync_users_password")), "users": users, "groups": groups, "apps": apps}


def _sync(p):
    """Runs Authentik's LDAP sync now and waits for it to finish."""
    started = datetime.datetime.now(datetime.timezone.utc)
    source = _api(p, "GET", SOURCE)
    schedules = [item for item in _list(p, "/api/v3/tasks/schedules/", rel_obj_id=source["pk"])
                 if item.get("actor_name") == SYNC and item.get("uid") == SYNC + ":lucia-ldap"]
    if len(schedules) != 1:
        raise RuntimeError("Authentik's directory sync schedule is missing.")
    _api(p, "POST", f"/api/v3/tasks/schedules/{schedules[0]['id']}/send/")
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        time.sleep(2)
        status = _api(p, "GET", SOURCE + "sync/status/")
        last = status.get("last_successful_sync")
        if last and not status.get("is_running") and datetime.datetime.fromisoformat(last.replace("Z", "+00:00")) >= started:
            return
    raise RuntimeError("Authentik didn't finish syncing the directory.")


def _person(s, value):
    if not isinstance(value, str) or value not in s["people"]:
        raise Refused("That person is no longer in the directory. Refresh and try again.")
    return value


def _group(s, value):
    if not isinstance(value, str) or value not in _visible(s):
        raise Refused("That group is no longer in the directory. Refresh and try again.")
    return value


def _list_of(values, check, limit=200):
    if not isinstance(values, list) or len(values) > limit or len(set(map(str, values))) != len(values):
        raise Refused("Lucia sent an invalid list.")
    return {check(value) for value in values}


def _set_password(p, s, username, password):
    dn = _dn(s, username)
    if user := _ak_user(s, username):
        # Authentik keeps its own copy once someone changes their password there; keep it in step.
        _api(p, "POST", f"{USERS}{user['pk']}/set_password/", {"password": password})
    if _ldap(p, "ldappasswd", "-T", "/dev/stdin", dn, text=password).returncode:
        raise RuntimeError("The directory refused the password.")
    _change(p, f"dn: {dn}\nchangetype: modify\nreplace: pwdReset\npwdReset: FALSE\n")


def _write_members(p, s, name, wanted):
    """Sets a group's members to exactly these people, keeping entries Lucia doesn't show."""
    if name == MEMBERS:
        return False
    current = [member for member in s["groups"][name].get("uniqueMember", []) if member]
    people = {_dn(s, username).lower() for username in s["people"]}
    kept = [member for member in current if member.lower() not in people]
    result = kept + [_dn(s, username) for username in sorted(wanted)]
    if sorted(map(str.lower, result)) == sorted(map(str.lower, current)):
        return False
    lines = "".join(_value("uniqueMember", member) for member in result) or "uniqueMember:\n"
    _change(p, f"dn: cn={name},ou=Groups,{s['base']}\nchangetype: modify\nreplace: uniqueMember\n{lines}")
    return True


def _write_lucia_members(p, s, wanted):
    group = _ak_group(s, MEMBERS)
    if group is None:
        if wanted:
            raise Refused("Lucia's access group isn't ready yet.")
        return
    for username in s["people"]:
        user = _ak_user(s, username)
        inside = user is not None and user["pk"] in group["users"]
        if (username in wanted) != inside:
            if user is None:
                raise Refused("Authentik hasn't picked up this person yet. Try again in a minute.")
            action = "add_user" if username in wanted else "remove_user"
            _api(p, "POST", f"{GROUPS}{group['pk']}/{action}/", {"pk": user["pk"]})


def _memberships(p, s, installation, changes):
    """changes: group name -> wanted members. Writes LDAP, syncs once, then Authentik-only groups."""
    ldap = [_write_members(p, s, name, wanted) for name, wanted in changes.items() if name != MEMBERS]
    if any(ldap):
        _sync(p)
    if MEMBERS in changes:
        s = _state(p, installation) if any(ldap) else s
        _write_lucia_members(p, s, changes[MEMBERS])


def _next_number(p, s):
    used = [int(value) for entry in _search(p, s["base"], "(|(uidNumber=*)(gidNumber=*))", "uidNumber", "gidNumber")
            for key in ("uidNumber", "gidNumber") for value in entry.get(key, []) if value.isdigit()]
    return max([9999, *used]) + 1


def _create_user(p, s, v, installation):
    username = _name(v["username"], RESERVED_USERS, "Usernames")
    if username in s["people"] or any(user["username"] == username for user in s["ak_users"]):
        raise Refused("That username is already taken.")
    name, email, password = _text(v["name"], 64, "name"), _email(v["email"]), check_password(v["password"])
    groups = _list_of(v["groups"], lambda value: _group(s, value), 64)
    number = _next_number(p, s)
    dn = _dn(s, username)
    _change(p, f"dn: {dn}\nobjectClass: inetOrgPerson\nobjectClass: posixAccount\nuid: {username}\n"
            + _value("cn", name) + _value("sn", name.split()[-1]) + (_value("mail", email) if email else "")
            + f"uidNumber: {number}\ngidNumber: {number}\nhomeDirectory: /home/{username}\nloginShell: /bin/bash\n", "ldapadd")
    s = _state(p, installation)
    _set_password(p, s, username, password)
    changes = {group: _members(s, group) | {username} for group in groups}
    if any(group != MEMBERS for group in groups):
        _memberships(p, s, installation, changes)
    else:
        _sync(p)
        _memberships(p, _state(p, installation), installation, changes)


def _update_user(p, s, v, installation):
    username = _person(s, v["username"])
    name, email = _text(v["name"], 64, "name"), _email(v["email"])
    lines = _value("cn", name) + "-\n" + "replace: sn\n" + _value("sn", name.split()[-1]) + "-\n" + "replace: mail\n" \
        + (_value("mail", email) if email else "")
    _change(p, f"dn: {_dn(s, username)}\nchangetype: modify\nreplace: cn\n{lines}")
    _sync(p)
    if user := _ak_user(_state(p, installation), username):
        _api(p, "PATCH", f"{USERS}{user['pk']}/", {"name": name, "email": email})


def _set_user_groups(p, s, v, installation):
    username = _person(s, v["username"])
    wanted = _list_of(v["groups"], lambda value: _group(s, value), 64)
    if username == s["owner"] and OWNERS not in wanted:
        raise Refused("Lucia's first owner always stays in lucia-owners.")
    changes = {}
    for name in _visible(s):
        members = _members(s, name)
        if (name in wanted) != (username in members):
            changes[name] = members | {username} if name in wanted else members - {username}
    _memberships(p, s, installation, changes)


def _set_password_request(p, s, v, installation):
    _set_password(p, s, _person(s, v["username"]), check_password(v["password"]))


def _set_user_active(p, s, v, installation):
    username = _person(s, v["username"])
    if not isinstance(v["active"], bool):
        raise Refused("Lucia sent an invalid change.")
    if username == s["owner"] and not v["active"]:
        raise Refused("Lucia's first owner can't be disabled.")
    locked = "pwdAccountLockedTime" in s["people"][username]
    if v["active"] and locked:
        _change(p, f"dn: {_dn(s, username)}\nchangetype: modify\ndelete: pwdAccountLockedTime\n")
    elif not v["active"] and not locked:
        _change(p, f"dn: {_dn(s, username)}\nchangetype: modify\nreplace: pwdAccountLockedTime\npwdAccountLockedTime: {LOCKED}\n")
    if (user := _ak_user(s, username)) and user["is_active"] != v["active"]:
        _api(p, "PATCH", f"{USERS}{user['pk']}/", {"is_active": v["active"]})


def _delete_user(p, s, v, installation):
    username = _person(s, v["username"])
    if username == s["owner"]:
        raise Refused("Lucia's first owner can't be deleted.")
    user = _ak_user(s, username)
    # The directory drops the person from every group with them (refint).
    result = _ldap(p, "ldapdelete", _dn(s, username))
    if result.returncode not in (0, 32):
        raise RuntimeError("The directory refused the change.")
    if user:
        _api(p, "DELETE", f"{USERS}{user['pk']}/")


def _create_group(p, s, v, installation):
    name = _name(v["group"], RESERVED_GROUPS, "Group names")
    if name in s["groups"] or any(group["name"].lower() == name for group in s["ak_groups"]):
        raise Refused("A group with that name already exists.")
    description = _text(v["description"], 200, "description", optional=True)
    number = _next_number(p, s)
    _change(p, f"dn: cn={name},ou=Groups,{s['base']}\nobjectClass: groupOfUniqueNames\nobjectClass: posixGroup\n"
            f"cn: {name}\ngidNumber: {number}\nuniqueMember:\n" + (_value("description", description) if description else ""),
            "ldapadd")
    _sync(p)


def _update_group(p, s, v, installation):
    name = _group(s, v["group"])
    if name in (OWNERS, MEMBERS):
        raise Refused(f"{name} is managed by Lucia.")
    description = _text(v["description"], 200, "description", optional=True)
    _change(p, f"dn: cn={name},ou=Groups,{s['base']}\nchangetype: modify\nreplace: description\n"
            + (_value("description", description) if description else ""))


def _set_group_members(p, s, v, installation):
    name = _group(s, v["group"])
    wanted = _list_of(v["members"], lambda value: _person(s, value))
    if name == OWNERS and s["owner"] not in wanted:
        raise Refused("Lucia's first owner always stays in lucia-owners.")
    _memberships(p, s, installation, {name: wanted})


def _delete_group(p, s, v, installation):
    name = _group(s, v["group"])
    if name in (OWNERS, MEMBERS):
        raise Refused(f"{name} is managed by Lucia and can't be deleted.")
    group = _ak_group(s, name)
    result = _ldap(p, "ldapdelete", f"cn={name},ou=Groups,{s['base']}")
    if result.returncode not in (0, 32):
        raise RuntimeError("The directory refused the change.")
    if group:
        _api(p, "DELETE", f"{GROUPS}{group['pk']}/")


def _set_app_groups(p, s, v, installation):
    app = next((item for item in s["apps"] if item["slug"] == v["app"]), None)
    if app is None:
        raise Refused("That app doesn't sign in with Lucia any more. Refresh and try again.")
    wanted = _list_of(v["groups"], lambda value: _group(s, value), 64) | {OWNERS}
    if any(_ak_group(s, name) is None for name in wanted):
        _sync(p)
        s = _state(p, installation)
        app = next(item for item in s["apps"] if item["slug"] == v["app"])
    groups = {}
    for name in _visible(s):
        if group := _ak_group(s, name):
            groups[group["pk"]] = name
    missing = [name for name in wanted if name not in groups.values()]
    if missing:
        raise Refused("Authentik hasn't picked up that group yet. Try again in a minute.")
    bound = set()
    for binding in app["bindings"]:
        name = groups.get(binding.get("group"))
        if name is None or binding.get("policy") or binding.get("user"):
            continue
        if name in wanted and binding["enabled"] and not binding["negate"]:
            bound.add(name)
        elif name != OWNERS:
            _api(p, "DELETE", f"{BINDINGS}{binding['pk']}/")
    order = max([0, *(item["order"] for item in app["bindings"] if item["order"] < 1000000)]) + 1
    for name in sorted(wanted - bound):
        pk = next(key for key, value in groups.items() if value == name)
        _api(p, "POST", BINDINGS, {"target": app["pk"], "group": pk, "policy": None, "user": None, "negate": False,
                                   "enabled": True, "order": order, "timeout": 30, "failure_result": False})
        order += 1


ACTIONS = {
    "createUser": _create_user, "updateUser": _update_user, "setUserGroups": _set_user_groups,
    "setPassword": _set_password_request, "setUserActive": _set_user_active, "deleteUser": _delete_user,
    "createGroup": _create_group, "updateGroup": _update_group, "setGroupMembers": _set_group_members,
    "deleteGroup": _delete_group, "setAppGroups": _set_app_groups,
}


def apply(p, value, request_id, installation):
    """Applies one queued change or raises Refused with a message for the owner."""
    action = value.get("action") if isinstance(value, dict) else None
    if (action not in ACTIONS or set(value) != COMMON | FIELDS[action] or type(value["schemaVersion"]) is not int
            or value["schemaVersion"] != 1 or value["id"] != request_id):
        raise Refused("The identity service didn't accept this change. Update Lucia and try again.")
    ACTIONS[action](p, _state(p, installation), value, installation)


def enable_password_changes(p):
    """Lets people change their own password in Authentik: Authentik writes it back to LDAP, and only passwords."""
    base = p.settings["ldap_base_dn"]
    reader, dn = "uid=authentik,ou=Services," + base, "cn=ldap-password-reset,ou=Groups," + base
    found = _search(p, "ou=Groups," + base, "(cn=ldap-password-reset)", "uniqueMember")
    if not found:
        _change(p, f"dn: {dn}\nobjectClass: groupOfUniqueNames\ncn: ldap-password-reset\nuniqueMember: {reader}\n"
                "description: Lucia: may set directory passwords\n", "ldapadd")
    elif reader.lower() not in {member.lower() for member in found[0].get("uniqueMember", [])}:
        _change(p, f"dn: {dn}\nchangetype: modify\nadd: uniqueMember\nuniqueMember: {reader}\n")
    # A change Authentik writes for someone would otherwise mark it as an admin reset that must be changed again.
    policy = "cn=DefaultPasswordPolicy,ou=Policies," + base
    if any(entry.get("pwdMustChange") == ["TRUE"] for entry in _search(p, policy, "(objectClass=pwdPolicy)", "pwdMustChange")):
        _change(p, f"dn: {policy}\nchangetype: modify\nreplace: pwdMustChange\npwdMustChange: FALSE\n")
    if not _api(p, "GET", SOURCE).get("sync_users_password"):
        _api(p, "PATCH", SOURCE, {"sync_users_password": True})
