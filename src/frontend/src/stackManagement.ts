export type StackTone = 'green' | 'amber' | 'failed' | 'muted' | 'accent'
export interface StackService { service: string; state: string; health: string | null; image: string | null; exitCode: number | null }
export interface NodeContainer { id: string; name: string; image: string; state: string; status: string | null; project: string | null; service: string | null; ports: string | null }
export interface NodeListener { protocol: 'tcp' | 'udp'; address: string; port: number; process: string | null; containerId: string | null }
export interface StackStatus { name: string; state: string; appliedRevision: number | null; message: string | null; services: StackService[] }
export interface StackPlacement { node: string | null; require: string[] }
export interface StackMove {
  from: string; to: string; startedAt: string; startedBy: string
  progress: { bytes: number; total: number | null } | null; target: StackStatus | null
}
/** A catalog app's place in a stack: Lucia renders the compose from these settings. */
export interface StackTemplateInfo { id: string; version: number; latest: number | null; name: string | null; settings: Record<string, string>; serverBound: boolean }
export interface StackSummary {
  name: string; node: string; nodeId: string | null; desired: 'Running' | 'Stopped'; revision: number
  createdAt: string; updatedAt: string; updatedBy: string; reportedAt: string | null
  status: StackStatus | null; containers: NodeContainer[]; placement: StackPlacement; move: StackMove | null
  template: StackTemplateInfo | null; restore: StackRestore | null; appAddress: AppAddress | null
}
/** The app's own address on the network. `status` is the server's last report, missing until it tries to take it. */
export interface AppAddress { ip: string; status: { state: 'Held' | 'InUse' | 'NoSubnet'; message: string | null } | null }
export interface StackRestore { id: string; snapshot: string; startedAt: string; startedBy: string }
export interface CatalogOption { value: string; label: string; help: string }
/** `when` is `id=value` or `id=one|other`: the field applies only while that setting has one of those values. Hidden fields are set by Lucia's own screens. */
export interface CatalogField {
  id: string; label: string; kind: 'port' | 'text' | 'gpus' | 'choice' | 'secret' | 'hidden'; default: string | null; help: string | null
  options: CatalogOption[]; when: string | null; optional: boolean
}
export interface CatalogGpu { uuid: string; model: string; memoryBytes: number | null; unsupported: string | null }
export interface CatalogServer { nodeId: string; hostname: string; unmet: string | null; reason: string | null; gpus: CatalogGpu[] | null }
export interface CatalogApp {
  id: string; version: number; name: string; summary: string; needs: string; require: string[]; fields: CatalogField[]
  serverBound: boolean; usesAddress: boolean; servers: CatalogServer[]
}
/** A web address: its internal URL and, while public access is on, the public one its public name answers at. */
export interface StackWebAddress { host: string; url: string | null; public: string | null; publicUrl: string | null }
export interface StackDetail {
  stack: StackSummary; compose: string; env: string; placement: StackPlacement; address: string | null; urls: Record<string, string>
  routes: StackWebAddress[]; zone: string | null; publicAccess: boolean
}
export interface NodeInventory { reportedAt: string; containers: NodeContainer[]; listeners: NodeListener[] }

export const stackNamePattern = /^[a-z](?:[a-z0-9-]{0,38}[a-z0-9])?$/
export const logLineChoices = [200, 1000, 5000] as const

const invalid = () => new Error('Lucia returned app information it could not read.')
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw invalid()
  return value as Record<string, unknown>
}
function text(value: unknown): string { if (typeof value !== 'string') throw invalid(); return value }
function optional(value: unknown): string | null { return value === null || value === undefined ? null : text(value) }
function integer(value: unknown): number { if (typeof value !== 'number' || !Number.isSafeInteger(value)) throw invalid(); return value }
function list<T>(value: unknown, parse: (item: unknown) => T, limit = 1024): T[] {
  if (!Array.isArray(value) || value.length > limit) throw invalid()
  return value.map(parse)
}
function timestamp(value: unknown): string { const result = text(value); if (Number.isNaN(Date.parse(result))) throw invalid(); return result }

export function parseContainer(value: unknown): NodeContainer {
  const item = object(value)
  return { id: text(item.id), name: text(item.name), image: text(item.image), state: text(item.state), status: optional(item.status),
    project: optional(item.project), service: optional(item.service), ports: optional(item.ports) }
}

function absent(value: unknown): boolean { return value === null || value === undefined }

function parseStatus(value: unknown): StackStatus {
  const entry = object(value)
  return { name: text(entry.name), state: text(entry.state),
    appliedRevision: absent(entry.appliedRevision) ? null : integer(entry.appliedRevision),
    message: optional(entry.message),
    services: list(entry.services, service => {
      const row = object(service)
      return { service: text(row.service), state: text(row.state), health: optional(row.health), image: optional(row.image),
        exitCode: absent(row.exitCode) ? null : integer(row.exitCode) }
    }, 64) }
}

function parsePlacement(value: unknown): StackPlacement {
  const item = absent(value) ? {} : object(value)
  return { node: optional(item.node), require: absent(item.require) ? [] : list(item.require, text, 16) }
}

function parseMove(value: unknown): StackMove | null {
  if (absent(value)) return null
  const item = object(value)
  const progress = absent(item.progress) ? null : object(item.progress)
  return { from: text(item.from), to: text(item.to), startedAt: timestamp(item.startedAt), startedBy: text(item.startedBy),
    progress: progress && { bytes: integer(progress.bytes), total: absent(progress.total) ? null : integer(progress.total) },
    target: absent(item.target) ? null : parseStatus(item.target) }
}

