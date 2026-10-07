---
title: Observability
description: Spark health on the Home page, per-machine telemetry relays, the Observability app with Grafana, and inference metrics.
section: architecture
order: 9
---

Lucia's monitoring comes in two layers. A small, always-on layer samples the Spark's health for the Home page and keeps the last hour in memory. The full layer is the optional **Observability** catalog app: an OpenTelemetry collector, Prometheus, Loki, Tempo and Grafana, fed by a telemetry relay on every machine Lucia manages. Install it when you want graphs. Skip it if the Home page tells you enough.

## Spark health (always on)

The web host samples the Spark directly from the host's `/proc` counters and its data volume, and shows them on **Home**.

| What | Value |
|---|---|
| Sample interval | 10 seconds |
| History kept | 1 hour (3,600 seconds), at most 361 samples |
| Storage | In memory only. A host restart starts the history over |
| Marked stale | When the newest reading is more than 30 seconds old |
| Attention: disk | Under 5% free on the host volume |
| Attention: memory | Under 3% memory available |

Each sample covers:

- CPU use, core count and load average;
- memory (the Spark's unified memory is flagged as such);
- data-volume capacity;
- network throughput on the main interface;
- GPU name, utilization, temperature and power;
- uptime.

Readings that the platform can't provide are listed as unavailable, never guessed. The production deployment turns this on with `SparkTelemetry__Enabled=true`. It's off by default in code, so a bare development run shows "Spark monitoring is not configured."

![The Home page with Spark CPU, memory, GPU and storage readings](/screenshots/home.png)

Managed servers report their status to Lucia through their 30-second heartbeat. **Devices** shows a server as online while its last heartbeat is under 2 minutes old. Detailed per-server metrics come from the relays below.

## The Observability app

Install **Observability** from **Apps** on any server with Docker ready and room for about 50 GB of telemetry.

| Component | Version | Role |
|---|---|---|
| OpenTelemetry Collector (contrib) | 0.161.0 | Receives OTLP with a password; routes traces, metrics and logs |
| Prometheus | v3.15.0 | Metrics. Keeps 30 days, capped at 40 GB |
| Loki | 3.7.8 | Logs. Keeps 168 hours (7 days) |
| Tempo | 3.1.0 | Traces. Keeps its default 336 hours (14 days) |
| Grafana | 13.2.2 | Dashboards and exploration |

All images are pinned by digest. Default ports:

| Setting | Default |
|---|---|
| Grafana port | 3030 |
| OTLP gRPC port | 4317 |
| OTLP HTTP port | 4318 |
| Grafana name | `grafana` under your domain, for example `grafana.homelab.example.com` |
| OTLP name | `otlp` under your domain |

**Grafana sign-in** goes through Lucia's Authentik. Members of `lucia-owners` become Grafana admins. Until a domain is active, Grafana also answers on `http://<server>:3030`.

## Telemetry relays

While an Observability app is installed, every managed server runs a system app called **telemetry-relay**. On each server, the relay is an OpenTelemetry collector that:

- accepts OTLP from apps and Lucia's agent on loopback only: `127.0.0.1:14317` (gRPC) and `127.0.0.1:14318` (HTTP);
- scrapes the machine's hardware every 30 seconds through **node-exporter** v1.10.2, and NVIDIA GPUs through **nvidia_gpu_exporter** 1.15.1;
- scrapes Local AI engine metrics where they run (vLLM, llama.cpp);
- labels everything with the machine's name and forwards it to the Observability app with the app's credentials.

Removing the Observability app removes the relays too.

### The Spark's relay

The Spark runs its own relay beside the controller, with node-exporter and the GPU exporter. It doesn't get the Observability app's credentials. Instead, it posts to the controller at `/api/host/telemetry/relay/v1/{traces|metrics|logs}` with a random token that the controller generated. The controller forwards the data to wherever the Observability app currently runs, so an app move doesn't break anything. The Traefik gateway's access logs take the same path, with credentials and query strings dropped.

When the TensorFold recipe is running, the Spark's relay also scrapes TensorFold's Prometheus metrics. The model worker lists the metrics address in a file the relay watches (`spark-model.json`).

### Apps

Catalog apps that can send telemetry get the Observability app's OTLP address and credentials in their environment. Lucia re-checks this every 15 seconds. Once your domain is active it points the apps at the Observability app's web address, which follows the app if it moves.

## Dashboards

Lucia provisions two read-only dashboards in Grafana, tagged `lucia`. Both refresh every 30 seconds.

**Hardware** is Grafana's home dashboard. It shows every machine Lucia manages, with these rows:

- Machines (a current table);
- CPU and memory;
- Storage and network;
- Temperatures;
- GPUs.

Container and bridge interfaces are filtered out so network traffic isn't double counted.

**Inference** puts every engine's raw performance side by side, with Latency, Speed and Volume rows:

| Engine | Source |
|---|---|
| Lucia (TensorSharp, in-process) | `lucia_inference_*` metrics from the web host |
| vLLM | Scraped by the server's relay |
| llama.cpp | Scraped by the relay, per model. Running totals only, so there's no time-to-first-token or request count |
| TensorFold | Scraped by the Spark's relay while the recipe runs. Doesn't report KV cache reuse |

> [!NOTE]
> Lucia doesn't provision any alert rules yet, so nothing pages you from Prometheus. For "tell me when something's wrong", the current tool is an [assistant job](/docs/architecture/assistant-and-jobs/) that checks your lab on a schedule and notifies you.
