export type Health = 'ok' | 'warn' | 'failed' | 'stale' | 'unknown'
export type ClientGroup = 'phones' | 'computers' | 'iot' | 'entertainment' | 'infrastructure' | 'unknown'
export type TrafficMode = 'full' | 'reduced' | 'none'

export interface MapNetwork { id: string; name: string; vlan: number | null; subnet: string | null; purpose: string | null; zoneId: string | null }
export interface MapZone { id: string; name: string; networkIds: string[] }
export interface MapPort { index: number; name: string; up: boolean; speedMbps: number | null; poe: boolean }
export interface MapDevice { id: string; kind: 'gateway' | 'switch' | 'ap'; name: string; model: string | null; address: string | null; mac: string
  health: Health; parentId: string | null; clientCount: number; updateAvailable: boolean; ports: MapPort[] }
export interface MapHost { id: string; kind: 'spark' | 'node'; name: string; address: string | null; networkId: string | null; parentId: string | null
  health: Health; lastSeenAt: string | null; cpuPercent: number | null; memoryPercent: number | null; updatesAvailable: number; gpu: string | null; href: string; category: string; categoryChosen: boolean }
// Building categories colour the home side of the map; Lucia guesses one per object and the owner can pick another or type their own.
export const deviceCategories = ['phone', 'watch', 'tablet', 'computer', 'tv', 'speaker', 'console', 'camera', 'printer', 'light', 'sensor', 'hub', 'appliance', 'network', 'server', 'storage'] as const
export const appCategories = ['media', 'ai', 'automation', 'monitoring', 'dev', 'database'] as const
export const buildingCategories: string[] = [...deviceCategories, ...appCategories, 'other']
export const buildingCategoryLabels: Record<string, string> = {
  phone: 'Phones', watch: 'Watches', tablet: 'Tablets', computer: 'Computers', tv: 'TVs', speaker: 'Speakers', console: 'Consoles', camera: 'Cameras', printer: 'Printers',
  light: 'Lights and plugs', sensor: 'Sensors', hub: 'Hubs', appliance: 'Appliances', network: 'Network gear', server: 'Servers', storage: 'Storage',
  media: 'Media', ai: 'AI', automation: 'Automation', monitoring: 'Monitoring', dev: 'Dev tools', database: 'Databases', other: 'Other',
}
const buildingPalette: Record<string, number> = {
  phone: 0x39d5ff, watch: 0x7fe8ff, tablet: 0x4f9dff, computer: 0x5b7cff, tv: 0xff4fa3, speaker: 0xff7ad9, console: 0xe36bff, camera: 0x2ef2c2, printer: 0xffc48a,
  light: 0xfff27a, sensor: 0xd4ff3a, hub: 0x8affc1, appliance: 0xb8f5a0, network: 0x00f0ff, server: 0x9fb4ff, storage: 0xb08cff,
  media: 0xff5fb0, ai: 0xc59bff, automation: 0x9dff6a, monitoring: 0x52ffe0, dev: 0x6fa8ff, database: 0xe0b3ff, other: 0xf2f4ff,
}
export const buildingCategoryLabel = (category: string) => buildingCategoryLabels[category] ?? category
// Owner-typed categories get a steady hue of their own, kept clear of the reds and ambers that mean trouble.
export const buildingCategoryColor = (category: string) => {
  if (buildingPalette[category] !== undefined) return buildingPalette[category]
  let s = 2166136261
  for (const ch of category.toLowerCase()) s = Math.imul(s ^ ch.charCodeAt(0), 16777619)
  const h = 60 + (s >>> 0) % 280, a = 0.85 * Math.min(0.64, 0.36)
  const f = (n: number) => { const k = (n + h / 30) % 12; return Math.round(255 * (0.64 - a * Math.max(-1, Math.min(k - 3, 9 - k, 1)))) }
  return f(0) << 16 | f(8) << 8 | f(4)
}
export const buildingCategoryValue = (name: string) => {
  const tidy = name.trim().replace(/\s+/g, ' '), lower = tidy.toLowerCase()
  return buildingCategories.find(item => item === lower || buildingCategoryLabels[item].toLowerCase() === lower) ?? tidy
}
export const buildingCategoriesOf = (categories: Iterable<string>) => {
  const present = new Set(categories), own = [...present].filter(item => !buildingCategories.includes(item)).sort((a, b) => a.localeCompare(b))
  return [...buildingCategories.filter(item => item !== 'other' && present.has(item)), ...own, ...(present.has('other') ? ['other'] : [])]
}
export const services = ['web', 'dns', 'streaming', 'gaming', 'remote', 'vpn', 'mail', 'files', 'iot', 'time', 'other'] as const
export type Service = typeof services[number]
export const serviceLabels: Record<Service, string> = {
  web: 'Web', dns: 'DNS', streaming: 'Streaming', gaming: 'Gaming', remote: 'Remote access', vpn: 'VPN', mail: 'Mail', files: 'File sharing', iot: 'Smart home', time: 'Time', other: 'Other',
}
// Grey belongs to offline clients alone, so every traffic type and category glows in a colour of its own.
export const serviceColors: Record<Service, number> = {
  web: 0x39d5ff, dns: 0xa78bfa, streaming: 0xff4fa3, gaming: 0xff9a3c, remote: 0x2ef2c2, vpn: 0x5b7cff, mail: 0xe8d28a, files: 0xffe066, iot: 0xd4ff3a, time: 0x5cff8f, other: 0xf2f4ff,
}
// Lucia's districts in their city order; the owner can name more, which stand after these and before Elsewhere.
export const destCategories = ['streaming', 'gaming', 'social', 'cloud', 'cdn', 'comms', 'updates', 'other'] as const
export type DestCategory = string
export const categoryLabels: Record<string, string> = {
  streaming: 'Streaming', gaming: 'Gaming', social: 'Social', cloud: 'Cloud', cdn: 'Content networks', comms: 'Chat and calls', updates: 'Updates', other: 'Elsewhere',
}
export const categoryLabel = (category: DestCategory) => categoryLabels[category] ?? category
// A typed district name as the server keeps it: one of Lucia's by id or label, else the name itself.
export const districtValue = (name: string) => {
  const tidy = name.trim().replace(/\s+/g, ' '), lower = tidy.toLowerCase()
  return destCategories.find(item => item === lower || categoryLabels[item].toLowerCase() === lower) ?? tidy
}
export const districtsOf = (categories: Iterable<DestCategory>) => {
  const present = new Set(categories), own = [...present].filter(item => !(destCategories as readonly string[]).includes(item)).sort((a, b) => a.localeCompare(b))
  return [...destCategories.filter(item => item !== 'other' && present.has(item)), ...own, ...(present.has('other') ? ['other'] : [])]
}
export type Mix = Partial<Record<Service, number>>
export interface Listen { port: number; protocol: 'tcp' | 'udp'; service: Service; bps?: number }
export interface Destination { id: string; name: string; category: DestCategory; chosen: boolean; org: string | null; asn: string | null; domains: string[]; bps: number; mix: Mix }
// A destination the map has seen recently; the Internet city keeps it standing for a while after its traffic stops.
export interface CitySite { id: string; name: string; category: DestCategory; chosen: boolean; service: Service; peak: number; org: string | null; domains: string[] }
export interface MapContainer { id: string; name: string; image: string; state: string; health: Health
  networks: { name: string; address: string | null }[]; mounts: { source: string; destination: string; storageId: string | null }[]; ports: Listen[] }
export interface MapApp { id: string; name: string; hostId: string; desired: 'Running' | 'Stopped'; health: Health; address: string | null; networkId: string | null
  href: string; updateCount: number; containers: MapContainer[]; category: string; categoryChosen: boolean }
export interface MapStorage { id: string; name: string; kind: 'nas'; address: string | null; health: Health; shares: string[]; mounts: { hostId: string; share: string; health: Health }[]; category: string; categoryChosen: boolean }
export interface MapClient { mac: string; name: string; address: string | null; online: boolean; group: ClientGroup; overridden: boolean; parentId: string | null; vendor: string | null; lastSeenAt: string | null
  category: string; categoryChosen: boolean }
export interface MapClientGroup { id: string; networkId: string; group: ClientGroup; online: number; total: number; members: MapClient[] }
export interface LabMap {
  generatedAt: string
  unifi: { state: 'connected' | 'not-connected' | 'error'; message: string | null; site: string | null }
  traffic: TrafficMode
  sources: { snmp: boolean; netflow: boolean; dockerStats: boolean }
  gateway: MapDevice | null
  wan: { id: 'wan'; name: string; address: string | null; isp: string | null; health: Health }
  networks: MapNetwork[]
  zones: MapZone[]
  devices: MapDevice[]
  hosts: MapHost[]
  apps: MapApp[]
  storage: MapStorage[]
  clientGroups: MapClientGroup[]
}
export interface Rate { rxBps: number; txBps: number }
export interface Flow { from: string; to: string; label: string | null; bps: number; service: Service }
export interface LabMapLive {
  at: string
  traffic: TrafficMode
  rates: Record<string, Rate>
  load: Record<string, { cpuPercent: number | null; memoryPercent: number | null }>
  health: Record<string, Health>
  flows: Flow[]
  mix: Record<string, Mix>
  listening: Record<string, Listen[]>
  destinations: Destination[]
}

