import type { AuthenticationSession } from './authentication.js'

export interface HardwareDisk {
  id: string | null
  path: string
  model: string | null
  serial: string | null
  sizeBytes: number
  isRemovable: boolean
  isReadOnly: boolean
}

export interface HardwareReport {
  architecture: string
  bootMode: string
  secureBoot: boolean | null
  manufacturer: string | null
  model: string | null
  serialNumber: string | null
  hardwareUuid: string | null
  cpuModel: string | null
  logicalCpuCount: number
  memoryBytes: number
  interfaces: { name: string; macAddress: string | null; addresses: string[] }[]
  disks: HardwareDisk[]
}

const devicePhases = ['Discovered', 'Approved', 'Installing', 'AwaitingEnrollment', 'Managed', 'Failed', 'Rejected'] as const
const taskPhases = ['Approved', 'GrantIssued', 'Installing', 'AwaitingEnrollment', 'Failed', 'Invalidated', 'Managed'] as const
export type DevicePhase = typeof devicePhases[number]
export type TaskPhase = typeof taskPhases[number]

export interface OnboardingDevice {
  id: string
  verificationCode: string
  phase: DevicePhase
  hardware: HardwareReport
  inventoryRevision: number
  discoveredAt: string
  updatedAt: string
  lastSeenAt: string
  lastHeartbeatAt: string | null
  heartbeatFreshness: 'Unknown' | 'Fresh' | 'Stale'
  discoveryExpiresAt: string
  taskId: string | null
  statusMessage: string | null
  dismissedAt: string | null
}

export interface InstallationTask {
  id: string
  deviceId: string
  phase: TaskPhase
  hostname: string
  diskId: string
  inventoryRevision: number
  approvedBy: string
  approvedAt: string
  authorityExpiresAt: string
  grantIssuedAt: string | null
  updatedAt: string
  statusMessage: string | null
}

export interface OnboardingSnapshot {
  window: { isOpen: boolean; expiresAt: string | null }
  readiness: { canDiscover: boolean; canInstall: boolean; reasons: string[] }
  devices: OnboardingDevice[]
  tasks: InstallationTask[]
}

export interface NodeRuntimeSummary {
  state: 'Preparing' | 'Ready' | 'Failed'; dockerVersion: string | null; composeVersion: string | null; gpuContainers: boolean
  gpus: { vendor: string; model: string; memoryBytes: number | null; computeCapability: string | null; uuid: string | null }[]
  message: string | null; driverVersion: string | null; cudaVersion: string | null
}

export interface ManagedNodeSummary {
  nodeId: string; hostname: string; state: 'Online' | 'Stale' | 'AwaitingHeartbeat'
  certificateExpiresAt: string; lastSeenAt: string | null
  /** The address its latest heartbeat came from, and the DNS name Lucia publishes for it under the active domain. */
  address: string | null; dnsName: string | null
  status: { osVersion: string; uptimeSeconds: number; loadAverage: number | null; memoryTotalBytes: number
    memoryAvailableBytes: number; storageTotalBytes: number | null; storageAvailableBytes: number | null
    runtime: NodeRuntimeSummary | null } | null
  gpu: NodeGpuSettings; gpuWarning: string | null
}

export interface NodeGpuSettings { cudaLine: CudaLine | null }
export type CudaLine = 12 | 13
export const cudaLines: { line: CudaLine; minimumCompute: number; note: string }[] = [
  { line: 13, minimumCompute: 7.5, note: 'Newest. Needs compute 7.5 or newer (RTX 20 series and later).' },
  { line: 12, minimumCompute: 5.0, note: 'For older GPUs, back to compute 5.0 (GTX 900 series).' },
]

/** Mirrors the server's CudaLines.Unsupported: why this server can't use the line, or null when it can. */
export function cudaLineUnsupported(line: CudaLine, runtime: NodeRuntimeSummary | null): string | null {
  const minimum = cudaLines.find(item => item.line === line)!.minimumCompute
  if (!runtime?.gpus.length) return 'This server hasn’t reported an NVIDIA GPU.'
  const supported = runtime.cudaVersion?.match(/^(\d+)\.\d+$/)
  if (!supported) return 'The NVIDIA driver hasn’t reported which CUDA versions it supports. Update the node agent or check the driver.'
  if (Number(supported[1]) < line) return `The NVIDIA driver supports up to CUDA ${runtime.cudaVersion}. Update the driver to use CUDA ${line}.`
  for (const gpu of runtime.gpus) {
    const compute = Number(gpu.computeCapability)
    if (!gpu.computeCapability || !Number.isFinite(compute))
      return `The ${gpu.model} didn’t report its compute capability, so Lucia can’t confirm it supports CUDA ${line}.`
    if (compute < minimum) return `CUDA ${line} doesn’t support the ${gpu.model} (compute ${gpu.computeCapability}). It needs ${minimum.toFixed(1)} or newer.`
  }
  return null
}

