export type ModelKind = 'Chat' | 'Embedding'
export interface ModelInspection {
  architecture: string; kind: ModelKind; fileBytes: number; weightBytes: number
  nativeContextTokens: number; attentionLayers: number; kvBytesPerToken: number; tensorTypes: string[]
}
export interface ContextPlan {
  model: ModelInspection; memoryLimitBytes: number; osReserveBytes: number; servicesReserveBytes: number
  voiceReserveBytes: number; runtimeReserveBytes: number; residentWeightBytes: number; otherResidentBytes: number
  cacheBudgetBytes: number; memoryLimitedContextTokens: number; effectiveContextTokens: number; qualification: string
}

export function chatContextRange(plan: Pick<ContextPlan, 'memoryLimitedContextTokens' | 'effectiveContextTokens'>) {
  const min = 8192
  const step = 256
  const max = Math.floor(plan.memoryLimitedContextTokens / step) * step
  return {
    min, max, step, available: max >= min,
    initial: max >= min ? Math.min(max, Math.max(min, Math.floor(plan.effectiveContextTokens / step) * step)) : min,
  }
}
export interface DownloadRequest {
  provider: 'huggingface'; repository: string; file: string; kind: ModelKind; revision: string; pro: boolean
}
export interface LocalModel {
  id: string; source: DownloadRequest; state: 'Queued' | 'Downloading' | 'Ready' | 'Failed' | 'Canceled' | 'Interrupted'
  createdAt: string; updatedAt: string; error: string | null; persistenceError: string | null; inspection: ModelInspection | null
}
export interface LoadedModel { id: string; name: string; plan: ContextPlan }
export interface ModelStatus { backend: string; chat: LoadedModel | null; embedding: LoadedModel | null; voiceReserveGiB: number; startupError: string | null }
export interface ProviderStatus { configured: boolean; accountName: string | null; source: string; validatedAt: string | null }
export interface SearchItem { repository: string; pipelineTag: string | null; downloads: number | null; likes: number | null; gated: boolean; private: boolean }
export interface ModelChoice {
  file: string; totalSizeBytes: number; quantization: string | null; labelSource: string; compatibility: string
  files: { path: string; sizeBytes: number }[]; download: DownloadRequest
}
export interface RepositoryModels {
  repository: string; requestedRevision: string; revision: string; kind: ModelKind; gated: boolean; private: boolean
  availability: string; choices: ModelChoice[]; warnings: string[]
  ggufMetadata: { source: string; scope: string; architecture: string | null; contextLength: number | null } | null
}