type Json = Record<string, unknown>
const healths: Health[] = ['ok', 'warn', 'failed', 'stale', 'unknown']
export const clientGroups: ClientGroup[] = ['phones', 'computers', 'iot', 'entertainment', 'infrastructure', 'unknown']
const obj = (value: unknown): Json => value && typeof value === 'object' && !Array.isArray(value) ? value as Json : {}
const text = (value: unknown) => typeof value === 'string' && value.trim() ? value : null
const str = (value: unknown, fallback = '') => typeof value === 'string' ? value : fallback
const num = (value: unknown) => typeof value === 'number' && Number.isFinite(value) ? value : null
const bool = (value: unknown) => value === true
const health = (value: unknown): Health => healths.includes(value as Health) ? value as Health : 'unknown'
const group = (value: unknown): ClientGroup => clientGroups.includes(value as ClientGroup) ? value as ClientGroup : 'unknown'
const traffic = (value: unknown): TrafficMode => value === 'full' || value === 'reduced' ? value : 'none'
const category = (item: Json, fallback: string) => ({ category: text(item.category)?.trim().slice(0, 32) || fallback, categoryChosen: bool(item.categoryChosen) })
function list<T>(value: unknown, map: (item: Json) => T | null): T[] {
  return Array.isArray(value) ? value.map(item => map(obj(item))).filter((item): item is T => item !== null) : []
}
const strings = (value: unknown) => Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : []
const service = (value: unknown): Service => services.includes(value as Service) ? value as Service : 'other'
const listen = (item: Json): Listen | null => {
  const port = num(item.port)
  return port !== null && port > 0 ? { port, protocol: item.protocol === 'udp' ? 'udp' : 'tcp', service: service(item.service), ...(num(item.bps) !== null ? { bps: Math.max(0, num(item.bps)!) } : {}) } : null
}
const mix = (value: unknown): Mix => {
  const out: Mix = {}
  for (const [key, bps] of Object.entries(obj(value))) { const amount = num(bps); if (amount && amount > 0) out[service(key)] = (out[service(key)] ?? 0) + amount }
  return out
}

function device(item: Json): MapDevice | null {
  const id = text(item.id)
  if (!id) return null
  const kind = item.kind === 'gateway' || item.kind === 'ap' ? item.kind : 'switch'
  return { id, kind, name: str(item.name, id), model: text(item.model), address: text(item.address), mac: str(item.mac), health: health(item.health),
    parentId: text(item.parentId), clientCount: num(item.clientCount) ?? 0, updateAvailable: bool(item.updateAvailable),
    ports: list(item.ports, port => ({ index: num(port.index) ?? 0, name: str(port.name), up: bool(port.up), speedMbps: num(port.speedMbps), poe: bool(port.poe) })) }
}

export function parseLabMap(value: unknown): LabMap {
  const root = obj(value)
  if (typeof root.generatedAt !== 'string') throw new Error('Lucia sent a lab map it could not read.')
  const unifi = obj(root.unifi), sources = obj(root.sources), wan = obj(root.wan)
  return {
    generatedAt: root.generatedAt,
    unifi: { state: unifi.state === 'connected' || unifi.state === 'error' ? unifi.state : 'not-connected', message: text(unifi.message), site: text(unifi.site) },
    traffic: traffic(root.traffic),
    sources: { snmp: bool(sources.snmp), netflow: bool(sources.netflow), dockerStats: bool(sources.dockerStats) },
    gateway: root.gateway ? device(obj(root.gateway)) : null,
    wan: { id: 'wan', name: str(wan.name, 'Internet'), address: text(wan.address), isp: text(wan.isp), health: health(wan.health) },
    networks: list(root.networks, item => text(item.id) ? { id: str(item.id), name: str(item.name, str(item.id)), vlan: num(item.vlan), subnet: text(item.subnet), purpose: text(item.purpose), zoneId: text(item.zoneId) } : null),
    zones: list(root.zones, item => text(item.id) ? { id: str(item.id), name: str(item.name, str(item.id)), networkIds: strings(item.networkIds) } : null),
    devices: list(root.devices, device),
    hosts: list(root.hosts, item => text(item.id) ? { id: str(item.id), kind: item.kind === 'spark' ? 'spark' : 'node', name: str(item.name, str(item.id)), address: text(item.address),
      networkId: text(item.networkId), parentId: text(item.parentId), health: health(item.health), lastSeenAt: text(item.lastSeenAt), cpuPercent: num(item.cpuPercent),
      memoryPercent: num(item.memoryPercent), updatesAvailable: num(item.updatesAvailable) ?? 0, gpu: text(item.gpu), href: str(item.href, '#/devices'), ...category(item, 'server') } : null),
    apps: list(root.apps, item => text(item.id) && text(item.hostId) ? { id: str(item.id), name: str(item.name, str(item.id)), hostId: str(item.hostId),
      desired: item.desired === 'Stopped' ? 'Stopped' : 'Running', health: health(item.health), address: text(item.address), networkId: text(item.networkId),
      href: str(item.href, '#/apps'), updateCount: num(item.updateCount) ?? 0, ...category(item, 'other'),
      containers: list(item.containers, container => text(container.id) ? { id: str(container.id), name: str(container.name), image: str(container.image), state: str(container.state),
        health: health(container.health), networks: list(container.networks, network => ({ name: str(network.name), address: text(network.address) })),
        mounts: list(container.mounts, mount => ({ source: str(mount.source), destination: str(mount.destination), storageId: text(mount.storageId) })),
        ports: list(container.ports, listen) } : null) } : null),
    storage: list(root.storage, item => text(item.id) ? { id: str(item.id), name: str(item.name, str(item.id)), kind: 'nas', address: text(item.address), health: health(item.health),
      shares: strings(item.shares), mounts: list(item.mounts, mount => text(mount.hostId) ? { hostId: str(mount.hostId), share: str(mount.share), health: health(mount.health) } : null), ...category(item, 'storage') } : null),
    clientGroups: list(root.clientGroups, item => text(item.id) && text(item.networkId) ? { id: str(item.id), networkId: str(item.networkId), group: group(item.group),
      online: num(item.online) ?? 0, total: num(item.total) ?? 0,
      members: list(item.members, member => text(member.mac) ? { mac: str(member.mac), name: str(member.name, str(member.mac)), address: text(member.address), online: bool(member.online),
        group: group(member.group), overridden: bool(member.overridden), parentId: text(member.parentId), vendor: text(member.vendor), lastSeenAt: text(member.lastSeenAt), ...category(member, 'other') } : null) } : null),
  }
}

export function parseLabMapLive(value: unknown): LabMapLive {
  const root = obj(value)
  if (typeof root.at !== 'string') throw new Error('Lucia sent live readings it could not read.')
  const rates: Record<string, Rate> = {}, load: LabMapLive['load'] = {}, healthById: Record<string, Health> = {}
  for (const [id, rate] of Object.entries(obj(root.rates))) rates[id] = { rxBps: Math.max(0, num(obj(rate).rxBps) ?? 0), txBps: Math.max(0, num(obj(rate).txBps) ?? 0) }
  for (const [id, reading] of Object.entries(obj(root.load))) load[id] = { cpuPercent: num(obj(reading).cpuPercent), memoryPercent: num(obj(reading).memoryPercent) }
  for (const [id, state] of Object.entries(obj(root.health))) healthById[id] = health(state)
  const mixes: LabMapLive['mix'] = {}, listening: LabMapLive['listening'] = {}
  for (const [id, value] of Object.entries(obj(root.mix))) mixes[id] = mix(value)
  for (const [id, value] of Object.entries(obj(root.listening))) listening[id] = list(value, listen).slice(0, 12)
  return { at: root.at, traffic: traffic(root.traffic), rates, load, health: healthById, mix: mixes, listening,
    flows: list(root.flows, item => text(item.from) && text(item.to) ? { from: str(item.from), to: str(item.to), label: text(item.label), bps: Math.max(0, num(item.bps) ?? 0), service: service(item.service) } : null).slice(0, 40),
    destinations: list(root.destinations, item => text(item.id) ? { id: str(item.id), name: str(item.name, str(item.id)), category: text(item.category)?.slice(0, 32) ?? 'other', chosen: bool(item.chosen),
      org: text(item.org), asn: text(item.asn), domains: strings(item.domains), bps: Math.max(0, num(item.bps) ?? 0), mix: mix(item.mix) } : null).slice(0, 40) }
}

export const mixList = (value: Mix | undefined) => Object.entries(value ?? {}).filter(([, bps]) => bps! > 0).map(([key, bps]) => ({ service: key as Service, bps: bps! })).sort((a, b) => b.bps - a.bps)
export const dominant = (value: Mix | undefined): Service => mixList(value)[0]?.service ?? 'other'

// Splits a road's cars across traffic types by share (largest remainder), interleaved so each colour is spread along the road.
export function carServices(value: Mix | undefined, count: number): Service[] {
  const items = mixList(value), total = items.reduce((sum, item) => sum + item.bps, 0)
  if (!total || count <= 0) return []
  const quota = items.map(item => ({ service: item.service, n: Math.floor(item.bps / total * count), r: item.bps / total * count % 1 }))
  let left = count - quota.reduce((sum, item) => sum + item.n, 0)
  for (const item of [...quota].sort((a, b) => b.r - a.r)) { if (left <= 0) break; item.n++; left-- }
  return quota.flatMap(item => Array.from({ length: item.n }, (_, k) => ({ service: item.service, at: (k + 0.5) / item.n }))).sort((a, b) => a.at - b.at).map(item => item.service)
}

// Each object's traffic types, rolled up every hop toward the Internet so switches, APs and the gateway carry their whole subtree.
export function mixTotals(layout: Layout, live: LabMapLive | null): Map<string, Mix> {
  const totals = new Map<string, Mix>()
  const add = (id: string, value: Mix) => { const into = totals.get(id) ?? {}; for (const { service: key, bps } of mixList(value)) into[key] = (into[key] ?? 0) + bps; totals.set(id, into) }
  if (!live) return totals
  for (const [id, value] of Object.entries(live.mix)) for (const step of routeOf(layout, id)) add(step, value)
  for (const dest of live.destinations) { add(dest.id, dest.mix); const region = layout.nodes.get(dest.id)?.block; if (region) add(region, dest.mix) }
  return totals
}

// Garage doors: ports the map knows an app publishes, plus ports Lucia has seen the object answer on.
export function portsOf(layout: Layout, live: LabMapLive | null, id: string): Listen[] {
  const out = new Map<string, Listen>()
  for (const item of [...live?.listening[id] ?? [], ...layout.nodes.get(id)?.ports ?? []]) { const key = `${item.port}/${item.protocol}`; if (!out.has(key)) out.set(key, item) }
  return [...out.values()].sort((a, b) => (b.bps ?? 0) - (a.bps ?? 0) || a.port - b.port).slice(0, 12)
}

