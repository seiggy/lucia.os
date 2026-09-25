# Hugging Face management checks and integration

Run from the repository root:

```powershell
dotnet run --project .\tests\Lucia.Homelab.HuggingFaceChecks\Lucia.Homelab.HuggingFaceChecks.csproj
```

Checks use synthetic tokens, a fake provider HTTP handler, real disposable Data
Protection keys, private storage under this test project, and a loopback HTTP
server. No live credentials, model downloads, model loading, or GPU are required.
The disposable files are removed even on failure. POSIX permission checks run on
Unix; Windows uses a current-user-only ACL. Symlink checks run where the OS permits
creating symlinks.

## Parent integration

Register with `builder.AddHuggingFaceManagement()` and map
`app.MapHuggingFaceManagement()` alongside existing host endpoints. Keep the
existing authentication, `HostOwner` policy, and `UseHostCsrf()` middleware.
The registration uses ordinary verified HTTPS, disables redirects and cookies,
and does not use the OIDC custom-CA handler or change host Data Protection keys.
Do not replace the registered HTTP transport with a redirect-enabled client.

Set `HuggingFaceManagement:CredentialsDirectory` to `/data/provider-credentials`
in production. Its default is the current user's local application-data directory
plus `Lucia/provider-credentials`. The final directory is private (0700), records
are 0600, and symbolic links/reparse points anywhere along the path are rejected.
On Windows, current-user-only ACLs provide the corresponding restriction.
The directory must be writable only by the service account; use one host instance
per credential directory. Records are Data Protection-protected and atomically
replaced on the same filesystem. Keep the host's existing persisted DP keys.

Inject `HuggingFaceCredentialService` into the existing downloader and call
`GetDownloadCredentialsAsync(cancellationToken)` for each operation. This returns
`HuggingFaceDownloadCredentials` with `Token` (server-only, JSON-ignored) and
`DisableImplicitCredentials`. Never log it or pass the token as a CLI argument:

* Always remove inherited `HUGGING_FACE_HUB_TOKEN`.
* With an active token, set `HF_TOKEN` and `HF_HUB_DISABLE_IMPLICIT_TOKEN=0`.
* Without an active token, remove inherited `HF_TOKEN` and set
  `HF_HUB_DISABLE_IMPLICIT_TOKEN=1` to suppress cached CLI credentials.
* Never call `hf auth login`.

An existing managed record supersedes `HostPlatformOptions.HuggingFaceToken`.
DELETE writes a protected disconnected marker; it does **not** delete the record
and reactivate legacy credentials. An absent record alone allows legacy fallback.
Corrupt/unreadable records fail closed, including for downloads. DELETE can
replace a damaged regular record, but cannot repair unsafe linked paths.
GET never calls `whoami`; the timestamp is the time of the last successful PUT
validation, not a claim that the token is still valid. Legacy account/timestamp
are unknown (`null`).

## Endpoint contract

All five endpoints use `/api/host/huggingface`, require `HostOwner`, and preserve
global CSRF enforcement for cookie mutations. Success and handled error responses
are `Cache-Control: no-store`.

* `GET /credentials` → `{ configured, accountName, source, validatedAt }`.
* `PUT /credentials`, JSON `{ "token": "hf_..." }` → the same status after
  successful validation **only** against `https://huggingface.co/api/whoami-v2`.
* `DELETE /credentials` → the same status with `source:"disconnected"`.
* `GET /search?query=...&kind=Chat|Embedding` →
  `{ query, kind, items, limit:30, limitReached, compatibility:"unverified" }`.
  Each item is `{ repository, pipelineTag, downloads, likes, gated, private }`.
  Query is 1–100 characters. Kind defaults to Chat. Search uses the GGUF filter
  and the `text-generation` or `feature-extraction` pipeline respectively.
  These intent filters depend on repository tags, not compatibility detection.
  Only one page is read; no pagination links are followed.
* `GET /repository?repository=owner/name&revision=main&kind=Chat|Embedding` →
  `{ repository, requestedRevision, revision, kind, gated, private, availability,
  choices, ggufMetadata, warnings }`. Revision defaults to `main`, kind to Chat.
  The returned `revision` is a concrete lowercase 40-hex commit SHA.

Credential `source` is `managed`, `legacy`, `disconnected`, or `none`.
`validatedAt` is an ISO timestamp or null; token/permissions/email are never
returned. PUT failure preserves the previous record. Requests are limited to
4096 bytes and tokens to 512 ASCII characters.

Each choice is:

```json
{
  "file": "model-Q4_K_M-00001-of-00002.gguf",
  "files": [
    { "path": "model-Q4_K_M-00001-of-00002.gguf", "sizeBytes": 100 },
    { "path": "model-Q4_K_M-00002-of-00002.gguf", "sizeBytes": 200 }
  ],
  "totalSizeBytes": 300,
  "quantization": "Q4_K_M",
  "labelSource": "filename_inferred",
  "compatibility": "unverified",
  "download": {
    "provider": "huggingface",
    "repository": "owner/name",
    "file": "model-Q4_K_M-00001-of-00002.gguf",
    "kind": "Chat",
    "revision": "1234567890abcdef1234567890abcdef12345678",
    "pro": false
  }
}
```