export function parseStackSummary(value: unknown): StackSummary {
  const item = object(value)
  const name = text(item.name)
  if (!stackNamePattern.test(name) || (item.desired !== 'Running' && item.desired !== 'Stopped')) throw invalid()
  return { name, node: text(item.node), nodeId: optional(item.nodeId), desired: item.desired, revision: integer(item.revision),
    createdAt: timestamp(item.createdAt), updatedAt: timestamp(item.updatedAt), updatedBy: text(item.updatedBy),
    reportedAt: absent(item.reportedAt) ? null : timestamp(item.reportedAt),
    status: absent(item.status) ? null : parseStatus(item.status),
    containers: absent(item.containers) ? [] : list(item.containers, parseContainer, 256),
    placement: parsePlacement(item.placement), move: parseMove(item.move), template: parseTemplate(item.template),
    restore: parseRestore(item.restore), appAddress: parseAppAddress(item.appAddress) }
}

function parseAppAddress(value: unknown): AppAddress | null {
  if (absent(value)) return null
  const item = object(value)
  const status = absent(item.status) ? null : object(item.status)
  if (status && status.state !== 'Held' && status.state !== 'InUse' && status.state !== 'NoSubnet') throw invalid()
  return { ip: text(item.ip), status: status && { state: status.state as 'Held' | 'InUse' | 'NoSubnet', message: optional(status.message) } }
}

const quad = /^(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)$/
/** Mirrors the server: a private IPv4 address that isn't a subnet's .0 or .255. */
export function appAddressProblem(value: string): string | null {
  const address = value.trim()
  if (!address) return 'Enter an address, such as 192.168.1.53.'
  const [a, b, , d] = address.split('.').map(Number)
  if (!quad.test(address) || !(a === 10 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168)) || d === 0 || d === 255)
    return 'Use a private IPv4 address on your network, such as 192.168.1.53.'
  return null
}

/** How the app's address stands, for its page. */
export function appAddressState(stack: StackSummary): { label: string; tone: StackTone; detail: string } | null {
  const address = stack.appAddress
  if (!address) return null
  if (stack.move) return { label: 'Released', tone: 'muted', detail: `Given up while the app moves. ${stack.move.to} takes it when the move finishes.` }
  if (stack.desired === 'Stopped') return { label: 'Not taken', tone: 'muted', detail: `${stack.node} takes it when the app starts.` }
  if (!address.status) return { label: 'Waiting', tone: 'amber', detail: `${stack.node} takes it on its next check-in.` }
  if (address.status.state === 'Held') return { label: 'Active', tone: 'green', detail: `${stack.node} answers for it.` }
  return { label: address.status.state === 'InUse' ? 'In use' : 'Wrong network', tone: 'failed',
    detail: `${address.status.message ?? ''} The app starts once ${stack.node} can take it.`.trim() }
}

function parseRestore(value: unknown): StackRestore | null {
  if (absent(value)) return null
  const item = object(value)
  return { id: text(item.id), snapshot: text(item.snapshot), startedAt: timestamp(item.startedAt), startedBy: text(item.startedBy) }
}

function settingsOf(value: unknown): Record<string, string> {
  if (absent(value)) return {}
  return Object.fromEntries(Object.entries(object(value)).map(([key, entry]) => [key, text(entry)]))
}

function parseTemplate(value: unknown): StackTemplateInfo | null {
  if (absent(value)) return null
  const item = object(value)
  return { id: text(item.id), version: integer(item.version), latest: absent(item.latest) ? null : integer(item.latest),
    name: optional(item.name), settings: settingsOf(item.settings), serverBound: item.serverBound === true }
}

export function parseCatalog(value: unknown): CatalogApp[] {
  return list(object(value).apps, entry => {
    const app = object(entry)
    return { id: text(app.id), version: integer(app.version), name: text(app.name), summary: text(app.summary), needs: text(app.needs),
      require: list(app.require, text, 16), serverBound: app.serverBound === true, usesAddress: app.usesAddress === true,
      fields: list(app.fields, field => {
        const row = object(field)
        if (row.kind !== 'port' && row.kind !== 'text' && row.kind !== 'gpus' && row.kind !== 'choice' && row.kind !== 'secret' && row.kind !== 'hidden') throw invalid()
        const options = absent(row.options) ? [] : list(row.options, option => {
          const item = object(option)
          return { value: text(item.value), label: text(item.label), help: text(item.help) }
        }, 16)
        if (row.kind === 'choice' && options.length === 0) throw invalid()
        return { id: text(row.id), label: text(row.label), kind: row.kind, default: optional(row.default), help: optional(row.help),
          options, when: optional(row.when), optional: row.optional === true }
      }, 32),
      servers: list(app.servers, server => {
        const row = object(server)
        return { nodeId: text(row.nodeId), hostname: text(row.hostname), unmet: optional(row.unmet), reason: optional(row.reason),
          gpus: absent(row.gpus) ? null : list(row.gpus, gpu => {
            const card = object(gpu)
            return { uuid: text(card.uuid), model: text(card.model), memoryBytes: absent(card.memoryBytes) ? null : integer(card.memoryBytes),
              unsupported: optional(card.unsupported) }
          }, 16) }
      }) }
  }, 64)
}

/** Why a server can't run a catalog app, in words, or null when it can. */
export function catalogReason(server: CatalogServer): string | null {
  if (server.reason || !server.unmet) return server.reason
  const words = describeUnmet(server.unmet)
  return `${words.charAt(0).toUpperCase()}${words.slice(1)}.`
}

/** The environment key a secret setting is saved under. */
export const secretKey = (id: string) => id.toUpperCase().replace(/-/g, '_')