export type NodeKind = 'wan' | 'gateway' | 'switch' | 'ap' | 'spark' | 'node' | 'app' | 'storage' | 'clients' | 'client' | 'network' | 'region' | 'site'
export interface MapNode {
  id: string; kind: NodeKind; label: string; detail: string | null; parentId: string | null; networkId: string | null
  health: Health; x: number; y: number; z: number; size: number; href: string | null; search: string; count?: number; path?: Pt[]; height?: number; angle?: number; block?: string
  ports?: Listen[]; tint?: number; quarter?: string; category?: string; categoryChosen?: boolean; arch?: boolean
}
export type Pt = [number, number]
export interface Plinth { id: string; label: string; detail: string; x: number; z: number; polygon: Pt[]; labelX: number; labelZ: number; tint?: number; fill?: boolean; layer?: NodeKind }
// Road tiers widen as traffic leaves home: lane (neighborhood) < street (district) < avenue (between districts) < highway (to the Internet).
export interface Road { points: Pt[]; kind: 'lane' | 'street' | 'avenue' | 'highway' }
export interface Tether { from: string; to: string; kind: 'storage' | 'network' }
export interface Layout { nodes: Map<string, MapNode>; plinths: Plinth[]; blocks: Plinth[]; tethers: Tether[]; roads: Road[]; outline: Pt[]; radius: number; gatewayId: string
  groups: Map<string, MapClientGroup> }

export const groupLabels: Record<ClientGroup, string> = {
  phones: 'Phones', computers: 'Computers', iot: 'Smart home', entertainment: 'Entertainment', infrastructure: 'Infrastructure', unknown: 'Unsorted',
}
export const kindLabels: Record<NodeKind, string> = {
  wan: 'Internet', gateway: 'Gateway', switch: 'Switch', ap: 'Access point', spark: 'Spark', node: 'Server', app: 'App', storage: 'Storage', clients: 'Clients', client: 'Client', network: 'Quarter',
  region: 'Internet district', site: 'Internet destination',
}
export const healthLabels: Record<Health, string> = { ok: 'Running', warn: 'Needs attention', failed: 'Failed', stale: 'Not reporting', unknown: 'Unknown' }
export const lodLevel: Record<NodeKind, number> = { wan: 0, gateway: 0, network: 0, switch: 1, ap: 1, spark: 1, node: 1, storage: 1, clients: 1, client: 2, app: 2, region: 1, site: 2 }

export function inSubnet(address: string, cidr: string): boolean {
  const [base, bits] = cidr.split('/')
  const toInt = (ip: string) => {
    const parts = ip.split('.').map(Number)
    return parts.length === 4 && parts.every(part => Number.isInteger(part) && part >= 0 && part <= 255) ? parts.reduce((sum, part) => sum * 256 + part, 0) : null
  }
  const a = toInt(address), b = toInt(base), prefix = Number(bits)
  if (a === null || b === null || !Number.isInteger(prefix) || prefix < 0 || prefix > 32) return false
  const size = 2 ** (32 - prefix)
  return Math.floor(a / size) === Math.floor(b / size)
}

const vlanText = (network: MapNetwork) => [network.vlan === null ? 'Untagged' : `VLAN ${network.vlan}`, network.subnet].filter(Boolean).join(' · ')

const gap = (p: Pt, q: Pt) => Math.hypot(p[0] - q[0], p[1] - q[1])
const shoelace = (poly: Pt[]) => poly.reduce((sum, [x1, z1], i) => { const [x2, z2] = poly[(i + 1) % poly.length]; return sum + x1 * z2 - x2 * z1 }, 0) / 2
function centroidOf(poly: Pt[]): Pt {
  const a = shoelace(poly)
  if (Math.abs(a) < 1e-6) return poly.length ? [poly.reduce((s, p) => s + p[0], 0) / poly.length, poly.reduce((s, p) => s + p[1], 0) / poly.length] : [0, 0]
  let x = 0, z = 0
  poly.forEach(([x1, z1], i) => { const [x2, z2] = poly[(i + 1) % poly.length], cross = x1 * z2 - x2 * z1; x += (x1 + x2) * cross; z += (z1 + z2) * cross })
  return [x / (6 * a), z / (6 * a)]
}
// Sutherland–Hodgman against one line: keeps a·x + b·z ≤ c.
function clipHalf(poly: Pt[], a: number, b: number, c: number): Pt[] {
  const out: Pt[] = []
  poly.forEach((p, i) => {
    const q = poly[(i + 1) % poly.length], fp = a * p[0] + b * p[1] - c, fq = a * q[0] + b * q[1] - c
    if (fp <= 0) out.push(p)
    if ((fp < 0 && fq > 0) || (fp > 0 && fq < 0)) { const t = fp / (fp - fq); out.push([p[0] + (q[0] - p[0]) * t, p[1] + (q[1] - p[1]) * t]) }
  })
  return out
}
function hull(points: Pt[]): Pt[] {
  const sorted = [...points].sort((p, q) => p[0] - q[0] || p[1] - q[1])
  const cross = (o: Pt, a: Pt, b: Pt) => (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])
  const chain = (list: Pt[]) => list.reduce<Pt[]>((out, p) => { while (out.length >= 2 && cross(out[out.length - 2], out[out.length - 1], p) <= 0) out.pop(); out.push(p); return out }, [])
  return [...chain(sorted).slice(0, -1), ...chain(sorted.reverse()).slice(0, -1)]
}
const inward = (poly: Pt[]) => {
  const sign = Math.sign(shoelace(poly)) || 1
  return poly.map(([x1, z1], i) => { const [x2, z2] = poly[(i + 1) % poly.length], len = Math.hypot(x2 - x1, z2 - z1) || 1; return { p: [x1, z1] as Pt, nx: -sign * (z2 - z1) / len, nz: sign * (x2 - x1) / len } })
}
const insetPolygon = (poly: Pt[], d: number) => inward(poly).reduce((out, { p, nx, nz }) => out.length ? clipHalf(out, -nx, -nz, -(nx * p[0] + nz * p[1]) - d) : out, poly)
const clearance = (edges: ReturnType<typeof inward>, at: Pt) => Math.min(...edges.map(({ p, nx, nz }) => nx * (at[0] - p[0]) + nz * (at[1] - p[1])))
function seeded(text: string) {
  let s = 2166136261
  for (const ch of text) s = Math.imul(s ^ ch.charCodeAt(0), 16777619)
  return () => { s = (s + 0x6d2b79f5) >>> 0; let t = s; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296 }
}

const powerCells = (outline: Pt[], sites: Pt[], weights: number[]) => sites.map((si, i) => sites.reduce((cell, sj, j) => j === i || !cell.length ? cell
  : clipHalf(cell, 2 * (sj[0] - si[0]), 2 * (sj[1] - si[1]), sj[0] ** 2 + sj[1] ** 2 - si[0] ** 2 - si[1] ** 2 - weights[j] + weights[i]), outline))

// Voronoi treemap: power-diagram cells whose areas converge on each district's need.
function treemap(targets: number[], random: () => number, container?: Pt[]) {
  const total = targets.reduce((sum, t) => sum + t, 0), R = Math.sqrt(total / Math.PI), p1 = random() * Math.PI * 2, p2 = random() * Math.PI * 2
  const outline = container ?? hull(Array.from({ length: 28 }, (_, k): Pt => {
    const t = k / 28 * Math.PI * 2, wobble = 1 + 0.06 * Math.sin(2 * t + p1) + 0.04 * Math.sin(3 * t + p2)
    return [Math.cos(t) * R * 1.22 * wobble, Math.sin(t) * R / 1.22 * wobble]
  }))
  const [cx, cz] = centroidOf(outline), rx = Math.max(...outline.map(p => Math.abs(p[0] - cx))), rz = Math.max(...outline.map(p => Math.abs(p[1] - cz)))
  const n = targets.length, want = targets.map(t => t / total * Math.abs(shoelace(outline)))
  if (n === 1) return { cells: [outline], outline }
  const sites: Pt[] = new Array(n), weights: number[] = new Array(n).fill(0), turn = random() * Math.PI * 2
  targets.map((_, i) => i).sort((a, b) => targets[b] - targets[a]).forEach((i, rank) => {
    const r = 0.6 * Math.sqrt((rank + 0.5) / n), t = turn + rank * 2.399963
    sites[i] = [cx + Math.cos(t) * r * rx, cz + Math.sin(t) * r * rz]
  })
  let cells = powerCells(outline, sites, weights)
  for (let step = 0; step < 160; step++) {
    cells.forEach((cell, i) => {
      const a = cell.length >= 3 ? Math.abs(shoelace(cell)) : 0, now = Math.sqrt(a / Math.PI), goal = Math.sqrt(want[i] / Math.PI)
      if (a > 1e-6) sites[i] = centroidOf(cell)
      weights[i] += a > 1e-6 ? 2 * now * (goal - now) * 0.7 : goal * goal * 0.2
    })
    for (let i = 0; i < n; i++) for (let j = 0; j < n; j++)
      if (i !== j) weights[i] = Math.min(weights[i], weights[j] + 0.9 * gap(sites[i], sites[j]) ** 2)
    cells = powerCells(outline, sites, weights)
  }
  return { cells, outline }
}

