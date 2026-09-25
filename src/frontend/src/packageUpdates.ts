export type RestartClass = 'spark' | 'lucia' | 'services'
export type RestartPolicy = 'none' | 'services' | 'spark'
export interface PackageUpdate {
  name: string; summary: string; currentVersion: string | null; candidateVersion: string; source: string
  security: boolean; platform: boolean; restart: RestartClass; downloadBytes: number; keptBack: boolean; phased: boolean
}
export interface PackageOperation {
  id: string; action: string; state: 'running' | 'succeeded' | 'failed' | 'interrupted'
  startedAt: string; finishedAt: string | null; message: string | null; packages: string[]; restart: RestartPolicy | null; log: string[]
  progress: PackageProgress | null
}
export interface PackageProgress { step: string; done: number; total: number; package: string | null }
export interface PackageSchedule { runAt: string; packages: string[] | null; includePlatform: boolean; restart: RestartPolicy; createdAt: string }
export interface SparkUpdates {
  worker: { state: 'ready' | 'stale' | 'missing'; installCommand: string }
  system: { os: string; dgx: string | null; kernel: string; architecture: string } | null
  lastUpdateCheckAt: string | null; scannedAt: string | null; scanning: boolean
  updates: PackageUpdate[]
  restart: { sparkRequired: boolean; sparkReasons: string[]; kernelPending: boolean; services: string[] }
  automaticUpdates: { installed: boolean; enabled: boolean } | null
  packageDatabase: { healthy: boolean; message: string | null }
  operation: PackageOperation | null; history: PackageOperation[]; rebootingAt: string | null
  schedule: PackageSchedule | null
  lastSchedule: { runAt: string; state: 'started' | 'missed' | 'failed'; message: string; at: string } | null
  pendingRequest: boolean; modelsPaused: boolean
}

const invalid = () => new Error('Lucia returned incomplete update data. The current state of the Spark could not be confirmed.')
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw invalid()
  return value as Record<string, unknown>
}
const text = (value: unknown): string => { if (typeof value !== 'string') throw invalid(); return value }
const optionalText = (value: unknown): string | null => value === null || value === undefined ? null : text(value)
const flag = (value: unknown): boolean => { if (typeof value !== 'boolean') throw invalid(); return value }
const texts = (value: unknown): string[] => { if (!Array.isArray(value)) throw invalid(); return value.map(text) }
function oneOf<T extends string>(value: unknown, allowed: readonly T[]): T {
  if (!allowed.includes(value as T)) throw invalid()
  return value as T
}
const policies = ['none', 'services', 'spark'] as const

function update(value: unknown): PackageUpdate {
  const data = object(value)
  const bytes = data.downloadBytes
  if (typeof bytes !== 'number' || !Number.isFinite(bytes) || bytes < 0) throw invalid()
  return {
    name: text(data.name), summary: text(data.summary), currentVersion: optionalText(data.currentVersion),
    candidateVersion: text(data.candidateVersion), source: text(data.source), security: flag(data.security),
    platform: flag(data.platform), restart: oneOf(data.restart, ['spark', 'lucia', 'services'] as const),
    downloadBytes: bytes, keptBack: flag(data.keptBack), phased: data.phased === undefined ? false : flag(data.phased),
  }
}
function operation(value: unknown): PackageOperation {
  const data = object(value)
  return {
    id: text(data.id), action: text(data.action), state: oneOf(data.state, ['running', 'succeeded', 'failed', 'interrupted'] as const),
    startedAt: text(data.startedAt), finishedAt: optionalText(data.finishedAt), message: optionalText(data.message),
    packages: data.packages === undefined ? [] : texts(data.packages),
    restart: data.restart === null || data.restart === undefined ? null : oneOf(data.restart, policies),
    log: data.log === undefined ? [] : texts(data.log),
    progress: data.progress === null || data.progress === undefined ? null : progress(data.progress),
  }
}
function progress(value: unknown): PackageProgress {
  const data = object(value)
  const count = (item: unknown) => { if (typeof item !== 'number' || !Number.isInteger(item) || item < 0) throw invalid(); return item }
  return { step: text(data.step), done: count(data.done), total: count(data.total), package: optionalText(data.package) }
}

export function parseSparkUpdates(value: unknown): SparkUpdates {
  const data = object(value)
  const worker = object(data.worker)
  const restart = data.restart === undefined ? null : object(data.restart)
  const database = data.packageDatabase === undefined ? null : object(data.packageDatabase)
  const system = data.system === undefined ? null : object(data.system)
  const automatic = data.automaticUpdates === undefined ? null : object(data.automaticUpdates)
  const schedule = data.schedule === null || data.schedule === undefined ? null : object(data.schedule)
  const last = data.lastSchedule === null || data.lastSchedule === undefined ? null : object(data.lastSchedule)
  return {
    worker: { state: oneOf(worker.state, ['ready', 'stale', 'missing'] as const), installCommand: text(worker.installCommand) },
    system: system && { os: text(system.os), dgx: optionalText(system.dgx), kernel: text(system.kernel), architecture: text(system.architecture) },
    lastUpdateCheckAt: optionalText(data.lastUpdateCheckAt), scannedAt: optionalText(data.scannedAt),
    scanning: data.scanning === undefined ? false : flag(data.scanning),
    updates: data.updates === undefined ? [] : Array.isArray(data.updates) ? data.updates.map(update) : (() => { throw invalid() })(),
    restart: restart ? { sparkRequired: flag(restart.sparkRequired), sparkReasons: texts(restart.sparkReasons),
      kernelPending: flag(restart.kernelPending), services: texts(restart.services) }
      : { sparkRequired: false, sparkReasons: [], kernelPending: false, services: [] },
    automaticUpdates: automatic && { installed: flag(automatic.installed), enabled: flag(automatic.enabled) },
    packageDatabase: database ? { healthy: flag(database.healthy), message: optionalText(database.message) } : { healthy: true, message: null },
    operation: data.operation === null || data.operation === undefined ? null : operation(data.operation),
    history: data.history === undefined ? [] : Array.isArray(data.history) ? data.history.map(operation) : (() => { throw invalid() })(),
    rebootingAt: optionalText(data.rebootingAt),
    schedule: schedule && { runAt: text(schedule.runAt), packages: schedule.packages === null ? null : texts(schedule.packages),
      includePlatform: flag(schedule.includePlatform), restart: oneOf(schedule.restart, policies), createdAt: text(schedule.createdAt) },
    lastSchedule: last && { runAt: text(last.runAt), state: oneOf(last.state, ['started', 'missed', 'failed'] as const),
      message: text(last.message), at: text(last.at) },
    pendingRequest: flag(data.pendingRequest), modelsPaused: flag(data.modelsPaused),
  }
}

