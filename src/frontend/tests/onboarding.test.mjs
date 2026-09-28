import assert from 'node:assert/strict'
import {
  formatBytes, formatCountdown, installationBlockers, isInstallableDisk, onboardingRequest, parseOnboardingSnapshot,
  requestOnboarding, secondsUntil, validateInstallApproval, visibleDiscoveries, parseManagedNodes, cudaLineUnsupported,
} from '../.checks/onboarding.js'

const now = Date.parse('2026-09-22T16:00:00Z')
const expiry = '2026-09-22T16:30:00Z'
const deviceId = '13c7e7c6-12af-4ba1-b862-5701f857a8d2'
const diskId = '/dev/disk/by-id/nvme-Example_123'
const owner = { enabled: true, authenticated: true, canAccess: true, isOwner: true, username: 'owner', displayName: 'Owner', csrfToken: 'test-csrf' }
const device = {
  id: deviceId, verificationCode: 'ABCD-1234-EF56', phase: 'Discovered', inventoryRevision: 1,
  discoveredAt: '2026-09-22T16:00:00Z', updatedAt: '2026-09-22T16:00:00Z',
  lastSeenAt: '2026-09-22T16:00:00Z', lastHeartbeatAt: null, heartbeatFreshness: 'Unknown',
  discoveryExpiresAt: expiry, taskId: null, statusMessage: null, dismissedAt: null,
  hardware: {
    architecture: 'x86_64', bootMode: 'uefi', secureBoot: false, manufacturer: 'Example',
    model: 'Test server', serialNumber: '123', hardwareUuid: null, cpuModel: 'Test CPU',
    logicalCpuCount: 16, memoryBytes: 34359738368,
    interfaces: [{ name: 'eth0', macAddress: '00:11:22:33:44:55', addresses: ['192.168.0.25'] }],
    disks: [{ id: diskId, path: '/dev/nvme0n1', model: 'Test disk', serial: '123', sizeBytes: 1000000000000, isRemovable: false, isReadOnly: false }],
  },
}
const snapshot = { window: { isOpen: true, expiresAt: expiry }, readiness: { canDiscover: true, canInstall: true, reasons: [] }, devices: [device], tasks: [] }
const recoveryPublicKey = 'ssh-ed25519 ' + Buffer.from('0000000B7373682D6564323535313900000020D75A980182B10AB7D54BFED3C964073A0EE172F3DAA62325AF021A68F707511A', 'hex').toString('base64')
const approval = { hostname: 'dev-server', diskId, confirmation: 'ERASE', recoveryPublicKey }
assert.deepEqual(parseOnboardingSnapshot(snapshot), snapshot)
const legacyDevice = { ...device }
delete legacyDevice.dismissedAt
assert.equal(parseOnboardingSnapshot({ ...snapshot, devices: [legacyDevice] }).devices[0].dismissedAt, null)
const dismissed = { ...device, phase: 'Rejected', dismissedAt: device.updatedAt }
const mixed = parseOnboardingSnapshot({ ...snapshot, devices: [dismissed, { ...device, id: '22222222-2222-4222-8222-222222222222' }] }).devices
assert.equal(visibleDiscoveries(mixed).length, 1)
assert.deepEqual(visibleDiscoveries(mixed, true).map(item => item.id), [deviceId])
for (const invalidDismissal of [{ ...device, dismissedAt: device.updatedAt }, { ...dismissed, dismissedAt: 'not-a-date' },
  { ...dismissed, dismissedAt: '2026-09-22T15:00:00Z' }, { ...dismissed, dismissedAt: expiry }])
  assert.throws(() => parseOnboardingSnapshot({ ...snapshot, devices: [invalidDismissal] }), /invalid hardware/)