const invalid = () => new Error('Lucia returned incomplete or invalid hardware status. No installation controls have been enabled.')
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw invalid()
  return value as Record<string, unknown>
}
function text(value: unknown): string {
  if (typeof value !== 'string' || !value.trim() || value.length > 4096 || [...value].some(character => character.charCodeAt(0) < 32 || character.charCodeAt(0) === 127)) throw invalid()
  return value
}
function optionalText(value: unknown): string | null {
  return value === '' ? null : nullable(value, text)
}
function nullable<T>(value: unknown, parse: (input: unknown) => T): T | null {
  return value === null ? null : parse(value)
}
function boolean(value: unknown): boolean {
  if (typeof value !== 'boolean') throw invalid()
  return value
}
function integer(value: unknown): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 1) throw invalid()
  return value
}
function array<T>(value: unknown, parse: (input: unknown) => T): T[] {
  if (!Array.isArray(value) || value.length > 10000) throw invalid()
  return value.map(parse)
}
function enumeration<T extends string>(value: unknown, values: readonly T[]): T {
  if (typeof value !== 'string' || !values.includes(value as T)) throw invalid()
  return value as T
}
function uuid(value: unknown): string {
  const result = text(value)
  if (!/^[\da-f]{8}-(?:[\da-f]{4}-){3}[\da-f]{12}$/i.test(result) || /^0{8}-(?:0{4}-){3}0{12}$/.test(result)) throw invalid()
  return result
}
function timestamp(value: unknown): string {
  const result = text(value)
  if (!/^\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])T([01]\d|2[0-3]):[0-5]\d:[0-5]\d(?:\.\d{1,7})?(?:Z|[+-](?:[01]\d|2[0-3]):[0-5]\d)$/.test(result)
    || !Number.isFinite(Date.parse(result))) throw invalid()
  const [year, month, day] = result.slice(0, 10).split('-').map(Number)
  if (day > new Date(Date.UTC(year, month, 0)).getUTCDate()) throw invalid()
  return result
}
function unique<T extends { id: string | null }>(items: T[]): T[] {
  const ids = items.flatMap(item => item.id === null ? [] : [item.id])
  if (new Set(ids).size !== ids.length) throw invalid()
  return items
}
function verificationCode(value: unknown): string {
  const result = text(value)
  if (!/^[0-9A-F]{4}(?:-[0-9A-F]{4}){2}$/.test(result)) throw invalid()
  return result
}
function diskId(value: unknown): string {
  const result = text(value)
  if (!/^\/dev\/disk\/by-id\/[A-Za-z0-9][A-Za-z0-9._:+-]{0,199}$/.test(result) || result.includes('..') || /-part\d+$/.test(result)) throw invalid()
  return result
}
function parseHardware(value: unknown): HardwareReport {
  const data = object(value)
  const disks = unique(array(data.disks, value => {
    const disk = object(value)
    return {
      id: nullable(disk.id, diskId), path: text(disk.path), model: optionalText(disk.model), serial: optionalText(disk.serial),
      sizeBytes: integer(disk.sizeBytes), isRemovable: boolean(disk.isRemovable), isReadOnly: boolean(disk.isReadOnly),
    }
  }))
  if (new Set(disks.map(disk => disk.path)).size !== disks.length) throw invalid()
  return {
    architecture: text(data.architecture), bootMode: text(data.bootMode), secureBoot: nullable(data.secureBoot, boolean),
    manufacturer: optionalText(data.manufacturer), model: optionalText(data.model),
    serialNumber: optionalText(data.serialNumber), hardwareUuid: nullable(data.hardwareUuid, uuid),
    cpuModel: optionalText(data.cpuModel), logicalCpuCount: integer(data.logicalCpuCount), memoryBytes: integer(data.memoryBytes),
    interfaces: array(data.interfaces, value => {
      const nic = object(value)
      return { name: text(nic.name), macAddress: nullable(nic.macAddress, text), addresses: array(nic.addresses, text) }
    }),
    disks,
  }
}