export function groupUpdates(updates: PackageUpdate[]): { everyday: PackageUpdate[]; platform: PackageUpdate[] } {
  return { everyday: updates.filter(item => !item.platform), platform: updates.filter(item => item.platform) }
}

const plural = (count: number, word: string, many = word + 's') => `${count} ${count === 1 ? word : many}`

export function summarize(state: SparkUpdates): string {
  if (state.worker.state === 'missing') return 'Lucia can’t see the Spark’s packages yet. Its update service needs a one-time setup.'
  if (state.rebootingAt) return 'The Spark is restarting. Lucia will be back in a few minutes.'
  if (state.operation) return 'Lucia is working on an update task. You can leave this page; it keeps running.'
  const { everyday, platform } = groupUpdates(state.updates)
  const security = state.updates.filter(item => item.security).length
  const parts: string[] = []
  if (state.updates.length === 0) parts.push('The Spark is up to date.')
  else {
    parts.push(`${plural(state.updates.length, 'update')} ${state.updates.length === 1 ? 'is' : 'are'} available`
      + (security ? `, including ${plural(security, 'security fix', 'security fixes')}` : '') + '.')
    if (platform.length && everyday.length)
      parts.push(`${platform.length === 1 ? '1 is a platform update and needs' : `${platform.length} are platform updates and need`} their own review.`)
    else if (platform.length) parts.push(platform.length === 1 ? 'It is a platform update and needs its own review.' : 'They are all platform updates and need their own review.')
  }
  if (state.restart.sparkRequired) parts.push('A restart is needed to finish earlier updates.')
  return parts.join(' ')
}

export function restartNeeded(selection: PackageUpdate[]): RestartClass | null {
  if (selection.some(item => item.restart === 'spark')) return 'spark'
  if (selection.some(item => item.restart === 'lucia')) return 'lucia'
  return selection.length ? 'services' : null
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  const units = ['KB', 'MB', 'GB']
  let value = bytes / 1024, unit = 0
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++ }
  return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`
}

const pad = (value: number) => String(value).padStart(2, '0')
export function toLocalInput(date: Date): string {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`
}
/** Next 2:00 AM local time that is at least an hour away. */
export function defaultWindow(now = new Date()): string {
  const next = new Date(now)
  next.setHours(2, 0, 0, 0)
  while (next.getTime() - now.getTime() < 3600000) next.setDate(next.getDate() + 1)
  return toLocalInput(next)
}
export function fromLocalInput(value: string): string | null {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(value)) return null
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? null : date.toISOString()
}

export function actionLabel(action: string): string {
  return ({ check: 'Checked for updates', install: 'Installed updates', changelog: 'Read a changelog', 'restart-services': 'Restarted services',
    'restart-spark': 'Restarted the Spark', repair: 'Repaired the package database' } as Record<string, string>)[action] ?? action
}
export function runningLabel(action: string): string {
  return ({ check: 'Checking every package source', install: 'Installing updates', 'restart-services': 'Restarting services',
    'restart-spark': 'Restarting the Spark', repair: 'Repairing the package database' } as Record<string, string>)[action] ?? 'Working'
}

const stages: Record<string, [number, number, string]> = {
  downloading: [0, 30, 'Downloading'], unpacking: [30, 65, 'Installing'], configuring: [65, 100, 'Setting up'],
}
/** Overall install percent across download, unpack and configure; null when apt gives nothing countable. */
export function progressPercent(value: PackageProgress | null): number | null {
  const stage = value && stages[value.step]
  if (!value || !stage || !value.total) return null
  return Math.round(stage[0] + (stage[1] - stage[0]) * Math.min(value.done, value.total) / value.total)
}
export function progressLabel(value: PackageProgress | null): string {
  if (!value) return 'Starting…'
  const stage = stages[value.step]
  if (stage && value.total) return `${stage[2]} ${Math.min(value.done, value.total)} of ${value.total}`
  return ({ preparing: 'Checking what these updates need', refreshing: 'Downloading the latest package lists',
    restarting: 'Restarting services that use updated files', repairing: 'Finishing interrupted installs' } as Record<string, string>)[value.step] ?? 'Working'
}
export function resultLabel(item: PackageOperation): string {
  if (item.state === 'succeeded') return actionLabel(item.action)
  if (item.state === 'interrupted') return 'The task was interrupted'
  return ({ check: 'Couldn’t check for updates', install: 'Updates weren’t installed', 'restart-services': 'Services weren’t restarted',
    'restart-spark': 'The Spark didn’t restart', repair: 'Repair didn’t finish' } as Record<string, string>)[item.action] ?? 'The task failed'
}