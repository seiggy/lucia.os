# Your domain, local DNS, and HTTPS

Lucia's domain workflow uses **Cloudflare DNS** and **Let's Encrypt DNS-01**
for public certificates, and **AdGuard Home** for local service addresses.
The domain's registrar can remain elsewhere; Cloudflare must already be its
authoritative DNS provider. No registrar transfer or registrar API permission
is required.

Start at **Settings → AdGuard**, then **Settings → Domains**. Both pages require
a Lucia Owner. Configuring provider credentials does not itself change DNS,
request a certificate, or switch service URLs.

Once the domain is activated, revisiting **Domains** opens its operations
overview instead of the setup wizard:

- **Traefik routes** shows the managed public addresses, actual published proxy
  destinations or redirects, and differences from the reviewed configuration.
  Published configuration is not presented as a live endpoint health check.
- **Let's Encrypt** shows certificate names and expiry, issuance status, the
  scheduled/running renewal check, its last recorded outcome, and any renewal
  error. Older installations show "No renewal check recorded yet" until a check
  records an outcome.
- **AdGuard records** reads the matching exact/wildcard rules for Lucia's
  endpoints and flags missing, disabled, conflicting, or unavailable records.
  Unrelated AdGuard records are not shown; rules that existed before setup are
  distinguished from those Lucia created.

The snapshot refreshes every minute while visible, or with **Refresh status**.
Refreshing never changes DNS, issues certificates, or repairs configuration.
Setup history stays collapsed, and Cloudflare-token replacement and AdGuard
connection settings remain available without reopening onboarding.

## 1. Connect the AdGuard instance you already run

Enter the management interface's origin (scheme, host, and optional port),
username, and password. Do not append `/control`. Lucia reads the authenticated
profile, running version, DNS state, and rewrite capabilities before saving.
It does not read query logs or browsing history and does not create a test rule.
Service status may advertise plain IPs, IP:port pairs, and encrypted-DNS URLs;
these are descriptive metadata, not alternate destinations for API credentials.

AdGuard Home uses its web-account credentials rather than a scoped API token.
Treat the supplied account as administrative. Lucia encrypts the saved
connection on the Spark and does not return its password or store it in the
browser. Failed replacement verification preserves the previous connection.

HTTPS must pass ordinary certificate validation. Plain HTTP is accepted only
for a private-LAN endpoint with a separate, explicit acknowledgement that the
credentials are exposed on the network. There is no TLS-skip option.

**Verify connection** checks the current service again. **Disconnect** removes
Lucia's saved access but leaves all remote rewrites intact. Globally disabled DNS,
protection, or rewrites must be corrected explicitly in AdGuard; Lucia does not
silently enable them.

This release connects an external instance. Later, a Lucia-managed AdGuard
module can supply the same `ILocalDnsProvider` capability and connection; the
domain/certificate workflow need not change.

## 2. Connect Cloudflare

Start with **Edit zone DNS**, then narrow its scope and add Zone Read.
The template alone grants DNS Write to all zones by default; Lucia also needs
read access to the selected zone's details.

1. Open **Manage Account → Account API Tokens → Create Token**, not the user-token
   page under My Profile. Name the token **Lucia DNS & certificates**. Creating
   account tokens requires a Super Administrator; the token itself must not
   receive administrator rights.
2. Choose the **Edit zone DNS** template. Under **Permission policies**, find
   the policy showing **All zones in your account** and **DNS Write**.
3. Open that policy and change its zone selection from **All zones** to only
   the domain being onboarded. Keep **DNS Write**. Select the root zone, such as
   `example.com`, not `homelab.example.com` or a wildcard.
4. Select **Add policy**, grant **Zone Read**, and select that same single zone.
   The resulting policies should grant exactly:

   | Permission | Older three-part label | Purpose |
   |---|---|---|
   | DNS Write | Zone → DNS → Edit | Create/remove DNS-01 TXT records |
   | Zone Read | Zone → Zone → Read | List and verify zone details |

   **Zone Read is not DNS Read.** DNS Write already includes reading DNS records;
   a separate DNS Read permission is not needed.
5. Choose **Token expiration** according to your policy. If it expires, replace
   it in Lucia before that date to avoid blocking certificate renewal.
