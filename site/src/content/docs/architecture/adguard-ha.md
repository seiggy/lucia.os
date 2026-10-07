---
title: AdGuard HA
description: How Lucia runs AdGuard Home on every managed server, each on its own address, keeps them in sync and fails over when the primary stops answering.
section: architecture
order: 4
---

DNS is the service whose outage gets blamed on everything else. If your lab has one AdGuard Home on one box, that box can't reboot without someone in the house asking why the internet broke. Lucia's answer is to run AdGuard Home on **every** managed server, give each copy its own IPv4 address, copy one primary's settings to the others every minute, and promote a replica if the primary goes quiet.

This page explains how that works, and which part is still yours to do.

## Before it starts

The fleet controller waits for three prerequisites. Until each is met, the AdGuard page shows a matching message:

| Prerequisite | Message if missing |
|---|---|
| An AdGuard connection at **Settings → AdGuard** | "Connect AdGuard first." |
| An active domain at **Settings → Domains** | "Finish domain setup first." |
| The **AdGuard Home** catalog app installed once from **Apps** | "Install AdGuard Home from Apps first." |

Installing the catalog app once is the opt-in. After that, Lucia takes over.

> [!IMPORTANT]
> Turn on **Manage AdGuard's certificate** in **Settings → AdGuard**. A new instance doesn't finish setup until Lucia has a certificate to give it.

## What Lucia does

### One instance per server

Every 30 seconds, while the primary is healthy, the controller looks for online, Ready managed servers that have never had an AdGuard instance. On each one, it adds an `adguard-<server>` app.

It remembers every server that has had an instance. If you delete the instance from a server, Lucia **won't** put it back.

### An address of its own

Each instance gets a dedicated IPv4 address, separate from the server's own address. You choose the first instance's address when you install it from the catalog. After that, Lucia picks the first free address in the same /24 after the highest address already used by an AdGuard instance.

The node agent adds that address beside the server's own address on the matching network, but only after checking that no other device answers for it. It then announces the address.

- If another device turns out to hold the address, the instance moves to a new one.
- When an app moves to another server, the old server gives the address up before the new one claims it.
- After a reboot, the agent reclaims its addresses after the same check.

The maintainer's lab uses two instances at `192.168.0.230` and `192.168.0.231`, as an example.

No VRRP, keepalived or floating virtual IP is involved. Each instance always keeps its own address. "Failover" means changing which instance is the **primary**, not moving an address.

### Each new instance is set up for you

A fresh AdGuard Home starts with its first-run wizard. Lucia completes the wizard using the same account as your AdGuard connection. It then pushes the current certificate to the instance's address, once, over plain HTTP. Everything after that uses HTTPS. Each instance also gets a local name, `adguard-<server>.<namespace>`.

### The primary and sync

One instance is the **primary**. That's the instance that:

- your AdGuard connection points at;
- Lucia writes its DNS records to;
- the `adguard` name resolves to.

On first run, the primary is the instance named `adguard`, which is the one installed from the catalog. If there isn't one, it's the oldest instance.

Every minute, Lucia runs `adguardhome-sync` to copy the primary's settings, filter lists, clients and rewrites to every healthy replica. DHCP and TLS settings are not copied and stay per instance. Make changes on the primary. Changes made on a replica are overwritten on the next sync.

### Failover

An instance counts as healthy if it answered within the last 90 seconds. If the primary stops answering for **three minutes**, Lucia promotes the healthy instance that synced most recently, as long as that sync was within the last ten minutes. The AdGuard page shows when the promotion happened and which instance it replaced.

Lucia **never fails back on its own**. When the old primary returns, it stays a replica until you pick it again with **Make primary**. That only works for a running instance that has finished setup. Its settings then replace the others' on the next sync, so choose with care.

> [!CAUTION]
> Promoting an instance with **Make primary** makes its configuration the source of truth. Lists, rewrites and client settings that exist only on the old primary are overwritten on the other instances at the next sync, within about a minute.

## What stays manual: UniFi's DNS list

Lucia doesn't change your UniFi DHCP settings. For clients to actually use the fleet, set the network's DHCP DNS servers yourself in UniFi to the instance addresses, for example `192.168.0.230` and `192.168.0.231`.

Clients then fail over the usual way: when the first server doesn't answer, they ask the next. Lucia's job is to make sure the next server has the same answers.

The AdGuard page describes it this way: "Lucia runs AdGuard on every server and copies the primary's settings to the others each minute… If it stops answering for three minutes, the most recently synced server takes over."

## The Spark is not in the fleet

The fleet is made of managed servers only. The Spark is the controller, and doesn't run an AdGuard instance of its own. A lab with one managed server has one AdGuard instance and nothing to fail over to. You need at least two servers for this to help.

## Timings at a glance

| What | Value |
|---|---|
| Controller check | every 30 s |
| Settings sync | every 1 min |
| Healthy if it answered within | 90 s |
| Failover after primary is down for | 3 min |
| Eligible replica must have synced within | 10 min |
| Automatic failback | none |