Unrecognized quantization is null with `labelSource:"unknown"`. Only complete
split groups of up to 128 shards are selectable, using their first shard.
Unsafe paths, obvious projector/adapter/LoRA files, and files without byte sizes
are excluded. Browsing is limited to 4096 repository entries, 256 choices, and a
2 MiB repository response. No usable choices returns
`availability:"no_standalone_gguf"` plus warnings; otherwise `"available"`.

Optional `ggufMetadata` is
`{ source:"huggingface_api", scope:"repository", architecture, contextLength }`.
It only exposes allowlisted provider `gguf.architecture` and
`gguf.context_length` fields, not arbitrary metadata or chat templates. This is
repository-level advisory metadata, not a verified selected-file architecture,
KV-cache layout, usable context estimate, or loading guarantee. Numeric context
estimates are never inferred from filename labels. After installation use the
existing `/api/host/models/{id}/context` planner; load/unload/download remain the
existing catalog/runtime endpoints, not these services.

**Catalog integration remains with the parent:** existing `ModelCatalog` rejects
nonpreset `pro:false` sources. The parent must explicitly integrate these pinned
browse selections into its permitted download flow without implying TensorSharp
compatibility. This implementation does not change that guard or download models.

Errors are `{ error: { code, message, retryAfterSeconds } }`, never raw provider
bodies or exception details. Invalid query/repository/revision/kind/token format
is 400; invalid provider token is **422, not a Lucia-session 401**; provider access
denial is 403 with gated-model instructions; private/missing repo or revision is
404; provider rate limit is 429 (bounded Retry-After, when supplied). Redirects,
invalid/oversized provider JSON, network/upstream errors are 502; credential
storage failures are 503; provider timeout is 504. Oversized credential input is
413, non-JSON input is 415, oversized repository/choice collections are 422.
Each provider request, including streaming its body, has a 15-second deadline.

## Selected-file context preview

`AddHuggingFaceManagement()` also registers `HuggingFacePreviewService`. The
parent owns the `/api/host/huggingface/context-preview` route and the runtime
snapshot of the opposite model slot. This follow-up does **not** add that route.

Call this one public service method:

```csharp
Task<HuggingFaceContextPreview> PreviewAsync(
    string repository,
    string revision,
    string file,
    ModelKind kind,
    CancellationToken cancellationToken = default);
```

`revision` must already be the browser's concrete 40-hex SHA. The service
revalidates the repository at that SHA and requires `file` to match a selectable
standalone GGUF or complete group's first shard. It does not accept arbitrary
URLs, branches, auxiliary files, or later shards.

The result is exactly `{ inspection, reason, source, qualification }`:

* `inspection` is a nullable `ModelInspection`. Chat estimates use selected
  header architecture/context/KV metadata, **not** repository-level HF
  `gguf` hints or filename quantization. `FileBytes` and `WeightBytes` are the
  browser's total bytes for the complete group, as in local chat inspection.
  `TensorTypes` is empty because tensor descriptors and weights are not read.
* `reason` is `selected_header_estimate` on success, otherwise a stable
  unavailable code such as `embedding_requires_local_inspection`,
  `unsupported_cache_layout`, `missing_attention_metadata`,
  `invalid_attention_metadata`, `unsupported_attention_metadata_type`,
  `truncated_header`, `header_limit_exceeded`, `invalid_gguf_signature`,
  `unsupported_gguf_version`, `invalid_metadata_counts`, `invalid_metadata_key`,
  `metadata_string_too_large`, `invalid_metadata_type`, `invalid_metadata_array`,
  `unsupported_metadata_array`, `shard_metadata_mismatch`,
  `unsafe_provider_redirect`, `redirect_limit_exceeded`,
  `invalid_content_range`, `file_size_changed`, `encoded_header_response`,
  `invalid_provider_response`, `provider_access_denied`, `provider_not_found`,
  `provider_rate_limited`, `provider_unavailable`, or `provider_timeout`.
* `source` is `{ provider, repository, revision, file, files, totalSizeBytes,
  bytesRead, signature, ggufVersion, metadataComplete, metadataSha256 }`.
  `files` contains the same `{path,sizeBytes}` group members as the browser.
  Signature is the validated magic string `"GGUF"` or null, **not** a digital
  signature. The lowercase SHA-256 covers the GGUF preamble and complete metadata
  bytes, not the full model, tensor descriptors, or subsequent shards.
  `metadataSha256` is null until all metadata was read; `metadataComplete`
  indicates structural completion, not model compatibility.
