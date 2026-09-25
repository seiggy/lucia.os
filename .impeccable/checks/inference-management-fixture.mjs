const revision = 'a'.repeat(40)
const id = '11111111-1111-4111-8111-111111111111'
const source = { provider: 'huggingface', repository: 'fixture/chat-GGUF', file: 'fixture-Q4_K_M.gguf', kind: 'Chat', revision, pro: true }
const inspection = { architecture: 'llama', kind: 'Chat', fileBytes: 10 * 1024 ** 3, weightBytes: 10 * 1024 ** 3, nativeContextTokens: 32768, attentionLayers: 32, kvBytesPerToken: 1024, tensorTypes: ['Q4_K'] }
const plan = {
  model: inspection, memoryLimitBytes: 128 * 1024 ** 3, osReserveBytes: 8 * 1024 ** 3, servicesReserveBytes: 8 * 1024 ** 3,
  voiceReserveBytes: 8 * 1024 ** 3, runtimeReserveBytes: 8 * 1024 ** 3, residentWeightBytes: 20 * 1024 ** 3,
  otherResidentBytes: 0, cacheBudgetBytes: 76 * 1024 ** 3, memoryLimitedContextTokens: 32768, effectiveContextTokens: 8192,
  qualification: 'Synthetic estimate for UI checks, not a hardware qualification.',
}
const initial = { id, source, state: 'Ready', createdAt: '2026-09-22T12:00:00Z', updatedAt: '2026-09-22T12:00:00Z', error: null, persistenceError: null, inspection }
let keys = []
let connected = false
let models = [initial]
let loaded = { id, name: source.file, plan }

export function resetInferenceFixture() {
  keys = []; connected = false; models = [initial]; loaded = { id, name: source.file, plan }
}

export async function inferenceFixture(request, path, mode, json) {
  if (!path.startsWith('/api/host/inference-keys') && !path.startsWith('/api/host/huggingface')
    && !path.startsWith('/api/host/models') && path !== '/api/host/status') return false
  if (mode !== 'owner') { json({ error: { message: 'Synthetic Owner access required.' } }, 403); return true }
  let body
  if (request.method !== 'GET') {
    if (request.headers['x-csrf-token'] !== 'fixture-csrf-request') { json({ error: { message: 'Synthetic CSRF check failed.' } }, 403); return true }
    let text = ''
    for await (const chunk of request) { text += chunk; if (text.length > 4096) throw new Error('Fixture request too large') }
    body = text ? JSON.parse(text) : undefined
  }
  if (path === '/api/host/inference-keys') {
    if (request.method === 'POST') {
      const key = { id, name: body.name, hint: 'lucia_inf_AAAAAA...AAAA', createdAt: new Date().toISOString(), expiresAt: body.expiresAt }
      keys.push(key)
      json({ key, secret: 'lucia_inf_' + 'A'.repeat(43) })
    } else json(keys)
  } else if (path.startsWith('/api/host/inference-keys/')) {
    keys = []; json({}, 200)
  } else if (path === '/api/host/huggingface/credentials') {
    if (request.method === 'PUT') connected = true
    if (request.method === 'DELETE') connected = false
    json({ configured: connected, accountName: connected ? 'synthetic-user' : null,
      source: connected ? 'managed' : 'disconnected', validatedAt: connected ? new Date().toISOString() : null })
  } else if (path === '/api/host/huggingface/search') {
    json({ items: [{ repository: source.repository, pipelineTag: 'text-generation', downloads: 100, likes: 10, gated: false, private: false }], limitReached: false })
  } else if (path === '/api/host/huggingface/repository') {
    json({ repository: source.repository, requestedRevision: 'main', revision, kind: 'Chat', gated: false, private: false,
      availability: 'available', warnings: ['Synthetic repository fixture.'], ggufMetadata: { source: 'huggingface_api', scope: 'repository', architecture: 'llama', contextLength: 32768 },
      choices: [{ file: source.file, files: [{ path: source.file, sizeBytes: inspection.fileBytes }], totalSizeBytes: inspection.fileBytes,
        quantization: 'Q4_K_M', labelSource: 'filename_inferred', compatibility: 'unverified', download: source }] })
  } else if (path === '/api/host/huggingface/context-preview') {
    json({ available: true, plan, reason: null })
  } else if (path === '/api/host/status') {
    json({ backend: 'ggml_cuda', chat: loaded, embedding: null, voiceReserveGiB: 8, startupError: null })
  } else if (path === '/api/host/models') {
    json(models)
  } else if (path.endsWith('/context')) {
    json(plan)
  } else if (path.endsWith('/load')) {
    loaded = { id, name: source.file, plan: { ...plan, effectiveContextTokens: body.contextTokens } }; json(loaded.plan)
  } else if (path.endsWith('/unload')) {
    loaded = null; json({})
  } else if (path.endsWith('/download')) {
    models = [...models, { ...initial, id: '22222222-2222-4222-8222-222222222222', state: 'Queued', inspection: null }]
    json(models[models.length - 1], 202)
  } else if (path.endsWith('/cancel')) {
    models = models.map(model => model.state === 'Queued' ? { ...model, state: 'Canceled' } : model); json({}, 202)
  } else {
    json({ error: { message: 'Unsupported synthetic operation.' } }, 404)
  }
  return true
}