/** The settings a server starts with: field defaults, and every GPU that can run the app. */
export function catalogDefaults(app: CatalogApp, server: CatalogServer | undefined): Record<string, string> {
  return Object.fromEntries(app.fields.map(field => [field.id, field.kind === 'gpus'
    ? (server?.gpus ?? []).filter(gpu => !gpu.unsupported).map(gpu => gpu.uuid).join(',') : field.default ?? '']))
}

/** Whether a field applies to these settings: it isn't hidden, and its `when` condition holds. */
export function fieldShown(field: CatalogField, values: Record<string, string>): boolean {
  if (field.kind === 'hidden') return false
  if (!field.when) return true
  const [id, wanted] = field.when.split('=')
  return wanted.split('|').includes(values[id])
}

/** Mirrors the server's StackCatalog.Settings checks, so mistakes show before saving. */
export function settingsProblem(app: CatalogApp, values: Record<string, string>): string | null {
  for (const field of app.fields) {
    const value = (values[field.id] ?? field.default ?? '').trim()
    if (field.optional && !value) continue
    if (field.kind === 'choice' && !field.options.some(option => option.value === value)) return `Choose the ${field.label.toLowerCase()}.`
    if (field.kind === 'port' && !(/^\d{1,5}$/.test(value) && Number(value) >= 1 && Number(value) <= 65535)) return `${field.label} must be a port from 1 to 65535.`
    if (field.kind === 'gpus' && !value) return 'Choose at least one GPU.'
    if (field.kind === 'text' && value.length > 128) return `${field.label} can be up to 128 characters.`
    if (field.kind === 'secret' && !/^[A-Za-z0-9._~+/=-]{0,128}$/.test(value)) return `${field.label} can be up to 128 letters, digits and . _ ~ + / = -`
  }
  return null
}

/** A free name for a new install: the app's id, then id-2, id-3… */
export function freeName(id: string, taken: string[]): string {
  if (!taken.includes(id)) return id
  for (let n = 2; ; n++) if (!taken.includes(`${id}-${n}`)) return `${id}-${n}`
}

export function parseStackList(value: unknown): StackSummary[] {
  return list(object(value).stacks, parseStackSummary, 64)
}

export function parseStackDetail(value: unknown): StackDetail {
  const item = object(value)
  const urls = item.urls == null ? {} : Object.fromEntries(Object.entries(object(item.urls)).slice(0, 8).map(([host, url]) => {
    const address = text(url)
    if (!address.startsWith('https://')) throw invalid()
    return [host, address]
  }))
  const publicUrls = item.publicUrls == null ? {} : object(item.publicUrls)
  const routes = absent(object(item.manifest).routes) ? [] : list(object(item.manifest).routes, value => {
    const route = object(value)
    const host = text(route.host)
    return { host, url: urls[host] ?? null, public: optional(route.public), publicUrl: optional(publicUrls[host]) }
  }, 8)
  return { stack: parseStackSummary(item.stack), compose: text(item.compose), env: text(item.env),
    placement: parsePlacement(object(item.manifest).placement), address: optional(item.address), urls,
    routes, zone: optional(item.zone), publicAccess: item.publicAccess === true }
}

/** One value from a .env file, or null. */
export function envValue(env: string, key: string): string | null {
  const line = env.split('\n').find(entry => entry.startsWith(`${key}=`))
  return line ? line.slice(key.length + 1).trim() : null
}

export function parseInventory(value: unknown): NodeInventory {
  const item = object(value)
  return { reportedAt: timestamp(item.reportedAt), containers: list(item.containers, parseContainer, 256),
    listeners: list(item.listeners, entry => {
      const row = object(entry)
      if (row.protocol !== 'tcp' && row.protocol !== 'udp') throw invalid()
      return { protocol: row.protocol, address: text(row.address), port: integer(row.port), process: optional(row.process), containerId: optional(row.containerId) }
    }) }
}

export interface PortRow { port: number; protocol: 'tcp' | 'udp'; addresses: string[]; owner: PortOwner }
export interface PortOwner { label: string; detail: string; app: string | null; known: boolean }

const systemPrograms: Record<string, [string, string]> = {
  'sshd': ['SSH', 'Remote sign-in to this server.'],
  'sshd-session': ['SSH', 'Remote sign-in to this server.'],
  'rpcbind': ['NFS port mapper', 'Lets this server use NFS shares, such as your NAS. Normal when network storage is connected.'],
  'rpc.statd': ['NFS status monitor', 'Part of NFS. Keeps file locks consistent across restarts.'],
  'rpc.mountd': ['NFS mounts', 'Part of an NFS server.'],
  'kernel': ['Linux kernel', 'Opened by the system itself, usually for NFS file locking or sharing.'],
  'dhclient': ['DHCP client', 'How this server gets its network address from your router.'],
  'dhcpcd': ['DHCP client', 'How this server gets its network address from your router.'],
  'systemd-network': ['DHCP client', 'How this server gets its network address from your router.'],
  'NetworkManager': ['DHCP client', 'How this server gets its network address from your router.'],
  'avahi-daemon': ['Local discovery (mDNS)', 'Announces this server by name on your local network.'],
  'systemd-resolve': ['DNS cache', 'Looks up names for programs on this server.'],
  'chronyd': ['Time sync', 'Keeps this server\'s clock correct.'],
  'ntpd': ['Time sync', 'Keeps this server\'s clock correct.'],
  'systemd-timesyn': ['Time sync', 'Keeps this server\'s clock correct.'],
  'lucia-node-agent': ['Lucia agent', 'Lucia\'s management service on this server.'],
  'dockerd': ['Docker', 'The container engine.'],
  'containerd': ['Docker', 'The container runtime.'],
  'docker-proxy': ['Docker published port', 'Forwards this port to a container.'],
  'cupsd': ['Printing', 'The print service.'],
  'cups-browsed': ['Printer discovery', 'Finds printers on your network.'],
  'smbd': ['Windows file sharing', 'Shares folders with Windows and macOS (SMB).'],
  'nmbd': ['Windows file sharing', 'Announces SMB shares by name.'],
  'exim4': ['Mail', 'Sends system mail.'],
  'snmpd': ['SNMP', 'Lets monitoring tools read this server\'s status.'],
}

