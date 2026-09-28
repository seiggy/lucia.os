import assert from 'node:assert/strict'
import { containerState, describeUnmet, mountLabel, nasDraftProblem, parseNasList, shareNameFrom, shareState, moveState, parseInventory, parseStackDetail, parseStackList, portRows, requirementForm, requirementList, stackState, unmetRequirement, validateStackDraft } from '../.checks/stackManagement.js'
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

// Catalog
{
  const { parseCatalog, catalogReason, catalogDefaults, settingsProblem, freeName, envValue, parseStackSummary } = await import('../.checks/stackManagement.js')
  const gpu = { uuid: 'GPU-cbeac6c4-3134-d34a-9fb5-fc0a0daf1981', model: 'CMP 170HX', memoryBytes: 8 * 1024 ** 3, unsupported: null }
  const old = { uuid: 'GPU-00000000-0000-0000-0000-000000000000', model: 'GTX 1060', memoryBytes: 6 * 1024 ** 3, unsupported: 'Too old.' }
  const catalog = parseCatalog({ apps: [{ id: 'local-ai', version: 1, name: 'Local AI', summary: 's', needs: 'n', require: ['gpu.vendor=nvidia'], serverBound: true,
    fields: [{ id: 'gpus', label: 'GPUs', kind: 'gpus', default: null, help: 'h' }, { id: 'port', label: 'Port', kind: 'port', default: '8080', help: null }],
    servers: [{ nodeId, hostname: 'lucialab01', unmet: null, reason: null, gpus: [gpu, old] },
      { nodeId, hostname: 'spark', unmet: 'gpu.vendor=nvidia', reason: null, gpus: null },
      { nodeId, hostname: 'nas', unmet: null, reason: 'Pin a CUDA line first.', gpus: [] }] }] })
  const [app] = catalog
  assert.equal(catalogReason(app.servers[0]), null)
  assert.match(catalogReason(app.servers[1]), /^[A-Z].*\.$/)
  assert.equal(catalogReason(app.servers[2]), 'Pin a CUDA line first.')
  assert.throws(() => parseCatalog({ apps: [{ ...app, fields: [{ id: 'x', label: 'X', kind: 'color', default: null, help: null }] }] }))
  const defaults = catalogDefaults(app, app.servers[0])
  assert.deepEqual(defaults, { gpus: gpu.uuid, port: '8080' })
  assert.equal(settingsProblem(app, defaults), null)
  assert.match(settingsProblem(app, { ...defaults, port: '70000' }), /1 to 65535/)
  assert.match(settingsProblem(app, { ...defaults, gpus: '' }), /at least one GPU/)
  const { fieldShown } = await import('../.checks/stackManagement.js')
  const [engines] = parseCatalog({ apps: [{ ...app, fields: [...app.fields,
    { id: 'engine', label: 'Engine', kind: 'choice', default: 'lucia', options: [{ value: 'lucia', label: 'Lucia Inference', help: 'h' }, { value: 'vllm', label: 'vLLM', help: 'h' }] },
    { id: 'library', label: 'Model library', kind: 'text', optional: true },
    { id: 'library-port', label: 'Library port', kind: 'port', default: '8081', when: 'engine=vllm' },
    { id: 'vllm-model', label: 'vLLM model', kind: 'hidden', optional: true }] }] })
  const values = catalogDefaults(engines, engines.servers[0])
  assert.equal(values.engine, 'lucia')
  assert.equal(settingsProblem(engines, values), null, 'an empty optional library is fine')
  assert.match(settingsProblem(engines, { ...values, engine: 'ollama' }), /Choose the engine/)
  assert.equal(settingsProblem(engines, { gpus: gpu.uuid }), null, 'missing newer settings fall back to their defaults')
  const shown = settings => engines.fields.filter(field => fieldShown(field, settings)).map(field => field.id)
  assert.deepEqual(shown(values), ['gpus', 'port', 'engine', 'library'])
  assert.deepEqual(shown({ ...values, engine: 'vllm' }), ['gpus', 'port', 'engine', 'library', 'library-port'])
  const [shared] = parseCatalog({ apps: [{ ...app, fields: [{ id: 'engine', label: 'Engine', kind: 'choice', default: 'lucia', options: [{ value: 'lucia', label: 'L', help: 'h' }] },
    { id: 'library-port', label: 'Library port', kind: 'port', default: '8081', when: 'engine=vllm|llamacpp' }] }] })
  assert.deepEqual(['lucia', 'vllm', 'llamacpp'].map(engine => fieldShown(shared.fields[1], { engine })), [false, true, true])
  assert.throws(() => parseCatalog({ apps: [{ ...app, fields: [{ id: 'engine', label: 'Engine', kind: 'choice', default: 'lucia' }] }] }), 'a choice needs options')
  assert.equal(freeName('local-ai', ['media']), 'local-ai')
  assert.equal(freeName('local-ai', ['local-ai', 'local-ai-2']), 'local-ai-3')
  assert.equal(envValue('A=1\nLUCIA_INFERENCE_KEY=abc=\n', 'LUCIA_INFERENCE_KEY'), 'abc=')
  assert.equal(envValue('A=1', 'B'), null)
  assert.equal(parseStackSummary(summary).template, null)
  const managed = parseStackSummary({ ...summary, template: { id: 'local-ai', version: 1, latest: 2, name: 'Local AI', settings: { port: '8080' }, serverBound: true } })
  assert.deepEqual(managed.template, { id: 'local-ai', version: 1, latest: 2, name: 'Local AI', settings: { port: '8080' }, serverBound: true })
  assert.deepEqual(parseRoute('#/apps/catalog'), { page: 'apps', view: 'catalog' })
  assert.deepEqual(parseRoute('#/apps/install/local-ai'), { page: 'apps', view: 'install', name: 'local-ai' })
  assert.deepEqual(parseRoute('#/apps/install/local-ai/lucialab01'), { page: 'apps', view: 'install', name: 'local-ai', node: 'lucialab01' })
  assert.deepEqual(parseRoute('#/ai/models/on/lucialab01/find'), { page: 'ai-models', view: 'find', server: 'lucialab01' })
  assert.deepEqual(parseRoute('#/ai/models/on/lucialab01'), { page: 'ai-models', view: 'library', server: 'lucialab01' })
}
{
  const share = (mounts, usedBy = []) => ({ name: 'Media', path: '/var/nfs/shared/Media', mountPath: '/mnt/lucia/nas/unas/Media', usedBy, mounts })
  const [nas] = parseNasList({ servers: [{ id: 'unas', kind: 'nfs', host: '192.168.0.172', hasPassword: false, updatedAt: at, updatedBy: 'zack',
    shares: [share([{ node: 'lucialab01', status: { nas: 'unas', share: 'Media', state: 'Mounted' } }, { node: 'lucialab02', status: null }], ['jellyfin'])] }] })
  assert.equal(nas.username, null)
  assert.deepEqual(nas.shares[0].mounts, [{ node: 'lucialab01', state: 'Mounted', message: null }, { node: 'lucialab02', state: null, message: null }])
  assert.throws(() => parseNasList({ servers: [{ ...nas, kind: 'ftp' }] }))
  assert.throws(() => parseNasList({ servers: [{ ...nas, shares: [share([{ node: 'x', status: { state: 'Exploded' } }])] }] }))
  assert.equal(mountLabel(nas.shares[0].mounts[1]), 'Not reported')

  const mounted = { node: 'a', state: 'Mounted', message: null }, failed = { node: 'b', state: 'Failed', message: 'access denied' }
  assert.deepEqual(shareState(share([])), { label: 'No servers yet', tone: 'muted' })
  assert.deepEqual(shareState(share([mounted])), { label: 'Mounted', tone: 'green' })
  assert.deepEqual(shareState(share([mounted, { ...mounted, node: 'c' }])), { label: 'Mounted on all 2', tone: 'green' })
  assert.deepEqual(shareState(share([mounted, failed])), { label: "Can't mount on 1 server", tone: 'amber' })
  assert.deepEqual(shareState(share([failed])), { label: "Can't mount", tone: 'amber' })
  assert.deepEqual(shareState(share([mounted, { node: 'c', state: 'Pending', message: null }])), { label: 'Mounting', tone: 'accent' })
  assert.deepEqual(shareState(share([mounted, { node: 'c', state: null, message: null }])), { label: 'Mounted on 1 of 2 servers', tone: 'muted' })

  assert.equal(shareNameFrom('/var/nfs/shared/Media/'), 'Media')
  assert.equal(shareNameFrom('/volume1/my photos'), 'myphotos')
  assert.equal(shareNameFrom('.hidden'), 'hidden')

  const draft = { id: 'unas', kind: 'nfs', host: '192.168.0.172', username: '', password: '', shares: [{ name: 'Media', path: '/var/nfs/shared/Media' }] }
  assert.equal(nasDraftProblem(draft, null), null)
  assert.match(nasDraftProblem({ ...draft, id: 'My NAS' }, null), /lowercase/)
  assert.match(nasDraftProblem({ ...draft, host: 'http://nas' }, null), /address/)
  assert.match(nasDraftProblem({ ...draft, shares: [{ name: 'x', path: '/a/../etc' }] }, null), /export path/)
  assert.match(nasDraftProblem({ ...draft, shares: [draft.shares[0], { name: 'media', path: '/b' }] }, null), /different folder/)
  assert.match(nasDraftProblem({ ...draft, shares: [] }, null), /at least one/)
  const smb = { ...draft, kind: 'smb', username: 'zack', shares: [{ name: 'Photos', path: 'Photos' }] }
  assert.match(nasDraftProblem(smb, null), /password/)
  assert.equal(nasDraftProblem(smb, { ...nas, kind: 'smb', hasPassword: true }), null)
  assert.match(nasDraftProblem({ ...smb, password: 'secret', shares: [{ name: 'p', path: '/Photos' }] }, null), /share name/)

  assert.equal(describeUnmet('nas=unas/Media'), "the NAS share unas/Media isn't mounted")
  assert.deepEqual(requirementForm(['gpu', 'nas=unas/Media']).other, [])
  assert.equal(unmetRequirement(['nas=unas/Media'], { memoryTotalBytes: 1, runtime: null }), null)
  assert.deepEqual(parseRoute('#/settings/storage'), { page: 'storage-settings' })
}