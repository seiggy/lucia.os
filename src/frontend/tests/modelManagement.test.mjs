import assert from 'node:assert/strict'
import { chatContextRange, parseContextPlan, parseLocalModels, parseModelStatus, parseProviderStatus, parseRepository, parseSearch } from '../.checks/modelManagement.js'

const id = '11111111-1111-4111-8111-111111111111'
const revision = 'a'.repeat(40)
const source = { provider: 'huggingface', repository: 'fixture/model', file: 'model-Q4_K_M.gguf', kind: 'Chat', revision, pro: true }
const inspection = { architecture: 'llama', kind: 'Chat', fileBytes: 1000, weightBytes: 1000, nativeContextTokens: 32768, attentionLayers: 32, kvBytesPerToken: 1024, tensorTypes: ['Q4_K'] }
const plan = {
  model: inspection, memoryLimitBytes: 128 * 1024 ** 3, osReserveBytes: 8 * 1024 ** 3,
  servicesReserveBytes: 8 * 1024 ** 3, voiceReserveBytes: 8 * 1024 ** 3, runtimeReserveBytes: 8 * 1024 ** 3,
  residentWeightBytes: 2000, otherResidentBytes: 0, cacheBudgetBytes: 90 * 1024 ** 3,
  memoryLimitedContextTokens: 32768, effectiveContextTokens: 8192, qualification: 'Synthetic estimate, not a hardware guarantee.',
}
const model = { id, source, state: 'Ready', createdAt: '2026-09-22T12:00:00Z', updatedAt: '2026-09-22T12:00:00Z', error: null, persistenceError: null, inspection }
assert.equal(parseLocalModels([model])[0].source.kind, 'Chat')
assert.equal(parseContextPlan(plan).voiceReserveBytes, 8 * 1024 ** 3)
assert.deepEqual(chatContextRange(plan), { min: 8192, max: 32768, step: 256, available: true, initial: 8192 })
assert.equal(chatContextRange({ ...plan, effectiveContextTokens: 256 }).initial, 8192)
assert.equal(chatContextRange({ ...plan, effectiveContextTokens: 65536 }).initial, 32768)
assert.equal(chatContextRange({ ...plan, memoryLimitedContextTokens: 33000 }).max, 32768)
assert.equal(chatContextRange({ ...plan, effectiveContextTokens: 9000 }).initial, 8960)
assert.deepEqual(chatContextRange({ memoryLimitedContextTokens: 8192, effectiveContextTokens: 8192 }),
  { min: 8192, max: 8192, step: 256, available: true, initial: 8192 })
assert.equal(chatContextRange({ memoryLimitedContextTokens: 4096, effectiveContextTokens: 4096 }).available, false)
assert.equal(chatContextRange({ memoryLimitedContextTokens: 0, effectiveContextTokens: 0 }).available, false)
assert.equal(parseModelStatus({ backend: 'ggml_cuda', chat: { id, name: 'fixture', plan }, embedding: null, voiceReserveGiB: 8.5, startupError: null }).chat.id, id)
assert.equal(parseProviderStatus({ configured: false, accountName: null, source: 'disconnected', validatedAt: null }).configured, false)
assert.throws(() => parseLocalModels([{ ...model, state: 'PretendReady' }]), /incomplete model-management/)
assert.throws(() => parseContextPlan({ ...plan, memoryLimitBytes: -1 }), /incomplete model-management/)
const repository = {
  repository: 'fixture/model', requestedRevision: 'main', revision, kind: 'Chat', gated: false, private: false, availability: 'available',
  warnings: [], ggufMetadata: { source: 'huggingface_api', scope: 'repository', architecture: 'llama', contextLength: 32768 },
  choices: [{ file: source.file, files: [{ path: source.file, sizeBytes: 1000 }], totalSizeBytes: 1000, quantization: 'Q4_K_M', labelSource: 'filename_inferred', compatibility: 'unverified', download: source }],
}
assert.equal(parseRepository(repository).choices[0].download.revision, revision)
assert.throws(() => parseRepository({ ...repository, revision: 'main' }), /incomplete model-management/)
assert.throws(() => parseRepository({ ...repository, kind: 'Embedding' }), /incomplete model-management/)
assert.throws(() => parseRepository({ ...repository, repository: 'unrelated/model' }), /incomplete model-management/)
assert.deepEqual(parseSearch({ items: [], limitReached: false }), { items: [], limitReached: false })
console.log('Model-management checks passed: pinned choices, slot kinds, context reserves, provider status and invalid-data handling.')
