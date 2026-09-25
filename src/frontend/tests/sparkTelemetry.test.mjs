import assert from 'node:assert/strict'
import { bytes, historyPath, memoryPercent, parseSparkTelemetry, uptime } from '../.checks/sparkTelemetry.js'

const timestamp = '2026-09-22T22:00:00Z'
const sample = {
  timestamp, cpuPercent: 0, cpuCores: 20, loadAverage: 0.1,
  memoryTotalBytes: 128 * 1024 ** 3, memoryAvailableBytes: 64 * 1024 ** 3,
  storageTotalBytes: 4 * 1024 ** 4, storageAvailableBytes: 3 * 1024 ** 4,
  networkInterface: 'enP7s7', receiveBytesPerSecond: 0, transmitBytesPerSecond: 1024,
  gpuName: 'NVIDIA GB10', gpuPercent: 0, gpuTemperatureCelsius: 37, gpuPowerWatts: 10.1,
  unifiedMemory: true, uptimeSeconds: 90061, unavailable: [],
}
const response = {
  enabled: true, state: 'Healthy', message: 'Spark is reporting normally.',
  sampleIntervalSeconds: 10, retentionSeconds: 3600, latest: sample, history: [sample],
}
const parsed = parseSparkTelemetry(response)
assert.equal(parsed.latest.cpuPercent, 0)
assert.equal(parsed.latest.gpuPercent, 0)
assert.equal(parsed.latest.receiveBytesPerSecond, 0)
assert.equal(memoryPercent(parsed.latest), 50)
assert.equal(memoryPercent({ ...sample, memoryTotalBytes: null }), null)
assert.equal(memoryPercent({ ...sample, memoryTotalBytes: 0 }), null)
assert.equal(bytes(null), 'Unavailable')
assert.equal(bytes(1024 ** 3), '1 GiB')
assert.equal(uptime(90061), 'Up 1d 1h')
assert.equal(uptime(null), 'Uptime unavailable')
for (const state of ['Partial', 'Attention', 'Stale'])
  assert.equal(parseSparkTelemetry({ ...response, state }).state, state)
assert.equal(parseSparkTelemetry({ ...response, state: 'Starting', latest: null, history: [] }).latest, null)
for (const changes of [
  { retentionSeconds: 7200 }, { sampleIntervalSeconds: 0 }, { state: 'Everything is fine' },
  { latest: null }, { history: [] }, { history: Array(362).fill(sample) },
]) assert.throws(() => parseSparkTelemetry({ ...response, ...changes }), /invalid Spark readings/)
for (const changes of [
  { cpuPercent: 101 }, { gpuPercent: -1 }, { gpuPowerWatts: NaN }, { memoryTotalBytes: 0 },
  { memoryAvailableBytes: sample.memoryTotalBytes + 1 }, { cpuCores: 20.5 }, { timestamp: 'not a time' },
]) assert.throws(() => parseSparkTelemetry({ ...response, latest: { ...sample, ...changes }, history: [{ ...sample, ...changes }] }), /invalid Spark readings/)
const end = Date.parse(timestamp)
const history = [
  { ...sample, timestamp: new Date(end - 30000).toISOString(), cpuPercent: 0 },
  { ...sample, timestamp: new Date(end - 20000).toISOString(), cpuPercent: null },
  { ...sample, timestamp, cpuPercent: 40 },
]
const path = historyPath(history, sample => sample.cpuPercent, end)
assert.equal((path.match(/M/g) ?? []).length, 2, 'Missing measurements must break the trend line.')
assert.ok(!path.includes('NaN'))
const gap = historyPath([history[0], history[2]], sample => sample.cpuPercent, end)
assert.equal((gap.match(/M/g) ?? []).length, 2, 'A missed collection interval must not be interpolated.')
console.log('Spark UI checks passed: zero versus unavailable values, strict history bounds, meaningful units, and gaps.')
