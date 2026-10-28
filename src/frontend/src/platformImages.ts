export type OfferKind = 'rebuild' | 'patch' | 'minor' | 'release-line' | 'major'
export type Policy = 'patch' | 'authentik' | 'minor' | 'plugin'
export interface PlatformOffer {
  tag: string; digest: string; kind: OfferKind; applicable: boolean; notes: string | null; upgradeNotes: string | null; firstSeenAt: string
}
export interface PlatformImage {
  service: string; name: string; policy: Policy; image: string; tag: string; digest: string; override: boolean; security: boolean
  disruption: string; offers: PlatformOffer[]; problem: string | null
}
export type WorkerStep = 'verifying' | 'preflight' | 'backing-up' | 'deploying' | 'health-checking' | 'rolling-back'
export interface PlatformStatus {
  generation: number | null; state: 'idle' | 'working' | 'succeeded' | 'failed' | 'busy'; step: string | null; message: string | null
  startedAt: string | null; finishedAt: string | null; rolledBack: boolean; backup: string | null
}
export interface PlatformImages {
  worker: { state: 'ready' | 'stale' | 'missing'; installCommand: string }
  recorded: boolean; recordedAt: string | null; problem: string | null; checkedAt: string | null
  images: PlatformImage[]
  plugin: { name: string; version: string; latest: string | null; notes: string | null }
  status: PlatformStatus | null; pending: boolean
  securityWaiting: { service: string; name: string; tag: string; days: number }[]
  qwenRunning: boolean
}

const invalid = () => new Error('Lucia returned incomplete platform image data. What the Spark runs could not be confirmed.')
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw invalid()
  return value as Record<string, unknown>
}
const text = (value: unknown): string => { if (typeof value !== 'string') throw invalid(); return value }
const optionalText = (value: unknown): string | null => value === null || value === undefined ? null : text(value)
const flag = (value: unknown): boolean => { if (typeof value !== 'boolean') throw invalid(); return value }
const list = (value: unknown): unknown[] => { if (!Array.isArray(value)) throw invalid(); return value }
function oneOf<T extends string>(value: unknown, allowed: readonly T[]): T {
  if (!allowed.includes(value as T)) throw invalid()
  return value as T
}
/** Release notes open in a new tab, so only plain https links are shown. */
const link = (value: unknown): string | null => {
  const url = optionalText(value)
  return url && /^https:\/\/[^\s"'<>]+$/.test(url) ? url : null
}

function offer(value: unknown): PlatformOffer {
  const data = object(value)
  return {
    tag: text(data.tag), digest: text(data.digest), kind: oneOf(data.kind, ['rebuild', 'patch', 'minor', 'release-line', 'major'] as const),
    applicable: flag(data.applicable), notes: link(data.notes), upgradeNotes: link(data.upgradeNotes), firstSeenAt: text(data.firstSeenAt),
  }
}
function image(value: unknown): PlatformImage {
  const data = object(value)
  return {
    service: text(data.service), name: text(data.name), policy: oneOf(data.policy, ['patch', 'authentik', 'minor', 'plugin'] as const),
    image: text(data.image), tag: text(data.tag), digest: text(data.digest), override: flag(data.override), security: flag(data.security),
    disruption: text(data.disruption), offers: list(data.offers).map(offer), problem: optionalText(data.problem),
  }
}

export function parsePlatformImages(value: unknown): PlatformImages {
  const data = object(value)
  const worker = object(data.worker)
  const plugin = object(data.plugin)
  const status = data.status === null || data.status === undefined ? null : object(data.status)
  return {
    worker: { state: oneOf(worker.state, ['ready', 'stale', 'missing'] as const), installCommand: text(worker.installCommand) },
    recorded: flag(data.recorded), recordedAt: optionalText(data.recordedAt), problem: optionalText(data.problem), checkedAt: optionalText(data.checkedAt),
    images: list(data.images).map(image),
    plugin: { name: text(plugin.name), version: text(plugin.version), latest: optionalText(plugin.latest), notes: link(plugin.notes) },
    status: status && {
      generation: typeof status.generation === 'number' ? status.generation : null,
      state: oneOf(status.state, ['idle', 'working', 'succeeded', 'failed', 'busy'] as const), step: optionalText(status.step),
      message: optionalText(status.message), startedAt: optionalText(status.startedAt), finishedAt: optionalText(status.finishedAt),
      rolledBack: status.rolledBack === undefined || status.rolledBack === null ? false : flag(status.rolledBack), backup: optionalText(status.backup),
    },
    pending: flag(data.pending),
    securityWaiting: list(data.securityWaiting).map(item => {
      const entry = object(item)
      if (typeof entry.days !== 'number' || !Number.isInteger(entry.days) || entry.days < 0) throw invalid()
      return { service: text(entry.service), name: text(entry.name), tag: text(entry.tag), days: entry.days }
    }),
    qwenRunning: data.qwenRunning === true,
  }
}

/** Services with a patch or a rebuild of their tag: the low-risk set "Update all patches" takes. */
export function patchServices(images: PlatformImage[]): string[] {
  return images.filter(item => item.offers.some(entry => entry.applicable && (entry.kind === 'patch' || entry.kind === 'rebuild'))).map(item => item.service)
}

const traffic = ['authentik', 'gateway', 'database', 'directory']
/** What restarts, as the server says it (PlatformImageUpdates.Disruption). */
export function disruption(services: string[]): string {
  if (services.some(service => traffic.includes(service)))
    return 'Every web route through the gateway restarts: Lucia, sign-in and your apps’ web addresses are unreachable for about 10 to 15 seconds.'
  if (services.includes('ca')) return 'Certificates can’t be issued or renewed for a few seconds. Web traffic isn’t touched.'
  return 'Only telemetry restarts, so a few seconds of Spark metrics are missing. Web traffic isn’t touched.'
}

export function kindLabel(kind: OfferKind): string {
  return ({ rebuild: 'Rebuild with OS fixes', patch: 'Patch', minor: 'Minor update', 'release-line': 'Next release line', major: 'Major version' } as const)[kind]
}
export function policyLabel(policy: Policy): string {
  return ({ patch: 'Patches only', authentik: 'Patches, then one release line at a time', minor: 'Same major version', plugin: 'Shown only' } as const)[policy]
}
export function stepLabel(step: string | null): string {
  return ({ verifying: 'Checking each image with its registry', preflight: 'Checking the Spark is ready', 'backing-up': 'Backing up Authentik’s database',
    deploying: 'Pulling images and restarting services', 'health-checking': 'Waiting for every service to come back', 'rolling-back': 'Putting the previous images back',
  } as Record<string, string>)[step ?? ''] ?? 'Starting'
}
export function waiting(firstSeenAt: string, now = Date.now()): number {
  return Math.max(0, Math.floor((now - Date.parse(firstSeenAt)) / 86400000))
}
/** The dashboard's line when a security-relevant patch has waited more than a week; null otherwise. */
export function securityBadge(state: Pick<PlatformImages, 'securityWaiting'>): string | null {
  const oldest = [...state.securityWaiting].sort((a, b) => b.days - a.days)[0]
  if (!oldest) return null
  const others = state.securityWaiting.length - 1
  return `${oldest.name} ${oldest.tag} has waited ${oldest.days} days${others > 0 ? ` (and ${others} more)` : ''}`
}
