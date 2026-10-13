import type { DynamicToolUIPart } from 'ai'

export type AssistantMode = 'plan' | 'execute'
export type AssistantToolPart = DynamicToolUIPart
export type AssistantPart =
  | { type: 'text'; text: string; state?: 'streaming' | 'done' }
  | { type: 'reasoning'; text: string; state?: 'streaming' | 'done' }
  | AssistantToolPart
export type ContextUse = { used: number; limit?: number; system?: number; conversation?: number; tools?: number }
export type AssistantMetadata = {
  model?: string
  usage?: { inputTokens?: number; outputTokens?: number }
  context?: ContextUse
  stopped?: boolean
  error?: string
}
export type AssistantMessage = { id: string; role: 'user' | 'assistant'; parts: AssistantPart[]; metadata?: AssistantMetadata }
export type Transcript = { title: string; messages: AssistantMessage[]; running: boolean; job?: string }
export type ChatSummary = { id: string; title: string; updated: string; model?: string; running: boolean; job?: string }
export type ContextTier = { id: string; name: string; tokens?: number }
export type AssistantModel = { id: string; name: string; context?: number; reasoning?: string[]; defaultReasoning?: string; tiers?: ContextTier[] }
export type AssistantSource = { id: string; name: string; models: AssistantModel[]; reason?: string }
export type AssistantModels = { connected: boolean; defaultModel?: string; sources: AssistantSource[]; models: AssistantModel[] }
export type GitHubState = 'connected' | 'pending' | 'disconnected' | 'expired' | 'denied' | 'error'
export type GitHubStatus = { state: GitHubState; login?: string; userCode?: string; verificationUri?: string; message?: string }
export type DockSide = 'left' | 'right'
export type DockState = { open: boolean; side: DockSide }
export type DockLayout = 'push' | 'overlay' | 'sheet'
export type ToolTier = 'read' | 'web' | 'registry' | 'change' | 'destructive' | 'secret'
export type AssistantTool = { name: string; tier: ToolTier; description: string }
export type AssistantSettings = { autoTools: string[]; hosts: string[]; tools: AssistantTool[]; modelEfforts: Record<string, string[]> }
export type ToolTone = 'busy' | 'done' | 'failed' | 'waiting' | 'muted'
export type ToolStatus = { word: string; tone: ToolTone }

export const maxMessageLength = 32768
const chatIdPattern = /^[a-f0-9]{32}$/
const modelPattern = /^[A-Za-z0-9._:/-]{1,100}$/
const routePattern = /^\/[A-Za-z0-9/_.-]{0,200}$/
const githubStates: readonly string[] = ['connected', 'pending', 'disconnected', 'expired', 'denied', 'error']
const userCodePattern = /^[A-Za-z0-9-]{4,16}$/
const loginPattern = /^[A-Za-z0-9-]{1,39}$/

const record = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value)
const unexpected = () => new Error('Lucia returned an unexpected assistant response.')

export function newChatId(): string {
  return Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('')
}

export const isChatId = (value: unknown): value is string => typeof value === 'string' && chatIdPattern.test(value)
const jobOf = (value: unknown) => isChatId(value) ? { job: value } : {}

export const chatKey = 'lucia.assistant.chat.v1'
export const openChatEvent = 'lucia:open-chat'
// Jobs and notifications open a chat in the dock: a mounted panel switches to it, and one that mounts later reads it back.
export function openAssistantChat(id: string) {
  if (!isChatId(id)) return
  try { localStorage.setItem(chatKey, id) } catch { /* The event still opens it. */ }
  window.dispatchEvent(new CustomEvent(openChatEvent, { detail: id }))
}
// Another page asks a question: the dock opens, starts a new chat, and sends it once the chat can answer.
export const askEvent = 'lucia:ask'
let pendingAsk: string | null = null
export function askAssistant(text: string) {
  pendingAsk = text.trim().slice(0, maxMessageLength) || null
  if (pendingAsk) window.dispatchEvent(new CustomEvent(askEvent))
}
export function takeAsk(): string | null {
  const text = pendingAsk
  pendingAsk = null
  return text
}
export const isModelId = (value: unknown): value is string => typeof value === 'string' && modelPattern.test(value)
// Picking this asks for a GitHub sign-in; it is never sent.
export const signInModel = 'github:'
// Mirrors the host: LiteLLM and Local AI ids carry their source, and every other id is Copilot's.
export const isGitHubModel = (id?: string) => !id || !/^(litellm|local):/.test(id)

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
      ...(typeof item.model === 'string' && item.model ? { model: item.model } : {}), ...jobOf(item.job) }
  })
}

