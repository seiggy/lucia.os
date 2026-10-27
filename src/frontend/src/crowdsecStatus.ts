export type CrowdSecState = 'absent' | 'stopped' | 'moving' | 'waiting-domain' | 'waiting-certificate' | 'unreachable' | 'needs-observability' | 'no-logs' | 'healthy'
export type AlertState = 'on' | 'off' | 'update-observability' | 'waiting-domain' | 'waiting-certificate' | 'stopped' | 'unreachable'
export type ConsoleState = 'not-enrolled' | 'sending' | 'pending' | 'accepted' | 'failed'
export interface CrowdSecStatus {
  state: CrowdSecState; message: string | null; protecting: boolean; observabilityInstalled: boolean
  stack: string | null; node: string | null; address: string | null; name: string | null; version: string | null; image: string | null
  certificateExpiresAt: string | null
  capi: { registered: boolean | null; authenticated: boolean | null; sharing: boolean | null; blocklist: boolean | null } | null
  console: { state: ConsoleState; message: string | null; at: string | null; plan: string | null; url: string }
  bouncer: { name: string; lastPull: string | null; revoked: boolean } | null
  metrics: { linesRead: number; linesParsed: number; linesUnparsed: number; appSecProcessed: number; appSecBlocked: number } | null
  harborEntries: number | null; reportedAt: string | null
  /** Custom AppSec rules the engine has loaded, from its last report. */
  customRules: number | null
  /** Custom rules the engine couldn't read and runs without, in its own words. */
  customRulesProblem: string | null
  /** Where the engine's alerts go: Observability's TLS alert intake when on, otherwise why not. */
  alerts: { state: AlertState; message: string | null; intake: string | null } | null
}
export interface CrowdSecAlert {
  id: number; scenario: string; scope: string | null; value: string | null; country: string | null; asName: string | null
  at: string | null; events: number; decisions: number; simulated: boolean
  /** waf for a request AppSec blocked (scenario is then the rule that matched), crowdsec for a scenario. */
  kind: string; path: string | null
}
export interface CrowdSecDecision { id: number; value: string; scope: string; type: string; origin: string; scenario: string | null; until: string | null }
export type RuleField = 'path' | 'query' | 'user-agent' | 'header' | 'method'
export type RuleOperator = 'equals' | 'prefix' | 'contains' | 'regex'
/** One of the owner's custom AppSec rules; a block it causes is an alert named after it. */
export interface CrowdSecRule {
  id: string; name: string; field: RuleField; header: string | null; operator: RuleOperator; value: string; reason: string; createdAt: string; createdBy: string
}

const invalid = () => new Error('Lucia returned incomplete CrowdSec data. The engine’s current state could not be confirmed.')
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw invalid()
  return value as Record<string, unknown>
}
const text = (value: unknown, limit = 512): string => { if (typeof value !== 'string' || value.length > limit) throw invalid(); return value }
const optionalText = (value: unknown, limit = 512): string | null => value === null || value === undefined ? null : text(value, limit)
const flag = (value: unknown): boolean => { if (typeof value !== 'boolean') throw invalid(); return value }
const optionalFlag = (value: unknown): boolean | null => value === null || value === undefined ? null : flag(value)
function count(value: unknown): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0) throw invalid()
  return value
}
const optionalCount = (value: unknown): number | null => value === null || value === undefined ? null : count(value)
function oneOf<T extends string>(value: unknown, allowed: readonly T[]): T {
  if (!allowed.includes(value as T)) throw invalid()
  return value as T
}
function list(value: unknown, key: string, limit: number): unknown[] {
  const items = object(value)[key]
  if (!Array.isArray(items) || items.length > limit) throw invalid()
  return items
}

