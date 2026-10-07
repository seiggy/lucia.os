---
title: Local AI
description: How Lucia runs language and embedding models on the Spark, manages downloads and memory, and serves an OpenAI-compatible API.
section: architecture
order: 7
---

The DGX Spark is the controller and it's also a 128 GB unified-memory AI box. Lucia makes use of that. The host process runs a chat model and an embedding model **in-process** through TensorSharp on the GB10 GPU, and serves them through an OpenAI-compatible API that your apps and the assistant can use. For larger models, Lucia can hand the whole Spark over to a pinned TensorFold recipe.

![The Models page showing the loaded chat and embedding models and the model library](/screenshots/models.png)

## Engines

| Engine | Where | What it's for |
|---|---|---|
| **TensorSharp** (in-process) | Inside `lucia-host` on the Spark | Lucia's own chat and embedding models. One of each resident at a time |
| **TensorFold** (Spark recipe) | A container run by the `lucia-spark-model` user service on the Spark | Qwen3.8-Flash-Next with a 200,000-token context and 4 parallel conversations. Uses nearly all of the Spark's memory |
| **Local AI app** | Any managed server, from **Apps** | The catalog's Local AI app, with a choice of engine (Lucia, vLLM or llama.cpp), for servers with their own GPUs |

TensorSharp is the default. The packages Lucia builds against are a GB10-specific build that isn't on public NuGet. See [Install from source](/docs/getting-started/install-from-source/).

### TensorFold on the Spark

**Local AI → Models** shows a **Spark recipe** card for Qwen3.8-Flash-Next on TensorFold. Starting it:

1. unloads Lucia's own chat and embedding models first, because the recipe needs nearly all of the Spark's memory;
2. writes a request for the `lucia-spark-model` user service, which owns the pinned recipe and runs its container (the web host still has no Docker access);
3. downloads about 125 GB on the first start, then loads for a few minutes.

TensorFold has no authentication of its own. Lucia doesn't expose it directly. Instead, Lucia's authenticated `/v1` API forwards to it, and apps request it by the model name `Qwen3.8-Flash-Next`. Stopping it cuts off running requests, and Lucia then reloads its own models.

## Models and downloads

**Local AI → Models** is where you manage what's on disk and what's loaded.

- **Catalog first.** Normal downloads come from Lucia's model catalog, pinned by repository, revision and SHA-256. An explicit "pro" option allows other Hugging Face repositories. Those still go through checks on file paths, GGUF metadata, architecture support, tokenizer metadata and memory admission.
- **GGUF only** for the in-process engine. For split GGUFs, select the first shard and all numbered shards download together.
- **Hugging Face** downloads use the `hf` CLI. An optional Hugging Face access token, needed for gated repositories, is entered on the Models page and encrypted with the host's Data Protection keys. It's never sent back to the browser.
- **Previews** read at most 16 MiB of a file's GGUF header to show its metadata before you download all of it.

### The bundled model

The default release model is `unsloth/Qwen3.6-35B-A3B-MTP-GGUF`, file `Qwen3.6-35B-A3B-UD-Q6_K_XL.gguf`. It's 32.61 GB, pinned by revision and SHA-256. The catalog marks it **qualification pending**: that exact file, quantization, template and tool calling haven't yet been signed off on Spark hardware. Speculative decoding with its MTP weights stays off.

No embedding model is bundled yet.

## Memory and context

Lucia refuses to load a model that doesn't fit. Before any load, it estimates:

- resident weights;
- the other loaded model;
- KV storage for the requested context.

It then subtracts fixed reserves from the memory budget:

| Reserve | Code default | Shipped `appsettings.json` | Notes |
|---|---|---|---|
| OS | 4 GiB | 8 GiB | Tunable |
| Other services (Redis, Authentik, and so on) | 3 GiB | 8 GiB | Tunable |
| Voice | 8 GiB | 8 GiB | Can't go below 8 GiB |
| Runtime and scratch | 3 GiB | 8 GiB | Tunable |

These are capacity-planning reserves, not enforced memory partitions.

The voice reserve is held for a voice pipeline on the Spark that **doesn't exist yet**. The Voice app in the catalog (Whisper, Piper and openWakeWord for Home Assistant) runs on managed servers and doesn't use this reserve.

Context rules:

- The default served context, used when a load doesn't ask for a size, is `HostPlatform:ContextTokens`. The code default is 32,768 tokens. The Spark installer writes **8,192** on a fresh install and keeps your value on reinstall.
- The Models page offers a context slider from 8,192 tokens upward, in 256-token steps, up to what the memory estimate allows.
- A larger context can be loaded if it fits the estimate and the model's declared limit.
- Model swaps are serialized.

On GPU hosts other than the Spark, set `HostPlatform:MemoryBudgetGiB` explicitly, so system RAM isn't mistaken for VRAM. See [Configuration](/docs/reference/configuration/).

## The OpenAI-compatible API

The host serves these endpoints at `/v1`:

| Endpoint | Purpose |
|---|---|
| `GET /v1/models` | Loaded models. When TensorFold is running, its model is listed with backend `tensorfold` |
| `POST /v1/chat/completions` | Chat |
| `POST /v1/responses` | Responses API |
| `POST /v1/embeddings` | Embeddings |

Apps use the model's repository name, for example `Qwen/Qwen3-8B`, as the `model` value in requests.

### API keys

Create keys at **Local AI → API keys**. They're **inference-only**: a key can call `/v1` and nothing else. Each key:

- is shown **once**;
- is stored as a hash, never in plain text;
- can have an optional expiry date;
- stops working on the next request after you revoke it.

```bash
curl https://lucia.homelab.example.com/v1/chat/completions \
  -H "Authorization: Bearer $LUCIA_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"model": "<model-name>", "messages": [{"role": "user", "content": "Is the NAS up?"}]}'
```

### The Playground

**Local AI → Playground** is a chat page for trying the loaded model without writing a client. It uses your portal sign-in, so you don't need an API key. Unlike Models and API keys, the Playground is open to any signed-in account with Lucia access, not just owners. The conversation lives only in the browser tab, so refreshing or signing out clears it.

## Observability

TensorSharp exposes `lucia_inference_*` metrics. When TensorFold runs, Lucia also scrapes its Prometheus metrics. With the Observability app installed, both appear on Grafana's Inference dashboard. See [Observability](/docs/architecture/observability/).