function appOf(container: NodeContainer): string | null {
  return container.project?.startsWith('lucia-') ? container.project.slice(6) : null
}

/** Who opened a port, in words an owner who didn't set it up can follow. */
export function portOwner(listener: NodeListener, containers: NodeContainer[]): PortOwner {
  const published = new RegExp(`:${listener.port}->\\d+/${listener.protocol}\\b`)
  const container = containers.find(item => listener.containerId !== null && item.id === listener.containerId)
    ?? containers.find(item => item.ports !== null && published.test(item.ports))
  if (container) {
    const app = appOf(container)
    return { label: container.service ?? container.name, known: true, app,
      detail: app ? `Container in the app ${app}.` : `Container ${container.name}, not started by Lucia.` }
  }
  const program = listener.process === 'systemd' && listener.port === 111 ? systemPrograms.rpcbind
    : listener.process ? systemPrograms[listener.process] : undefined
  if (program) return { label: program[0], detail: program[1], app: null, known: true }
  if (listener.process) return { label: listener.process, detail: 'A program running on this server, outside any container.', app: null, known: false }
  return { label: 'Unknown', detail: 'The server didn\'t say which program opened this port.', app: null, known: false }
}

/** One row per port, protocol and owner; IPv4 and IPv6 sockets for the same thing are merged. */
export function portRows(inventory: NodeInventory): PortRow[] {
  const rows = new Map<string, PortRow>()
  for (const listener of inventory.listeners) {
    const owner = portOwner(listener, inventory.containers)
    const key = `${listener.protocol}/${listener.port}/${owner.label}`
    const row = rows.get(key)
    if (row) { if (!row.addresses.includes(listener.address)) row.addresses.push(listener.address) }
    else rows.set(key, { port: listener.port, protocol: listener.protocol, addresses: [listener.address], owner })
  }
  return [...rows.values()].sort((a, b) => a.port - b.port || a.protocol.localeCompare(b.protocol))
}

/** The plain-language state an owner sees for an app. */
export function stackState(stack: StackSummary): { label: string; tone: StackTone; detail: string } {
  if (stack.move) return moveState(stack.move, stack.status)
  if (stack.restore) return stack.status?.state === 'Failed'
    ? { label: 'Restore stalled', tone: 'failed', detail: `${stack.status.message ?? 'The restore failed.'} Lucia retries every 2 minutes, or cancel the restore.` }
    : { label: 'Restoring', tone: 'accent', detail: stack.status?.state === 'Restoring'
      ? `${stack.node} is copying the snapshot back and will start the app on it.`
      : `${stack.node} starts the restore at its next check-in.` }
  const services = stack.status?.services ?? []
  const running = services.filter(service => service.state === 'running' && service.health !== 'unhealthy').length
  const count = services.length === 1 ? '1 container' : `${services.length} containers`
  if (!stack.nodeId) return { label: 'Server not found', tone: 'amber', detail: `No managed server is named ${stack.node}. Choose another server.` }
  if (!stack.status) return stack.reportedAt
    ? { label: 'Waiting for server', tone: 'muted', detail: `${stack.node} will pick this up at its next check-in.` }
    : { label: 'Server not reporting', tone: 'amber', detail: `${stack.node} hasn't checked in recently. It may be offline or need an agent update.` }
  const state = stack.status.state
  // The agent reports waits on a NAS share or the app's address as failures; they clear themselves once the thing arrives.
  if (state === 'Failed' && stack.status.message?.startsWith('Waiting for ')) return { label: 'Waiting', tone: 'amber', detail: stack.status.message }
  if (state === 'Failed') return { label: 'Failed', tone: 'failed', detail: stack.status.message ?? 'The server could not apply this app.' }
  if (state === 'Removing') return { label: 'Removing', tone: 'muted', detail: 'Taking the containers down.' }
  if (state === 'Applying' || state === 'Pending' || stack.status.appliedRevision !== stack.revision)
    return { label: 'Applying changes', tone: 'accent', detail: `${stack.node} is pulling images and starting containers.` }
  if (stack.desired === 'Stopped') return running > 0
    ? { label: 'Stopping', tone: 'accent', detail: `${stack.node} is taking the containers down.` }
    : { label: 'Stopped', tone: 'muted', detail: 'Its containers are down. Data is kept.' }
  if (state === 'Stopped') return { label: 'Starting', tone: 'accent', detail: `${stack.node} is starting the containers.` }
  if (state === 'Running') return { label: 'Running', tone: 'green', detail: `${running === 1 ? '1 container' : `${running} containers`} running on ${stack.node}.` }
  return { label: 'Needs attention', tone: 'amber',
    detail: services.length === 0 ? 'No containers are running.' : `${running} of ${count} running. Check the logs.` }
}

export function containerState(container: { state: string; status: string | null }): { label: string; tone: StackTone } {
  const status = container.status ?? ''
  if (container.state === 'running' && status.includes('(unhealthy)')) return { label: 'Unhealthy', tone: 'failed' }
  if (container.state === 'running' && status.includes('(health: starting)')) return { label: 'Starting', tone: 'accent' }
  if (container.state === 'running') return { label: 'Running', tone: 'green' }
  if (container.state === 'restarting') return { label: 'Restarting', tone: 'amber' }
  if (container.state === 'exited' || container.state === 'dead') {
    const code = /^Exited \((-?\d+)\)/.exec(status)?.[1]
    return { label: code && code !== '0' ? `Exited (${code})` : 'Stopped', tone: code && code !== '0' ? 'failed' : 'muted' }
  }
  return { label: container.state.charAt(0).toUpperCase() + container.state.slice(1), tone: 'muted' }
}