// Building lots on a grid squared to the block's longest edge, filled from the middle out.
function lots(poly: Pt[], count: number, pitch: number, foot: number) {
  const center = centroidOf(poly), edges = inward(poly)
  const sides = poly.map((p, k) => [p, poly[(k + 1) % poly.length]] as [Pt, Pt]), long = sides.reduce((best, side) => gap(...side) > gap(...best) ? side : best, sides[0] ?? [center, center])
  const angle = Math.atan2(long[1][1] - long[0][1], long[1][0] - long[0][0]), ux = Math.cos(angle), uz = Math.sin(angle)
  const reach = Math.max(1, ...poly.map(p => gap(p, center)))
  let step = pitch / 0.88, spots: Pt[] = []
  for (let attempt = 0; attempt < 8 && spots.length < count; attempt++) {
    step *= 0.88
    spots = []
    const span = Math.ceil(reach / step)
    for (let i = -span; i <= span; i++) for (let j = -span; j <= span; j++) {
      const p: Pt = [center[0] + (i * ux - j * uz) * step, center[1] + (i * uz + j * ux) * step]
      if (poly.length < 3 || clearance(edges, p) >= foot * 0.72 * step / pitch) spots.push(p)
    }
  }
  spots.sort((a, b) => gap(a, center) - gap(b, center))
  while (spots.length < count) spots.push([center[0] + (spots.length % 3 - 1) * 0.4, center[1] + (Math.floor(spots.length / 3) % 3 - 1) * 0.4])
  return { angle: -angle, spots: spots.slice(0, count), scale: step / pitch, center, axis: [ux, uz] as Pt, step }
}
const noise = (id: string) => seeded(id)()
const clientHeight: Record<ClientGroup, number> = { computers: 1.6, entertainment: 1.4, infrastructure: 1.9, phones: 1, iot: 0.7, unknown: 0.9 }
const plate = (id: string, label: string, detail: string, polygon: Pt[]): Plinth => {
  const [x, z] = centroidOf(polygon), sides = polygon.map((p, k): [Pt, Pt] => [p, polygon[(k + 1) % polygon.length]])
  const front = sides.reduce((best, side) => side[0][1] + side[1][1] > best[0][1] + best[1][1] ? side : best, sides[0] ?? [[x, z], [x, z]])
  return { id, label, detail, x, z, polygon, labelX: (front[0][0] + front[1][0]) / 2, labelZ: (front[0][1] + front[1][1]) / 2 }
}