const trimmed = (value: unknown) => typeof value === 'string' ? value.trim() : ''
const tokenCount = (value: unknown) => typeof value === 'number' && Number.isFinite(value) && value > 0 ? Math.round(value) : undefined
const effortPattern = /^[a-z][a-z0-9_-]{0,15}$/
const tierIds: readonly string[] = ['default', 'long_context']

function parseModel(model: Record<string, unknown> & { id: string }): AssistantModel {
  const reasoning = Array.isArray(model.reasoning) ? model.reasoning.filter((item): item is string => typeof item === 'string' && effortPattern.test(item)) : []
  const defaultReasoning = typeof model.defaultReasoning === 'string' && reasoning.includes(model.defaultReasoning) ? model.defaultReasoning : undefined
  const tiers = Array.isArray(model.tiers) ? model.tiers.flatMap((tier): ContextTier[] => {
    if (!record(tier) || typeof tier.id !== 'string' || !tierIds.includes(tier.id)) return []
    const tokens = tokenCount(tier.tokens)
    return [{ id: tier.id, name: trimmed(tier.name) || tier.id, ...(tokens ? { tokens } : {}) }]
  }) : []
  const context = tokenCount(model.context)
  return { id: model.id, name: trimmed(model.name) || model.id, ...(context ? { context } : {}),
    ...(reasoning.length ? { reasoning } : {}), ...(defaultReasoning ? { defaultReasoning } : {}), ...(tiers.length > 1 ? { tiers } : {}) }
}

export function parseModels(value: unknown): AssistantModels {
  if (!record(value) || typeof value.connected !== 'boolean' || !Array.isArray(value.sources)) throw unexpected()
  const sources = value.sources.flatMap((item): AssistantSource[] => {
    if (!record(item) || !trimmed(item.id)) return []
    const models = Array.isArray(item.models) ? item.models.flatMap(model => record(model) && isModelId(model.id)
      ? [parseModel({ ...model, id: model.id })] : []) : []
    const reason = trimmed(item.reason).slice(0, 400)
    return [{ id: trimmed(item.id), name: trimmed(item.name) || trimmed(item.id), models, ...(reason ? { reason } : {}) }]
  })
  return { connected: value.connected, sources, models: sources.flatMap(source => source.models),
    ...(isModelId(value.defaultModel) ? { defaultModel: value.defaultModel } : {}) }
}

// The sign-in page opens in a new tab, so only GitHub's own HTTPS pages are accepted.
export function parseGitHub(value: unknown): GitHubStatus {
  if (!record(value) || typeof value.state !== 'string' || !githubStates.includes(value.state)) throw unexpected()
  const status: GitHubStatus = { state: value.state as GitHubState }
  if (status.state === 'pending') {
    let page: URL
    try { page = new URL(String(value.verificationUri)) } catch { throw unexpected() }
    if (typeof value.userCode !== 'string' || !userCodePattern.test(value.userCode) || page.origin !== 'https://github.com'
      || page.username || page.password) throw unexpected()
    status.userCode = value.userCode
    status.verificationUri = page.href
  }
  if (status.state === 'connected' && typeof value.login === 'string' && loginPattern.test(value.login)) status.login = value.login
  if (typeof value.message === 'string' && value.message.trim()) status.message = value.message.trim().slice(0, 400)
  return status
}

function parseMetadata(value: unknown): AssistantMetadata | undefined {
  if (!record(value)) return undefined
  const count = (item: unknown) => typeof item === 'number' && Number.isFinite(item) && item >= 0 ? item : undefined
  const usage = record(value.usage) ? { inputTokens: count(value.usage.inputTokens), outputTokens: count(value.usage.outputTokens) } : undefined
  const fill = record(value.context) ? value.context : undefined
  const used = fill ? count(fill.used) : undefined
  const some = (key: string, item: unknown) => count(item) ? { [key]: count(item) } : {}
  const context: ContextUse | undefined = fill && used !== undefined
    ? { used, ...some('limit', fill.limit), ...some('system', fill.system), ...some('conversation', fill.conversation), ...some('tools', fill.tools) } : undefined
  const metadata: AssistantMetadata = {
    ...(typeof value.model === 'string' && value.model ? { model: value.model.slice(0, 100) } : {}),
    ...(usage && (usage.inputTokens !== undefined || usage.outputTokens !== undefined) ? { usage } : {}),
    ...(context ? { context } : {}),
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
    const parts = item.parts.flatMap((part): AssistantPart[] => !record(part) ? []
      : part.type === 'dynamic-tool' ? parseToolPart(part)
      : (part.type === 'text' || part.type === 'reasoning') && typeof part.text === 'string' ? [{ type: part.type, text: part.text, state: 'done' }] : [])
    const metadata = parseMetadata(item.metadata)
    if (item.role === 'user' ? !messageText({ parts }) : !parts.length && !metadata) return []
    return [{ id: item.id, role: item.role, parts, ...(metadata ? { metadata } : {}) }]
  })
  return { title: typeof value.title === 'string' && value.title.trim() ? value.title : 'Chat', messages, running: value.running, ...jobOf(value.job) }
}

