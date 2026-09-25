---
version: 1
slug: "src-lucia-desktop-mainwindow-axaml"
primary_target: "src/Lucia.Desktop/MainWindow.axaml"
related_targets: ["src/Lucia.Desktop/App.axaml","src/Lucia.Desktop/MainWindow.axaml.cs","src/Lucia.Desktop/SetupForm.cs"]
---

# Lucia desktop setup

## Scope and visitor mode

Operate. A native Avalonia/.NET setup utility for Windows, macOS, and Linux, separate from the ongoing web dashboard. Starts after NVIDIA DGX OS setup with networking and SSH access. This milestone covers private CA, LDAP, Authentik, and the owner's real directory account.

## Job, action, and proof

Help a homelab owner get usable identity services without terminal commands. Connect to the Spark, confirm its SSH fingerprint before credentials are sent, review readiness and changes, supply the owner's username/password, monitor setup, and explicitly approve or export local certificate trust. Completion requires real LDAP-backed owner sign-in, not container health alone.

## Chosen direction

Extend the approved "A simple check-in" world: system sans typography, Ocean blue, quiet light/dark neutrals, familiar controls, and progressive disclosure. One primary task pane beside a five-step rail: Connect, Review, Your account, Set up, Ready. At compact desktop widths, replace the rail with a small current-step label. Keep the primary action anchored while long content scrolls.

## Behavior and constraints

- Enter a host manually; password and private-key SSH authentication are supported.
- Never silently accept a changed host key, reset an owner password, replace a CA, or adopt an unrelated installation.
- An existing owner signs in for verification; a new owner is created during provisioning. Credentials stay out of local persistence and logs.
- A running remote job can be monitored again without another installation or another owner-password prompt. Stopping monitoring is not cancellation of remote work.
- Errors remain actionable and visible. Unsupported prerequisites and incomplete sign-in are not success.
- Certificate trust has a separate explicit approval describing its OS-specific scope; export remains available.
- Light/dark/system appearance, accessible labels, keyboard controls, masked secrets, readable status words, and an intentionally bounded progress log.

## Evidence and limits

The native Windows UI has been exercised through real SSH fingerprint confirmation and preflight. Headless screenshots use explicitly labeled synthetic fixtures for additional states; they are not proof of deployment. The user called the native UI "pretty slick." Desktop/API SSO, model deployment, DNS providers, first-boot OS onboarding, signing/notarization, and native macOS/Linux qualification are outside this milestone.
