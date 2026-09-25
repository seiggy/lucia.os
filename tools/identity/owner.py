"""Initial LDAP-backed owner enrollment and real Authentik password sign-in.

provision.py --request-stdin accepts a private supplied password. Incomplete
enrollment retains it in secrets/owner-request-password (0600), separate from
legacy generated credentials. Only that request credential is removed after
verified enrollment; completed owners are verified, never password-reset.
Creation enforces the current password policy; existing sign-in passes a
nonempty supplied password unchanged to Authentik, including older policies.
"""

import base64
import http.cookiejar
import hmac
import json
import os
import pathlib
import re
import secrets
import ssl
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

from provision import authority, emit, exactly_one, write_file


def validate_username(username):
    if not re.fullmatch(r"[a-z][a-z0-9_-]{0,31}", username or "") or username in (
        "root", "admin", "akadmin", "authentik", "nobody",
    ):
        raise ValueError("Owner username must be 1-32 lowercase letters, digits, underscores, or hyphens, start with a letter, and not be a reserved service/admin name.")
    return username


def validate_password(password):
    if not isinstance(password, str) or not 14 <= len(password) <= 1024 or any(c in password for c in "\r\n\0"):
        raise ValueError("Owner password must contain 14-1024 characters, without newline or NUL characters.")
    return password


def validate_login_password(password):
    if not isinstance(password, str) or not password:
        raise ValueError("The existing owner password must not be empty.")
    return password


def load_owner(p):
    path = p.state / "owner.json"
    if not path.exists():
        raise RuntimeError("No LDAP-backed owner is enrolled. Run owner --owner-username <username>; infrastructure alone is not a complete identity setup.")
    record = json.loads(path.read_text())
    validate_username(record["username"])
    return record


def ldap_command(p, tool, *args, input_text=None):
    return p.run(["docker", "exec", "-i", "lucia-identity-ldap", tool, "-x", "-H", "ldap://127.0.0.1:389",
                  "-D", "uid=admin," + p.settings["ldap_base_dn"], "-y", "/run/secrets/ldap-admin-password",
                  *args], input_text=input_text)


def ldap_search(p, dn, query, *attributes):
    result = ldap_command(p, "ldapsearch", "-LLL", "-o", "ldif-wrap=no", "-b", p.settings["ldap_base_dn"],
                          query, *attributes)
    entries = []
    for block in result.stdout.strip().split("\n\n"):
        entry = {}
        for line in block.splitlines():
            if not line or line.startswith("#"):
                continue
            key, value = line.split(":", 1)
            value = base64.b64decode(value[1:].strip()).decode() if value.startswith(":") else value.lstrip()
            entry.setdefault(key, []).append(value)
        if entry and (dn is None or entry.get("dn") == [dn]):
            entries.append(entry)
    return entries


