import assert from 'node:assert/strict'
import { containerState, describeUnmet, moveState, parseInventory, parseStackDetail, parseStackList, portRows, requirementForm, requirementList, stackState, unmetRequirement, validateStackDraft } from '../.checks/stackManagement.js'
import { parseRoute } from '../.checks/dashboard.js'

const at = '2026-09-22T16:00:00Z'
const nodeId = '13c7e7c6-12af-4ba1-b862-5701f857a8d2'
const container = { id: 'a'.repeat(64), name: 'lucia-media-sonarr-1', image: 'lscr.io/linuxserver/sonarr', state: 'running',
  status: 'Up 2 hours (healthy)', project: 'lucia-media', service: 'sonarr', ports: '0.0.0.0:8989->8989/tcp' }
const summary = { name: 'media', node: 'lucialab01', nodeId, desired: 'Running', revision: 3, createdAt: at, updatedAt: at, updatedBy: 'zack',
  reportedAt: at, status: { name: 'media', state: 'Running', appliedRevision: 3, message: null,
    services: [{ service: 'sonarr', state: 'running', health: 'healthy', image: 'lscr.io/linuxserver/sonarr', exitCode: null }] },
  containers: [container] }

const [stack] = parseStackList({ stacks: [summary] })
assert.equal(stack.containers[0].service, 'sonarr')
assert.deepEqual(stackState(stack), { label: 'Running', tone: 'green', detail: '1 container running on lucialab01.' })
assert.equal(stackState({ ...stack, status: { ...stack.status, appliedRevision: 2 } }).label, 'Applying changes')
assert.equal(stackState({ ...stack, status: { ...stack.status, state: 'Failed', message: 'pull access denied' } }).detail, 'pull access denied')
assert.equal(stackState({ ...stack, status: { ...stack.status, state: 'Degraded', services: [{ ...stack.status.services[0], state: 'exited' }] } }).label, 'Needs attention')
assert.equal(stackState({ ...stack, desired: 'Stopped' }).label, 'Stopping')
assert.equal(stackState({ ...stack, desired: 'Stopped', status: { ...stack.status, services: [] } }).label, 'Stopped')
assert.equal(stackState({ ...stack, status: { ...stack.status, state: 'Stopped', services: [] } }).label, 'Starting')
assert.equal(stackState({ ...stack, nodeId: null }).label, 'Server not found')
assert.equal(stackState({ ...stack, status: null, reportedAt: null }).label, 'Server not reporting')
assert.equal(stackState({ ...stack, status: null }).label, 'Waiting for server')
assert.throws(() => parseStackList({ stacks: [{ ...summary, name: '../x' }] }))
assert.throws(() => parseStackList({ stacks: [{ ...summary, desired: 'Maybe' }] }))

const detail = parseStackDetail({ stack: summary, compose: 'services: {}\n', env: 'TZ=UTC\n', manifest: { schemaVersion: 1, placement: { node: 'lucialab01' } } })
assert.deepEqual(detail.placement, { node: 'lucialab01', require: [] })
assert.deepEqual(parseStackDetail({ stack: summary, compose: '', env: '', manifest: { schemaVersion: 1, placement: { node: null, require: ['gpu'] } } }).placement, { node: null, require: ['gpu'] })
assert.deepEqual(stack.placement, { node: null, require: [] })
assert.equal(stack.move, null)
const moving = parseStackList({ stacks: [{ ...summary, move: { from: 'lucialab01', to: 'lucialab02', startedAt: at, startedBy: 'zack', progress: { bytes: 512 * 2 ** 20, total: 2 * 2 ** 30, updatedAt: at },
  target: { name: 'media', state: 'Receiving', appliedRevision: null, message: null, services: [] } } }] })[0]
assert.equal(stackState(moving).label, 'Moving')
assert.equal(moveState(moving.move, moving.status).percent, 25)
assert.match(moveState(moving.move).detail, /512 MB of about 2\.0 GB/)
assert.equal(moveState({ ...moving.move, progress: null, target: { ...moving.move.target, state: 'Failed', message: 'disk full' } }).label, 'Move stalled')
assert.match(moveState({ ...moving.move, progress: null, target: null }).detail, /^Stopping the app on lucialab01/)

