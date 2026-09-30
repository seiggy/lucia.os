export type AssistantMode = 'plan' | 'execute'
export type AssistantPart =
  | { type: 'text'; text: string; state?: 'streaming' | 'done' }
  | { type: 'reasoning'; text: string; state?: 'streaming' | 'done' }
export type AssistantMetadata = {
  model?: string
  usage?: { inputTokens?: number; outputTokens?: number }
  stopped?: boolean
  error?: string
}
export type AssistantMessage = { id: string; role: 'user' | 'assistant'; parts: AssistantPart[]; metadata?: AssistantMetadata }
export type Transcript = { title: string; messages: AssistantMessage[]; running: boolean }
export type ChatSummary = { id: string; title: string; updated: string; model?: string; running: boolean }
export type AssistantModel = { id: string; name: string }
export type AssistantModels = { connected: boolean; defaultModel?: string; models: AssistantModel[] }
export type DockSide = 'left' | 'right'
export type DockState = { open: boolean; side: DockSide }
export type DockLayout = 'push' | 'overlay' | 'sheet'

export const maxMessageLength = 32768
const chatIdPattern = /^[a-f0-9]{32}$/
const modelPattern = /^[A-Za-z0-9._:/-]{1,100}$/
const routePattern = /^\/[A-Za-z0-9/_.-]{0,200}$/

const record = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value)
const unexpected = () => new Error('Lucia returned an unexpected assistant response.')

export function newChatId(): string {
  return Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('')
}

export const isChatId = (value: unknown): value is string => typeof value === 'string' && chatIdPattern.test(value)
export const isModelId = (value: unknown): value is string => typeof value === 'string' && modelPattern.test(value)

export function pageRoute(hash: string): string | undefined {
  const route = hash.replace(/^#/, '').split('?')[0] || '/'
  return routePattern.test(route) ? route : undefined
}

export function messageText(message: { parts: readonly { type: string; text?: unknown }[] }): string {
  return message.parts.map(part => part.type === 'text' && typeof part.text === 'string' ? part.text : '').join('')
}

const bareMarker = /^[ \t]*(?:>[ \t]*)*(?:[-*+]|#{1,6}|\d{1,9}[.)])?[ \t]*$/
const fenceLine = /^[ \t]*(?:`{3,}|~{3,})/
const listLine = /^[ \t]*(?:>[ \t]*)*(?:[-*+]|\d{1,9}[.)])[ \t]/

// A stopped or failed answer can end in block syntax with nothing after it, which renders as an empty list item, heading, or code block.
export function settledText(text: string): string {
  const lines = text.trimEnd().split('\n')
  while (lines.length) {
    const last = lines[lines.length - 1]
    const previous = lines.length > 1 ? lines[lines.length - 2] : ''
    const openFence = fenceLine.test(last) && lines.filter(line => fenceLine.test(line)).length % 2 === 1
    // After other text, a bare number such as "2024." ends the sentence rather than starting a list.
    const bare = bareMarker.test(last) && !(/\d[.)][ \t]*$/.test(last) && previous.trim() && !listLine.test(previous))
    if (!openFence && !bare) break
    lines.pop()
    while (lines.length && !lines[lines.length - 1].trim()) lines.pop()
  }
  return lines.join('\n')
}

// Mirrors the host's title rule so a new chat is named before the server saves it.
export function chatTitle(text: string): string {
  const line = text.trim().split(/[\r\n]/)[0]
  return line.length > 80 ? `${line.slice(0, 79).trimEnd()}…` : line
}

export function parseSessions(value: unknown): ChatSummary[] {
  if (!record(value) || !Array.isArray(value.sessions)) throw unexpected()
  return value.sessions.map(item => {
    if (!record(item) || !isChatId(item.id) || typeof item.title !== 'string' || typeof item.updated !== 'string'
      || typeof item.running !== 'boolean') throw unexpected()
    return { id: item.id, title: item.title || 'Chat', updated: item.updated, running: item.running,
      ...(typeof item.model === 'string' && item.model ? { model: item.model } : {}) }
  })
}

export function parseModels(value: unknown): AssistantModels {
  if (!record(value) || typeof value.connected !== 'boolean' || !Array.isArray(value.models)) throw unexpected()
  const models = value.models.flatMap(item => record(item) && isModelId(item.id)
    ? [{ id: item.id, name: typeof item.name === 'string' && item.name.trim() ? item.name.trim() : item.id }] : [])
  return { connected: value.connected, models, ...(isModelId(value.defaultModel) ? { defaultModel: value.defaultModel } : {}) }
}

function parseMetadata(value: unknown): AssistantMetadata | undefined {
  if (!record(value)) return undefined
  const count = (item: unknown) => typeof item === 'number' && Number.isFinite(item) && item >= 0 ? item : undefined
  const usage = record(value.usage) ? { inputTokens: count(value.usage.inputTokens), outputTokens: count(value.usage.outputTokens) } : undefined
  const metadata: AssistantMetadata = {
    ...(typeof value.model === 'string' && value.model ? { model: value.model.slice(0, 100) } : {}),
    ...(usage && (usage.inputTokens !== undefined || usage.outputTokens !== undefined) ? { usage } : {}),
    ...(value.stopped === true ? { stopped: true } : {}),
    ...(typeof value.error === 'string' && value.error.trim() ? { error: value.error.trim().slice(0, 400) } : {}),
  }
  return Object.keys(metadata).length ? metadata : undefined
}

export function parseTranscript(value: unknown): Transcript {
  if (!record(value) || !Array.isArray(value.messages) || typeof value.running !== 'boolean') throw unexpected()
  const messages = value.messages.flatMap((item): AssistantMessage[] => {
    if (!record(item) || typeof item.id !== 'string' || !item.id || (item.role !== 'user' && item.role !== 'assistant')
      || !Array.isArray(item.parts)) throw unexpected()
    const parts = item.parts.flatMap((part): AssistantPart[] => record(part) && (part.type === 'text' || part.type === 'reasoning')
      && typeof part.text === 'string' ? [{ type: part.type, text: part.text, state: 'done' }] : [])
    const metadata = parseMetadata(item.metadata)
    if (item.role === 'user' ? !messageText({ parts }) : !parts.length && !metadata) return []
    return [{ id: item.id, role: item.role, parts, ...(metadata ? { metadata } : {}) }]
  })
  return { title: typeof value.title === 'string' && value.title.trim() ? value.title : 'Chat', messages, running: value.running }
}

export function chooseModel(models: AssistantModels, saved: string | null | undefined): string | undefined {
  const listed = (id: string) => models.models.some(model => model.id === id)
  if (saved && listed(saved)) return saved
  if (models.defaultModel && listed(models.defaultModel)) return models.defaultModel
  return models.models[0]?.id
}

export function parseDock(value: unknown, wide: boolean): DockState {
  const saved = record(value) ? value : {}
  return { open: wide && saved.open === true, side: saved.side === 'left' ? 'left' : 'right' }
}