assert.deepEqual(parseOnboardingSnapshot({ ...snapshot, devices: [], tasks: [] }).devices, [])
for (const invalid of [null, [], {}, { ...snapshot, devices: {} }, { ...snapshot, tasks: null },
  { ...snapshot, window: { isOpen: true, expiresAt: null } },
  { ...snapshot, readiness: { canDiscover: 'true', canInstall: true, reasons: [] } },
  { ...snapshot, readiness: { canDiscover: false, canInstall: true, reasons: [] } },
  { ...snapshot, devices: [device, device] },
  { ...snapshot, devices: [{ ...device, lastSeenAt: '2026-02-30T16:00:00Z' }] },
  { ...snapshot, devices: [{ ...device, phase: 'ready' }] },
  { ...snapshot, devices: [{ ...device, hardware: { ...device.hardware, secureBoot: undefined } }] },
  { ...snapshot, devices: [{ ...device, hardware: { ...device.hardware, memoryBytes: Number.MAX_SAFE_INTEGER + 1 } }] },
  { ...snapshot, devices: [{ ...device, hardware: { ...device.hardware, disks: [{ ...device.hardware.disks[0], id: '/dev/sda' }] } }] },
]) assert.throws(() => parseOnboardingSnapshot(invalid), /invalid hardware/)

for (const verificationCode of [undefined, null, '', 'abcd-1234-ef56', 'ABCD1234EF56', 'ABCD-1234-EF56-7890', 'ABCD-1234-EF5Z', 'A'.repeat(64)]) {
  assert.throws(() => parseOnboardingSnapshot({ ...snapshot, devices: [{ ...device, verificationCode }] }), /invalid hardware/)
}
const unknownDisk = { ...device.hardware.disks[0], id: null, path: '/dev/sdb' }
const nullableInventory = {
  ...device, hardware: {
    ...device.hardware, interfaces: [{ name: 'eth0', macAddress: null, addresses: [] }],
    disks: [device.hardware.disks[0], unknownDisk, { ...unknownDisk, path: '/dev/sdc' }],
  },
}
const parsedNullableInventory = parseOnboardingSnapshot({ ...snapshot, devices: [nullableInventory] }).devices[0]
assert.equal(parsedNullableInventory.hardware.interfaces[0].macAddress, null)
assert.deepEqual(parsedNullableInventory.hardware.disks.map(disk => [disk.id, disk.path]), [[diskId, '/dev/nvme0n1'], [null, '/dev/sdb'], [null, '/dev/sdc']])
assert.deepEqual(parsedNullableInventory.hardware.disks.filter(isInstallableDisk).map(disk => disk.id), [diskId])
assert.deepEqual(validateInstallApproval(snapshot, parsedNullableInventory, approval, true, now), approval)
for (const invalidDisk of [null, undefined, '', '/dev/sdb', '/dev/sdc']) {
  assert.throws(() => validateInstallApproval(snapshot, parsedNullableInventory, { ...approval, diskId: invalidDisk }, true, now), /disk/)
  assert.throws(() => onboardingRequest(owner, { kind: 'approve', deviceId, approval: { ...approval, diskId: invalidDisk } }), /disk/)
}
const unidentified = { ...nullableInventory, hardware: { ...nullableInventory.hardware, disks: nullableInventory.hardware.disks.slice(1) } }
assert.match(installationBlockers(snapshot, unidentified, now).join(), /No safely identified/)
assert.throws(() => validateInstallApproval(snapshot, unidentified, approval, true, now), /No safely identified/)
for (const disks of [
  [device.hardware.disks[0], { ...device.hardware.disks[0], path: '/dev/sdb' }],
  [unknownDisk, { ...unknownDisk }],
  [{ ...unknownDisk, id: '' }],
  [{ ...unknownDisk, id: undefined }],
]) assert.throws(() => parseOnboardingSnapshot({ ...snapshot, devices: [{ ...device, hardware: { ...device.hardware, disks } }] }), /invalid hardware/)

