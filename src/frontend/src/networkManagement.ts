export interface AdGuardStatus {
  configured: boolean; baseUrl: string | null; username: string | null
  allowInsecureHttp: boolean; version: string | null; lastVerifiedAt: string | null
}
export interface ServiceUrls { lucia: string; authentik: string; spark: string }
export interface DomainZone { id: string; name: string; status: string; nameServers: string[] }
export interface DomainPlan {
  id: string; reviewHash: string; expiresAt: string; termsUrl: string; ingressAddress: string; email: string
  naming: { domain: string; namespace: string; serviceUrls: ServiceUrls; certificateNames: string[]; localHostnames: string[] }
  rewrites: { domain: string; answer: string; alreadyPresent: boolean }[]; blockers: string[]; warnings: string[]
}
export interface DomainDiagnosis {
  code: string; title: string; summary: string; evidence: string[]; nextSteps: string[]; canRetry: boolean
}
export interface DomainSupport {
  state: 'Queued' | 'Running' | 'Complete' | 'Unavailable'
  model: string | null; explanation: string | null; error: string | null; updatedAt: string
}
export interface DomainJob {
  id: string; state: string; phase: string; message: string; serviceUrls: ServiceUrls
  events: { at: string; phase: string; message: string }[]
  renewalError: string | null; nextRenewalAt: string | null
  recoveryRequired: boolean; pendingRewrites: string[]
  certificate: { notAfter: string; dnsNames: string[]; certificateSha256: string } | null
  diagnosis: DomainDiagnosis | null; support: DomainSupport | null
  renewalCheckedAt: string | null; renewalOutcome: 'Renewed' | 'NotDue' | 'Failed' | null
  /** Public access: on, the change waiting for the worker (null when none), and why the last change failed. */
  public: boolean; publicRequested: boolean | null; publicError: string | null; zone: string | null
}
export interface DomainState {
  adguardConfigured: boolean; cloudflareConfigured: boolean; workerReady: boolean; configured: boolean
  defaults: { ingressAddress: string | null; propagationSeconds: number }; plan: DomainPlan | null; job: DomainJob | null
  cloudflare: { configured: boolean; accountId: string | null; expiresAt: string | null }
}

export function domainFailureSummary(job: DomainJob): string {
  return job.diagnosis?.summary.trim() || 'Lucia could not capture the specific error. This does not mean your DNS credentials are wrong.'
}