export function parseOnboardingSnapshot(value: unknown): OnboardingSnapshot {
  const data = object(value)
  const window = object(data.window)
  const readiness = object(data.readiness)
  const result: OnboardingSnapshot = {
    window: { isOpen: boolean(window.isOpen), expiresAt: nullable(window.expiresAt, timestamp) },
    readiness: { canDiscover: boolean(readiness.canDiscover), canInstall: boolean(readiness.canInstall), reasons: array(readiness.reasons, text) },
    devices: unique(array(data.devices, value => {
      const device = object(value)
      return {
        id: uuid(device.id), verificationCode: verificationCode(device.verificationCode),
        phase: enumeration(device.phase, devicePhases), hardware: parseHardware(device.hardware),
        inventoryRevision: integer(device.inventoryRevision), discoveredAt: timestamp(device.discoveredAt),
        updatedAt: timestamp(device.updatedAt), lastSeenAt: timestamp(device.lastSeenAt),
        lastHeartbeatAt: nullable(device.lastHeartbeatAt, timestamp),
        heartbeatFreshness: enumeration(device.heartbeatFreshness, ['Unknown', 'Fresh', 'Stale'] as const),
        discoveryExpiresAt: timestamp(device.discoveryExpiresAt), taskId: nullable(device.taskId, uuid),
        statusMessage: optionalText(device.statusMessage),
        dismissedAt: 'dismissedAt' in device ? nullable(device.dismissedAt, timestamp) : null,
      }
    })),
    tasks: unique(array(data.tasks, value => {
      const task = object(value)
      return {
        id: uuid(task.id), deviceId: uuid(task.deviceId), phase: enumeration(task.phase, taskPhases),
        hostname: text(task.hostname), diskId: diskId(task.diskId), inventoryRevision: integer(task.inventoryRevision),
        approvedBy: text(task.approvedBy), approvedAt: timestamp(task.approvedAt),
        authorityExpiresAt: timestamp(task.authorityExpiresAt), grantIssuedAt: nullable(task.grantIssuedAt, timestamp),
        updatedAt: timestamp(task.updatedAt), statusMessage: optionalText(task.statusMessage),
      }
    })),
  }
  if (result.window.isOpen && !result.window.expiresAt) throw invalid()
  if (result.devices.some(device => device.dismissedAt !== null && (device.phase !== 'Rejected'
    || Date.parse(device.dismissedAt) < Date.parse(device.discoveredAt) || Date.parse(device.dismissedAt) > Date.parse(device.updatedAt)))) throw invalid()
  if (result.readiness.canInstall && (!result.readiness.canDiscover || result.readiness.reasons.length > 0)) throw invalid()
  return result
}

export function visibleDiscoveries(devices: OnboardingDevice[], dismissed = false): OnboardingDevice[] {
  return devices.filter(device => (device.dismissedAt !== null) === dismissed)
}