assert.equal(secondsUntil(expiry, now), 1800)
assert.equal(secondsUntil(expiry, now + 1799500), 1)
assert.equal(secondsUntil(expiry, now + 1800000), 0)
assert.equal(secondsUntil(expiry, now + 1900000), 0)
assert.equal(secondsUntil(null, now), 0)
assert.equal(secondsUntil('invalid', now), 0)
assert.equal(formatCountdown(1800), '30m 00s')
assert.equal(formatCountdown(59), '0m 59s')
assert.equal(formatCountdown(-1), '0m 00s')
assert.equal(formatBytes(1000000000000), '1,000 GB')
assert.deepEqual(installationBlockers(snapshot, device, now), [])
for (const secureBoot of [false, true, null]) {
  const reported = { ...device, hardware: { ...device.hardware, secureBoot } }
  assert.equal(parseOnboardingSnapshot({ ...snapshot, devices: [reported] }).devices[0].hardware.secureBoot, secureBoot)
  assert.deepEqual(installationBlockers(snapshot, reported, now), [], 'Reported Secure Boot posture is not an approval gate.')
  assert.deepEqual(validateInstallApproval(snapshot, reported, approval, true, now), approval)
  assert.throws(() => validateInstallApproval({ ...snapshot, readiness: { ...snapshot.readiness, canInstall: false } }, reported, approval, true, now), /not ready/, 'No Secure Boot posture can override server readiness.')
}
assert.match(installationBlockers(snapshot, { ...device, phase: 'Managed' }, now).join(), /Reinstallation/)
assert.match(installationBlockers(snapshot, device, now + 1800000).join(), /expired/)
assert.deepEqual(validateInstallApproval(snapshot, device, approval, true, now), approval)
assert.deepEqual(validateInstallApproval({ ...snapshot, window: { isOpen: false, expiresAt: null } }, device, approval, true, now), approval, 'Closing discovery does not cancel an existing pending session or approve a reinstall.')
assert.throws(() => validateInstallApproval(snapshot, device, approval, false, now), /Confirm/)
assert.throws(() => validateInstallApproval({ ...snapshot, readiness: { ...snapshot.readiness, canInstall: false } }, device, approval, true, now), /not ready/)
for (const hostname of ['-host', 'host-', 'HOST', 'host.name', 'host;reboot', 'a'.repeat(64), '', null, undefined, 123]) {
  assert.throws(() => validateInstallApproval(snapshot, device, { ...approval, hostname }, true, now), /hostname/)
}
for (const invalidDisk of ['/dev/sda', `${diskId}-part1`, `${diskId}/../sda`, `${diskId};reboot`, '/dev/disk/by-id/other']) {
  assert.throws(() => validateInstallApproval(snapshot, device, { ...approval, diskId: invalidDisk }, true, now))
}
for (const flag of ['isRemovable', 'isReadOnly']) {
  const ineligible = { ...device, hardware: { ...device.hardware, disks: [{ ...device.hardware.disks[0], [flag]: true }] } }
  assert.throws(() => validateInstallApproval(snapshot, ineligible, approval, true, now), /disk/)
}
assert.throws(() => validateInstallApproval(snapshot, device, { ...approval, confirmation: 'erase' }, true, now), /ERASE/)
for (const key of ['', '-----BEGIN PRIVATE KEY-----', 'command="anything" ' + recoveryPublicKey, recoveryPublicKey + '\n' + recoveryPublicKey])
  assert.throws(() => validateInstallApproval(snapshot, device, { ...approval, recoveryPublicKey: key }, true, now), /SSH public key/)

const read = onboardingRequest(owner, { kind: 'snapshot' })
assert.equal(onboardingRequest(owner, { kind: 'managed' }).url, '/api/host/nodes')
const managedNode = { nodeId: deviceId, hostname: 'dev-server', state: 'Online', certificateExpiresAt: expiry, lastSeenAt: device.lastSeenAt,
  status: { osVersion: 'Debian GNU/Linux 13', uptimeSeconds: 60, loadAverage: 0.3, memoryTotalBytes: 8192, memoryAvailableBytes: 4096,
    storageTotalBytes: 102400, storageAvailableBytes: 51200, runtime: null },
  gpu: { cudaLine: null }, gpuWarning: null }