const invalid = () => new Error('Lucia returned incomplete configuration data. The latest state could not be confirmed; previously submitted work may still be running.')
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw invalid()
  return value as Record<string, unknown>
}
function text(value: unknown): string {
  if (typeof value !== 'string' || value.length > 4096) throw invalid()
  return value
}
function boolean(value: unknown): boolean {
  if (typeof value !== 'boolean') throw invalid()
  return value
}
function optional<T>(value: unknown, parse: (item: unknown) => T): T | null {
  return value === null ? null : parse(value)
}
function date(value: unknown): string {
  const result = text(value)
  if (!Number.isFinite(Date.parse(result))) throw invalid()
  return result
}
function id(value: unknown): string {
  const result = text(value)
  if (!/^[a-f\d]{8}-(?:[a-f\d]{4}-){3}[a-f\d]{12}$/i.test(result)) throw invalid()
  return result
}
function strings(value: unknown): string[] {
  if (!Array.isArray(value) || value.length > 100) throw invalid()
  return value.map(text)
}
function diagnosis(value: unknown): DomainDiagnosis {
  const x = object(value)
  return { code: text(x.code), title: text(x.title), summary: text(x.summary),
    evidence: strings(x.evidence), nextSteps: strings(x.nextSteps), canRetry: boolean(x.canRetry) }
}
function support(value: unknown): DomainSupport {
  const x = object(value)
  const state = text(x.state)
  if (state !== 'Queued' && state !== 'Running' && state !== 'Complete' && state !== 'Unavailable') throw invalid()
  return { state, model: optional(x.model, text), error: optional(x.error, text), updatedAt: date(x.updatedAt),
    explanation: optional(x.explanation, value => {
      if (typeof value !== 'string' || value.length > 16384) throw invalid()
      return value
    }) }
}
function urls(value: unknown): ServiceUrls {
  const x = object(value)
  function origin(input: unknown): string {
    const result = text(input)
    const url = new URL(result)
    if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash || url.port) throw invalid()
    return result
  }
  return { lucia: origin(x.lucia), authentik: origin(x.authentik), spark: origin(x.spark) }
}
export function parseAdGuardStatus(value: unknown): AdGuardStatus {
  const x = object(value)
  return { configured: boolean(x.configured), baseUrl: optional(x.baseUrl, text), username: optional(x.username, text),
    allowInsecureHttp: boolean(x.allowInsecureHttp), version: optional(x.version, text), lastVerifiedAt: optional(x.lastVerifiedAt, date) }
}
export interface AdGuardCertificate {
  enabled: boolean; name: string | null; notAfter: string | null; checkedAt: string | null; pushedAt: string | null; error: string | null
  coveredNames: string[]
}
export function parseAdGuardCertificate(value: unknown): AdGuardCertificate {
  const x = object(value)
  return { enabled: boolean(x.enabled), name: optional(x.name, text), notAfter: optional(x.notAfter, date),
    checkedAt: optional(x.checkedAt, date), pushedAt: optional(x.pushedAt, date), error: optional(x.error, text),
    coveredNames: optional(x.coveredNames, strings) ?? [] }
}
export interface AdGuardInstance {
  name: string; node: string; address: string | null; hostName: string | null; primary: boolean; healthy: boolean; setup: boolean
  healthyAt: string | null; syncedAt: string | null; error: string | null
}
export interface AdGuardFleet { instances: AdGuardInstance[]; problem: string | null; promotedAt: string | null; promotedFrom: string | null }
export function parseAdGuardFleet(value: unknown): AdGuardFleet {
  const x = object(value)
  if (!Array.isArray(x.instances) || x.instances.length > 100) throw invalid()
  return { problem: optional(x.problem, text), promotedAt: optional(x.promotedAt, date), promotedFrom: optional(x.promotedFrom, text),
    instances: x.instances.map(item => {
      const i = object(item)
      return { name: text(i.name), node: text(i.node), address: optional(i.address, text), hostName: optional(i.hostName, text),
        primary: boolean(i.primary), healthy: boolean(i.healthy), setup: boolean(i.setup), healthyAt: optional(i.healthyAt, date),
        syncedAt: optional(i.syncedAt, date), error: optional(i.error, text) }
    }) }
}
export interface UniFiStatus {
  configured: boolean; baseUrl: string | null; site: string | null; certificateSha256: string | null
  networkVersion: string | null; reserveNodeAddresses: boolean; lastVerifiedAt: string | null
}
export type DhcpState = 'Reserved' | 'Reserving' | 'ReservedElsewhere' | 'AddressTaken' | 'NotSeen' | 'Failed'
export interface DhcpReservations {
  enabled: boolean; checkedAt: string | null; error: string | null
  nodes: { hostname: string; address: string; mac: string | null; state: DhcpState; host: boolean }[]
}
export function parseUniFiStatus(value: unknown): UniFiStatus {
  const x = object(value)
  return { configured: boolean(x.configured), baseUrl: optional(x.baseUrl, text), site: optional(x.site, text),
    certificateSha256: optional(x.certificateSha256, value => { const pin = text(value); if (!/^[a-f\d]{64}$/.test(pin)) throw invalid(); return pin }),
    networkVersion: optional(x.networkVersion, text), reserveNodeAddresses: boolean(x.reserveNodeAddresses), lastVerifiedAt: optional(x.lastVerifiedAt, date) }
}
const dhcpStates: readonly DhcpState[] = ['Reserved', 'Reserving', 'ReservedElsewhere', 'AddressTaken', 'NotSeen', 'Failed']
export function parseDhcpReservations(value: unknown): DhcpReservations {
  const x = object(value)
  if (!Array.isArray(x.nodes) || x.nodes.length > 500) throw invalid()
  return { enabled: boolean(x.enabled), checkedAt: optional(x.checkedAt, date), error: optional(x.error, text),
    nodes: x.nodes.map(item => {
      const n = object(item)
      const state = text(n.state) as DhcpState
      if (!dhcpStates.includes(state)) throw invalid()
      return { hostname: text(n.hostname), address: text(n.address), mac: optional(n.mac, text), state, host: boolean(n.host) }
    }) }
}
export function parseDomainPlan(value: unknown): DomainPlan {
  const x = object(value)
  const naming = object(x.naming)
  const hash = text(x.reviewHash)
  if (!/^[a-f\d]{64}$/.test(hash) || !Array.isArray(x.rewrites) || x.rewrites.length > 20) throw invalid()
  const terms = new URL(text(x.termsUrl))
  if (terms.protocol !== 'https:' || terms.hostname !== 'letsencrypt.org' || terms.username || terms.password || terms.port) throw invalid()
  return {
    id: id(x.id), reviewHash: hash, expiresAt: date(x.expiresAt), termsUrl: terms.href, ingressAddress: text(x.ingressAddress), email: text(x.email),
    naming: { domain: text(naming.domain), namespace: text(naming.namespace), serviceUrls: urls(naming.serviceUrls),
      certificateNames: strings(naming.certificateNames), localHostnames: strings(naming.localHostnames) },
    rewrites: x.rewrites.map(item => { const r = object(item); return { domain: text(r.domain), answer: text(r.answer), alreadyPresent: boolean(r.alreadyPresent) } }),
    blockers: strings(x.blockers), warnings: strings(x.warnings),
  }
}
export function parseDomainState(value: unknown): DomainState {
  const x = object(value)
  const defaults = object(x.defaults)
  const cloudflare = object(x.cloudflare)
  if (typeof defaults.propagationSeconds !== 'number' || defaults.propagationSeconds < 10 || defaults.propagationSeconds > 600) throw invalid()
  return {
    adguardConfigured: boolean(x.adguardConfigured), cloudflareConfigured: boolean(x.cloudflareConfigured),
    workerReady: boolean(x.workerReady), configured: boolean(x.configured),
    defaults: { ingressAddress: optional(defaults.ingressAddress, text), propagationSeconds: defaults.propagationSeconds },
    cloudflare: { configured: boolean(cloudflare.configured), accountId: optional(cloudflare.accountId, text), expiresAt: optional(cloudflare.expiresAt, date) },
    plan: optional(x.plan, parseDomainPlan),
    job: optional(x.job, value => {
      const j = object(value)
      const state = text(j.state)
      if (!['Queued', 'Running', 'Activating', 'Active', 'Failed'].includes(state) || !Array.isArray(j.events) || j.events.length > 100) throw invalid()
      return { id: id(j.id), state, phase: text(j.phase), message: text(j.message), serviceUrls: urls(j.serviceUrls),
        events: j.events.map(item => { const e = object(item); return { at: date(e.at), phase: text(e.phase), message: text(e.message) } }),
        renewalError: optional(j.renewalError, text), nextRenewalAt: optional(j.nextRenewalAt, date),
        recoveryRequired: boolean(j.recoveryRequired), pendingRewrites: strings(j.pendingRewrites),
        diagnosis: 'diagnosis' in j ? optional(j.diagnosis, diagnosis) : null,
        support: 'support' in j ? optional(j.support, support) : null,
        renewalCheckedAt: 'renewalCheckedAt' in j ? optional(j.renewalCheckedAt, date) : null,
        renewalOutcome: 'renewalOutcome' in j ? optional(j.renewalOutcome, value => choice(value, ['Renewed', 'NotDue', 'Failed'] as const)) : null,
        public: j.public === true, publicRequested: 'publicRequested' in j ? optional(j.publicRequested, boolean) : null,
        publicError: 'publicError' in j ? optional(j.publicError, text) : null, zone: 'zone' in j ? optional(j.zone, text) : null,
        certificate: optional(j.certificate, value => { const c = object(value); return { notAfter: date(c.notAfter), dnsNames: strings(c.dnsNames), certificateSha256: text(c.certificateSha256) } }) }
    }),
  }
}