export function parseManagedNodes(value: unknown): ManagedNodeSummary[] {
  function nonnegative(input: unknown): number {
    if (typeof input !== 'number' || !Number.isFinite(input) || input < 0) throw invalid()
    return input
  }
  return array(value, entry => {
    const node = object(entry)
    return { nodeId: uuid(node.nodeId), hostname: text(node.hostname),
      state: enumeration(node.state, ['Online', 'Stale', 'AwaitingHeartbeat'] as const),
      certificateExpiresAt: timestamp(node.certificateExpiresAt), lastSeenAt: nullable(node.lastSeenAt, timestamp),
      address: nullable(node.address ?? null, text), dnsName: nullable(node.dnsName ?? null, text),
      status: nullable(node.status, entry => {
        const status = object(entry)
        const total = integer(status.memoryTotalBytes)
        const available = nonnegative(status.memoryAvailableBytes)
        const storageTotal = nullable(status.storageTotalBytes, integer)
        const storageAvailable = nullable(status.storageAvailableBytes, nonnegative)
        if (available > total || (storageTotal === null) !== (storageAvailable === null)
          || storageTotal !== null && storageAvailable !== null && storageAvailable > storageTotal) throw invalid()
        return { osVersion: text(status.osVersion), uptimeSeconds: nonnegative(status.uptimeSeconds),
          loadAverage: nullable(status.loadAverage, nonnegative), memoryTotalBytes: total, memoryAvailableBytes: available,
          storageTotalBytes: storageTotal, storageAvailableBytes: storageAvailable,
          runtime: status.runtime === undefined ? null : nullable(status.runtime, entry => {
            const runtime = object(entry)
            return { state: enumeration(runtime.state, ['Preparing', 'Ready', 'Failed'] as const),
              dockerVersion: nullable(runtime.dockerVersion, text), composeVersion: nullable(runtime.composeVersion, text),
              gpuContainers: boolean(runtime.gpuContainers), message: optionalText(runtime.message ?? null),
              driverVersion: nullable(runtime.driverVersion ?? null, text), cudaVersion: nullable(runtime.cudaVersion ?? null, text),
              gpus: array(runtime.gpus, entry => {
                const gpu = object(entry)
                return { vendor: text(gpu.vendor), model: text(gpu.model), memoryBytes: nullable(gpu.memoryBytes, integer),
                  computeCapability: nullable(gpu.computeCapability, text), uuid: nullable(gpu.uuid ?? null, text) }
              }) }
          }) }
      }),
      gpu: node.gpu === undefined || node.gpu === null ? { cudaLine: null } : (() => {
        const gpu = object(node.gpu)
        return { cudaLine: nullable(gpu.cudaLine, line => { if (line !== 12 && line !== 13) throw invalid(); return line as CudaLine }) }
      })(),
      gpuWarning: nullable(node.gpuWarning ?? null, text) }
  })
}

export function secondsUntil(expiresAt: string | null, now = Date.now()): number {
  if (!expiresAt || !Number.isFinite(now)) return 0
  const expiry = Date.parse(expiresAt)
  return Number.isFinite(expiry) ? Math.max(0, Math.ceil((expiry - now) / 1000)) : 0
}

export function formatCountdown(seconds: number): string {
  const remaining = Math.max(0, Math.floor(seconds))
  return `${Math.floor(remaining / 60)}m ${String(remaining % 60).padStart(2, '0')}s`
}

export function formatBytes(bytes: number): string {
  if (bytes < 1_000_000_000) return `${(bytes / 1_000_000).toLocaleString(undefined, { maximumFractionDigits: 1 })} MB`
  return `${(bytes / 1_000_000_000).toLocaleString(undefined, { maximumFractionDigits: 1 })} GB`
}

export function isInstallableDisk(disk: HardwareDisk): disk is HardwareDisk & { id: string } {
  return typeof disk.id === 'string' && !disk.isReadOnly && !disk.isRemovable
}

export function installationBlockers(snapshot: OnboardingSnapshot, device: OnboardingDevice, now = Date.now()): string[] {
  const reasons: string[] = []
  if (!snapshot.readiness.canInstall) reasons.push('Installation is not ready. The host must qualify boot, private CA, and directory enrollment first.')
  if (device.phase !== 'Discovered') reasons.push('Only new discoveries can be approved. Reinstallation is not available.')
  if (!secondsUntil(device.discoveryExpiresAt, now)) reasons.push('This discovery session has expired. Start a new discovery from the device.')
  if (device.hardware.architecture !== 'x86_64' || device.hardware.bootMode !== 'uefi')
    reasons.push('Installation currently requires an x86_64 device started in UEFI mode.')
  if (!device.hardware.disks.some(isInstallableDisk))
    reasons.push('No safely identified, writable, nonremovable disk was reported.')
  return reasons
}

export interface InstallApproval { hostname: string; diskId: string; confirmation: string; recoveryPublicKey: string }

export function validateInstallApproval(snapshot: OnboardingSnapshot, device: OnboardingDevice, input: InstallApproval, acknowledged: boolean, now = Date.now()): InstallApproval {
  const blockers = installationBlockers(snapshot, device, now)
  if (blockers.length) throw new Error(blockers[0])
  if (!acknowledged) throw new Error('Confirm that all data on the selected disk may be erased.')
  validateApprovalFields(input)
  const disk = device.hardware.disks.find(disk => disk.id === input.diskId)
  if (!disk || !isInstallableDisk(disk)) throw new Error('Choose a safely identified, writable, nonremovable disk from this device.')
  if (snapshot.tasks.some(task => task.hostname === input.hostname && task.phase !== 'Failed' && task.phase !== 'Invalidated'))
    throw new Error('That hostname is already reserved by another installation.')
  return { hostname: input.hostname, diskId: disk.id, confirmation: input.confirmation, recoveryPublicKey: input.recoveryPublicKey.trim() }
}