const vlanTints = [0x00f0ff, 0x8a7dff, 0x3dffb0, 0xff5fd2, 0x5b9dff, 0xd4ff3a, 0xc59bff, 0x52ffe0]
const footOn = (p: Pt, a: Pt, b: Pt): Pt => {
  const dx = b[0] - a[0], dz = b[1] - a[1], t = Math.min(1, Math.max(0, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dz) / (dx * dx + dz * dz || 1)))
  return [a[0] + dx * t, a[1] + dz * t]
}
// True when a segment lies along one side of the polygon, so it is already a street.
const along = (poly: Pt[], p: Pt, q: Pt) => poly.some((a, k) => { const b = poly[(k + 1) % poly.length]; return gap(p, footOn(p, a, b)) < 0.3 && gap(q, footOn(q, a, b)) < 0.3 })
export function layoutLab(map: LabMap, sites: CitySite[] = []): Layout {
  const nodes = new Map<string, MapNode>()
  const gatewayId = map.gateway?.id ?? 'gateway'
  const networks = [...map.networks]
  const networkOf = (id: string | null, address: string | null) =>
    (id && networks.some(network => network.id === id) ? id : null) ?? (address ? networks.find(network => network.subnet && inSubnet(address, network.subnet))?.id ?? null : null)
  const hosts = map.hosts.map(host => ({ ...host, networkId: networkOf(host.networkId, host.address) }))
  for (const app of map.apps)
    if (!hosts.some(host => host.id === app.hostId))
      hosts.push({ id: app.hostId, kind: 'node', name: app.hostId, address: null, networkId: null, parentId: null, health: 'unknown', lastSeenAt: null,
        cpuPercent: null, memoryPercent: null, updatesAvailable: 0, gpu: null, href: '#/devices', category: 'server', categoryChosen: false })
  const storage = map.storage.map(item => ({ ...item, networkId: networkOf(null, item.address) }))
  if ([...hosts, ...storage].some(item => !item.networkId) || map.clientGroups.some(item => !networks.some(network => network.id === item.networkId)))
    networks.push({ id: 'net:other', name: 'Other addresses', vlan: null, subnet: null, purpose: null, zoneId: null })
  const appsOf = (hostId: string) => map.apps.filter(app => app.hostId === hostId).sort((a, b) => a.name.localeCompare(b.name))
  // A network keeps one ground tint in every quarter it reaches, so a VLAN reads the same wherever it lands.
  const ordered = [...networks].sort((a, b) => (a.vlan ?? -1) - (b.vlan ?? -1) || a.name.localeCompare(b.name))
  const netTint = (networkId: string) => { const k = ordered.findIndex(network => network.id === networkId); return k < 0 || ordered[k].vlan === null ? vlanTints[0] : vlanTints[k % vlanTints.length] }
  const netName = (networkId: string) => networks.find(network => network.id === networkId)?.name ?? 'Other addresses'

  // Quarters: each AP or switch is the entrance to what it feeds. The core switch is the archway into the whole home side and takes in anything unplaced.
  const devices = map.devices.filter(item => item.id !== gatewayId)
  const parentOfDevice = new Map(devices.map(item => [item.id, item.parentId && devices.some(other => other.id === item.parentId) ? item.parentId : gatewayId]))
  const ancestors = (id: string) => { const out: string[] = []; let at = parentOfDevice.get(id); while (at && at !== gatewayId && out.length < 12) { out.push(at); at = parentOfDevice.get(at) } return out }
  const below = (id: string) => devices.filter(item => ancestors(item.id).includes(id)).length
  const core = devices.filter(item => item.kind === 'switch' && parentOfDevice.get(item.id) === gatewayId).sort((a, b) => below(b.id) - below(a.id) || a.name.localeCompare(b.name))[0]?.id ?? null
  const fallback = core ?? gatewayId
  const quarterFor = (id: string | null) => id && parentOfDevice.has(id) ? id : fallback
  const deviceName = (id: string) => devices.find(item => item.id === id)?.name ?? map.gateway?.name ?? 'Network'

  // Inside a quarter: a district per client group (split by network when a group spans more than one) and one for its servers; inside a district, a neighborhood per category.
  interface Hood { id: string; label: string; detail: string; count: number; pitch: number; foot: number; tint: number; via: string | null; people: MapClient[] }
  interface District { id: string; label: string; detail: string; tint: number; networkId: string; hoods: Hood[]; group?: MapClientGroup }
  const quarters = new Map<string, District[]>(), groups = new Map<string, MapClientGroup>()
  const inQuarter = (q: string) => { if (!quarters.has(q)) quarters.set(q, []); return quarters.get(q)! }
  const hostQuarter = new Map(hosts.map(host => [host.id, quarterFor(host.parentId)]))
  const servers = new Map<string, Map<string, Hood[]>>()
  const serve = (q: string, networkId: string, hood: Hood) => { const byNet = servers.get(q) ?? new Map<string, Hood[]>(); byNet.set(networkId, [...byNet.get(networkId) ?? [], hood]); servers.set(q, byNet) }
  for (const host of [...hosts].sort((a, b) => appsOf(b.id).length - appsOf(a.id).length || a.name.localeCompare(b.name)))
    serve(hostQuarter.get(host.id)!, host.networkId ?? 'net:other', { id: host.id, label: host.name, detail: `${appsOf(host.id).length} apps`, count: 1 + appsOf(host.id).length, pitch: 3.8, foot: 2.3,
      tint: buildingCategoryColor(host.category), via: host.parentId, people: [] })
  for (const item of storage)
    serve(hostQuarter.get(item.mounts.find(mount => hostQuarter.has(mount.hostId))?.hostId ?? '') ?? fallback, item.networkId ?? 'net:other',
      { id: item.id, label: item.name, detail: 'Storage', count: 1, pitch: 4, foot: 2.4, tint: buildingCategoryColor(item.category), via: null, people: [] })
  for (const [q, byNet] of servers) for (const [networkId, list] of byNet)
    inQuarter(q).push({ id: `srv:${q}${byNet.size > 1 ? `:${networkId}` : ''}`, label: byNet.size > 1 ? `Servers · ${netName(networkId)}` : 'Servers', detail: netName(networkId), tint: netTint(networkId), networkId, hoods: list })
  const buckets = new Map<string, { q: string; kind: ClientGroup; nets: Map<string, MapClient[]> }>(), placed = new Set<string>()
  for (const item of [...map.clientGroups].sort((a, b) => clientGroups.indexOf(a.group) - clientGroups.indexOf(b.group))) {
    const usual = commonParent(item.members, gatewayId), networkId = networks.some(network => network.id === item.networkId) ? item.networkId : 'net:other'
    for (const member of item.members) {
      if (placed.has(member.mac)) continue
      placed.add(member.mac)
      const q = quarterFor(member.parentId ?? usual), key = `${q}|${item.group}`, bucket = buckets.get(key) ?? { q, kind: item.group, nets: new Map<string, MapClient[]>() }
      bucket.nets.set(networkId, [...bucket.nets.get(networkId) ?? [], member])
      buckets.set(key, bucket)
    }
  }
  for (const { q, kind, nets } of buckets.values()) for (const [networkId, people] of nets) {
    const split = nets.size > 1, id = `grp:${q}:${kind}${split ? `:${networkId}` : ''}`, online = people.filter(member => member.online).length
    const item: MapClientGroup = { id, networkId, group: kind, online, total: people.length, members: people }
    groups.set(id, item)
    inQuarter(q).push({ id, label: `${groupLabels[kind]}${split ? ` · ${netName(networkId)}` : ''}`, detail: `${online} of ${people.length} online`, tint: netTint(networkId), networkId, group: item,
      hoods: buildingCategoriesOf(people.map(member => member.category)).map(cat => {
        const list = people.filter(member => member.category === cat).sort((a, b) => Number(b.online) - Number(a.online) || a.name.localeCompare(b.name))
        return { id: `hood:${id}:${cat}`, label: buildingCategoryLabel(cat), detail: `${list.filter(member => member.online).length} of ${list.length} online`, count: list.length, pitch: 3, foot: 1.4,
          tint: buildingCategoryColor(cat), via: commonParent(list, q), people: list }
      }) })
  }
  const grow = (n: number, road: number, extra: number) => n * 1.1 + 2 * Math.sqrt(Math.PI * n) * road + extra
  const hoodNeed = (hood: Hood) => { const a = hood.count * hood.pitch ** 2; return a * 1.6 + 2 * Math.sqrt(Math.PI * a) * 2.6 + 14 }
  const districtNeed = (district: District) => grow(district.hoods.reduce((sum, hood) => sum + hoodNeed(hood), 0), 1.3, 10)
  const shown = [...quarters.keys()].sort((a, b) => deviceName(a).localeCompare(deviceName(b)) || a.localeCompare(b))
  const targets = shown.map(q => Math.max(140, grow(quarters.get(q)!.reduce((sum, district) => sum + districtNeed(district), 0), 3, 30)))
  const { cells, outline } = shown.length ? treemap(targets, seeded(shown.join('|'))) : { cells: [] as Pt[][], outline: [] as Pt[] }
  const centroids = cells.map(centroidOf)

  // Street graph: quarter edges, merged where cells meet, long blocks split.
  const verts: Pt[] = [], cellsAt: Set<number>[] = []
  const vertex = (p: Pt) => { let k = verts.findIndex(v => gap(v, p) < 0.6); if (k < 0) { k = verts.length; verts.push(p); cellsAt.push(new Set()) } return k }
  const cellNodes = cells.map(() => new Set<number>()), chains = new Map<string, number[]>()
  cells.forEach((cell, i) => {
    const ids = cell.map(vertex)
    ids.forEach((a, k) => {
      const b = ids[(k + 1) % ids.length]
      if (a === b) return
      const [lo, hi] = a < b ? [a, b] : [b, a], key = `${lo}:${hi}`
      let chain = chains.get(key)
      if (!chain) {
        const pieces = Math.ceil(gap(verts[lo], verts[hi]) / 16)
        chain = [lo]
        for (let s = 1; s < pieces; s++) { const t = s / pieces; chain.push(verts.length); verts.push([verts[lo][0] + (verts[hi][0] - verts[lo][0]) * t, verts[lo][1] + (verts[hi][1] - verts[lo][1]) * t]); cellsAt.push(new Set()) }
        chain.push(hi)
        chains.set(key, chain)
      }
      for (const v of chain) { cellNodes[i].add(v); cellsAt[v].add(i) }
    })
  })
  if (!verts.length) { verts.push([0, 0]); cellsAt.push(new Set()) }
  const links: Map<number, number>[] = verts.map(() => new Map())
  for (const chain of chains.values()) for (let s = 1; s < chain.length; s++) {
    const a = chain[s - 1], b = chain[s], d = gap(verts[a], verts[b])
    links[a].set(b, d); links[b].set(a, d)
  }
  const shortest = (from: number, to: number): number[] => {
    const dist = verts.map(() => Infinity), prev = verts.map(() => -1), done = verts.map(() => false)
    dist[from] = 0
    for (;;) {
      let u = -1
      for (let k = 0; k < verts.length; k++) if (!done[k] && dist[k] < Infinity && (u < 0 || dist[k] < dist[u])) u = k
      if (u < 0 || u === to) break
      done[u] = true
      for (const [v, w] of links[u]) if (dist[u] + w < dist[v]) { dist[v] = dist[u] + w; prev[v] = u }
    }
    if (dist[to] === Infinity) return [from, to]
    const path = [to]
    while (path[path.length - 1] !== from) path.push(prev[path[path.length - 1]])
    return path.reverse()
  }

  const gw = verts.reduce((best, v, k) => v[0] < verts[best][0] ? k : best, 0)
  const minX = Math.min(0, ...outline.map(p => p[0])), spread = Math.max(30, ...outline.map(p => Math.hypot(...p)) )
  const wanAt: Pt = [minX - Math.max(36, spread * 0.55), verts[gw][1] * 0.3]

  const add = (node: Omit<MapNode, 'search'>, extra: (string | null | undefined)[] = []) =>
    nodes.set(node.id, { ...node, search: [node.label, node.detail, kindLabels[node.kind], ...extra].filter(Boolean).join(' ').toLowerCase() })
  const gateway = map.gateway
  const lead: Pt = [verts[gw][0] - 8, verts[gw][1]], reach = lead[0] - wanAt[0]
  const bend = Array.from({ length: 13 }, (_, s): Pt => {
    const t = s / 12, u = 1 - t, c1: Pt = [lead[0] - reach / 3, lead[1]], c2: Pt = [wanAt[0] + reach / 3, wanAt[1]]
    return [u ** 3 * lead[0] + 3 * u * u * t * c1[0] + 3 * u * t * t * c2[0] + t ** 3 * wanAt[0], u ** 3 * lead[1] + 3 * u * u * t * c1[1] + 3 * u * t * t * c2[1] + t ** 3 * wanAt[1]]
  })
  // The gateway is a bridge partway down the highway; everything the gateway feeds drives out to it.
  const gatePath: Pt[] = [verts[gw], ...bend], bridge = gatePath[4]
  const heading = (a: Pt, b: Pt) => Math.atan2(b[0] - a[0], b[1] - a[1])
  const hop = (parent: string, path: Pt[]) => parent === gatewayId ? [...path, ...gatePath.slice(1, 5)] : path
  add({ id: 'wan', kind: 'wan', label: map.wan.name, detail: map.wan.isp ?? map.wan.address, parentId: null, networkId: null, health: map.wan.health, x: wanAt[0], y: 0, z: wanAt[1], size: 1, href: null }, [map.wan.address])
  add({ id: gatewayId, kind: 'gateway', label: gateway?.name ?? 'Network', detail: gateway ? gateway.model ?? gateway.address : 'UniFi not connected', parentId: 'wan', networkId: null,
    health: gateway?.health ?? 'unknown', x: bridge[0], y: 0, z: bridge[1], size: 6, href: '#/settings/unifi', path: gatePath.slice(4), angle: heading(bridge, gatePath[5]) }, [gateway?.address, gateway?.mac])

  const quarterOf = new Map(shown.map((q, i) => [q, i]))
  const plinths: Plinth[] = shown.map((q, i) => {
    const id = `quarter:${q}`, device = devices.find(item => item.id === q), people = quarters.get(q)!.reduce((sum, district) => sum + (district.group?.total ?? 0), 0)
    const detail = `${q === core ? 'Core switch' : device?.kind === 'ap' ? 'Access point' : device ? 'Switch' : 'Gateway'} · ${people} client${people === 1 ? '' : 's'}`
    add({ id, kind: 'network', label: deviceName(q), detail, parentId: q, networkId: null, health: 'ok', x: centroids[i][0], y: 0, z: centroids[i][1],
      size: Math.sqrt(Math.abs(shoelace(cells[i]))), href: null, quarter: id })
    return plate(id, deviceName(q), detail, insetPolygon(cells[i], 1.1))
  })

  const served = new Map<string, Pt[]>()
  shown.forEach((q, i) => { for (const at of [q, ...ancestors(q)]) if (parentOfDevice.has(at)) served.set(at, [...served.get(at) ?? [], centroids[i]]) })
  const spot = new Map<string, Pt>([[gatewayId, verts[gw]]]), corner = new Map<string, number>([[gatewayId, gw]]), used = new Set([gw])
  for (const item of [...devices].sort((a, b) => ancestors(a.id).length - ancestors(b.id).length || a.name.localeCompare(b.name))) {
    const parent = parentOfDevice.get(item.id)!, from = spot.get(parent) ?? verts[gw], points = served.get(item.id) ?? []
    let path: Pt[], at: Pt, angle: number | undefined
    if (item.id === core) {
      // The core switch spans the road where the home side meets the highway.
      spot.set(item.id, verts[gw]); corner.set(item.id, gw)
      path = gatePath.slice(0, 5)
      at = [(verts[gw][0] + bend[0][0]) / 2, (verts[gw][1] + bend[0][1]) / 2]
      angle = heading(verts[gw], bend[0])
    } else {
      // A quarter's entrance sits on its own ring road nearest its uplink; other gear sits between the quarters it serves.
      const i = quarterOf.get(item.id), pool = i === undefined ? [] : [...cellNodes[i]].filter(k => !used.has(k))
      const wish: Pt = pool.length || !points.length ? from
        : [0.65 * points.reduce((s, p) => s + p[0], 0) / points.length + 0.35 * from[0], 0.65 * points.reduce((s, p) => s + p[1], 0) / points.length + 0.35 * from[1]]
      let best = -1, score = Infinity
      for (const k of pool.length ? pool : verts.keys()) { if (used.has(k)) continue; const s = gap(verts[k], wish) + (!pool.length && cellsAt[k].size < 3 ? 4 : 0); if (s < score) { score = s; best = k } }
      if (best >= 0) {
        used.add(best); corner.set(item.id, best); spot.set(item.id, verts[best])
        const home = corner.get(parent)
        path = home === undefined ? [verts[best], from] : shortest(best, home).map(k => verts[k])
      } else {
        const turn = devices.indexOf(item) * 2.399963
        spot.set(item.id, [from[0] + Math.cos(turn) * 6, from[1] + Math.sin(turn) * 6])
        path = [spot.get(item.id)!, from]
      }
      path = hop(parent, path)
      at = spot.get(item.id)!
    }
    add({ id: item.id, kind: item.kind === 'gateway' ? 'switch' : item.kind, label: item.name, detail: item.model ?? item.address, parentId: parent, networkId: null,
      health: item.health, x: at[0], y: item.kind === 'ap' ? 3 : 0, z: at[1], size: item.kind === 'ap' ? 1.4 : 3, href: '#/settings/unifi', path,
      ...(angle === undefined ? {} : { angle, arch: true }), ...(quarterOf.has(item.id) ? { quarter: `quarter:${item.id}` } : {}) }, [item.address, item.mac, item.model])
  }
  const parentOf = (hood: Hood, q: string) => hood.via && nodes.has(hood.via) ? hood.via : q

  const blocks: Plinth[] = [], lanes: Road[] = [], paved = new Set<string>()
  const hoodAt = new Map<string, { center: Pt; drive: number; angle: number; spots: Pt[]; scale: number; spine: [Pt, Pt]; out: (p: Pt) => Pt[] }>()
  shown.forEach((q, i) => {
    const list = quarters.get(q)!
    const floor = insetPolygon(cells[i], 3), c = centroids[i]
    const area = floor.length >= 3 ? floor : [[c[0] - 6, c[1] - 6], [c[0] + 6, c[1] - 6], [c[0] + 6, c[1] + 6], [c[0] - 6, c[1] + 6]] as Pt[]
    const { cells: parts } = treemap(list.map(districtNeed), seeded(`quarter:${q}`), area)
    // District edges become the quarter's streets, tied into the ring avenue at each corner; neighborhoods inside a district meet on lanes.
    const local = verts.length
    const join = (p: Pt) => { for (let v = local; v < verts.length; v++) if (gap(verts[v], p) < 0.3) return v; verts.push(p); cellsAt.push(new Set([i])); links.push(new Map()); return verts.length - 1 }
    const tie = (a: number, b: number) => { if (a === b) return; const d = gap(verts[a], verts[b]); links[a].set(b, d); links[b].set(a, d) }
    const taps = new Map<string, number[]>()
    const link = (a: number, b: number, kind: Road['kind']) => {
      if (a === b) return
      const key = a < b ? `${a}:${b}` : `${b}:${a}`
      tie(a, b)
      if (!paved.has(key)) { paved.add(key); lanes.push({ points: [verts[a], verts[b]], kind }) }
    }
    for (const part of parts) part.forEach((p, k) => link(join(p), join(part[(k + 1) % part.length]), 'street'))
    for (const p of area) {
      let ring = -1
      for (const v of cellNodes[i]) if (ring < 0 || gap(verts[v], p) < gap(verts[ring], p)) ring = v
      if (ring >= 0) link(join(p), ring, 'street')
    }
    list.forEach((district, d) => {
      const dpoly = parts[d].length >= 3 ? parts[d] : area, ground = insetPolygon(dpoly, 0.45)
      blocks.push({ ...plate(district.id, district.label, district.detail, ground.length >= 3 ? ground : dpoly), tint: district.tint, fill: true })
      const { cells: rooms } = treemap(district.hoods.map(hoodNeed), seeded(district.id), dpoly)
      for (const room of rooms) room.forEach((p, k) => { const n = room[(k + 1) % room.length]; if (along(dpoly, p, n)) tie(join(p), join(n)); else link(join(p), join(n), 'lane') })
      district.hoods.forEach((hood, k) => {
        const edge = rooms[k].length >= 3 ? rooms[k] : dpoly, inset = insetPolygon(edge, 1.4), poly = inset.length >= 3 ? inset : edge
        const toward = spot.get(parentOf(hood, q)) ?? verts[gw]
        const lot = lots(poly, hood.count, hood.pitch, hood.foot), { center, step } = lot, [ux, uz] = lot.axis
        // A small street grid: a lane between every pair of lot rows, and a cross lane out to the block's nearer side.
        const at = (u: number, v: number): Pt => [center[0] + u * ux - v * uz, center[1] + u * uz + v * ux]
        const uv = (p: Pt) => { const dx = p[0] - center[0], dz = p[1] - center[1]; return [dx * ux + dz * uz, dz * ux - dx * uz] }
        const rowOf = (v: number) => (2 * Math.floor(Math.round(v / step) / 2) + 0.5) * step, cross = step / 2, o = at(cross, 0)
        let exit: Pt = verts[gw], drive = -1, cost = Infinity, sign = 1
        for (const s of [1, -1]) edge.forEach((a, e) => {
          const b = edge[(e + 1) % edge.length], dx = -uz * s, dz = ux * s, ex = b[0] - a[0], ez = b[1] - a[1], den = dx * ez - dz * ex
          if (Math.abs(den) < 1e-9) return
          const t = ((a[0] - o[0]) * ez - (a[1] - o[1]) * ex) / den, w = ((a[0] - o[0]) * dz - (a[1] - o[1]) * dx) / den
          if (t <= 0 || w < 0 || w > 1) return
          const p: Pt = [o[0] + dx * t, o[1] + dz * t], c = t + 0.6 * gap(p, toward)
          if (c < cost) { cost = c; exit = p; sign = s; drive = e }
        })
        if (drive >= 0) {
          const a = join(edge[drive]), b = join(edge[(drive + 1) % edge.length]), p = join(exit), key = a < b ? `${a}:${b}` : `${b}:${a}`, on = taps.get(key) ?? []
          for (const v of [a, b, ...on]) tie(p, v)
          taps.set(key, [...on, p])
          drive = p
        } else for (const v of edge.map(join)) { const c = gap(center, verts[v]) + 0.6 * gap(verts[v], toward); if (c < cost) { cost = c; drive = v; exit = verts[v] } }
        const rows = new Map<number, number[]>()
        for (const p of lot.spots) { const [u, v] = uv(p), r = rowOf(v); rows.set(r, [...(rows.get(r) ?? []), u]) }
        for (const [r, us] of rows) { const lo = Math.min(cross, ...us), hi = Math.max(cross, ...us); if (hi > lo) lanes.push({ points: [at(lo, r), at(hi, r)], kind: 'lane' }) }
        const far = sign > 0 ? Math.min(...rows.keys()) : Math.max(...rows.keys()), spine: [Pt, Pt] = [at(cross, far), exit]
        const out = (p: Pt): Pt[] => { const [u, v] = uv(p), r = rowOf(v); return [p, at(u, r), at(cross, r), exit] }
        blocks.push({ ...plate(hood.id, hood.label, hood.detail, poly), tint: hood.tint, ...(hood.people.length ? { layer: 'client' as const } : {}) })
        lanes.push({ points: spine, kind: 'lane' })
        hoodAt.set(hood.id, { drive, spine, out, ...lot })
      })
    })
  })
  const hoodOf = (id: string) => hoodAt.get(id) ?? { center: verts[gw], drive: -1, angle: 0, spots: [] as Pt[], scale: 1, spine: [verts[gw], verts[gw]] as [Pt, Pt], out: (p: Pt) => [p] }
  const lotOf = (hood: ReturnType<typeof hoodOf>, index: number): Pt => hood.spots[index] ?? [hood.center[0] + index * 0.3, hood.center[1]]
  const routes = new Map<string, Pt[]>()
  const toParent = (hood: ReturnType<typeof hoodOf>, parent: string): Pt[] => {
    const key = `${hood.drive}>${parent}`, home = corner.get(parent), there = spot.get(parent) ?? verts[gw]
    if (!routes.has(key)) routes.set(key, hop(parent, hood.drive < 0 ? [there] : home === undefined ? [verts[hood.drive], there] : shortest(hood.drive, home).map(k => verts[k])))
    return routes.get(key)!
  }
  const driveway = (hood: ReturnType<typeof hoodOf>, p: Pt, parent: string): Pt[] => {
    const out = hood.out(p)
    lanes.push({ points: out.slice(0, 2), kind: 'lane' })
    return [...out, ...toParent(hood, parent)]
  }
  const tagged = (category: string, chosen: boolean) => ({ tint: buildingCategoryColor(category), category, categoryChosen: chosen })

  for (const [q, list] of quarters) for (const district of list) {
    const quarter = `quarter:${q}`
    if (district.group) {
      const first = hoodOf(district.hoods[0].id), ground = blocks.find(block => block.id === district.id)!, usual = commonParent(district.group.members, q), parent = nodes.has(usual) ? usual : q
      add({ id: district.id, kind: 'clients', label: district.label, detail: district.detail, parentId: parent, networkId: district.networkId, health: district.group.online ? 'ok' : 'unknown',
        x: ground.x, y: 0, z: ground.z, size: 2.6, href: null, count: district.group.online, block: district.id, tint: district.tint, quarter, path: [first.spine[0], ...toParent(first, parent)] },
      [netName(district.networkId)])
    }
    for (const hood of district.hoods) {
      const lots = hoodOf(hood.id), parent = parentOf(hood, q), host = hosts.find(item => item.id === hood.id), item = storage.find(entry => entry.id === hood.id)
      if (host) {
        const at = lotOf(lots, 0)
        add({ id: host.id, kind: host.kind, label: host.name, detail: host.address, parentId: parent, networkId: host.networkId ?? 'net:other', health: host.health,
          x: at[0], y: 0, z: at[1], size: (host.kind === 'spark' ? 2.5 : 2.3) * lots.scale, height: host.kind === 'spark' ? 7 : 4.6 + noise(host.id) * 1.2, angle: lots.angle, block: host.id,
          href: host.href, path: driveway(lots, at, parent), quarter, ...tagged(host.category, host.categoryChosen) }, [host.gpu, buildingCategoryLabel(host.category)])
        appsOf(host.id).forEach((app, index) => {
          const p = lotOf(lots, index + 1), there = lots.out(p), ports = portsOfApp(app)
          lanes.push({ points: there.slice(0, 2), kind: 'lane' })
          add({ id: app.id, kind: 'app', label: app.name, detail: app.address, parentId: host.id, networkId: networkOf(app.networkId, app.address), health: app.health,
            x: p[0], y: 0, z: p[1], size: 1.7 * lots.scale * (1 + Math.min(0.3, ports.length * 0.06)), height: Math.min(4.5, 1.2 + app.containers.length * 0.6 + noise(app.id) * 1.2), angle: lots.angle, block: host.id,
            href: app.href, path: [...there.slice(0, 3), ...lots.out(at).slice(0, 3).reverse()], ports, quarter, ...tagged(app.category, app.categoryChosen) },
          [host.name, buildingCategoryLabel(app.category), ...app.containers.flatMap(container => [container.name, container.image]), ...ports.map(port => String(port.port))])
        })
      } else if (item) {
        const at = lotOf(lots, 0)
        add({ id: item.id, kind: 'storage', label: item.name, detail: item.address, parentId: parent, networkId: item.networkId ?? 'net:other', health: item.health,
          x: at[0], y: 0, z: at[1], size: 2.4 * lots.scale, height: 3, angle: lots.angle, block: item.id, href: '#/settings/storage', path: driveway(lots, at, parent), quarter,
          ...tagged(item.category, item.categoryChosen) }, item.shares)
      } else hood.people.forEach((member, index) => {
        const p = lotOf(lots, index), via = member.parentId && nodes.has(member.parentId) ? member.parentId : parent
        add({ id: `client:${member.mac}`, kind: 'client', label: member.name, detail: member.address ?? member.vendor, parentId: via, networkId: district.networkId, health: member.online ? 'ok' : 'stale',
          x: p[0], y: 0, z: p[1], size: 1.4 * lots.scale, height: member.online ? clientHeight[district.group!.group] * (0.6 + noise(member.mac) * 0.9) : 0.35, angle: lots.angle, block: district.id,
          href: null, path: driveway(lots, p, via), quarter, ...tagged(member.category, member.categoryChosen) },
        [member.address, member.vendor, member.mac, groupLabels[district.group!.group], buildingCategoryLabel(member.category), netName(district.networkId)])
      })
    }
  }
  // The Internet city: a district per destination category west of the WAN, its own street grid fed by the highway.
  const kinds = districtsOf(sites.map(site => site.category))
  if (kinds.length) {
    const bands = kinds.map(category => sites.filter(site => site.category === category).sort((a, b) => b.peak - a.peak || a.id.localeCompare(b.id)))
    const { cells: raw, outline: rim } = treemap(bands.map(list => Math.max(150, (list.length * 6 + 20) ** 2 / (4 * Math.PI) * 1.5)), seeded(kinds.join('|')))
    const shift = wanAt[0] - 18 - Math.max(...rim.map(p => p[0]))
    const parts = raw.map(cell => cell.map(([x, z]): Pt => [x + shift, z + wanAt[1]]))
    const start = verts.length, built = new Set<string>()
    const at = (q: Pt) => { for (let v = start; v < verts.length; v++) if (gap(verts[v], q) < 0.6) return v; verts.push(q); cellsAt.push(new Set()); links.push(new Map()); return verts.length - 1 }
    for (const cell of parts) cell.forEach((q, k) => {
      const a = at(q), b = at(cell[(k + 1) % cell.length]), key = a < b ? `${a}:${b}` : `${b}:${a}`
      if (a === b || built.has(key)) return
      built.add(key)
      const d = gap(verts[a], verts[b])
      links[a].set(b, d); links[b].set(a, d)
      lanes.push({ points: [verts[a], verts[b]], kind: 'avenue' })
    })
    let entry = start
    for (let v = start; v < verts.length; v++) if (gap(verts[v], wanAt) < gap(verts[entry], wanAt)) entry = v
    lanes.push({ points: [wanAt, verts[entry]], kind: 'highway' })
    kinds.forEach((category, i) => {
      const cell = parts[i], inner = insetPolygon(cell, 2.6), ring = inner.length >= 3 ? inner : cell, id = `region:${category}`, list = bands[i]
      const sides = ring.map((p, k): [Pt, Pt] => [p, ring[(k + 1) % ring.length]]), around = sides.reduce((sum, side) => sum + gap(...side), 0)
      blocks.push(plate(id, categoryLabel(category), `${list.length} destination${list.length === 1 ? '' : 's'}`, insetPolygon(cell, 0.9)))
      const [cx, cz] = centroidOf(cell)
      add({ id, kind: 'region', label: categoryLabel(category), detail: `${list.length} destination${list.length === 1 ? '' : 's'}`, parentId: 'wan', networkId: null, health: 'ok',
        x: cx, y: 0, z: cz, size: Math.sqrt(Math.abs(shoelace(cell))), href: null, block: id }, ['internet'])
      // Destinations front the district's streets, spaced evenly around its edge, each with a short drive out.
      list.forEach((site, k) => {
        let d = (k + 0.5) / list.length * around, e = 0
        while (e < sides.length - 1 && d > gap(...sides[e])) { d -= gap(...sides[e]); e++ }
        const [a, b] = sides[e], t = Math.min(1, d / (gap(a, b) || 1)), p: Pt = [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t]
        let curb = p, best = Infinity, ends: [number, number] = [entry, entry]
        cell.forEach((c, j) => { const q = footOn(p, c, cell[(j + 1) % cell.length]); if (gap(p, q) < best) { best = gap(p, q); curb = q; ends = [at(c), at(cell[(j + 1) % cell.length])] } })
        const exit = gap(curb, verts[ends[0]]) <= gap(curb, verts[ends[1]]) ? ends[0] : ends[1]
        lanes.push({ points: [p, curb], kind: 'lane' })
        add({ id: site.id, kind: 'site', label: site.name, detail: site.org ?? site.domains[0] ?? null, parentId: 'wan', networkId: null, health: 'ok',
          x: p[0], y: 0, z: p[1], size: 1.9, height: 1.4 + Math.min(9, Math.log10(site.peak / 1000 + 1) * 3), angle: -Math.atan2(b[1] - a[1], b[0] - a[0]), block: id, href: null,
          tint: serviceColors[site.service], path: [p, curb, ...shortest(exit, entry).map(v => verts[v]), wanAt] }, [site.org, categoryLabel(category), 'internet', ...site.domains])
      })
    })
  }
  const roads: Road[] = [{ points: gatePath, kind: 'highway' }, ...lanes]
  for (const chain of chains.values()) for (let s = 1; s < chain.length; s++) {
    const a = chain[s - 1], b = chain[s]
    roads.push({ points: [verts[a], verts[b]], kind: 'avenue' })
  }

  const tethers: Tether[] = []
  const seen = new Set<string>()
  const tether = (from: string, to: string, kind: Tether['kind']) => {
    const key = `${from}>${to}`
    if (nodes.has(from) && nodes.has(to) && !seen.has(key)) { seen.add(key); tethers.push({ from, to, kind }) }
  }
  for (const app of map.apps) {
    for (const container of app.containers) for (const mount of container.mounts) if (mount.storageId) tether(mount.storageId, app.id, 'storage')
  }
  for (const item of storage) for (const mount of item.mounts) if (!tethers.some(line => line.from === item.id && nodes.get(line.to)?.parentId === mount.hostId)) tether(item.id, mount.hostId, 'storage')
  return { nodes, plinths, blocks, tethers, roads, outline, radius: Math.max(30, spread * 1.1), gatewayId, groups }
}