export interface DomainOperations {
  checkedAt: string; namespace: string; ingressAddress: string; gatewayError: string | null; dnsError: string | null
  routes: { name: string; origin: string; kind: 'Proxy' | 'Redirect' | 'Public'; target: string | null; configuration: 'Published' | 'Missing' | 'Changed' | 'Unavailable' }[]
  dnsRecords: { hostname: string; expectedAddress: string; state: 'Matches' | 'Missing' | 'Disabled' | 'Conflict' | 'Unavailable'; ownership: string
    records: { domain: string; answer: string; enabled: boolean }[]; totalRecords: number }[]
}
function choice<T extends string>(value: unknown, values: readonly T[]): T {
  if (typeof value !== 'string') throw invalid()
  const result = values.find(item => item === value)
  if (result === undefined) throw invalid()
  return result
}
export function parseDomainOperations(value: unknown): DomainOperations {
  const x = object(value)
  if (!Array.isArray(x.routes) || x.routes.length > 100 || !Array.isArray(x.dnsRecords) || x.dnsRecords.length > 100) throw invalid()
  return { checkedAt: date(x.checkedAt), namespace: text(x.namespace), ingressAddress: text(x.ingressAddress),
    gatewayError: optional(x.gatewayError, text), dnsError: optional(x.dnsError, text),
    routes: x.routes.map(value => {
      const r = object(value)
      const origin = urls({ lucia: r.origin, authentik: r.origin, spark: r.origin }).lucia
      return { name: text(r.name), origin, kind: choice(r.kind, ['Proxy', 'Redirect', 'Public'] as const), target: optional(r.target, text),
        configuration: choice(r.configuration, ['Published', 'Missing', 'Changed', 'Unavailable'] as const) }
    }),
    dnsRecords: x.dnsRecords.map(value => {
      const r = object(value)
      if (!Array.isArray(r.records) || r.records.length > 20 || !Number.isSafeInteger(r.totalRecords)
        || typeof r.totalRecords !== 'number' || r.totalRecords < r.records.length) throw invalid()
      return { hostname: text(r.hostname), expectedAddress: text(r.expectedAddress), ownership: text(r.ownership),
        state: choice(r.state, ['Matches', 'Missing', 'Disabled', 'Conflict', 'Unavailable'] as const),
        totalRecords: r.totalRecords, records: r.records.map(value => {
          const record = object(value)
          return { domain: text(record.domain), answer: text(record.answer), enabled: boolean(record.enabled) }
        }) }
    }) }
}
export interface PublicIngressStatus {
  checkedAt: string; enabled: boolean; wanAddress: string | null; forward: string | null; forwardError: string | null
  records: { name: string; state: 'Published' | 'Adopted' | 'Updated' | 'Conflict' | 'Removed'; detail: string | null }[]; recordsError: string | null
}
export function parsePublicIngress(value: unknown): PublicIngressStatus | null {
  return optional(object(value).status ?? null, value => {
    const x = object(value)
    if (!Array.isArray(x.records) || x.records.length > 100) throw invalid()
    return { checkedAt: date(x.checkedAt), enabled: boolean(x.enabled), wanAddress: optional(x.wanAddress ?? null, text),
      forward: optional(x.forward ?? null, text), forwardError: optional(x.forwardError ?? null, text), recordsError: optional(x.recordsError ?? null, text),
      records: x.records.map(value => { const r = object(value); return { name: text(r.name),
        state: choice(r.state, ['Published', 'Adopted', 'Updated', 'Conflict', 'Removed'] as const), detail: optional(r.detail ?? null, text) } }) }
  })
}
/** A service outside Lucia's apps, such as a NAS page, that the gateway forwards a name to. */
export interface ExternalRoute { host: string; address: string; port: number; public: string | null }
export function parseExternalRoutes(value: unknown): ExternalRoute[] {
  const routes = object(value).routes
  if (!Array.isArray(routes) || routes.length > 32) throw invalid()
  return routes.map(value => {
    const r = object(value)
    if (typeof r.port !== 'number' || !Number.isSafeInteger(r.port)) throw invalid()
    return { host: text(r.host), address: text(r.address), port: r.port, public: optional(r.public ?? null, text) }
  })
}
export function parseDomainZones(value: unknown): DomainZone[] {
  if (!Array.isArray(value) || value.length > 1000) throw invalid()
  return value.map(item => {
    const x = object(item)
    const zoneId = text(x.id)
    if (!/^[a-f\d]{32}$/i.test(zoneId)) throw invalid()
    return { id: zoneId, name: text(x.name), status: text(x.status), nameServers: strings(x.nameServers) }
  })
}