export function parseCrowdSecStatus(value: unknown): CrowdSecStatus {
  const data = object(value)
  const capi = data.capi === null || data.capi === undefined ? null : object(data.capi)
  const consoleData = object(data.console)
  const bouncer = data.bouncer === null || data.bouncer === undefined ? null : object(data.bouncer)
  const metrics = data.metrics === null || data.metrics === undefined ? null : object(data.metrics)
  const alerts = data.alerts === null || data.alerts === undefined ? null : object(data.alerts)
  const url = text(consoleData.url)
  if (url !== 'https://app.crowdsec.net') throw invalid()
  return {
    state: oneOf(data.state, ['absent', 'stopped', 'moving', 'waiting-domain', 'waiting-certificate', 'unreachable', 'needs-observability', 'no-logs', 'healthy'] as const),
    message: optionalText(data.message), protecting: flag(data.protecting), observabilityInstalled: flag(data.observabilityInstalled),
    stack: optionalText(data.stack, 64), node: optionalText(data.node, 64), address: optionalText(data.address, 64), name: optionalText(data.name, 253),
    version: optionalText(data.version, 64), image: optionalText(data.image, 256), certificateExpiresAt: optionalText(data.certificateExpiresAt, 64),
    capi: capi && { registered: optionalFlag(capi.registered), authenticated: optionalFlag(capi.authenticated), sharing: optionalFlag(capi.sharing), blocklist: optionalFlag(capi.blocklist) },
    console: {
      state: oneOf(consoleData.state, ['not-enrolled', 'sending', 'pending', 'accepted', 'failed'] as const),
      message: optionalText(consoleData.message), at: optionalText(consoleData.at, 64), plan: optionalText(consoleData.plan, 64), url,
    },
    bouncer: bouncer && { name: text(bouncer.name, 128), lastPull: optionalText(bouncer.lastPull, 64), revoked: flag(bouncer.revoked) },
    metrics: metrics && {
      linesRead: count(metrics.linesRead), linesParsed: count(metrics.linesParsed), linesUnparsed: count(metrics.linesUnparsed),
      appSecProcessed: count(metrics.appSecProcessed), appSecBlocked: count(metrics.appSecBlocked),
    },
    harborEntries: optionalCount(data.harborEntries), reportedAt: optionalText(data.reportedAt, 64), customRules: optionalCount(data.customRules), customRulesProblem: optionalText(data.customRulesProblem, 400),
    alerts: alerts && {
      state: oneOf(alerts.state, ['on', 'off', 'update-observability', 'waiting-domain', 'waiting-certificate', 'stopped', 'unreachable'] as const),
      message: optionalText(alerts.message), intake: optionalText(alerts.intake, 253),
    },
  }
}

export function parseAlerts(value: unknown): CrowdSecAlert[] {
  return list(value, 'alerts', 200).map(item => {
    const data = object(item)
    return {
      id: count(data.id), scenario: text(data.scenario, 256), scope: optionalText(data.scope, 32), value: optionalText(data.value, 128),
      country: optionalText(data.country, 8), asName: optionalText(data.asName, 256), at: optionalText(data.at, 64),
      events: count(data.events), decisions: count(data.decisions), simulated: flag(data.simulated),
      kind: text(data.kind, 32), path: optionalText(data.path, 256),
    }
  })
}

export function parseDecisions(value: unknown): CrowdSecDecision[] {
  return list(value, 'decisions', 2000).map(item => {
    const data = object(item)
    return {
      id: count(data.id), value: text(data.value, 128), scope: text(data.scope, 32), type: text(data.type, 32), origin: text(data.origin, 64),
      scenario: optionalText(data.scenario, 256), until: optionalText(data.until, 64),
    }
  })
}

export function parseRules(value: unknown): { rules: CrowdSecRule[]; max: number } {
  const data = object(value)
  return {
    max: count(data.max),
    rules: list(value, 'rules', 50).map(item => {
      const rule = object(item)
      const id = text(rule.id, 8)
      if (!/^[0-9a-f]{8}$/.test(id) || rule.name !== `lucia/custom-${id}`) throw invalid()
      return {
        id, name: rule.name, field: oneOf(rule.field, ['path', 'query', 'user-agent', 'header', 'method'] as const), header: optionalText(rule.header, 64),
        operator: oneOf(rule.operator, ['equals', 'prefix', 'contains', 'regex'] as const), value: text(rule.value, 200), reason: text(rule.reason, 200),
        createdAt: text(rule.createdAt, 64), createdBy: text(rule.createdBy, 120),
      }
    }),
  }
}

const ruleParts: Record<RuleField, string> = { path: 'Path', query: 'A query value', 'user-agent': 'User agent', header: 'Header', method: 'Method' }
const ruleVerbs: Record<RuleOperator, string> = { equals: 'is', prefix: 'starts with', contains: 'contains', regex: 'matches' }
/** "Path starts with /wp-admin", "X-Api-Version header contains beta". */
export function describeRule(rule: CrowdSecRule): string {
  const part = rule.field === 'header' ? `The ${rule.header ?? 'named'} header` : ruleParts[rule.field]
  return `${part} ${ruleVerbs[rule.operator]} ${rule.value}`
}