const portsOfApp = (app: MapApp) => {
  const out = new Map<string, Listen>()
  for (const container of app.containers) for (const port of container.ports) out.set(`${port.port}/${port.protocol}`, port)
  return [...out.values()].sort((a, b) => a.port - b.port)
}

function commonParent(members: { parentId: string | null }[], fallback: string) {
  const counts = new Map<string, number>()
  for (const member of members) if (member.parentId) counts.set(member.parentId, (counts.get(member.parentId) ?? 0) + 1)
  return [...counts].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))[0]?.[0] ?? fallback
}

// Street route between two nodes: up from each to their nearest shared ancestor.
export function roadPath(layout: Layout, from: string, to: string): Pt[] {
  const up = routeOf(layout, from), down = routeOf(layout, to), meet = up.find(id => down.includes(id))
  if (!meet) return []
  const climb = (chain: string[]) => chain.slice(0, chain.indexOf(meet)).flatMap(id => layout.nodes.get(id)?.path ?? [])
  const path: Pt[] = []
  for (const p of [...climb(up), ...climb(down).reverse()]) {
    const last = path[path.length - 1]
    if (last && last[0] === p[0] && last[1] === p[1]) continue
    const back = path[path.length - 2]
    if (back && back[0] === p[0] && back[1] === p[1]) { path.pop(); continue }
    path.push(p)
  }
  return path
}