const invalid = () => new Error('Lucia returned incomplete model-management data. No model changes have been enabled.')
export function record(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw invalid()
  return value as Record<string, unknown>
}
function text(value: unknown): string {
  if (typeof value !== 'string' || !value.length || value.length > 4096) throw invalid()
  return value
}
function number(value: unknown): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0) throw invalid()
  return value
}
function boolean(value: unknown): boolean {
  if (typeof value !== 'boolean') throw invalid()
  return value
}
function finite(value: unknown): number {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) throw invalid()
  return value
}
function nullable<T>(value: unknown, parse: (input: unknown) => T): T | null {
  return value === null ? null : parse(value)
}
function array<T>(value: unknown, parse: (input: unknown) => T, limit = 1000): T[] {
  if (!Array.isArray(value) || value.length > limit) throw invalid()
  return value.map(parse)
}
function kind(value: unknown): ModelKind {
  if (value !== 'Chat' && value !== 'Embedding') throw invalid()
  return value
}
function id(value: unknown): string {
  const result = text(value)
  if (!/^[a-f\d]{8}-(?:[a-f\d]{4}-){3}[a-f\d]{12}$/i.test(result)) throw invalid()
  return result
}
function date(value: unknown): string {
  const result = text(value)
  if (!Number.isFinite(Date.parse(result))) throw invalid()
  return result
}
function inspection(value: unknown): ModelInspection {
  const x = record(value)
  return {
    architecture: text(x.architecture), kind: kind(x.kind), fileBytes: number(x.fileBytes), weightBytes: number(x.weightBytes),
    nativeContextTokens: number(x.nativeContextTokens), attentionLayers: number(x.attentionLayers),
    kvBytesPerToken: number(x.kvBytesPerToken), tensorTypes: array(x.tensorTypes, text),
  }
}
export function parseContextPlan(value: unknown): ContextPlan {
  const x = record(value)
  return {
    model: inspection(x.model), memoryLimitBytes: number(x.memoryLimitBytes), osReserveBytes: number(x.osReserveBytes),
    servicesReserveBytes: number(x.servicesReserveBytes), voiceReserveBytes: number(x.voiceReserveBytes),
    runtimeReserveBytes: number(x.runtimeReserveBytes), residentWeightBytes: number(x.residentWeightBytes),
    otherResidentBytes: number(x.otherResidentBytes), cacheBudgetBytes: number(x.cacheBudgetBytes),
    memoryLimitedContextTokens: number(x.memoryLimitedContextTokens), effectiveContextTokens: number(x.effectiveContextTokens),
    qualification: text(x.qualification),
  }
}
function source(value: unknown): DownloadRequest {
  const x = record(value)
  if (x.provider !== 'huggingface') throw invalid()
  return { provider: x.provider, repository: text(x.repository), file: text(x.file), kind: kind(x.kind), revision: text(x.revision), pro: boolean(x.pro) }
}
export function parseLocalModels(value: unknown): LocalModel[] {
  return array(value, value => {
    const x = record(value)
    const state = text(x.state)
    if (!['Queued', 'Downloading', 'Ready', 'Failed', 'Canceled', 'Interrupted'].includes(state)) throw invalid()
    return { id: id(x.id), source: source(x.source), state: state as LocalModel['state'], createdAt: date(x.createdAt),
      updatedAt: date(x.updatedAt), error: nullable(x.error, text), persistenceError: nullable(x.persistenceError, text),
      inspection: nullable(x.inspection, inspection) }
  })
}
export function parseModelStatus(value: unknown): ModelStatus {
  const x = record(value)
  const loaded = (value: unknown): LoadedModel => {
    const item = record(value)
    return { id: id(item.id), name: text(item.name), plan: parseContextPlan(item.plan) }
  }
  return { backend: text(x.backend), chat: nullable(x.chat, loaded), embedding: nullable(x.embedding, loaded),
    voiceReserveGiB: finite(x.voiceReserveGiB), startupError: nullable(x.startupError, text) }
}
export function parseProviderStatus(value: unknown): ProviderStatus {
  const x = record(value)
  return { configured: boolean(x.configured), accountName: nullable(x.accountName, text), source: text(x.source),
    validatedAt: nullable(x.validatedAt, date) }
}
export function parseSearch(value: unknown): { items: SearchItem[]; limitReached: boolean } {
  const x = record(value)
  return { limitReached: boolean(x.limitReached), items: array(x.items, value => {
    const item = record(value)
    return { repository: text(item.repository), pipelineTag: nullable(item.pipelineTag, text),
      downloads: nullable(item.downloads, number), likes: nullable(item.likes, number), gated: boolean(item.gated), private: boolean(item.private) }
  }, 30) }
}
export function parseRepository(value: unknown): RepositoryModels {
  const x = record(value)
  const revision = text(x.revision)
  if (!/^[a-f\d]{40}$/.test(revision)) throw invalid()
  const choices = array(x.choices, value => {
    const choice = record(value)
    const download = source(choice.download)
    if (download.revision !== revision || download.repository !== x.repository || download.file !== choice.file || download.kind !== x.kind) throw invalid()
    return { file: text(choice.file), totalSizeBytes: number(choice.totalSizeBytes), quantization: nullable(choice.quantization, text),
      labelSource: text(choice.labelSource), compatibility: text(choice.compatibility), download,
      files: array(choice.files, value => { const file = record(value); return { path: text(file.path), sizeBytes: number(file.sizeBytes) } }, 128) }
  }, 256)
  return {
    repository: text(x.repository), requestedRevision: text(x.requestedRevision), revision, kind: kind(x.kind),
    gated: boolean(x.gated), private: boolean(x.private), availability: text(x.availability), choices,
    warnings: array(x.warnings, text),
    ggufMetadata: nullable(x.ggufMetadata, value => {
      const metadata = record(value)
      return { source: text(metadata.source), scope: text(metadata.scope), architecture: nullable(metadata.architecture, text),
        contextLength: nullable(metadata.contextLength, number) }
    }),
  }
}