* `qualification` explains the estimate's limits and requirement to apply
  current residency and all host reserves.

Input/selection errors still throw sanitized `HuggingFaceManagementException`
(`pinned_revision_required` or `invalid_preview_file`, HTTP 400). Repository
browser and credential failures preserve their existing errors. Once selection
is validated, unavailable header/network results return a null inspection and
safe reason; cancellation still propagates.

For a non-null inspection, the parent should call
`ModelInspector.Plan(inspection, otherResidentBytes, requestedContext)` under its
normal residency synchronization. This preserves the existing OS, services,
voice, runtime, and weight-multiplier reserves. `ModelInspector.Calculate`
remains available for deterministic checks. No planner policy changed:
`Inspect` and previews now share `EstimateChatMetadata(ChatKvMetadata,
fileBytes, tensorTypes)` for the exact existing mirrored-F32 KV arithmetic and
hybrid attention counting. That helper performs memory arithmetic, not admission.

### Preview safety and deliberate bounds

The preview client uses `ResponseHeadersRead` and `Range: bytes=0-8388607` (or
less for a smaller first shard). The streaming parser stops immediately after
metadata, never explicitly reading the tensor index, tensor payload, or weights.
If Range is ignored, the same application-read cap applies; unread bodies are
closed without background draining. No sparse files, temporary model files,
`GgufFile` tricks, or full downloads are used.

The parser supports little-endian GGUF v2/v3; at most 4096 metadata entries,
1,000,000 declared tensors, 512-byte keys, 1,000,000 elements in skipped arrays,
and 4096 retained `layer_types` strings of at most 64 bytes. Irrelevant strings,
tokenizer arrays, and chat templates are skipped as bounded opaque bytes, not
retained or interpreted. Nested arrays, variable-array attention dimensions, and
unsupported metadata types fail closed. Required attention counts are bounded
unsigned integer values; malformed/truncated/oversized metadata never yields a
fallback KV estimate.

Only the known standard/hybrid cache layouts `llama`, `qwen2`, `qwen2moe`,
`qwen3`, `qwen3moe`, `qwen35`, `qwen35moe`, and `qwen3next` are estimated.
Other architectures, including MLA/SSM-only layouts, return
`unsupported_cache_layout`; this allowlist is **not** a TensorSharp support list.
Split headers must declare `split.no=0` and the correct `split.count`.
Embedding float-weight requirements remain unavailable until local inspection.

Header acquisition and parsing share a 20-second deadline (in addition to the
browser's bounded metadata request). At most four redirects are followed, only
to default-port HTTPS `huggingface.co`, `hf.co`, subdomains of `.hf.co`, or legacy
`cdn-lfs.huggingface.co`, without user-info or fragments. Every request preserves
the byte Range and requests identity encoding. Authorization is constructed
only for **exactly** `huggingface.co`, never a CDN. TLS validation is unchanged.
The isolated transport disables cookies, redirects, response draining, automatic
decompression, and trace-context propagation; the host's OpenTelemetry HTTP
instrumentation is suppressed while handling signed locations. Signed URLs,
arbitrary metadata strings, error bodies, and credentials are never returned or
logged by the service.

Checks cover ignored/chunked Range responses, exact 16 MiB termination, metadata
hash provenance, skipped tokenizer/template values, malformed/truncated/bounded
inputs, known/unsupported layouts, shard identity, redirects/header leakage,
quantization-size context differences, residency/reserves, and a tiny local GGUF
regression through `ModelInspector.Inspect`. All provider responses remain fake.

## Provider evidence

* [Official Hub API documentation](https://huggingface.co/docs/hub/api)
  links the current [OpenAPI schema](https://huggingface.co/.well-known/openapi.json).
  `GET /api/whoami-v2` returns the user `type` and `name`; its other fields are
  deliberately discarded.
* [Official HfApi reference](https://huggingface.co/docs/huggingface_hub/package_reference/hf_api)
  and [official implementation](https://github.com/huggingface/huggingface_hub/blob/main/src/huggingface_hub/hf_api.py):
  `list_models` uses `filter`, `pipeline_tag`, `search`, `limit`, and `expand`;
  `model_info(revision=..., files_metadata=True)` uses
  `/api/models/{repo}/revision/{encoded-revision}?blobs=true`, returning `sha` and
  `siblings` with `rfilename`, `size`, and optional `lfs.size`.
* A [public official model API response](https://huggingface.co/api/models/Qwen/Qwen2.5-0.5B-Instruct-GGUF?expand=gguf)
  confirms the optional `gguf.architecture` and `gguf.context_length` fields.
  No model file was fetched to establish these fields.
* The [official GGUF specification](https://github.com/ggml-org/ggml/blob/master/docs/gguf.md)
  defines the versioned preamble, metadata type IDs, string/array length fields,
  and standard architecture/attention keys used by the bounded header parser.