export function validateStackDraft(name: string, node: string | null, compose: string): string | null {
  if (!stackNamePattern.test(name)) return 'Use up to 40 lowercase letters, digits and hyphens, starting with a letter.'
  if (node === '') return 'Choose the server this app runs on.'
  if (!compose.trim()) return 'Paste a Docker Compose file.'
  if (new TextEncoder().encode(compose).length > 128 * 1024) return 'The compose file is larger than 128 KiB.'
  return null
}

export function formatBytes(bytes: number): string {
  const units = ['bytes', 'KB', 'MB', 'GB', 'TB']
  let value = bytes, unit = 0
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++ }
  return unit === 0 ? `${bytes} bytes` : `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`
}

/** Where a move is, from the two servers' reports and how much data has crossed. */
export function moveState(move: StackMove, source: StackStatus | null = null): { label: string; tone: StackTone; detail: string; percent: number | null } {
  const target = move.target
  const percent = move.progress?.total ? Math.min(99, Math.floor(move.progress.bytes * 100 / move.progress.total)) : null
  if (target?.state === 'Failed') return { label: 'Move stalled', tone: 'failed', percent,
    detail: `${move.to} couldn't take the app: ${target.message ?? 'no reason given'}. Lucia retries every 2 minutes, or cancel to keep it on ${move.from}.` }
  if (source?.state === 'Failed') return { label: 'Move stalled', tone: 'failed', percent,
    detail: `${move.from} couldn't send the app: ${source.message ?? 'no reason given'}. Lucia retries every 2 minutes, or cancel to keep it on ${move.from}.` }
  if (move.progress && move.progress.bytes > 0) return { label: 'Moving', tone: 'accent', percent,
    detail: `Copying data to ${move.to}: ${formatBytes(move.progress.bytes)}${move.progress.total ? ` of about ${formatBytes(move.progress.total)}` : ''}.` }
  if (source?.state === 'Sending' || target?.state === 'Receiving') return { label: 'Moving', tone: 'accent', percent,
    detail: `${move.from} has stopped the app and is connecting to ${move.to}.` }
  return { label: 'Moving', tone: 'accent', percent, detail: `Stopping the app on ${move.from} before copying its data to ${move.to}.` }
}

export interface RequirementFacts {
  memoryTotalBytes: number
  runtime: { state: string; gpuContainers: boolean; gpus: { vendor: string; model: string; memoryBytes: number | null; computeCapability: string | null }[] } | null
}

const requirementPattern = /^(gpu(?:\.(?:vendor|model|vram|compute))?|memory|cuda|nas)(?:\s*(>=|<=|!=|=|~)\s*(.+))?$/
const sizePattern = /^(\d{1,6}(?:\.\d{1,3})?)\s*(M|MB|G|GB|T|TB)?$/i

function sizeMatches(actual: number, op: string, value: string): boolean {
  const match = sizePattern.exec(value)
  if (!match) return false
  const unit = (match[2] ?? 'G').toUpperCase()
  const bytes = Number(match[1]) * (unit.startsWith('M') ? 2 ** 20 : unit.startsWith('T') ? 2 ** 40 : 2 ** 30)
  return op === '>=' ? actual >= bytes * 0.95 : actual <= bytes
}

/** The first requirement a server doesn't meet, or null. Mirrors the server's check so the editor can preview eligibility. */
export function unmetRequirement(requirements: string[], facts: RequirementFacts | null,
  settings: { gpu: { cudaLine: number | null }; gpuWarning: string | null } | null = null): string | null {
  if (requirements.length === 0) return null
  if (!facts) return requirements[0]
  const parsed = requirements.map(text => { const match = requirementPattern.exec(text.trim()); return { text, key: match?.[1] ?? '', op: match?.[2] ?? '', value: match?.[3]?.trim() ?? '' } })
  for (const item of parsed) if (item.key === 'memory' && !sizeMatches(facts.memoryTotalBytes, item.op, item.value)) return item.text
  for (const item of parsed) if (item.key === 'cuda' && (settings?.gpu.cudaLine === null || settings?.gpu.cudaLine === undefined
    || String(settings.gpu.cudaLine) !== item.value || settings.gpuWarning)) return item.text
  const gpu = parsed.filter(item => item.key.startsWith('gpu'))
  if (gpu.length === 0) return null
  if (!facts.runtime?.gpuContainers) return gpu[0].text
  let closest: string | null = null
  for (const card of facts.runtime.gpus) {
    const miss = gpu.find(item => {
      const textMatch = (actual: string) => item.op === '=' ? actual.toLowerCase() === item.value.toLowerCase()
        : item.op === '!=' ? actual.toLowerCase() !== item.value.toLowerCase() : actual.toLowerCase().includes(item.value.toLowerCase())
      switch (item.key) {
        case 'gpu': return false
        case 'gpu.vendor': return !textMatch(card.vendor)
        case 'gpu.model': return !textMatch(card.model)
        case 'gpu.vram': return card.memoryBytes === null || !sizeMatches(card.memoryBytes, item.op, item.value)
        case 'gpu.compute': {
          const actual = Number(card.computeCapability), wanted = Number(item.value)
          return card.computeCapability === null || Number.isNaN(actual)
            || !(item.op === '>=' ? actual >= wanted : item.op === '<=' ? actual <= wanted : Math.abs(actual - wanted) < 1e-9)
        }
        default: return true
      }
    })
    if (!miss) return null
    closest ??= miss.text
  }
  return closest ?? gpu[0].text
}