/** Where the engine's alerts go, in words, for the page. */
export function alertsSummary(status: CrowdSecStatus): string {
  const alerts = status.alerts
  if (!alerts) return 'Not reported yet'
  if (alerts.state === 'on') return `To Observability over TLS${alerts.intake ? ` at ${alerts.intake}` : ''}, for Grafana`
  return alerts.message ?? 'Kept in CrowdSec only for now'
}

/** Who placed a ban, in words: CrowdSec itself, the owner on this page, Lucia's assistant, or another CrowdSec tool. */
export function banOrigin(origin: string): string {
  switch (origin) {
    case 'crowdsec': return 'CrowdSec'
    case 'lucia': return 'You, from Lucia'
    case 'lucia-assistant': return 'Lucia’s assistant'
    case 'cscli': return 'cscli'
    case 'console': return 'The CrowdSec Console'
    default: return origin
  }
}

/** The same rule as the host's: one plain token, so a key can never add lines to the app's environment. */
export const validEnrollKey = (key: string): boolean => /^[A-Za-z0-9_-]{8,128}$/.test(key)

/** crowdsecurity/http-probing becomes "http-probing"; the hub author stays in the tooltip. Lucia's own names read as words. */
export function scenarioName(scenario: string): string {
  if (scenario === 'lucia/manual-ban') return 'banned by hand'
  const custom = /^lucia\/custom-([0-9a-f]{8})$/.exec(scenario)
  if (custom) return `custom rule ${custom[1]}`
  const name = scenario.includes('/') ? scenario.slice(scenario.lastIndexOf('/') + 1) : scenario
  return name.length > 0 ? name : scenario
}

export const isAppSec = (scenario: string): boolean => /appsec|vpatch/i.test(scenario)

export function remaining(until: string | null, now: number): string {
  if (!until) return 'until lifted'
  const minutes = Math.max(0, Math.round((Date.parse(until) - now) / 60000))
  if (minutes < 60) return `${minutes} min left`
  const hours = Math.floor(minutes / 60)
  if (hours < 48) return `${hours} h ${minutes % 60} min left`
  return `${Math.round(hours / 24)} days left`
}

export function summarize(status: CrowdSecStatus): { tone: 'good' | 'warning' | 'neutral'; title: string; detail: string } {
  switch (status.state) {
    case 'absent':
      return {
        tone: 'neutral', title: 'CrowdSec isn’t installed',
        detail: status.observabilityInstalled
          ? 'Install CrowdSec from Apps to ban attackers and block known exploits at the gateway.'
          : 'CrowdSec reads the gateway’s access log through Observability’s collector, so install Observability first, then CrowdSec.',
      }
    case 'healthy':
      return { tone: 'good', title: 'Protecting the gateway', detail: `CrowdSec runs on ${status.node ?? 'its server'} and the gateway checks every request with it.` }
    case 'stopped':
    case 'moving':
      return { tone: 'warning', title: status.state === 'stopped' ? 'CrowdSec is stopped' : 'CrowdSec is moving', detail: status.message ?? 'The gateway lets everything through until it’s back.' }
    case 'needs-observability':
      return { tone: 'warning', title: 'Update Observability', detail: status.message ?? 'CrowdSec isn’t getting the gateway’s access log, so only AppSec and the community blocklist protect the gateway.' }
    case 'no-logs':
      return { tone: 'warning', title: 'No access log arriving', detail: status.message ?? 'CrowdSec hasn’t received the gateway’s access log for a while. Check that Observability is running.' }
    case 'waiting-domain':
      return { tone: 'warning', title: 'Waiting for your domain', detail: status.message ?? 'CrowdSec needs your lab’s domain: the gateway reaches it by name. Set up the domain on the Domains page.' }
    case 'waiting-certificate':
      return { tone: 'warning', title: 'Waiting for a certificate', detail: status.message ?? 'Lucia is getting this engine’s certificate from its authority.' }
    default:
      return { tone: 'warning', title: 'CrowdSec isn’t answering', detail: status.message ?? 'The gateway lets everything through until it answers again.' }
  }
}

export function consoleSummary(status: CrowdSecStatus): string {
  switch (status.console.state) {
    case 'accepted': return `Enrolled in the CrowdSec Console${status.console.plan ? ` (${status.console.plan} plan)` : ''}.`
    case 'pending': return 'Enrollment sent. Accept this engine in the CrowdSec Console to finish.'
    case 'sending': return 'Lucia gave CrowdSec your key. The engine enrolls within a minute.'
    case 'failed': return `The Console refused the key${status.console.message ? `: ${status.console.message}` : '.'}`
    default: return 'Not enrolled. The engine works without the Console; enrolling adds its web dashboard and alert history.'
  }
}