assert.deepEqual(parseManagedNodes([managedNode]), [managedNode])
const { gpu: _gpu, gpuWarning: _warning, ...olderServer } = managedNode
assert.deepEqual(parseManagedNodes([olderServer]), [managedNode])
const pinned = { ...managedNode, gpu: { cudaLine: 13 }, gpuWarning: 'Update the driver.' }
assert.deepEqual(parseManagedNodes([pinned]), [pinned])
assert.throws(() => parseManagedNodes([{ ...pinned, gpu: { ...pinned.gpu, cudaLine: 11 } }]))
const { runtime: _omitted, ...olderStatus } = managedNode.status
assert.deepEqual(parseManagedNodes([{ ...managedNode, status: olderStatus }]), [managedNode])
const runtime = { state: 'Ready', dockerVersion: '26.1.5', composeVersion: '2.26.1', gpuContainers: true, message: null,
  driverVersion: '610.43.03', cudaVersion: '13.3',
  gpus: [{ vendor: 'nvidia', model: 'NVIDIA CMP 170HX', memoryBytes: 68719476736, computeCapability: '8.0', uuid: 'GPU-cbeac6c4-3134-d34a-9fb5-fc0a0daf1981' }] }
const gpuNode = { ...managedNode, status: { ...managedNode.status, runtime } }
assert.deepEqual(parseManagedNodes([gpuNode]), [gpuNode])
const { driverVersion: _driver, cudaVersion: _cuda, ...olderRuntime } = runtime
const { uuid: _uuid, ...olderGpu } = runtime.gpus[0]
assert.deepEqual(parseManagedNodes([{ ...gpuNode, status: { ...gpuNode.status, runtime: { ...olderRuntime, gpus: [olderGpu] } } }])[0].status.runtime,
  { ...runtime, driverVersion: null, cudaVersion: null, gpus: [{ ...olderGpu, uuid: null }] })