// UniFi parents a client on its AP when it's on Wi-Fi, and on a switch port when it's wired.
export function wirelessVia(layout: Layout, id: string): string | null {
  const node = layout.nodes.get(id), parent = node?.parentId ? layout.nodes.get(node.parentId) : undefined
  return node && node.kind !== 'clients' && parent?.kind === 'ap' ? parent.id : null
}

export function routeOf(layout: Layout, id: string): string[] {
  const route: string[] = []
  let at: string | null = id
  while (at && layout.nodes.has(at) && !route.includes(at) && route.length < 32) {
    route.push(at)
    at = layout.nodes.get(at)!.parentId
  }
  if (route.length && route[route.length - 1] !== 'wan' && layout.nodes.has('wan')) {
    if (route[route.length - 1] !== layout.gatewayId) route.push(layout.gatewayId)
    route.push('wan')
  }
  return route
}

export interface SearchHit { id: string; label: string; detail: string }
export function searchLab(_map: LabMap, layout: Layout, query: string, limit = 8): SearchHit[] {
  const terms = query.toLowerCase().split(/\s+/).filter(Boolean)
  if (!terms.length) return []
  const hits: (SearchHit & { rank: number })[] = []
  for (const node of layout.nodes.values()) {
    if (!terms.every(term => node.search.includes(term))) continue
    hits.push({ id: node.id, label: node.label, detail: [kindLabels[node.kind], node.detail].filter(Boolean).join(' · '), rank: node.label.toLowerCase().startsWith(terms[0]) ? 0 : 1 })
  }
  return hits.sort((a, b) => a.rank - b.rank || a.label.localeCompare(b.label)).slice(0, limit).map(({ id, label, detail }) => ({ id, label, detail }))
}

export function formatBps(bps: number): string {
  if (bps < 1000) return bps < 1 ? 'Idle' : `${Math.round(bps)} b/s`
  const units = ['kb/s', 'Mb/s', 'Gb/s']
  let value = bps / 1000, unit = 0
  while (value >= 1000 && unit < units.length - 1) { value /= 1000; unit++ }
  return `${value >= 100 ? Math.round(value) : value.toFixed(1)} ${units[unit]}`
}

export function streaks(bps: number): { count: number; speed: number } {
  if (!(bps > 0)) return { count: 0, speed: 0 }
  const scale = Math.log10(bps / 1000 + 1)
  return { count: Math.min(10, Math.max(1, Math.round(scale * 2))), speed: 0.12 + scale * 0.08 }
}

