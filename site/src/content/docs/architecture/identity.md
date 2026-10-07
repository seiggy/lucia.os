---
title: Identity
description: How Lucia handles people, machines and certificates with OpenLDAP, Authentik and a private CA, and how to recover when something breaks.
section: architecture
order: 2
---

Lucia's identity stack is the part of the lab you hope never to think about. It runs on the Spark beside the host. People get a real directory account that works for the portal, the apps and SSH on every managed server. Machines get short-lived certificates instead of passwords.

## The pieces

| Component | What it does |
|---|---|
| **OpenLDAP** | The source of truth for people and groups. Exposed only as LDAPS on port 636 through the gateway |
| **Authentik** | Sign-in (OIDC) for the Lucia portal and for apps that support it. Port 9443 |
| **PostgreSQL** | Authentik's database. Never published |
| **step-ca** | Lucia's private certificate authority. Its ACME and API endpoint is on port 9444 |
| **Renewer** | Keeps gateway and service certificates fresh |
| **Traefik gateway** | Terminates TLS for all of the above |

The installer is `tools/identity/provision.py`. The desktop runs it for you. Its state lives on the Spark in `~/.local/share/lucia/identity`.

## People and groups

The directory base is `dc=lucia,dc=home,dc=arpa`, with people under `ou=Users` and groups under `ou=Groups`. POSIX user IDs start at 10000, so they never collide with local system accounts.

| Group | Meaning |
|---|---|
| `lucia-owners` | Lab owners. Full portal access, `sudo` on every managed server and Authentik administration |
| `ldap-admins` | Directory administrators. The first owner is a member |
| `ldap-password-reset` | Holds Authentik's service account, `uid=authentik`, so that password changes made in Authentik are written back to LDAP |

When someone changes their password in Authentik (Settings → Change password), Authentik's service account writes it to LDAP. Authentik's `akadmin` user is kept only as a break-glass account. Its password is stored in `secrets/authentik-admin-password` under the identity state directory.

### The owner account

The desktop's **Your account** step creates the first owner. It writes the account to LDAP and adds it to `ldap-admins` and `lucia-owners`. Setup isn't marked successful until a real sign-in through Authentik works. You can re-run either step from a checkout on the Spark:

```bash
python3 tools/identity/provision.py owner --owner-username <your-username>
python3 tools/identity/provision.py verify-owner-login
```

### Settings → People

Owners manage everyone else from **Settings → People**. The web host doesn't hold an Authentik admin token, so it can't change the directory itself. Instead it queues the request, and the `lucia-domain-activation` user service on the Spark applies it with `tools/identity/people.py`.

Some rules are enforced on purpose:

- The first owner can't be removed.
- Adding someone to `lucia-owners` needs an extra confirmation. Owners get `sudo` on every server and administer Authentik.
- A new person's temporary password is shown once.

### Apps that sign in with Lucia

Some catalog apps support single sign-on: Grafana, Immich and LiteLLM. For each one, Lucia registers an Authentik OIDC application named `lucia-app-<stack>` and binds it to `lucia-owners`. In Grafana, owners become admins.

## Certificates

### The private CA

Every Lucia install gets its own Smallstep CA on day one. It issues:

- the gateway's server certificates for the portal, Authentik, LDAPS and the CA itself, until a public domain takes over the browser-facing names;
- the enrollment certificates for managed servers.

The CA's private keys never leave the Spark. Only two public files are meant to be copied:

- `trust/lucia-root-ca.crt`
- `trust/fingerprint.txt` (the SHA-256 of the DER root)

Network installs carry the root's fingerprint and check it. For other Debian-family machines, the identity state directory includes `trust/install-node-trust.sh`. Run it as root. It validates the fingerprint, refuses to replace a different Lucia root, and calls `update-ca-certificates`:

```bash
sh install-node-trust.sh lucia-root-ca.crt <sha256-fingerprint>
```

> [!NOTE]
> The private CA publishes no CRL or OCSP endpoint. Clients that insist on revocation checking, including curl on Windows with Schannel, may reject its certificates even when you give them the CA file. Don't turn off certificate checking to work around it. Lucia keeps service and machine certificates short-lived to limit exposure.

### Public certificates

Once you connect a Cloudflare domain under **Settings → Domains**, the browser-facing names switch to Let's Encrypt certificates obtained by DNS-01. Machine and LDAPS traffic stays on the private CA. See [Networking and DNS](/docs/architecture/networking-and-dns/).

## Machine identities

Managed servers never use a person's password. During enrollment, the node agent generates a key pair on the node and sends a certificate signing request, authorized by the one-time installation grant. The host returns a certificate from the private CA, valid for about a day. The agent renews it when less than six hours remain.

Every request the agent makes is a proof: a signature over a fresh server challenge, bound to that certificate. A node that goes offline long enough for its certificate to lapse goes through a recovery path. Nothing skips verification.

People reach managed servers through SSH with their LDAP account, and only members of `lucia-owners` may log in. `sudo` asks for the same Lucia password.

Each server also has a local `lucia-recovery` account. It accepts only the recovery public key you supplied when you approved the installation, and its password is locked. It's there for when LDAP is unreachable.

## Recovery and backups

Back up `~/.local/share/lucia/identity` (encrypted). It holds the CA keys, the bootstrap secrets and the installation settings. The installer refuses to regenerate missing secrets. If they're gone, you restore them rather than quietly getting a new CA that nothing trusts.

> [!CAUTION]
> Don't run `aspire destroy` against a running identity install. Its teardown can remove the Docker volumes holding PostgreSQL and Authentik data. Aspire also derives volume names from the AppHost path, so moving the AppHost directory looks like a fresh install.

If you're locked out of the portal:

- **Your password is unknown:** another owner can set a temporary password in **Settings → People**, or you can use the `akadmin` break-glass account in Authentik.
- **The directory is down:** managed servers still accept the `lucia-recovery` key.