const vendorNames: Record<string, string> = { nvidia: 'NVIDIA', amd: 'AMD', intel: 'Intel' }

/** A requirement a server misses, as a reason an owner can read. */
export function describeUnmet(requirement: string): string {
  const match = requirementPattern.exec(requirement.trim())
  const [key, op, value] = [match?.[1], match?.[2], match?.[3]?.trim() ?? '']
  const size = value.replace(/\s*(G|GB)$/i, ' GB').replace(/\s*(M|MB)$/i, ' MB').replace(/\s*(T|TB)$/i, ' TB').replace(/^(\d+(?:\.\d+)?)$/, '$1 GB')
  if (key === 'gpu') return 'no GPU that containers can use'
  if (key === 'gpu.vendor') return op === '!=' ? `only ${vendorNames[value.toLowerCase()] ?? value} GPUs` : `no ${vendorNames[value.toLowerCase()] ?? value} GPU`
  if (key === 'gpu.model') return `no GPU matching “${value}”`
  if (key === 'gpu.vram') return op === '>=' ? `no GPU with ${size} of memory` : `every GPU has more than ${size}`
  if (key === 'gpu.compute') return `no GPU with compute capability ${op === '>=' ? `${value} or newer` : op === '<=' ? `${value} or older` : value}`
  if (key === 'memory') return op === '>=' ? `less than ${size} of memory` : `more than ${size} of memory`
  if (key === 'cuda') return `not set to CUDA ${value}`
  if (key === 'nas') return `the NAS share ${value} isn't mounted`
  return `doesn't meet ${requirement}`
}

export interface RequirementForm { gpu: boolean; vendor: string; model: string; vram: string; compute: string; cuda: '' | '12' | '13'; memory: string; other: string[] }

/** Splits requirements into the editor's fields; anything the fields can't show is kept as-is. */
export function requirementForm(requirements: string[]): RequirementForm {
  const form: RequirementForm = { gpu: false, vendor: '', model: '', vram: '', compute: '', cuda: '', memory: '', other: [] }
  for (const requirement of requirements) {
    const match = requirementPattern.exec(requirement.trim())
    const [key, op, value] = [match?.[1], match?.[2], match?.[3]?.trim() ?? '']
    const gigabytes = /^(\d{1,6}(?:\.\d{1,3})?)\s*(?:G|GB)?$/i.exec(value)?.[1]
    if (key === 'gpu' && !op) form.gpu = true
    else if (key === 'gpu.vendor' && op === '=' && !form.vendor) { form.gpu = true; form.vendor = value.toLowerCase() }
    else if (key === 'gpu.model' && op === '~' && !form.model) { form.gpu = true; form.model = value }
    else if (key === 'gpu.vram' && op === '>=' && gigabytes && !form.vram) { form.gpu = true; form.vram = gigabytes }
    else if (key === 'gpu.compute' && op === '>=' && !form.compute) { form.gpu = true; form.compute = value }
    else if (key === 'cuda' && op === '=' && (value === '12' || value === '13') && !form.cuda) { form.gpu = true; form.cuda = value }
    else if (key === 'memory' && op === '>=' && gigabytes && !form.memory) form.memory = gigabytes
    // Lucia derives these from the compose file's /mnt/lucia/nas paths each time it's saved.
    else if (key === 'nas') continue
    else form.other.push(requirement)
  }
  return form
}

export function requirementList(form: RequirementForm): string[] {
  const list: string[] = []
  if (form.gpu) {
    list.push('gpu')
    if (form.vendor) list.push(`gpu.vendor=${form.vendor}`)
    if (form.model.trim()) list.push(`gpu.model~${form.model.trim()}`)
    if (form.vram.trim()) list.push(`gpu.vram>=${form.vram.trim()}G`)
    if (form.compute.trim()) list.push(`gpu.compute>=${form.compute.trim()}`)
    if (form.cuda) list.push(`cuda=${form.cuda}`)
  }
  if (form.memory.trim()) list.push(`memory>=${form.memory.trim()}G`)
  return [...list, ...form.other.filter(item => form.gpu || !item.startsWith('gpu') && !item.startsWith('cuda'))]
}

export const composeExample = `services:
  app:
    image: nginx:alpine
    restart: unless-stopped
    ports:
      - "8080:80"
    env_file: .env
    volumes:
      - data:/usr/share/nginx/html
volumes:
  data:
`

export type NasKind = 'nfs' | 'smb'
export type MountState = 'Mounted' | 'Pending' | 'Failed'
export interface NasMount { node: string; state: MountState | null; message: string | null }
export interface NasShare { name: string; path: string; mountPath: string; usedBy: string[]; mounts: NasMount[] }
export interface NasServer { id: string; kind: NasKind; host: string; username: string | null; hasPassword: boolean; updatedAt: string; updatedBy: string; shares: NasShare[] }
export interface NasShareDraft { name: string; path: string }
export interface NasDraft { id: string; kind: NasKind; host: string; username: string; password: string; shares: NasShareDraft[] }

export const nasIdPattern = /^[a-z](?:[a-z0-9-]{0,30}[a-z0-9])?$/
export const shareNamePattern = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/
const exportPattern = /^\/(?:[A-Za-z0-9._-]+\/?){0,16}$/
const smbSharePattern = /^[A-Za-z0-9._$-]{1,80}$/
const hostPattern = /^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$/

function parseNasMount(value: unknown): NasMount {
  const item = object(value)
  if (absent(item.status)) return { node: text(item.node), state: null, message: null }
  const status = object(item.status)
  const state = text(status.state)
  if (state !== 'Mounted' && state !== 'Pending' && state !== 'Failed') throw invalid()
  return { node: text(item.node), state, message: optional(status.message) }
}