// Choosing GitHub stays chosen until Copilot can be used, so a refusal after signing in still explains itself.
export function chooseModel(models: AssistantModels, saved: string | null | undefined): string | undefined {
  const listed = (id: string) => models.models.some(model => model.id === id)
  if (saved && (listed(saved) || (saved === signInModel && !models.connected))) return saved
  if (models.defaultModel && listed(models.defaultModel)) return models.defaultModel
  return models.models[0]?.id ?? (models.connected ? undefined : signInModel)
}

// Reasoning efforts with the short name the composer button shows, in order of cost.
const efforts = new Map([['none', ['0', 'Disabled']], ['minimal', ['XS', 'Minimal']], ['low', ['S', 'Low']], ['medium', ['M', 'Medium']],
  ['high', ['L', 'High']], ['xhigh', ['XL', 'Extra high']], ['max', ['XXL', 'Max']]])
// The efforts an owner can allow for a model of their own, cheapest first; the host keeps to the same list.
export const reasoningEfforts = ['none', 'low', 'medium', 'high', 'xhigh']
export const effortShort = (effort: string) => efforts.get(effort)?.[0] ?? effort.slice(0, 2).toUpperCase()
export const effortLabel = (effort: string) => efforts.get(effort)?.[1] ?? effort[0].toUpperCase() + effort.slice(1)
export const findModel = (models: AssistantModels | null | undefined, id?: string) => models?.models.find(model => model.id === id)

// What the owner picked per model is kept on this browser; a choice the model no longer offers is ignored.
export const reasoningKey = 'lucia.assistant.reasoning.v1'
export const contextKey = 'lucia.assistant.context.v1'
export function parseChoices(text: string | null): Record<string, string> {
  try {
    const saved: unknown = text ? JSON.parse(text) : {}
    return record(saved) ? Object.fromEntries(Object.entries(saved).filter(([id, choice]) => isModelId(id) && typeof choice === 'string' && effortPattern.test(choice))) as Record<string, string> : {}
  } catch { return {} }
}

// The effort to send: the saved choice when the model still offers it, else none, which leaves it to the model.
export const chosenEffort = (model: AssistantModel | undefined, saved: string | undefined) =>
  saved && model?.reasoning?.includes(saved) ? saved : undefined

// The context window in use for a model that offers a choice: the saved tier, else its first (standard).
export const currentTier = (model: AssistantModel | undefined, saved: string | undefined): ContextTier | undefined =>
  model?.tiers?.find(tier => tier.id === saved) ?? model?.tiers?.[0]

export function tokenText(tokens: number): string {
  if (tokens >= 1_000_000) return `${Number((tokens / 1_000_000).toFixed(1))}M`
  return tokens >= 1_000 ? `${Math.round(tokens / 1_000)}K` : String(tokens)
}

// How full the context is. A model with a choice of windows shows the chosen one at once; otherwise the host's own limit leads.
export function contextFill(model: AssistantModel | undefined, tier: ContextTier | undefined, last: ContextUse | undefined) {
  const limit = model?.tiers ? tier?.tokens ?? last?.limit : last?.limit ?? model?.context
  const used = last?.used ?? 0
  return { used, limit, share: limit ? Math.min(1, used / limit) : undefined, detail: last }
}

export const lastContext = (messages: readonly { role: string; metadata?: AssistantMetadata }[]): ContextUse | undefined =>
  [...messages].reverse().find(message => message.role === 'assistant' && message.metadata?.context)?.metadata?.context