assert.throws(() => parseManagedNodes([{ ...gpuNode, status: { ...gpuNode.status, runtime: { ...runtime, state: 'Healthy' } } }]))
assert.throws(() => parseManagedNodes([{ ...managedNode, state: 'InventedHealthy' }]))
const cudaRuntime = { ...runtime, cudaVersion: '13.3', gpus: [{ ...runtime.gpus[0], computeCapability: '12.1' }] }
assert.equal(cudaLineUnsupported(13, cudaRuntime), null)
assert.match(cudaLineUnsupported(13, { ...cudaRuntime, cudaVersion: '12.8' }), /supports up to CUDA 12\.8/)
assert.match(cudaLineUnsupported(13, { ...cudaRuntime, gpus: [{ ...cudaRuntime.gpus[0], computeCapability: '6.1' }] }), /needs 7\.5/)
assert.equal(cudaLineUnsupported(12, { ...cudaRuntime, gpus: [{ ...cudaRuntime.gpus[0], computeCapability: '6.1' }] }), null)
assert.notEqual(cudaLineUnsupported(12, { ...cudaRuntime, cudaVersion: null }), null)
assert.notEqual(cudaLineUnsupported(12, { ...cudaRuntime, gpus: [] }), null)
assert.throws(() => parseManagedNodes([{ ...managedNode, status: { ...managedNode.status, memoryAvailableBytes: 9000 } }]))
assert.equal(read.url, '/api/host/onboarding')
assert.deepEqual(read.init, { method: 'GET', credentials: 'same-origin', cache: 'no-store' })
const open = onboardingRequest(owner, { kind: 'open' })
assert.equal(open.url, '/api/host/onboarding/window')
assert.equal(open.init.body, '{"minutes":30}')
assert.equal(open.init.headers['X-CSRF-TOKEN'], 'test-csrf')
const close = onboardingRequest(owner, { kind: 'close' })
assert.equal(close.init.method, 'DELETE')
assert.equal(close.init.body, undefined)
const approve = onboardingRequest(owner, { kind: 'approve', deviceId, approval: { ...approval, ignored: 'must not leak' } })
assert.equal(approve.url, `/api/host/devices/${deviceId}/approve-install`)
assert.equal(approve.init.body, JSON.stringify(approval))
assert.equal(approve.init.headers['Content-Type'], 'application/json')
assert.equal(approve.init.credentials, 'same-origin')
assert.equal(approve.init.cache, 'no-store')
const reject = onboardingRequest(owner, { kind: 'reject', deviceId })
assert.equal(reject.url, `/api/host/devices/${deviceId}/reject`)
assert.equal(reject.init.body, undefined)
for (const kind of ['dismiss', 'restore']) {
  const request = onboardingRequest(owner, { kind, deviceId })
  assert.equal(request.url, `/api/host/devices/${deviceId}/${kind}`)
  assert.equal(request.init.method, 'POST')
  assert.equal(request.init.body, undefined)
  assert.equal(request.init.headers['X-CSRF-TOKEN'], 'test-csrf')
  assert.throws(() => onboardingRequest({ ...owner, csrfToken: null }, { kind, deviceId }), /security token/)
}
for (const action of [{ kind: 'snapshot' }, { kind: 'open' }, { kind: 'close' }, { kind: 'reject', deviceId },
  { kind: 'dismiss', deviceId }, { kind: 'restore', deviceId }, { kind: 'approve', deviceId, approval }]) {
  assert.throws(() => onboardingRequest({ ...owner, isOwner: false }, action), /Owner/)
  assert.throws(() => onboardingRequest({ ...owner, canAccess: false }, action), /Owner/)
  assert.throws(() => onboardingRequest({ ...owner, authenticated: false }, action), /Owner/)
}
assert.throws(() => onboardingRequest({ ...owner, csrfToken: null }, { kind: 'open' }), /security token/)
assert.throws(() => onboardingRequest(owner, { kind: 'reject', deviceId: '../onboarding/window' }))
assert.throws(() => onboardingRequest(owner, { kind: 'approve', deviceId, approval: { ...approval, confirmation: 'no' } }), /ERASE/)

const originalFetch = globalThis.fetch
try {
  let refreshed = 0
  let requested = 0
  const signal = new AbortController().signal
  globalThis.fetch = async (_url, init) => { requested++; assert.equal(init.signal, signal); return new Response(null, { status: 401 }) }
  await assert.rejects(() => requestOnboarding(owner, async () => { refreshed++ }, { kind: 'snapshot' }, signal), /expired/)
  assert.equal(refreshed, 1)
  await assert.rejects(() => requestOnboarding({ ...owner, isOwner: false }, async () => {}, { kind: 'snapshot' }, signal), /Owner/)
  assert.equal(requested, 1, 'Non-owners must not make Owner API requests.')
  globalThis.fetch = async () => new Response(JSON.stringify({ error: { message: 'Installation is disabled.' } }), { status: 409 })
  await assert.rejects(() => requestOnboarding(owner, async () => {}, { kind: 'approve', deviceId, approval }, signal), /Installation is disabled/)
  globalThis.fetch = async () => new Response('Forbidden', { status: 403 })
  await assert.rejects(() => requestOnboarding(owner, async () => {}, { kind: 'snapshot' }, signal), /Owner access/)
  assert.equal(refreshed, 1, 'A 403 must not be silently treated as an expired session.')
} finally {
  globalThis.fetch = originalFetch
}

console.log('Onboarding checks passed: strict real-data parsing, expiry countdown, per-disk approval, safe exact request scope, Owner denial, CSRF, and HTTP failures.')