const gib = 2 ** 30
const spark = { memoryTotalBytes: 120 * gib, runtime: { state: 'Ready', gpuContainers: true, gpus: [{ vendor: 'nvidia', model: 'NVIDIA GB10', memoryBytes: null, computeCapability: '12.1' }] } }
const lab = { memoryTotalBytes: 64 * gib, runtime: { state: 'Ready', gpuContainers: true, gpus: [{ vendor: 'nvidia', model: 'NVIDIA GeForce RTX 4090', memoryBytes: 23.6 * gib, computeCapability: '8.9' }, { vendor: 'nvidia', model: 'NVIDIA T400', memoryBytes: 2 * gib, computeCapability: '7.5' }] } }
assert.equal(unmetRequirement(['gpu', 'gpu.vram>=24G', 'gpu.compute>=8.6', 'memory>=64G'], lab), null)
assert.equal(unmetRequirement(['gpu.vram>=24G'], spark), 'gpu.vram>=24G')
assert.equal(unmetRequirement(['memory>=96G'], lab), 'memory>=96G')
assert.notEqual(unmetRequirement(['gpu.model~T400', 'gpu.vram>=8G'], lab), null)
assert.equal(unmetRequirement(['gpu'], { ...lab, runtime: { ...lab.runtime, gpuContainers: false } }), 'gpu')
assert.equal(unmetRequirement([], null), null)
const pinned13 = { gpu: { cudaLine: 13 }, gpuWarning: null }
assert.equal(unmetRequirement(['gpu', 'cuda=13'], spark, pinned13), null)
assert.equal(unmetRequirement(['cuda=12'], spark, pinned13), 'cuda=12')
assert.equal(unmetRequirement(['cuda=13'], spark), 'cuda=13')
assert.equal(unmetRequirement(['cuda=13'], spark, { ...pinned13, gpuWarning: 'Update the driver.' }), 'cuda=13')
assert.equal(describeUnmet('cuda=13'), 'not set to CUDA 13')
assert.equal(describeUnmet('gpu.vram>=24G'), 'no GPU with 24 GB of memory')
assert.equal(describeUnmet('gpu.vendor=nvidia'), 'no NVIDIA GPU')
assert.equal(describeUnmet('memory>=32'), 'less than 32 GB of memory')
const form = requirementForm(['gpu.vendor=nvidia', 'gpu.vram>=24G', 'memory>=16', 'gpu.model!=T400'])
assert.deepEqual(form, { gpu: true, vendor: 'nvidia', model: '', vram: '24', compute: '', cuda: '', memory: '16', other: ['gpu.model!=T400'] })
assert.deepEqual(requirementList(requirementForm(['cuda=12'])), ['gpu', 'cuda=12'])
assert.deepEqual(requirementList({ ...requirementForm(['cuda=12', 'cuda=13']), gpu: false }), [])
assert.deepEqual(requirementList(form), ['gpu', 'gpu.vendor=nvidia', 'gpu.vram>=24G', 'memory>=16G', 'gpu.model!=T400'])
assert.deepEqual(requirementList({ ...form, gpu: false }), ['memory>=16G'])
assert.equal(validateStackDraft('media', null, 'services: {}'), null)
assert.equal(parseInventory({ reportedAt: at, containers: [container], listeners: [{ protocol: 'tcp', address: '0.0.0.0', port: 8989 }] }).listeners[0].port, 8989)
const host = '0'.repeat(63) + 'b'
const inventory = parseInventory({ reportedAt: at, containers: [container, { ...container, id: host, name: 'plex', project: null, service: null, ports: '' }], listeners: [
  { protocol: 'tcp', address: '0.0.0.0', port: 8989, process: 'docker-proxy' }, { protocol: 'tcp', address: '::', port: 8989, process: 'docker-proxy' },
  { protocol: 'tcp', address: '0.0.0.0', port: 32400, process: 'Plex Media Serv', containerId: host },
  { protocol: 'tcp', address: '0.0.0.0', port: 111, process: 'systemd' }, { protocol: 'tcp', address: '0.0.0.0', port: 43017, process: 'kernel' },
  { protocol: 'udp', address: '127.0.0.1', port: 9999, process: 'mystery' }, { protocol: 'udp', address: '0.0.0.0', port: 5000, process: null }] })
const rows = portRows(inventory)
assert.deepEqual(rows.map(row => [row.port, row.owner.label]), [[111, 'NFS port mapper'], [5000, 'Unknown'], [8989, 'sonarr'], [9999, 'mystery'], [32400, 'plex'], [43017, 'Linux kernel']])
assert.deepEqual(rows[2].addresses, ['0.0.0.0', '::'])
assert.equal(rows[2].owner.app, 'media')
assert.match(rows[4].owner.detail, /not started by Lucia/)
assert.equal(rows[3].owner.known, false)
assert.throws(() => parseInventory({ reportedAt: at, containers: [], listeners: [{ protocol: 'icmp', address: '0.0.0.0', port: 1 }] }))

assert.deepEqual(containerState({ state: 'exited', status: 'Exited (137) 2 days ago' }), { label: 'Exited (137)', tone: 'failed' })
assert.deepEqual(containerState({ state: 'exited', status: 'Exited (0) 1 minute ago' }), { label: 'Stopped', tone: 'muted' })
assert.equal(containerState({ state: 'running', status: 'Up 1 minute (unhealthy)' }).label, 'Unhealthy')

assert.equal(validateStackDraft('media', 'lucialab01', 'services: {}'), null)
assert.match(validateStackDraft('Media', 'lucialab01', 'services: {}'), /lowercase/)
assert.match(validateStackDraft('media', '', 'services: {}'), /server/)
assert.match(validateStackDraft('media', 'lucialab01', '  '), /Compose/)

assert.deepEqual(parseRoute('#/apps'), { page: 'apps', view: 'list' })
assert.deepEqual(parseRoute('#/apps/containers'), { page: 'apps', view: 'containers' })
assert.deepEqual(parseRoute('#/apps/new'), { page: 'apps', view: 'new' })
assert.deepEqual(parseRoute('#/apps/media-automation'), { page: 'apps', view: 'app', name: 'media-automation' })
assert.deepEqual(parseRoute('#/apps/Bad_Name'), { page: 'not-found' })
console.log('stacks checks passed')