export function parseDock(value: unknown, wide: boolean): DockState {
  const saved = record(value) ? value : {}
  return { open: wide && saved.open === true, side: saved.side === 'left' ? 'left' : 'right' }
}

const toolStates: readonly string[] = ['input-streaming', 'input-available', 'approval-requested', 'approval-responded',
  'output-available', 'output-error', 'output-denied']
const questionTools: readonly string[] = ['ask_owner', 'request_secret']
// Mirrors the host's destructive tier, which always asks and can't be allowed for a whole chat.
const destructiveTools: readonly string[] = ['delete_app', 'restore_backup', 'set_public_route', 'node_action', 'run_command', 'install_spark_updates']
// The host gives these reasons for denials of its own, in the field that otherwise carries the owner's reason for declining.
const systemDenials: readonly string[] = ['No answer within 30 minutes.', 'The run stopped before you answered.']
// The host fails calls still running when their turn ends with this; the call itself didn't fail.
const cutShort = 'The run stopped before this finished.'

export const isQuestionTool = (name: string) => questionTools.includes(name)
export const isDestructiveTool = (name: string) => destructiveTools.includes(name)

function parseToolPart(part: Record<string, unknown>): AssistantToolPart[] {
  const { toolCallId, toolName, state } = part
  if (typeof toolCallId !== 'string' || !toolCallId || toolCallId.length > 128 || typeof toolName !== 'string' || !toolName
    || typeof state !== 'string' || !toolStates.includes(state)) return []
  const saved = record(part.approval) && typeof part.approval.id === 'string' ? part.approval : undefined
  const approval = saved && {
    id: saved.id,
    ...(typeof saved.requestReason === 'string' ? { requestReason: saved.requestReason } : {}),
    ...(saved.isAutomatic === true ? { isAutomatic: true } : {}),
    ...(typeof saved.approved === 'boolean' ? { approved: saved.approved } : {}),
    ...(typeof saved.reason === 'string' ? { reason: saved.reason } : {}),
  }
  if ((state.startsWith('approval-') || state === 'output-denied') && !approval) return []
  if (state === 'output-error' && typeof part.errorText !== 'string') return []
  return [{
    type: 'dynamic-tool', toolCallId, toolName, state, input: part.input,
    ...(state === 'output-available' ? { output: part.output } : {}),
    ...(state === 'output-error' ? { errorText: part.errorText } : {}),
    ...(approval ? { approval } : {}),
  } as unknown as AssistantToolPart]
}

// A question or an approval the owner can answer now. An automatic approval settles on its own a moment later.
export const waitingPart = (part: AssistantToolPart) => part.state === 'approval-requested' ? !part.approval.isAutomatic
  : part.state === 'input-available' && isQuestionTool(part.toolName)

export function waitingParts(messages: readonly { role: string; parts: readonly { type: string }[] }[], live: boolean): AssistantToolPart[] {
  const last = messages.at(-1)
  return live && last?.role === 'assistant'
    ? last.parts.filter((part): part is AssistantToolPart => part.type === 'dynamic-tool' && waitingPart(part as AssistantToolPart)) : []
}

const toolTitles = new Map([
  ['list_nodes', 'List servers'],
  ['get_node', 'Check server {node}'],
  ['list_containers', 'List containers on {node}'],
  ['read_logs', 'Read logs of {container} on {node}'],
  ['list_apps', 'List apps'],
  ['get_app', 'Check app {app}'],
  ['list_catalog', 'Browse the app catalog'],
  ['get_catalog_app', 'Look up {app} in the catalog'],
  ['list_storage', 'Check storage'],
  ['list_backups', 'List backups'],
  ['dns_lookup', 'Look up {name} in DNS'],
  ['list_network_clients', 'List network clients'],
  ['ask_owner', '{question}'],
  ['read_web_page', 'Read {url}'],
  ['query_registry', 'Look up {image} in its registry'],
  ['request_secret', '{app} needs {name}'],
  ['save_custom_app', 'Save app {app}'],
  ['install_catalog_app', 'Install {app}'],
  ['app_action:start', 'Start {app}'],
  ['app_action:stop', 'Stop {app}'],
  ['app_action:restart', 'Restart {app}'],
  ['app_action:update', 'Update {app}'],
  ['app_action:cancel-move', 'Cancel moving {app}'],
  ['app_action:cancel-restore', 'Cancel restoring {app}'],
  ['move_app', 'Move {app} to {node}'],
  ['run_backup', 'Back up {app}'],
  ['upgrade_app_images', 'Upgrade images of {app}'],
  ['check_node_updates', 'Check {node} for updates'],
  ['get_spark_health', 'Check the Spark’s health'],
  ['get_spark_updates', 'Check the Spark’s updates'],
  ['check_spark_updates', 'Check the Spark for updates'],
  ['list_node_images', 'Check images pulled on {node}'],
  ['delete_app', 'Delete {app}'],
  ['restore_backup', 'Restore {app} from a backup'],
  ['set_public_route:on', 'Publish {host} as {publicName}'],
  ['set_public_route:off', 'Take {host} off the internet'],
  ['node_action:install-updates', 'Install updates on {node}'],
  ['node_action:restart', 'Restart server {node}'],
  ['node_action:update-agent', 'Update Lucia’s agent on {node}'],
  ['run_command', 'Run a command on {node}'],
  ['install_spark_updates', 'Install updates on the Spark'],
])