export function parseNasList(value: unknown): NasServer[] {
  return list(object(value).servers, server => {
    const item = object(server)
    const kind = text(item.kind)
    if (kind !== 'nfs' && kind !== 'smb' || typeof item.hasPassword !== 'boolean') throw invalid()
    return {
      id: text(item.id), kind, host: text(item.host), username: optional(item.username), hasPassword: item.hasPassword,
      updatedAt: timestamp(item.updatedAt), updatedBy: text(item.updatedBy),
      shares: list(item.shares, share => {
        const entry = object(share)
        return { name: text(entry.name), path: text(entry.path), mountPath: text(entry.mountPath), usedBy: list(entry.usedBy, text), mounts: list(entry.mounts, parseNasMount) }
      }, 64),
    }
  }, 32)
}

/** How a share is doing across every managed server. */
export function shareState(share: NasShare): { label: string; tone: StackTone } {
  const count = (state: MountState | null) => share.mounts.filter(mount => mount.state === state).length
  const servers = (n: number) => n === 1 ? '1 server' : `${n} servers`
  if (share.mounts.length === 0) return { label: 'No servers yet', tone: 'muted' }
  if (count('Failed')) return { label: count('Failed') === share.mounts.length ? "Can't mount" : `Can't mount on ${servers(count('Failed'))}`, tone: 'amber' }
  if (count('Mounted') === share.mounts.length) return { label: share.mounts.length === 1 ? 'Mounted' : `Mounted on all ${share.mounts.length}`, tone: 'green' }
  if (count('Pending')) return { label: 'Mounting', tone: 'accent' }
  return { label: `Mounted on ${count('Mounted')} of ${servers(share.mounts.length)}`, tone: 'muted' }
}

export function mountLabel(mount: NasMount): string {
  return mount.state === 'Mounted' ? 'Mounted' : mount.state === 'Pending' ? 'Mounting' : mount.state === 'Failed' ? "Can't mount" : 'Not reported'
}

/** A folder name for a share from its path: the last segment, cleaned up. */
export function shareNameFrom(path: string): string {
  const last = path.trim().split('/').filter(Boolean).pop() ?? ''
  return last.replace(/[^A-Za-z0-9._-]/g, '').replace(/^[._-]+/, '').slice(0, 64)
}

export function nasDraftProblem(draft: NasDraft, saved: NasServer | null): string | null {
  if (!nasIdPattern.test(draft.id)) return 'Name the NAS with lowercase letters, digits and hyphens, starting with a letter.'
  if (!hostPattern.test(draft.host.trim())) return "Enter the NAS's IP address or hostname."
  if (draft.kind === 'smb' && !/^[A-Za-z0-9._@-]{1,64}$/.test(draft.username.trim())) return 'Enter the SMB username.'
  if (draft.kind === 'smb' && !draft.password && !(saved?.kind === 'smb' && saved.hasPassword)) return 'Enter the SMB password.'
  if (draft.shares.length === 0) return 'Add at least one share.'
  for (const share of draft.shares) {
    const path = share.path.trim()
    if (draft.kind === 'nfs' ? !exportPattern.test(path) || path.split('/').some(part => part === '.' || part === '..') : !smbSharePattern.test(path))
      return draft.kind === 'nfs' ? `“${path || 'Blank'}” isn't an export path. Write it as the NAS shows it, such as /volume1/media.`
        : `“${path || 'Blank'}” isn't a share name. Use the name alone, such as Media.`
    if (!shareNamePattern.test(share.name.trim())) return `Give the share at ${path} a folder name of letters, digits, dots, dashes or underscores.`
  }
  const names = draft.shares.map(share => share.name.trim().toLowerCase())
  if (new Set(names).size !== names.length) return 'Give each share a different folder name.'
  return null
}
export type BackupState = 'Running' | 'Succeeded' | 'Failed'
export interface BackupStatus {
  id: string; state: BackupState; startedAt: string; finishedAt: string | null; snapshot: string | null
  added: number | null; total: number | null; message: string | null
}
export interface BackupSnapshot { id: string; stack: string; host: string; time: string; size: number | null }
export interface BackupApp {
  name: string; node: string; enabled: boolean; mode: 'live' | 'stop'; exclude: string[]
  requested: { at: string; by: string } | null; running: BackupStatus | null
  last: BackupStatus | null; lastNode: string | null; lastSuccess: BackupStatus | null; nextRun: string | null; snapshots: BackupSnapshot[]
}
export interface BackupNode { node: string; state: MountState | null; message: string | null; repositoryError: string | null }
export interface BackupDestination {
  nas: string; share: string; folder: string; path: string; source: string | null; timeZone: string
  updatedAt: string; updatedBy: string; nodes: BackupNode[]
}
export interface BackupOverview {
  destination: BackupDestination | null
  schedule: { time: string; keepDaily: number; keepWeekly: number; keepMonthly: number }
  shares: { nas: string; share: string; mountPath: string }[]
  apps: BackupApp[]
}
export interface BackupRecovery { repository: string; source: string | null; kind: NasKind | null; password: string; updatedAt: string }

export const backupFolderPattern = /^[A-Za-z0-9._-]{1,64}(?:\/[A-Za-z0-9._-]{1,64}){0,3}$/

function parseBackupStatus(value: unknown): BackupStatus | null {
  if (absent(value)) return null
  const item = object(value)
  const state = text(item.state)
  if (state !== 'Running' && state !== 'Succeeded' && state !== 'Failed') throw invalid()
  const size = (entry: unknown) => absent(entry) ? null : integer(entry)
  return { id: text(item.id), state, startedAt: timestamp(item.startedAt), finishedAt: absent(item.finishedAt) ? null : timestamp(item.finishedAt),
    snapshot: optional(item.snapshot), added: size(item.added), total: size(item.total), message: optional(item.message) }
}