def enroll_ldap_owner(p, username=None, password=None):
    path = p.state / "owner.json"
    generated_path = p.state / "secrets" / "owner-initial-password"
    request_path = p.state / "secrets" / "owner-request-password"
    record = load_owner(p) if path.exists() else None
    if password is not None:
        validator = validate_login_password if record and record.get("complete") is True else validate_password
        p.values["owner-request-password"] = validator(password)
    base = p.settings["ldap_base_dn"]
    if record is not None:
        if username and username != record["username"]:
            raise RuntimeError("An initial owner already exists. Owner changes require an explicit account migration.")
    else:
        username = validate_username(username)
        if ldap_search(p, None, f"(uid={username})", "uid"):
            raise RuntimeError("That LDAP username already exists; refusing to adopt or elevate an existing account.")
        users = p.api("GET", "/api/v3/core/users/?" + urllib.parse.urlencode({"username": username}))["results"]
        if any(user["username"] == username for user in users):
            raise RuntimeError("That Authentik username already exists; refusing to merge or elevate it.")
        if ldap_search(p, None, "(cn=lucia-owners)", "cn"):
            raise RuntimeError("The Lucia owners group already exists without an enrollment record; refusing to adopt it.")
        if any(candidate.exists() or candidate.is_symlink() for candidate in (generated_path, request_path)):
            raise RuntimeError("An owner password exists without an enrollment record. Recover the record before proceeding.")
        entries = ldap_search(p, None, "(|(uidNumber=*)(gidNumber=*))", "uidNumber", "gidNumber")
        used = [int(value) for entry in entries for key in ("uidNumber", "gidNumber") for value in entry.get(key, [])]
        number = max([9999, *used]) + 1
        record = {"schema_version": 1, "username": username, "uid_number": number, "gid_number": number,
                  "marker": "Lucia initial owner " + str(uuid.uuid4()), "complete": False}
        password_path = generated_path
        if password is not None:
            record["credential_source"] = "request"
            password_path = request_path
        with os.fdopen(os.open(password_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "w") as output:
            output.write(password if password is not None else secrets.token_urlsafe(24) + "aA1!")
        write_file(path, json.dumps(record, indent=2) + "\n")

    password_path = request_path if record.get("credential_source") == "request" else generated_path
    if not record["complete"]:
        if password_path.is_symlink() or not password_path.is_file():
            raise RuntimeError("Initial owner credential is missing or unsafe. Restore it; reruns never reset an owner's password.")
        if record.get("credential_source") == "request":
            os.chmod(password_path, 0o600)
        p.values["owner-initial-password"] = password_path.read_text().rstrip("\r\n")
        minimum = 14 if record.get("credential_source") == "request" else 32
        if len(p.values["owner-initial-password"]) < minimum:
            raise RuntimeError("Initial owner credential is incomplete. Restore it instead of regenerating it.")
        if password is not None and not hmac.compare_digest(password.encode(), p.values["owner-initial-password"].encode()):
            raise RuntimeError("Incomplete enrollment has a different saved credential. Supply the original password; reruns never reset it.")
    username = record["username"]
    user_dn = f"uid={username},ou=Users,{base}"
    group_dn = f"cn=lucia-owners,ou=Groups,{base}"
    users = ldap_search(p, user_dn, f"(uid={username})", "uid", "uidNumber", "gidNumber", "description", "entryUUID", "userPassword")
    if not users:
        if record["complete"] or record.get("user_uuid"):
            raise RuntimeError("The enrolled LDAP owner disappeared. Restore the directory; refusing to recreate the identity.")
        ldap_command(p, "ldapadd", input_text=(
            f"dn: {user_dn}\nobjectClass: inetOrgPerson\nobjectClass: posixAccount\n"
            f"uid: {username}\ncn: {username}\nsn: {username}\n"
            f"uidNumber: {record['uid_number']}\ngidNumber: {record['gid_number']}\n"
            f"homeDirectory: /home/{username}\nloginShell: /bin/bash\ndescription: {record['marker']}\n"))
        users = ldap_search(p, user_dn, f"(uid={username})", "uidNumber", "gidNumber", "description", "entryUUID", "userPassword")
    user = exactly_one(users, "enrolled LDAP owner")
    if (user.get("description") != [record["marker"]] or user.get("uidNumber") != [str(record["uid_number"])]
            or user.get("gidNumber") != [str(record["gid_number"])]
            or record.get("user_uuid", user["entryUUID"][0]) != user["entryUUID"][0]):
        raise RuntimeError("LDAP owner identity drifted; refusing to reset or elevate a different account.")
    if not record["complete"]:
        password = p.values["owner-initial-password"]
        if "userPassword" not in user:
            ldap_command(p, "ldappasswd", "-T", "/dev/stdin", user_dn, input_text=password)
        ldap_command(p, "ldapmodify", input_text=f"dn: {user_dn}\nchangetype: modify\nreplace: pwdReset\npwdReset: FALSE\n")
        p.run(["docker", "exec", "-i", "-e", "LDAPTLS_CACERT=/trust/lucia-root-ca.crt",
               "-e", "LDAPTLS_REQCERT=demand", "lucia-identity-ldap", "ldapwhoami", "-x",
               "-H", "ldaps://identity-gateway:8636", "-D", user_dn, "-y", "/dev/stdin"], input_text=password)

    groups = ldap_search(p, group_dn, "(cn=lucia-owners)", "description", "gidNumber", "entryUUID", "uniqueMember")
    if not groups:
        if record["complete"] or record.get("group_uuid"):
            raise RuntimeError("The enrolled owners group disappeared; restore it instead of recreating its identity.")
        ldap_command(p, "ldapadd", input_text=(
            f"dn: {group_dn}\nobjectClass: groupOfUniqueNames\nobjectClass: posixGroup\ncn: lucia-owners\n"
            f"gidNumber: {record['gid_number']}\nuniqueMember: {user_dn}\ndescription: {record['marker']}\n"))
        groups = ldap_search(p, group_dn, "(cn=lucia-owners)", "description", "gidNumber", "entryUUID", "uniqueMember")
    group = exactly_one(groups, "enrolled owners group")
    if (group.get("description") != [record["marker"]] or group.get("gidNumber") != [str(record["gid_number"])]
            or record.get("group_uuid", group["entryUUID"][0]) != group["entryUUID"][0]
            or user_dn not in group.get("uniqueMember", [])):
        raise RuntimeError("LDAP owner group or membership changed; review the change before provisioning.")
    admins_dn = f"cn=ldap-admins,ou=Groups,{base}"
    admins = exactly_one(ldap_search(p, admins_dn, "(cn=ldap-admins)", "uniqueMember"), "LDAP administrators group")
    if user_dn not in admins.get("uniqueMember", []):
        if record["complete"] or record.get("user_uuid"):
            raise RuntimeError("The initial owner's LDAP administrator membership was removed; refusing to undo that change silently.")
        ldap_command(p, "ldapmodify", input_text=f"dn: {admins_dn}\nchangetype: modify\nadd: uniqueMember\nuniqueMember: {user_dn}\n")
    record.update(user_uuid=user["entryUUID"][0], group_uuid=group["entryUUID"][0])
    write_file(path, json.dumps(record, indent=2) + "\n")
    emit("owner", "LDAP owner and administrative groups are enrolled; existing passwords were not reset.", username=username)
    return record


def owner_identity(p, record, grant=False):
    base = p.settings["ldap_base_dn"]
    dn = f"uid={record['username']},ou=Users,{base}"
    user = exactly_one(ldap_search(p, dn, f"(uid={record['username']})", "entryUUID"), "LDAP owner")
    group = exactly_one(ldap_search(p, f"cn=lucia-owners,ou=Groups,{base}", "(cn=lucia-owners)",
                                    "entryUUID", "uniqueMember"), "LDAP owners group")
    if user["entryUUID"] != [record["user_uuid"]] or group["entryUUID"] != [record["group_uuid"]] or dn not in group.get("uniqueMember", []):
        raise RuntimeError("Enrolled LDAP owner/group identity or membership changed.")
    admins = exactly_one(ldap_search(p, f"cn=ldap-admins,ou=Groups,{base}", "(cn=ldap-admins)", "uniqueMember"), "LDAP administrators group")
    if dn not in admins.get("uniqueMember", []):
        raise RuntimeError("The initial owner's LDAP administrator membership was removed.")
    source = p.api("GET", "/api/v3/sources/ldap/lucia-ldap/")
    users = p.api("GET", "/api/v3/core/users/?" + urllib.parse.urlencode({"username": record["username"]}))["results"]
    user = exactly_one([item for item in users if item["username"] == record["username"]], "synced owner")
    links = p.api("GET", f"/api/v3/sources/user_connections/ldap/?source__slug=lucia-ldap&user={user['pk']}")["results"]
    if not any(link["source"] == source["pk"] and link["user"] == user["pk"] and link["identifier"] == record["user_uuid"] for link in links):
        raise RuntimeError("Authentik owner is not linked to the enrolled LDAP identity; refusing to grant access.")
    groups = p.api("GET", "/api/v3/core/groups/?name=lucia-owners")["results"]
    group = exactly_one([item for item in groups if item["name"] == "lucia-owners"], "synced owners group")
    links = p.api("GET", f"/api/v3/sources/group_connections/ldap/?source__slug=lucia-ldap&group={group['pk']}")["results"]
    if not any(link["source"] == source["pk"] and link["group"] == group["pk"] and link["identifier"] == record["group_uuid"] for link in links):
        raise RuntimeError("Authentik owners group is not linked to the enrolled LDAP group; refusing to grant access.")
    if user["pk"] not in group["users"] or not user["is_active"]:
        raise RuntimeError("Owner is disabled or LDAP group membership has not synchronized.")
    if not group["is_superuser"] and grant and not record["complete"] and not record.get("authentik_admin_granted"):
        p.api("PATCH", f"/api/v3/core/groups/{group['pk']}/", {"is_superuser": True})
    elif not group["is_superuser"]:
        raise RuntimeError("Owners group's Authentik administrator permission was removed; review it explicitly.")
    if grant and not record["complete"] and not record.get("authentik_admin_granted"):
        record["authentik_admin_granted"] = True
        write_file(p.state / "owner.json", json.dumps(record, indent=2) + "\n")
    return user["pk"]


def verify_login(p, record, user_pk, password_file=None, reject_wrong_password=False, *, password=None):
    if password is not None and password_file is not None:
        raise ValueError("Choose private request data or a password file, not both.")
    if password is None:
        name = "owner-request-password" if record.get("credential_source") == "request" else "owner-initial-password"
        password_path = pathlib.Path(password_file).expanduser() if password_file else p.state / "secrets" / name
        if password_path.is_symlink():
            raise RuntimeError("Owner password file must not be a symbolic link.")
        password = password_path.read_text().rstrip("\r\n")
    validate_login_password(password)
    p.values["owner-login-password"] = password
    origin = "https://" + authority(p.settings["public_host"], p.settings["ports"]["authentik"])
    flow = "/api/v3/flows/executor/default-authentication-flow/?query="
    context = ssl.create_default_context(cafile=str(p.state / "trust/lucia-root-ca.crt"))
    user_agent = "Lucia-Owner-Setup/" + str(uuid.uuid4())

    def attempt(candidate, success):
        cookies = http.cookiejar.CookieJar()
        client = urllib.request.build_opener(urllib.request.HTTPSHandler(context=context), urllib.request.HTTPCookieProcessor(cookies))

        def request(method, path, body=None):
            csrf = next((cookie.value for cookie in cookies if cookie.name == "authentik_csrf"), "")
            req = urllib.request.Request(origin + path, method=method,
                                         data=None if body is None else json.dumps(body).encode(),
                                         headers={"Content-Type": "application/json", "X-authentik-CSRF": csrf,
                                                  "Referer": origin + "/if/flow/default-authentication-flow/",
                                                  "User-Agent": user_agent})
            try:
                with client.open(req, timeout=30) as response:
                    data = response.read()
                    return json.loads(data) if data else None
            except urllib.error.HTTPError as error:
                if error.code == 400 and path == flow:
                    return json.loads(error.read())
                raise RuntimeError(f"Owner sign-in {method} {path} returned HTTP {error.code}.") from error

        challenge = request("GET", flow)
        if challenge.get("component") != "ak-stage-identification":
            raise RuntimeError("Sign-in requires an unexpected stage; complete enrollment interactively instead.")
        challenge = request("POST", flow, {"component": "ak-stage-identification", "uid_field": record["username"]})
        if challenge.get("component") != "ak-stage-password":
            raise RuntimeError("LDAP owner did not reach the password stage.")
        challenge = request("POST", flow, {"component": "ak-stage-password", "password": candidate})
        if not success:
            if challenge.get("component") != "ak-stage-password" or not challenge.get("response_errors"):
                raise RuntimeError("Wrong-password rejection could not be confirmed.")
            return
        if challenge.get("component") != "xak-flow-redirect":
            raise RuntimeError("Owner password sign-in did not complete. Additional authentication or a valid password is required.")
        sessions = request("GET", "/api/v3/core/authenticated_sessions/")["results"]
        session = exactly_one([item for item in sessions if item["current"]], "temporary verification session")
        try:
            user = request("GET", "/api/v3/core/users/me/")["user"]
            if user["pk"] != user_pk or user["username"] != record["username"] or not user["is_superuser"]:
                raise RuntimeError("Password sign-in did not resolve to the expected LDAP-backed administrator.")
            source_pk = p.api("GET", "/api/v3/sources/ldap/lucia-ldap/")["pk"].replace("-", "")
            for _ in range(10):
                events = p.api("GET", "/api/v3/events/events/?action=login&ordering=-created&page_size=100")["results"]
                event = next((item for item in events if item.get("user", {}).get("username") == record["username"]
                              and item.get("context", {}).get("http_request", {}).get("user_agent") == user_agent), None)
                if event:
                    audit = event["context"]
                    if (audit.get("auth_method") != "ldap"
                            or audit.get("auth_method_args", {}).get("source", {}).get("pk", "").replace("-", "") != source_pk):
                        raise RuntimeError("Owner signed in through a different backend; directory-owned authentication was not verified.")
                    break
                time.sleep(0.5)
            else:
                raise RuntimeError("The owner login audit event was not recorded; LDAP authentication could not be verified.")
        finally:
            request("DELETE", f"/api/v3/core/authenticated_sessions/{session['uuid']}/")

    if reject_wrong_password:
        attempt(secrets.token_urlsafe(32) + "not-the-password", False)
    attempt(password, True)
    emit("owner-login", "LDAP-backed owner completed Authentik password sign-in with administrator access; the verification session was revoked.",
         username=record["username"])


def remove_request_credential(p, record):
    if record.get("credential_source") == "request" and record["complete"]:
        credential = p.state / "secrets/owner-request-password"
        if credential.is_symlink():
            raise RuntimeError("Saved request credential is unsafe; refusing to remove it.")
        credential.unlink(missing_ok=True)


def finish_owner(p, record, password=None):
    user_pk = owner_identity(p, record, grant=True)
    if not record["complete"]:
        verify_login(p, record, user_pk, reject_wrong_password=True, password=password)
        record["complete"] = True
        write_file(p.state / "owner.json", json.dumps(record, indent=2) + "\n")
    elif password is not None:
        verify_login(p, record, user_pk, password=password)
    remove_request_credential(p, record)
    if record.get("credential_source") == "request":
        emit("owner", "Owner enrollment is complete.", username=record["username"])
    else:
        emit("owner", "Owner enrollment is complete.", username=record["username"],
             initial_password_file=str(p.state / "secrets/owner-initial-password"))