export function urlHost(url: unknown): string | undefined {
  try { return typeof url === 'string' ? new URL(url).host || undefined : undefined } catch { return undefined }
}

// Names the call from its input; the bare tool name stands in until the input is complete.
export function toolTitle(name: string, input: unknown): string {
  const values = record(input) ? input : {}
  const key = name === 'app_action' || name === 'node_action' ? `${name}:${String(values.action)}`
    : name === 'set_public_route' ? `${name}:${values.publicName ? 'on' : 'off'}` : name
  let missing = false
  const title = toolTitles.get(key)?.replace(/\{(\w+)\}/g, (_, field: string) => {
    const value = values[field]
    const text = (field === 'url' ? urlHost(value) ?? '' : typeof value === 'string' || typeof value === 'number' ? String(value) : '')
      .replace(/\s+/g, ' ').trim()
    if (!text) missing = true
    return text.length > 200 ? `${text.slice(0, 199).trimEnd()}…` : text
  })
  return title && !missing ? title : name
}

const answered = (output: unknown): output is { answer: string } => record(output) && typeof output.answer === 'string'
const secretSaved = (output: unknown) => record(output) && output.saved === true

export function toolStatus(part: AssistantToolPart, live: boolean): ToolStatus {
  const running: ToolStatus = live ? { word: 'Running…', tone: 'busy' } : { word: 'Not finished', tone: 'muted' }
  switch (part.state) {
    case 'input-streaming':
    case 'input-available':
      return live && part.state === 'input-available' && isQuestionTool(part.toolName) ? { word: 'Waiting for you', tone: 'waiting' } : running
    case 'approval-requested':
      return part.approval.isAutomatic ? running : live ? { word: 'Needs your approval', tone: 'waiting' } : { word: 'Not answered', tone: 'muted' }
    case 'approval-responded':
      return part.approval.approved ? running : { word: 'Not run', tone: 'muted' }
    case 'output-available':
      if (part.toolName === 'ask_owner') return answered(part.output) ? { word: 'Answered', tone: 'done' } : { word: 'Skipped', tone: 'muted' }
      if (part.toolName === 'request_secret') return secretSaved(part.output) ? { word: 'Saved', tone: 'done' } : { word: 'Not given', tone: 'muted' }
      return { word: 'Done', tone: 'done' }
    case 'output-error':
      return part.errorText === cutShort ? { word: 'Not finished', tone: 'muted' } : { word: 'Failed', tone: 'failed' }
    case 'output-denied':
      return { word: 'Not run', tone: 'muted' }
  }
}

// The one line under a settled call's title: why it didn't run, how it failed, or what the owner answered.
export function toolNote(part: AssistantToolPart): { text: string; tone: ToolTone } | undefined {
  if (part.state === 'output-denied') {
    const { isAutomatic, reason } = part.approval
    return { tone: 'muted', text: isAutomatic ? reason || 'Lucia’s policy doesn’t allow this.'
      : reason && systemDenials.includes(reason) ? reason : reason ? `You declined: “${reason}”` : 'You declined this.' }
  }
  if (part.state === 'output-error') return { text: part.errorText, tone: part.errorText === cutShort ? 'muted' : 'failed' }
  if (part.state !== 'output-available') return undefined
  if (part.toolName === 'ask_owner')
    return { tone: 'muted', text: answered(part.output) ? `You answered: ${part.output.answer}` : 'You skipped this question.' }
  if (part.toolName === 'request_secret') {
    const app = record(part.output) && typeof part.output.app === 'string' ? part.output.app : 'the app'
    return { tone: 'muted', text: secretSaved(part.output) ? `Saved in ${app}’s settings. The assistant never sees it.`
      : 'You chose not to give it. Nothing was saved.' }
  }
  return undefined
}