6. Select **Continue to summary**. Confirm only DNS Write and Zone Read for the
   selected domain, with no all-domain, account-administration, token-management,
   or registrar access. Select **Create Token**, then copy its secret immediately:
   Cloudflare shows it only once.
7. In Cloudflare's Search, enter **Copy account ID** and select the result.
   Paste that account ID and the token into Lucia. Do not use the zone ID.

DNS Write is not limited to `_acme-challenge`: protect this token because it can
edit other DNS records in its permitted zone.

Lucia verifies the account-token
endpoint, validity dates, and accessible active full-setup zones. This read-only
check does not prove DNS-write access; the later Certbot staging step does.
If the domain is absent, check its account, nameserver delegation, and the
token's zone scope.

Cloudflare references: [account-token creation](https://developers.cloudflare.com/fundamentals/api/get-started/account-owned-tokens/),
[permission names](https://developers.cloudflare.com/fundamentals/api/reference/permissions/),
and [finding the account ID](https://developers.cloudflare.com/fundamentals/account/find-account-and-zone-ids/).

Saved tokens use the host's persistent Data Protection keys. During a certificate
operation, Certbot receives a private temporary credentials file, not a command-line
argument. The file is removed after the operation; renewal recreates it from the
current saved token. A hard process crash can leave the private file until the
next operation replaces it. Do not run an independent Certbot timer.

## 3. Choose names

Select a verified Cloudflare zone, enter a relative local subdomain such as
`homelab`, and name the Spark, for example `atlas`.

For `example.com`, suggestions are:

| Service | Suggested URL |
|---|---|
| Lucia | `https://lucia.homelab.example.com` |
| Authentik | `https://auth.homelab.example.com` |
| Spark alias | `https://atlas.homelab.example.com` |

**Every suggestion can be overridden.** Open **Customize service URLs** to
use different names, including a name elsewhere inside the selected zone.
Changing the Spark name does not erase custom service URL choices.
The Spark name is a DNS alias; it does not rename the operating-system host.

The current gateway profile supports distinct HTTPS origins on port 443,
without path prefixes, query strings, or custom ports. The Spark alias redirects
to the chosen Lucia URL. Enter the Spark's private IPv4 ingress address; the
workflow checks its existing HTTPS identity before accepting the review.

The certificate contains both the namespace and its wildcard:
`homelab.example.com` and `*.homelab.example.com`. A TLS wildcard covers only
one label, not the namespace apex or deeper names. Custom URLs outside that
coverage receive additional exact certificate names.

## 4. Review and explicitly approve

Lucia shows the exact service URLs, certificate names, local rewrite records,
and blockers. It checks Cloudflare's public nameserver delegation, intervening
NS/CNAME delegation, and AdGuard collisions. V1 does not follow delegated
ACME challenge aliases.

The review requires an ACME contact email, acceptance of the current
Let's Encrypt subscriber agreement, and separate approval for the listed
DNS/activation changes. Public certificates disclose their names through
Certificate Transparency.

The approved workflow:

1. Rechecks providers and the saved review.
2. Runs Certbot against Let's Encrypt staging. This still creates/removes real
   public DNS TXT records.
3. Obtains and validates the production certificate.
4. Publishes an immutable certificate/key pair to the existing gateway and
   verifies HTTPS by SNI before changing local DNS.
5. Adds only missing, exact, approved AdGuard rewrites. Existing matching rules
   are preserved, not adopted. Conflicting or duplicate rules block setup.
6. Verifies AdGuard answers and the Spark's normal DNS resolution.
7. Requests the scoped Authentik registration update, then restarts the host
   with the chosen canonical URLs.

Cloudflare receives challenge TXT changes only: Lucia does not publish the
private service IPs as public A/AAAA records or open WAN ports. Clients must use
AdGuard, directly or through the network's DNS forwarding. UniFi remains the
LAN/DHCP gateway; this workflow does not change its DHCP settings.

Existing private CA, LDAP, directory accounts, client secrets, API keys, and
session-protection keys remain in place. The legacy Lucia HTTPS origin remains
an API/recovery alias. Use the new URL for fresh browser sign-in. Previously
issued JWTs using the old issuer are not valid under the new issuer.

## Scoped activation service

The web host does **not** receive Authentik's bootstrap-administrator token or
the Docker socket. A user-level service on the Spark reads bounded, reviewed
activation requests and can update only the owned Lucia provider's exact
callback list and application's launch URL.

The setup account's persistent user services must be explicitly approved
(`loginctl enable-linger <setup-user>`). The desktop bootstrap installs the
worker when that prerequisite is already enabled; otherwise domain activation
remains blocked with an explanation. No password or arbitrary shell command is
accepted in the activation queue.

Native unit: `lucia-domain-activation.service`. Its receipts stay in private
identity state; job requests/responses contain domain/profile metadata, not
Cloudflare or Authentik credentials.

The native worker also notices changes to the fixed `gateway/domains/domain.yml`
file and touches the existing top-level `gateway/tls.yml` file to notify Traefik.
It does not change that file's contents or expose Docker to the web host.
Traefik accepts YAML/TOML configuration names, not `.json`, and its file watcher
does not watch nested directories. JSON syntax is valid YAML, so Lucia retains
its structured serializer and publishes with the supported `.yml` extension.
Publication, renewal, and rollback all use the same reload path.

## Renewal and recovery

The host checks the managed certificate lineage every 12 hours. Certbot
renews only when due, using the saved Cloudflare connection. Certificate
validation and gateway deployment are separate checks; an issuance exit code
alone is not activation success. Renewal errors and certificate expiry are
visible on the domain page. Replace an expiring Cloudflare token there before
renewal is blocked.

Provider connection replacement is serialized against active setup/renewal.
Failed activation preserves the private recovery path and attempts only
ownership-checked cleanup. It does not delete unrelated TXT records or reset
AdGuard settings. Interruptions and externally changed resources require review;
do not delete workflow/lease files to manufacture a fresh installation.

The first public-domain configuration is supported. A later domain replacement
is a separate migration, not an implicit overwrite of an active profile.

## Local DNS support agent

After a setup failure, Lucia keeps the failed task and automatically prepares a
confirmed diagnosis, followed by a plain-language explanation from a
**Microsoft Agent Framework (MAF)** agent. It uses the currently loaded Lucia
chat model through the same in-process inference adapter as the local SRE agent.
It does not choose a fixed model, load one automatically, or fall back to a cloud
LLM.

The DNS support skill is a bounded, code-backed runbook. It recognizes selected
Certbot folder, storage, plugin, Cloudflare authentication, DNS propagation, and
ACME rate-limit failures. Only fixed diagnostic facts reach the model. Raw logs,
account identifiers, email addresses, provider tokens, and private keys do not.
Existing failures can be diagnosed only when the private log matches the job's
certificate lineage and time window.

The failure page shows one confirmed plain-language explanation and the relevant
next action. **Technical details** is collapsed by default and contains diagnostic
evidence, local AI advice, model attribution, and the phase history. A certificate
that was issued but never activated is not described as having active renewal.
Unknown failures explicitly identify a diagnostics gap rather than implying that
no error occurred.

Gateway checks retain the failed phase, checked HTTPS address, HTTP status when
available, and a typed TLS/connection/response failure. These facts reach the
support skill without exposing raw exception text or secrets. If no model is
loaded, generation fails, or the host restarts, the confirmed diagnosis remains
available and the details offer an explicit explanation retry. Each attempt has
a 90-second deadline and bounded output; concurrent requests for the same
explanation are deduplicated. Completed reports are retained with the job.

This first skill is advisory: the agent has no command execution, permission
editing, or DNS mutation tools. Deterministic managed setup code handles known
local corrections, such as Certbot's exact `0755` config/work/hook-directory
requirement inside a private `0700` parent. Credentials and logs remain private.
That correction applies on the next owner-reviewed attempt, not merely because
the model described it. Recovery blockers remain authoritative, and new
DNS/ACME work still requires a fresh review and consent.

## Qualification boundary

Provider clients, credential storage, naming/certificate coverage, consent
checks, and activation helpers have isolated tests. A user's real Cloudflare
account and AdGuard instance still must pass the workflow's checks. In
particular, Certbot 5.8.0 account-token DNS-01 compatibility is gated by the
real staging request; read-only token verification does not bypass it.

This setup does not deploy AdGuard, transfer a domain, replace LDAP trust,
or enable hardware imaging.
