export interface SparkSample {
  timestamp: string
  cpuPercent: number | null
  cpuCores: number | null
  loadAverage: number | null
  memoryTotalBytes: number | null
  memoryAvailableBytes: number | null
  storageTotalBytes: number | null
  storageAvailableBytes: number | null
  networkInterface: string | null
  receiveBytesPerSecond: number | null
  transmitBytesPerSecond: number | null
  gpuName: string | null
  gpuPercent: number | null
  gpuTemperatureCelsius: number | null
  gpuPowerWatts: number | null
  unifiedMemory: boolean
  uptimeSeconds: number | null
  unavailable: string[]
}

export interface SparkTelemetry {
  enabled: boolean
  state: 'Starting' | 'Healthy' | 'Partial' | 'Attention' | 'Stale' | 'Unavailable'
  message: string
  sampleIntervalSeconds: number
  retentionSeconds: number
  latest: SparkSample | null
  history: SparkSample[]
}

const invalid = () => new Error('Lucia returned invalid Spark readings. Current health cannot be confirmed.')
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw invalid()
  return value as Record<string, unknown>
}
function text(value: unknown): string {
  if (typeof value !== 'string' || !value.length || value.length > 512) throw invalid()
  return value
}
function boolean(value: unknown): boolean {
  if (typeof value !== 'boolean') throw invalid()
  return value
}
function number(value: unknown, minimum = 0, maximum = Number.MAX_SAFE_INTEGER): number {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < minimum || value > maximum) throw invalid()
  return value
}
function integer(value: unknown, minimum = 0, maximum = Number.MAX_SAFE_INTEGER): number {
  const result = number(value, minimum, maximum)
  if (!Number.isSafeInteger(result)) throw invalid()
  return result
}
function optional<T>(value: unknown, parse: (item: unknown) => T): T | null {
  return value === null ? null : parse(value)
}
function sample(value: unknown): SparkSample {
  const data = object(value)
  const timestamp = text(data.timestamp)
  if (!Number.isFinite(Date.parse(timestamp))) throw invalid()
  if (!Array.isArray(data.unavailable) || data.unavailable.length > 16) throw invalid()
  const result = {
    timestamp, cpuPercent: optional(data.cpuPercent, value => number(value, 0, 100)),
    cpuCores: optional(data.cpuCores, value => integer(value, 1, 4096)),
    loadAverage: optional(data.loadAverage, number),
    memoryTotalBytes: optional(data.memoryTotalBytes, value => integer(value, 1)), memoryAvailableBytes: optional(data.memoryAvailableBytes, integer),
    storageTotalBytes: optional(data.storageTotalBytes, value => integer(value, 1)), storageAvailableBytes: optional(data.storageAvailableBytes, integer),
    networkInterface: optional(data.networkInterface, text),
    receiveBytesPerSecond: optional(data.receiveBytesPerSecond, number), transmitBytesPerSecond: optional(data.transmitBytesPerSecond, number),
    gpuName: optional(data.gpuName, text), gpuPercent: optional(data.gpuPercent, value => number(value, 0, 100)),
    gpuTemperatureCelsius: optional(data.gpuTemperatureCelsius, value => number(value, -50, 200)),
    gpuPowerWatts: optional(data.gpuPowerWatts, number), unifiedMemory: boolean(data.unifiedMemory),
    uptimeSeconds: optional(data.uptimeSeconds, number), unavailable: data.unavailable.map(text),
  }
  if ((result.memoryTotalBytes !== null && result.memoryAvailableBytes !== null && result.memoryAvailableBytes > result.memoryTotalBytes)
    || (result.storageTotalBytes !== null && result.storageAvailableBytes !== null && result.storageAvailableBytes > result.storageTotalBytes)) throw invalid()
  return result
}

export function parseSparkTelemetry(value: unknown): SparkTelemetry {
  const data = object(value)
  const state = text(data.state)
  if (!['Starting', 'Healthy', 'Partial', 'Attention', 'Stale', 'Unavailable'].includes(state)
    || data.sampleIntervalSeconds !== 10 || data.retentionSeconds !== 3600
    || !Array.isArray(data.history) || data.history.length > 361) throw invalid()
  const history = data.history.map(sample)
  if (history.some((item, index) => index > 0 && Date.parse(item.timestamp) < Date.parse(history[index - 1].timestamp))) throw invalid()
  if (history.length > 1 && Date.parse(history[history.length - 1].timestamp) - Date.parse(history[0].timestamp) > 3600000) throw invalid()
  const latest = optional(data.latest, sample)
  if ((latest === null) !== (history.length === 0)
    || (latest !== null && latest.timestamp !== history[history.length - 1].timestamp)) throw invalid()
  if (latest === null && ['Healthy', 'Partial', 'Attention', 'Stale'].includes(state)) throw invalid()
  return {
    enabled: boolean(data.enabled), state: state as SparkTelemetry['state'], message: text(data.message),
    sampleIntervalSeconds: 10, retentionSeconds: 3600, latest, history,
  }
}

export function memoryPercent(sample: SparkSample): number | null {
  return sample.memoryTotalBytes !== null && sample.memoryTotalBytes > 0 && sample.memoryAvailableBytes !== null
    ? 100 * (sample.memoryTotalBytes - sample.memoryAvailableBytes) / sample.memoryTotalBytes : null
}

export function historyPath(history: SparkSample[], value: (sample: SparkSample) => number | null, end: number): string {
  let previous: number | null = null
  return history.map(sample => {
    const amount = value(sample)
    const at = Date.parse(sample.timestamp)
    if (amount === null || at < end - 3600000 || at > end) { previous = null; return '' }
    const x = 600 * (at - end + 3600000) / 3600000
    const y = 88 - amount * 0.8
    const command = previous === null || at - previous > 25000 ? 'M' : 'L'
    previous = at
    return `${command}${x.toFixed(2)},${y.toFixed(2)}`
  }).join(' ')
}

export function bytes(value: number | null): string {
  if (value === null) return 'Unavailable'
  const unit = value >= 1024 ** 4 ? 'TiB' : value >= 1024 ** 3 ? 'GiB' : value >= 1024 ** 2 ? 'MiB' : 'KiB'
  const scale = unit === 'TiB' ? 1024 ** 4 : unit === 'GiB' ? 1024 ** 3 : unit === 'MiB' ? 1024 ** 2 : 1024
  return `${(value / scale).toLocaleString(undefined, { maximumFractionDigits: 1 })} ${unit}`
}

export function uptime(value: number | null): string {
  if (value === null) return 'Uptime unavailable'
  const hours = Math.floor(value / 3600)
  return hours >= 24 ? `Up ${Math.floor(hours / 24)}d ${hours % 24}h`
    : hours > 0 ? `Up ${hours}h ${Math.floor(value / 60) % 60}m` : `Up ${Math.floor(value / 60)}m`
}