// Why an approved call ran, for its details.
export function approvalNote(part: AssistantToolPart): string | undefined {
  const approval = part.approval
  return approval?.approved ? approval.isAutomatic ? approval.reason : 'You approved this.' : undefined
}

const tiers: readonly string[] = ['read', 'web', 'registry', 'change', 'destructive', 'secret']
const strings = (value: unknown): value is string[] => Array.isArray(value) && value.every(item => typeof item === 'string')

export function parseSettings(value: unknown): AssistantSettings {
  if (!record(value) || !strings(value.autoTools) || !strings(value.hosts) || !Array.isArray(value.tools)) throw unexpected()
  const tools = value.tools.map((tool): AssistantTool => {
    if (!record(tool) || typeof tool.name !== 'string' || !tool.name || typeof tool.tier !== 'string' || !tiers.includes(tool.tier)
      || typeof tool.description !== 'string') throw unexpected()
    return { name: tool.name, tier: tool.tier as ToolTier, description: tool.description }
  })
  const modelEfforts: Record<string, string[]> = {}
  if (record(value.modelEfforts))
    for (const [id, list] of Object.entries(value.modelEfforts))
      if (isModelId(id) && Array.isArray(list)) modelEfforts[id] = list.filter((item): item is string => typeof item === 'string' && reasoningEfforts.includes(item))
  return { autoTools: value.autoTools, hosts: value.hosts, tools, modelEfforts }
}

// The host's descriptions are written for the model; the settings page names what each change tool lets the assistant do.
const changeLabels = new Map([
  ['save_custom_app', 'Create and change custom apps'],
  ['install_catalog_app', 'Install catalog apps and change their settings'],
  ['app_action', 'Start, stop, restart and update apps'],
  ['move_app', 'Move apps between servers'],
  ['run_backup', 'Back up apps'],
  ['upgrade_app_images', 'Upgrade app images'],
  ['check_node_updates', 'Check servers for updates'],
  ['check_spark_updates', 'Check the Spark for updates'],
  ['list_node_images', 'Query node container cache'],
  // Only a job may be granted these.
  ['delete_app', 'Delete apps'],
  ['restore_backup', 'Restore app backups'],
  ['set_public_route', 'Put apps on the internet or take them off'],
  ['node_action', 'Update or restart servers'],
  ['install_spark_updates', 'Install updates on the Spark'],
  ['run_command', 'Run commands on servers'],
])

export const toolLabel = (tool: AssistantTool) => changeLabels.get(tool.name) ?? tool.description

// "Approve for the rest of this chat" grants the whole change tool, or the whole site for a web read; destructive calls always ask.
// In a job's chat the grant is saved to the job, destructive tools included.
export function grantScope(part: AssistantToolPart, job = false): string | undefined {
  const host = part.toolName === 'read_web_page' && record(part.input) ? urlHost(part.input.url) : undefined
  const label = !job && isDestructiveTool(part.toolName) ? undefined : changeLabels.get(part.toolName)
  if (part.toolName === 'query_registry' && record(part.input) && typeof part.input.image === 'string') {
    const first = part.input.image.split('/')[0]
    return `query ${/[.:]/.test(first) ? first : 'docker.io'}`
  }
  return host ? `read ${host}` : label && label[0].toLowerCase() + label.slice(1)
}

export function grantText(part: AssistantToolPart, job = false): string | undefined {
  const scope = grantScope(part, job)
  return scope && (job ? `Approve, and let this job ${scope} without asking` : `Approve, and let it ${scope} for the rest of this chat`)
}

function inputText(input: unknown, field: string) {
  const value = record(input) ? input[field] : undefined
  return typeof value === 'string' ? value.trim() : ''
}

export function questionOf(input: unknown) {
  const choices = record(input) && strings(input.choices) ? input.choices.map(choice => choice.trim()).filter(Boolean) : []
  return { question: inputText(input, 'question'), choices, freeform: !choices.length || (record(input) && input.allowFreeform !== false) }
}

export const secretOf = (input: unknown) => ({ app: inputText(input, 'app'), name: inputText(input, 'name'), description: inputText(input, 'description') })