function validateApprovalFields(input: InstallApproval) {
  if (typeof input.hostname !== 'string' || !/^[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(input.hostname) || input.hostname === 'localhost')
    throw new Error('Use a hostname of 1–63 lowercase letters, numbers, or hyphens. Start with a letter, end with a letter or number, and do not use localhost.')
  try { diskId(input.diskId) } catch { throw new Error('Choose an exact, whole-disk identifier from this device.') }
  if (input.confirmation !== 'ERASE') throw new Error('Type ERASE exactly to approve erasing this disk.')
  if (typeof input.recoveryPublicKey !== 'string' || input.recoveryPublicKey.length > 16384
    || /[\r\n]/.test(input.recoveryPublicKey) || !/^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp256) [A-Za-z0-9+/]+=*(?: [^\r\n]*)?$/.test(input.recoveryPublicKey.trim()))
    throw new Error('Paste one supported SSH public key for the local recovery administrator, or import one from GitHub. Never paste a private key.')
}

export type OnboardingAction =
  | { kind: 'snapshot' }
  | { kind: 'managed' }
  | { kind: 'open' }
  | { kind: 'close' }
  | { kind: 'reject'; deviceId: string }
  | { kind: 'dismiss' | 'restore'; deviceId: string }
  | { kind: 'approve'; deviceId: string; approval: InstallApproval }

export function onboardingRequest(session: AuthenticationSession, action: OnboardingAction): { url: string; init: RequestInit } {
  if (!session.authenticated || !session.canAccess || !session.isOwner) throw new Error('Owner access is needed to manage hardware.')
  const init: RequestInit = { credentials: 'same-origin', cache: 'no-store', method: 'GET' }
  let url = '/api/host/onboarding'
  if (action.kind === 'snapshot') return { url, init }
  if (action.kind === 'managed') return { url: '/api/host/nodes', init }
  if (!session.csrfToken) throw new Error('Your session is missing its security token. Sign in again before making changes.')
  init.method = action.kind === 'close' ? 'DELETE' : 'POST'
  init.headers = { 'X-CSRF-TOKEN': session.csrfToken }
  if (action.kind === 'open' || action.kind === 'close') {
    url += '/window'
    if (action.kind === 'open') init.body = JSON.stringify({ minutes: 30 })
  } else {
    url = `/api/host/devices/${uuid(action.deviceId)}/${action.kind === 'approve' ? 'approve-install' : action.kind}`
    if (action.kind === 'approve') {
      validateApprovalFields(action.approval)
      const { hostname, diskId, confirmation, recoveryPublicKey } = action.approval
      init.body = JSON.stringify({ hostname, diskId, confirmation, recoveryPublicKey: recoveryPublicKey.trim() })
    }
  }
  if (init.body) init.headers = { ...init.headers, 'Content-Type': 'application/json' }
  return { url, init }
}

export async function requestOnboarding(session: AuthenticationSession, refreshSession: () => Promise<void>, action: OnboardingAction, signal: AbortSignal): Promise<Response> {
  const { url, init } = onboardingRequest(session, action)
  const response = await fetch(url, { ...init, signal })
  if (response.ok) return response
  if (response.status === 401) {
    try { await refreshSession() } catch { /* The expired session is still reported below. */ }
    throw new Error('Your sign-in has expired. Sign in again before managing hardware.')
  }
  let message = response.status === 403
    ? 'Owner access or a valid security token is needed. Check your sign-in before trying again.'
    : `Hardware status could not be confirmed (HTTP ${response.status}). Check the connection and try again.`
  try {
    const data = object(await response.json())
    const error = object(data.error)
    if (typeof error.message === 'string' && error.message.trim()) message = error.message.slice(0, 1000)
  } catch { /* Keep the HTTP error when the response is not a JSON error. */ }
  throw new Error(message)
}
