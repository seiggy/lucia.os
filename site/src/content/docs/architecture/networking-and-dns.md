---
title: Networking and DNS
description: How Lucia's gateway, local DNS, Cloudflare certificates and optional public access fit together, and what Lucia changes on your network.
section: architecture
order: 3
---

Lucia's networking rule is: be useful on the LAN, publish nothing by default, and touch as little of your existing network as possible. One Traefik gateway on the Spark terminates TLS for everything. AdGuard Home answers the local names. Cloudflare is used only to prove you own the domain, unless you explicitly ask for public access.

![The DNS & certificates page showing Traefik routes, the Let's Encrypt certificate and AdGuard records](/screenshots/domains.png)

## The gateway

The Traefik v3.6 container is the Spark's only TLS front door.

| Port | What's behind it |
|---|---|
| 443 | Lucia portal and API, the Spark alias, and app routes such as `grafana.<namespace>` |
| 9443 | Authentik |
| 636 | LDAPS to OpenLDAP |
| 9444 | The private CA (step-ca) |
| Public entrypoint | Used only when public access is on (see below) |

Until you connect a domain, the gateway serves private-CA certificates for the Spark's own name, for example `https://spark-9423/`. The gateway's routes are generated files that Lucia rewrites and Traefik watches. The web host never gets the Docker socket to reload anything.

## Local DNS with AdGuard Home

Lucia uses AdGuard Home's rewrite rules for every local name. Connect it at **Settings → AdGuard** with the management URL, username and password:

- **HTTPS** must pass normal certificate validation. There's no "skip TLS" switch.
- **Plain HTTP** is allowed only for a private-LAN address, after a separate acknowledgement that the credentials cross the network in clear text.
- The saved connection is encrypted on the Spark. The password is never sent back to the browser.
- **Disconnect** removes Lucia's access, but leaves the rewrites it created in place.

Lucia never reads query logs or browsing history. It also won't silently turn DNS, protection or rewrites back on if you disabled them.

Once a domain is active, Lucia keeps these rewrites up to date:

| Rewrite | Points at | Kept by |
|---|---|---|
| Lucia, Authentik, Spark alias | The Spark | Domain activation |
| `<server>.<namespace>` for each managed server | The address from that server's latest heartbeat | A check every 5 minutes |
| App names such as `immich.<namespace>` | The Spark gateway, which forwards to the app | App routes |

Lucia adds only rules that are missing and exact. It keeps rules that already exist without taking ownership of them, and it blocks rather than overwrites when a rule conflicts. **Settings → Domains** shows the conflicts it found.

Running AdGuard itself on your managed servers, with failover, is covered in [AdGuard HA](/docs/architecture/adguard-ha/).

## Connecting a domain

Setup lives in **Settings → Domains** (page title: **DNS & certificates**) and needs an owner. Connect AdGuard first.

### 1. Create a scoped Cloudflare token

Cloudflare must already be the zone's authoritative DNS. The registrar doesn't matter, and Lucia needs no registrar access.

1. In Cloudflare, open **Manage Account → Account API Tokens → Create Token**. Use the account page, not the user tokens under **My Profile**. Name the token **Lucia DNS & certificates**.
2. Start from the **Edit zone DNS** template. Narrow its **DNS Write** policy from **All zones** to the single root zone you're onboarding, for example `example.com`.
3. Add a second policy granting **Zone Read** on that same zone. Zone Read isn't the same as DNS Read. DNS Write already covers reading records.
4. Create the token and copy it. Cloudflare shows it only once.
5. Search Cloudflare for **Copy account ID**. Paste the **account ID** and the token into Lucia. Don't use the zone ID.

> [!NOTE]
> DNS Write isn't limited to `_acme-challenge` records. Anyone holding this token can edit any record in that zone, so treat it like a password. Lucia encrypts it with the host's Data Protection keys. Certbot gets it only through a private temporary file that's deleted when the operation finishes.

### 2. Choose names

Pick a zone and a namespace, for example `homelab`. Lucia then suggests names:

| Service | Example |
|---|---|
| Lucia | `https://lucia.homelab.example.com` |
| Authentik | `https://auth.homelab.example.com` |
| Spark alias | `https://<spark-name>.homelab.example.com`, which redirects to Lucia |

You can override any of them. The certificate always covers the namespace and one wildcard level, for example `homelab.example.com` and `*.homelab.example.com`. Custom names outside that get added as exact names.

Origins must be plain HTTPS on port 443, with no path prefixes or custom ports. Enter the Spark's private IPv4 address as the ingress address.

The maintainer's lab (an example) runs `lucia.homelab.seiggy.com`.

### 3. Review and approve

The review shows every URL, certificate name and rewrite Lucia intends to create, plus any blockers. Blockers include broken nameserver delegation and AdGuard collisions.

You also need to:

- give an ACME contact email;
- accept the current Let's Encrypt subscriber agreement;
- approve the DNS and activation changes separately.

Remember that public certificates list their names in Certificate Transparency logs.

### 4. What activation does

1. Rechecks both providers and your saved review.
2. Runs Certbot against Let's Encrypt **staging**. This still creates and removes real TXT records.
3. Gets and validates the **production** certificate.
4. Publishes it to the gateway, then checks HTTPS by SNI before changing any DNS.
5. Adds only the missing AdGuard rewrites.
6. Checks that AdGuard answers and that the Spark's own resolution works.
7. Updates Lucia's Authentik application, then restarts the host on the new URLs.

Steps 7 and the gateway reload are carried out by the `lucia-domain-activation` user service on the Spark, so `loginctl enable-linger` must be on. The old Spark origin keeps working as an API and recovery alias. Sessions signed under the old issuer must sign in again.

> [!IMPORTANT]
> Lucia supports one public domain profile. Replacing it later is a separate migration, not an edit.

### Renewal and failures

The host checks the certificate every **12 hours**. Certbot renews only when a renewal is due. **Settings → Domains** shows:

- the routes;
- the certificate's names and expiry, and the last renewal outcome;
- the matching AdGuard records.

The page refreshes every minute while it's open. Refreshing never changes anything. Replace an expiring Cloudflare token there before it blocks a renewal.

If activation fails, Lucia keeps the failed job and writes a confirmed diagnosis. It can also ask the currently loaded local chat model to explain that diagnosis in plain language. This support agent is advisory only:

- It has no tools.
- It sees only fixed diagnostic facts, never logs, tokens or keys.
- It gets a 90-second deadline.
- It never falls back to a cloud model.

## What stays private

By default:

- Cloudflare gets only short-lived `_acme-challenge` TXT records.
- No private address is published as a public A or AAAA record.
- No WAN port is opened.
- Clients resolve Lucia's names through AdGuard, either directly or through your network's DNS settings.

UniFi stays in charge of DHCP. Domain setup doesn't change it.

## UniFi: reservations only

With a UniFi API key at **Settings → UniFi Network**, Lucia checks every 5 minutes and adds DHCP reservations for the Spark and each managed server at the address it already has. It only ever **adds** reservations. Addresses already reserved for another device are left alone and reported.

Some UniFi settings stay with you:

- **Network Boot**, which supplies DHCP options 66 and 67. See [Nodes](/docs/architecture/nodes/).
- The DNS servers that DHCP hands out.
- Firewall rules.

## Optional public access

Some apps are meant to be reached from outside, such as Immich for sharing photos. For those, Lucia has an opt-in public mode. When you turn it on for an app, Lucia:

1. Creates **proxied** Cloudflare A records for Authentik and that app's public name, pointing at your WAN address, with the comment "Managed by Lucia".
2. Has UniFi forward TCP 443 to the gateway's separate public entrypoint. That entrypoint accepts connections only from Cloudflare's published IP ranges.
3. Rechecks the records, the WAN address and the forward every 5 minutes.

Turning public access off removes Lucia's records and disables its port forward. Lucia changes or removes only records that carry its comment. An existing record is adopted only if it already points at your WAN address.

The assistant can't make an app public on its own. `set_public_route` is one of the tools that always asks. See [Assistant and jobs](/docs/architecture/assistant-and-jobs/).
