import assert from 'node:assert/strict'
import { defaultWindow, formatBytes, fromLocalInput, groupUpdates, parseSparkUpdates, progressLabel, progressPercent, restartNeeded, resultLabel, summarize, toLocalInput } from '../.checks/packageUpdates.js'
import { parseRoute } from '../.checks/dashboard.js'
import { findDestinations } from '../.checks/navigation.js'

const update = (name, extra = {}) => ({ name, summary: `${name} package`, currentVersion: '1.0', candidateVersion: '1.1', source: 'Ubuntu updates',
  security: false, platform: false, restart: 'services', downloadBytes: 2048, keptBack: false, phased: false, ...extra })
const status = {
  schemaVersion: 1, ready: true, checkedAt: '2026-09-25T02:00:00Z', scanning: false,
  system: { os: 'Ubuntu 24.04.5 LTS', dgx: 'NVIDIA DGX Spark', kernel: '7.0.0-1019-nvidia', architecture: 'aarch64' },
  lastUpdateCheckAt: '2026-09-16T23:24:21Z', scannedAt: '2026-09-25T02:00:00Z',
  updates: [update('netplan.io', { phased: true }), update('openssl', { security: true, source: 'Ubuntu security' }),
    update('linux-image-nvidia', { platform: true, restart: 'spark' }), update('docker-ce', { platform: true, restart: 'lucia' })],
  restart: { sparkRequired: false, sparkReasons: [], kernelPending: false, services: ['dgx-dashboard.service'] },
  automaticUpdates: { installed: false, enabled: false }, packageDatabase: { healthy: true, message: null },
  operation: null, history: [{ id: 'a'.repeat(32), action: 'check', state: 'succeeded', startedAt: '2026-09-25T01:59:00Z',
    finishedAt: '2026-09-25T02:00:00Z', message: 'Checked.', packages: [], restart: null, log: [] }], rebootingAt: null,
  worker: { state: 'ready', installCommand: 'sudo python3 package_worker.py install' },
  schedule: { runAt: '2026-09-26T09:00:00Z', packages: null, includePlatform: false, restart: 'spark', createdAt: '2026-09-25T02:00:00Z' },
  lastSchedule: null, pendingRequest: false, modelsPaused: false,
}
const state = parseSparkUpdates(status)
assert.equal(state.updates.length, 4)
assert.equal(state.schedule.packages, null)
const { everyday, platform } = groupUpdates(state.updates)
assert.deepEqual(everyday.map(item => item.name), ['netplan.io', 'openssl'])
assert.deepEqual(platform.map(item => item.name), ['linux-image-nvidia', 'docker-ce'])
assert.equal(summarize(state), '4 updates are available, including 1 security fix. 2 are platform updates and need their own review.')
assert.equal(summarize({ ...state, updates: [] }), 'The Spark is up to date.')
assert.match(summarize({ ...state, updates: [], restart: { ...state.restart, sparkRequired: true } }), /restart is needed/)
assert.match(summarize({ ...state, worker: { ...state.worker, state: 'missing' } }), /one-time setup/)
assert.equal(restartNeeded(everyday), 'services')
assert.equal(restartNeeded(platform), 'spark')
assert.equal(restartNeeded([platform[1]]), 'lucia')
assert.equal(restartNeeded([]), null)
assert.match(summarize({ ...state, updates: [update('a', { security: true }), update('b', { security: true })] }), /2 security fixes/)

// Live install progress is optional, parsed strictly when present, and summarized across stages.
assert.equal(state.history[0].progress, null)
const running = parseSparkUpdates({ ...status, operation: { ...status.history[0], action: 'install', state: 'running', finishedAt: null,
  progress: { step: 'unpacking', done: 5, total: 10, package: 'curl' } } }).operation
assert.deepEqual(running.progress, { step: 'unpacking', done: 5, total: 10, package: 'curl' })
assert.equal(progressLabel(running.progress), 'Installing 5 of 10')
assert.equal(progressPercent(running.progress), 48)
assert.equal(progressPercent({ step: 'downloading', done: 0, total: 10, package: null }), 0)
assert.equal(progressPercent({ step: 'configuring', done: 12, total: 10, package: null }), 100)
assert.equal(progressPercent({ step: 'refreshing', done: 0, total: 0, package: null }), null)
assert.equal(progressLabel({ step: 'restarting', done: 0, total: 0, package: null }), 'Restarting services that use updated files')
assert.equal(progressLabel(null), 'Starting…')
assert.throws(() => parseSparkUpdates({ ...status, operation: { ...status.history[0], progress: { step: 'x', done: -1, total: 1, package: null } } }))
assert.equal(resultLabel({ ...running, state: 'failed' }), 'Updates weren’t installed')
assert.equal(resultLabel({ ...running, state: 'succeeded' }), 'Installed updates')
// A worker that isn't installed still yields a usable state.
const missing = parseSparkUpdates({ worker: { state: 'missing', installCommand: 'x' }, schedule: null, lastSchedule: null, pendingRequest: false, modelsPaused: false })
assert.equal(missing.updates.length, 0)
assert.equal(missing.system, null)

for (const change of [{ updates: [update('x', { restart: 'maybe' })] }, { updates: [update('x', { downloadBytes: -1 })] },
  { operation: { ...status.history[0], state: 'pretend' } }, { worker: { state: 'fine', installCommand: 'x' } },
  { schedule: { ...status.schedule, restart: 'always' } }, { pendingRequest: 'no' }])
  assert.throws(() => parseSparkUpdates({ ...status, ...change }))

assert.equal(formatBytes(512), '512 B')
assert.equal(formatBytes(2048), '2.0 KB')
assert.equal(formatBytes(150 * 1024 * 1024), '150 MB')
const evening = new Date(2026, 8, 24, 21, 30)
assert.equal(defaultWindow(evening), '2026-09-25T02:00')
assert.equal(defaultWindow(new Date(2026, 8, 25, 1, 30)), '2026-09-26T02:00')
assert.equal(defaultWindow(new Date(2026, 8, 25, 0, 30)), '2026-09-25T02:00')
assert.equal(toLocalInput(evening), '2026-09-24T21:30')
assert.equal(fromLocalInput('2026-09-25T02:00'), new Date(2026, 8, 25, 2, 0).toISOString())
assert.equal(fromLocalInput('tomorrow'), null)

assert.deepEqual(parseRoute('#/updates'), { page: 'updates' })
assert.equal(findDestinations('kernel reboot', true)[0]?.page, 'updates')
assert.equal(findDestinations('apt', false).length, 0)
console.log('package update checks passed')