export function parseBackups(value: unknown): BackupOverview {
  const item = object(value)
  const schedule = object(item.schedule)
  const destination = absent(item.destination) ? null : object(item.destination)
  return {
    destination: destination && {
      nas: text(destination.nas), share: text(destination.share), folder: text(destination.folder), path: text(destination.path),
      source: optional(destination.source), timeZone: text(destination.timeZone), updatedAt: timestamp(destination.updatedAt),
      updatedBy: text(destination.updatedBy),
      nodes: list(destination.mounts, entry => {
        const row = object(entry)
        return { ...parseNasMount(row), repositoryError: optional(row.repositoryError) }
      }, 256),
    },
    schedule: { time: text(schedule.time), keepDaily: integer(schedule.keepDaily), keepWeekly: integer(schedule.keepWeekly), keepMonthly: integer(schedule.keepMonthly) },
    shares: list(item.shares, entry => {
      const row = object(entry)
      return { nas: text(row.nas), share: text(row.share), mountPath: text(row.mountPath) }
    }, 2048),
    apps: list(item.apps, entry => {
      const row = object(entry)
      if (typeof row.enabled !== 'boolean' || (row.mode !== 'live' && row.mode !== 'stop')) throw invalid()
      const requested = absent(row.requested) ? null : object(row.requested)
      return {
        name: text(row.name), node: text(row.node), enabled: row.enabled, mode: row.mode, exclude: list(row.exclude, text, 32),
        requested: requested && { at: timestamp(requested.at), by: text(requested.by) },
        running: parseBackupStatus(row.running), last: parseBackupStatus(row.last), lastNode: optional(row.lastNode),
        lastSuccess: parseBackupStatus(row.lastSuccess), nextRun: absent(row.nextRun) ? null : timestamp(row.nextRun),
        snapshots: list(row.snapshots, snapshot => {
          const shot = object(snapshot)
          return { id: text(shot.id), stack: text(shot.stack), host: text(shot.host), time: timestamp(shot.time), size: absent(shot.size) ? null : integer(shot.size) }
        }, 100),
      }
    }, 64),
  }
}

export function parseBackupRecovery(value: unknown): BackupRecovery {
  const item = object(value)
  const kind = optional(item.kind)
  if (kind !== null && kind !== 'nfs' && kind !== 'smb') throw invalid()
  return { repository: text(item.repository), source: optional(item.source), kind: kind as NasKind | null, password: text(item.password), updatedAt: timestamp(item.updatedAt) }
}

/** A short "3 hours ago" for a past moment, or the date once it's more than a week old. */
export function ago(at: string, now = Date.now()): string {
  const minutes = Math.round((now - Date.parse(at)) / 60000)
  if (minutes < 1) return 'just now'
  if (minutes < 60) return minutes === 1 ? '1 minute ago' : `${minutes} minutes ago`
  const hours = Math.round(minutes / 60)
  if (hours < 24) return hours === 1 ? '1 hour ago' : `${hours} hours ago`
  const days = Math.round(hours / 24)
  return days < 8 ? (days === 1 ? 'yesterday' : `${days} days ago`) : new Date(at).toLocaleDateString()
}

/** Where an app's backups stand, for the owner. */
export function backupState(app: BackupApp, destination: BackupDestination | null, now = Date.now()): { label: string; tone: StackTone; detail: string } {
  if (!destination) return { label: 'Not set up', tone: 'muted', detail: 'Choose where backups go first.' }
  if (app.running) return { label: 'Backing up', tone: 'accent', detail: `${app.node} started ${ago(app.running.startedAt, now)}.` }
  if (app.requested) return { label: 'Queued', tone: 'accent', detail: `${app.node} starts it at its next check-in, within a minute.` }
  if (!app.enabled) return { label: 'Off', tone: 'muted', detail: 'Lucia doesn\'t back this app up.' }
  if (app.last?.state === 'Failed') return { label: 'Failed', tone: 'failed', detail: (app.last.message ?? 'The backup failed.')
    + (app.lastSuccess ? ` Last good backup ${ago(app.lastSuccess.finishedAt ?? app.lastSuccess.startedAt, now)}.` : ' It has never been backed up.') }
  if (app.lastSuccess) {
    const at = app.lastSuccess.finishedAt ?? app.lastSuccess.startedAt
    const stale = now - Date.parse(at) > 2 * 24 * 3600 * 1000
    return { label: stale ? 'Overdue' : 'Backed up', tone: stale ? 'amber' : 'green',
      detail: `${ago(at, now)}${app.lastSuccess.total !== null ? `, ${formatBytes(app.lastSuccess.total)}` : ''}.${app.last?.message ? ` ${app.last.message}` : ''}` }
  }
  return { label: 'Not backed up yet', tone: 'muted', detail: app.nextRun ? 'The first backup runs tonight.' : 'Waiting for its first backup.' }
}

/** Apps whose backups need attention first, then the rest by name. */
export function backupOrder(apps: BackupApp[], destination: BackupDestination | null, now = Date.now()): BackupApp[] {
  const rank = { failed: 0, amber: 1, accent: 2, muted: 3, green: 4 } as const
  return [...apps].sort((a, b) => rank[backupState(a, destination, now).tone] - rank[backupState(b, destination, now).tone] || a.name.localeCompare(b.name))
}

export function backupFolderProblem(folder: string): string | null {
  const clean = folder.trim().replace(/^\/+|\/+$/g, '')
  if (clean && (!backupFolderPattern.test(clean) || clean.split('/').some(part => part === '.' || part === '..')))
    return 'Use letters, digits, dots, dashes and underscores for the folder, with / between levels.'
  return null
}