export function ago(at: string | null, now: number): string {
  if (!at) return 'Never'
  const seconds = Math.max(0, Math.round((now - Date.parse(at)) / 1000))
  if (!Number.isFinite(seconds)) return 'Unknown'
  if (seconds < 60) return `${seconds} s ago`
  if (seconds < 3600) return `${Math.round(seconds / 60)} min ago`
  if (seconds < 86400) return `${Math.round(seconds / 3600)} h ago`
  return `${Math.round(seconds / 86400)} d ago`
}

export const effectiveHealth = (node: MapNode, live: LabMapLive | null): Health => live?.health[node.id] ?? node.health

export interface Fact { label: string; value: string }
const percent = (value: number | null) => value === null ? 'Not reported' : `${Math.round(value)}%`

export function factsFor(map: LabMap, layout: Layout, live: LabMapLive | null, id: string, now: number): Fact[] {
  const node = layout.nodes.get(id)
  if (!node) return []
  const facts: Fact[] = [{ label: 'State', value: healthLabels[effectiveHealth(node, live)] }]
  const rate = live?.rates[id]
  const traffic = () => { if (rate) facts.push({ label: 'Traffic', value: `${formatBps(rate.rxBps)} in · ${formatBps(rate.txBps)} out` }) }
  const networkName = (networkId: string | null) => {
    const network = networkId ? map.networks.find(item => item.id === networkId) : undefined
    return network ? `${network.name} (${vlanText(network)})` : 'Not matched to a network'
  }
  const placed = () => {
    if (node.category) facts.push({ label: 'Category', value: `${buildingCategoryLabel(node.category)}${node.categoryChosen ? '' : ' · guessed'}` })
    const quarter = node.quarter ? layout.nodes.get(node.quarter) : undefined
    if (quarter && quarter.id !== id) facts.push({ label: 'Quarter', value: quarter.label })
  }
  if (node.kind === 'wan') {
    facts.push({ label: 'Address', value: map.wan.address ?? 'Not reported' }, { label: 'Provider', value: map.wan.isp ?? 'Not reported' })
    traffic()
  } else if (node.kind === 'gateway' || node.kind === 'switch' || node.kind === 'ap') {
    const item = node.kind === 'gateway' ? map.gateway : map.devices.find(device => device.id === id)
    if (!item) facts.push({ label: 'UniFi', value: map.unifi.message ?? 'Connect UniFi to see your network hardware.' })
    else {
      facts.push({ label: 'Model', value: item.model ?? 'Not reported' }, { label: 'Address', value: item.address ?? 'Not reported' }, { label: 'MAC', value: item.mac || 'Not reported' },
        { label: 'Clients', value: String(item.clientCount) })
      if (node.kind === 'ap' && live) {
        const air = [...layout.nodes.values()].filter(other => wirelessVia(layout, other.id) === id).map(other => live.rates[other.id]).filter(Boolean)
        if (air.length) facts.push({ label: 'Wi-Fi traffic', value: `${formatBps(air.reduce((sum, item) => sum + item.rxBps, 0))} in · ${formatBps(air.reduce((sum, item) => sum + item.txBps, 0))} out` })
      }
      if (item.ports.length) facts.push({ label: 'Ports up', value: `${item.ports.filter(port => port.up).length} of ${item.ports.length}` })
      if (item.updateAvailable) facts.push({ label: 'Firmware', value: 'Update available' })
      traffic()
    }
  } else if (node.kind === 'spark' || node.kind === 'node') {
    const host = map.hosts.find(item => item.id === id), load = live?.load[id]
    facts.push({ label: 'Address', value: host?.address ?? 'Not reported' }, { label: 'Network', value: networkName(node.networkId) },
      { label: 'CPU', value: percent(load?.cpuPercent ?? host?.cpuPercent ?? null) }, { label: 'Memory', value: percent(load?.memoryPercent ?? host?.memoryPercent ?? null) })
    if (host?.gpu) facts.push({ label: 'GPU', value: host.gpu })
    facts.push({ label: 'Apps', value: String(map.apps.filter(app => app.hostId === id).length) })
    if (host?.updatesAvailable) facts.push({ label: 'Updates', value: `${host.updatesAvailable} available` })
    facts.push({ label: 'Last report', value: ago(host?.lastSeenAt ?? null, now) })
    placed()
    traffic()
  } else if (node.kind === 'app') {
    const app = map.apps.find(item => item.id === id)!, host = layout.nodes.get(app.hostId), load = live?.load[id]
    facts.push({ label: 'Server', value: host?.label ?? app.hostId }, { label: 'Wanted', value: app.desired }, { label: 'Address', value: app.address ?? 'Uses the server address' },
      { label: 'Network', value: networkName(node.networkId) }, { label: 'Containers', value: String(app.containers.length) })
    if (load) facts.push({ label: 'CPU', value: percent(load.cpuPercent) }, { label: 'Memory', value: percent(load.memoryPercent) })
    if (app.updateCount) facts.push({ label: 'Updates', value: `${app.updateCount} image${app.updateCount === 1 ? '' : 's'}` })
    placed()
    traffic()
  } else if (node.kind === 'storage') {
    const item = map.storage.find(storage => storage.id === id)!
    facts.push({ label: 'Address', value: item.address ?? 'Not reported' }, { label: 'Shares', value: item.shares.join(', ') || 'None reported' })
    for (const mount of item.mounts) facts.push({ label: layout.nodes.get(mount.hostId)?.label ?? mount.hostId, value: `${mount.share} · ${healthLabels[mount.health]}` })
    placed()
  } else if (node.kind === 'client') {
    const item = map.clientGroups.find(groupItem => groupItem.members.some(member => `client:${member.mac}` === id))
    const member = item?.members.find(entry => `client:${entry.mac}` === id)
    facts.push({ label: 'Network', value: networkName(node.networkId) }, { label: 'Group', value: item ? groupLabels[member?.group ?? item.group] : 'Unsorted' },
      { label: 'Address', value: member?.address ?? 'Not reported' }, { label: 'MAC', value: member?.mac ?? 'Not reported' }, { label: 'Vendor', value: member?.vendor ?? 'Not reported' },
      { label: 'Connected via', value: node.parentId ? `${layout.nodes.get(node.parentId)?.label ?? node.parentId}${wirelessVia(layout, id) ? ' · Wi-Fi' : ' · Wired'}` : 'Unknown' },
      { label: 'Last seen', value: member?.online ? 'Online now' : ago(member?.lastSeenAt ?? null, now) })
    placed()
    traffic()
  } else if (node.kind === 'clients') {
    const item = layout.groups.get(id)
    facts.push({ label: 'Network', value: networkName(node.networkId) }, { label: 'Online', value: item ? `${item.online} of ${item.total}` : 'Not reported' })
    placed()
    traffic()
  } else if (node.kind === 'site') {
    const dest = live?.destinations.find(item => item.id === id), region = node.block ? layout.nodes.get(node.block) : undefined
    facts.push({ label: 'District', value: region?.label ?? 'Elsewhere' })
    if (dest?.org ?? node.detail) facts.push({ label: 'Owner', value: dest?.org ?? node.detail! })
    if (dest?.asn) facts.push({ label: 'Network', value: dest.asn })
    if (dest?.domains.length) facts.push({ label: 'Domains', value: dest.domains.join(', ') })
    facts[0] = { label: 'Traffic', value: dest ? formatBps(dest.bps) : 'Quiet now' }
  } else if (node.kind === 'region') {
    const inside = [...layout.nodes.values()].filter(item => item.kind === 'site' && item.block === id)
    facts[0] = { label: 'Destinations', value: String(inside.length) }
    facts.push({ label: 'Traffic', value: formatBps(inside.reduce((sum, item) => sum + (live?.destinations.find(dest => dest.id === item.id)?.bps ?? 0), 0)) })
  } else {
    const inside = [...layout.nodes.values()].filter(item => item.quarter === id && item.kind !== 'network')
    const nets = [...new Set(inside.filter(item => item.kind === 'clients' || item.kind === 'spark' || item.kind === 'node').map(item => item.networkId))]
    facts.push({ label: 'Entrance', value: node.parentId ? layout.nodes.get(node.parentId)?.label ?? node.parentId : 'Not reported' },
      { label: 'Networks', value: nets.map(networkId => map.networks.find(item => item.id === networkId)?.name ?? 'Other addresses').join(', ') || 'None' })
    facts.push({ label: 'Servers', value: String(inside.filter(item => item.kind === 'spark' || item.kind === 'node').length) },
      { label: 'Clients online', value: String(inside.filter(item => item.kind === 'clients').reduce((sum, item) => sum + (item.count ?? 0), 0)) })
  }
  return facts
}

export function labCounts(map: LabMap, layout: Layout, live: LabMapLive | null) {
  const attention = [...layout.nodes.values()].filter(node => node.kind !== 'network' && ['warn', 'failed'].includes(effectiveHealth(node, live)))
  return {
    servers: map.hosts.length,
    apps: map.apps.length,
    running: map.apps.filter(app => (live?.health[app.id] ?? app.health) === 'ok').length,
    clients: map.clientGroups.reduce((sum, item) => sum + item.online, 0),
    attention: attention.sort((a, b) => (effectiveHealth(a, live) === 'failed' ? 0 : 1) - (effectiveHealth(b, live) === 'failed' ? 0 : 1)).map(node => node.id),
  }
}

export function assistantPrompt(layout: Layout, facts: Fact[], id: string): string {
  const node = layout.nodes.get(id)
  if (!node) return ''
  const route = routeOf(layout, id).map(step => layout.nodes.get(step)?.label ?? step).join(' → ')
  return `On my lab map I selected ${kindLabels[node.kind].toLowerCase()} "${node.label}". ${facts.map(fact => `${fact.label}: ${fact.value}`).join('; ')}. Route: ${route}. How is it doing, and is there anything I should look at?`
}
