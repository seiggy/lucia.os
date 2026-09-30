import { createServer } from 'node:http'
import { readFile } from 'node:fs/promises'
import { resolve, extname, sep } from 'node:path'
import { inferenceFixture, resetInferenceFixture } from './inference-management-fixture.mjs'
import { dnsFixture, resetDnsFixture } from './dns-management-fixture.mjs'
import { assistantFixture, resetAssistantFixture } from './assistant-fixture.mjs'

const root = resolve('src/frontend/dist')
const anonymous = { enabled: true, authenticated: false, username: null, displayName: null, isOwner: false, canAccess: false, csrfToken: null }
let mode = 'anonymous'
let modelsMode = 'ready'
let onboardingMode = 'unready'
let metricsMode = 'healthy'
let fixtureTime = Date.now()
let discoveryDismissedAt = null
let discoveryUpdatedAt = null
const requests = []
const server = createServer(async (request, response) => {
  const path = new URL(request.url, 'http://127.0.0.1:4175').pathname
  const json = (data, status = 200) => {
    response.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' })
    response.end(JSON.stringify(data))
  }
  if (path === '/__fixture') {
    mode = new URL(request.url, 'http://127.0.0.1:4175').searchParams.get('mode') ?? 'anonymous'
    modelsMode = new URL(request.url, 'http://127.0.0.1:4175').searchParams.get('models') ?? 'ready'
    onboardingMode = new URL(request.url, 'http://127.0.0.1:4175').searchParams.get('onboarding') ?? 'unready'
    metricsMode = new URL(request.url, 'http://127.0.0.1:4175').searchParams.get('metrics') ?? 'healthy'
    fixtureTime = Date.now()
    discoveryDismissedAt = null
    discoveryUpdatedAt = null
    resetInferenceFixture()
    resetDnsFixture()
    resetAssistantFixture(new URL(request.url, 'http://127.0.0.1:4175').searchParams.get('assistant') ?? 'ready')
    if (mode === 'anonymous') requests.length = 0
    return json({ mode })
  }
  if (path === '/__requests') return json(requests)
  if (path === '/api/auth/session') {
    if (mode === 'error') return json({}, 503)
    return json(mode === 'anonymous' ? anonymous : {
      ...anonymous, authenticated: true, username: 'fixture-owner', displayName: 'Fixture owner',
      isOwner: mode === 'owner', canAccess: mode === 'owner' || mode === 'member', csrfToken: 'fixture-csrf-request',
    })
  }
  if (await inferenceFixture(request, path, mode, json)) return
  if (await dnsFixture(request, path, mode, json)) return
  if (await assistantFixture(request, response, path, mode, json)) return
  if (path === '/api/host/ssh-keys/github/synthetic-owner') {
    if (mode !== 'owner') return json({ error: { message: 'Synthetic Owner access is required.' } }, 403)
    const publicKey = 'ssh-ed25519 ' + Buffer.from('0000000B7373682D6564323535313900000020D75A980182B10AB7D54BFED3C964073A0EE172F3DAA62325AF021A68F707511A', 'hex').toString('base64')
    return json({ username: 'synthetic-owner', keys: [{ publicKey, algorithm: 'ssh-ed25519', fingerprint: 'SHA256:' + 'A'.repeat(43) }], unsupportedCount: 0 })
  }
  const visibilityAction = path.match(/^\/api\/host\/devices\/11111111-1111-4111-8111-111111111111\/(dismiss|restore)$/)
  if (visibilityAction && request.method === 'POST') {
    if (mode !== 'owner' || request.headers['x-csrf-token'] !== 'fixture-csrf-request')
      return json({ error: { message: 'Synthetic Owner access and CSRF are required.' } }, 403)
    if (onboardingMode !== 'rejected') return json({ error: { message: 'Only rejected discoveries can be dismissed.' } }, 409)
    discoveryUpdatedAt = new Date().toISOString()
    discoveryDismissedAt = visibilityAction[1] === 'dismiss' ? discoveryUpdatedAt : null
    return json({ dismissedAt: discoveryDismissedAt })
  }
  if (path === '/api/host/onboarding') {
    if (mode !== 'owner') return json({ error: { message: 'Synthetic owner access is required.' } }, 403)
    const discovered = onboardingMode === 'discovered' || onboardingMode === 'installable'
    const installable = onboardingMode === 'installable'
    const rejected = onboardingMode === 'rejected'
    const timestamp = new Date(fixtureTime).toISOString()
    const expiresAt = new Date(fixtureTime + 30 * 60 * 1000).toISOString()
    return json({
      window: { isOpen: discovered, expiresAt: discovered ? expiresAt : null },
      readiness: { canDiscover: discovered, canInstall: installable, reasons: installable ? [] : [discovered
        ? 'Synthetic fixture: directory-backed node enrollment is not qualified.'
        : 'Synthetic fixture: boot services have not been configured.'] },
      devices: discovered || rejected ? [{
        id: '11111111-1111-4111-8111-111111111111', phase: rejected ? 'Rejected' : 'Discovered',
        verificationCode: 'ABCD-EF01-2345', inventoryRevision: 1,
        discoveredAt: timestamp, updatedAt: discoveryUpdatedAt ?? timestamp, lastSeenAt: timestamp,
        lastHeartbeatAt: null, heartbeatFreshness: 'Unknown', discoveryExpiresAt: expiresAt,
        taskId: null, statusMessage: null, dismissedAt: discoveryDismissedAt,
        hardware: {
          architecture: 'x86_64', bootMode: 'uefi', secureBoot: false,
          manufacturer: 'Lucia test fixture', model: 'Synthetic UEFI server', serialNumber: 'SIMULATED-NO-HARDWARE',
          hardwareUuid: '22222222-2222-4222-8222-222222222222', cpuModel: 'Synthetic processor',
          logicalCpuCount: 16, memoryBytes: 32 * 1024 ** 3,
          interfaces: [{ name: 'eth0', macAddress: '02:00:00:00:00:01', addresses: ['192.0.2.10'] }],
          disks: [{
            id: '/dev/disk/by-id/ata-SYNTHETIC-SSD-1', path: '/dev/sda', model: 'Synthetic SSD',
            serial: 'SIMULATED-1', sizeBytes: 512_000_000_000, isReadOnly: false, isRemovable: false,
          }],
        },
      }] : [], tasks: [],
    })
  }
  if (path === '/api/host/telemetry') {
    if (mode !== 'owner') return json({ error: { message: 'Synthetic owner access is required.' } }, 403)
    if (metricsMode === 'error') return json({ error: { message: 'Synthetic metrics connection failure.' } }, 503)
    const end = Date.now()
    const history = metricsMode === 'starting' ? [] : Array.from({ length: 361 }, (_, index) => ({
      timestamp: new Date(end - (360 - index) * 10000).toISOString(),
      cpuPercent: 12 + Math.sin(index / 12) * 4, cpuCores: 20, loadAverage: 0.34,
      memoryTotalBytes: 128 * 1024 ** 3, memoryAvailableBytes: 64 * 1024 ** 3,
      storageTotalBytes: 4 * 1024 ** 4, storageAvailableBytes: 3 * 1024 ** 4,
      networkInterface: 'enP7s7', receiveBytesPerSecond: 10240, transmitBytesPerSecond: 4096,
      gpuName: 'NVIDIA GB10', gpuPercent: metricsMode === 'partial' ? null : Math.max(0, 20 * Math.sin(index / 18)),
      gpuTemperatureCelsius: metricsMode === 'partial' ? null : 37,
      gpuPowerWatts: metricsMode === 'partial' ? null : 10.1,
      unifiedMemory: true, uptimeSeconds: 90061,
      unavailable: metricsMode === 'partial' ? ['Some GPU sensors are not reported by the driver'] : [],
    }))
    return json({
      enabled: true, state: metricsMode === 'starting' ? 'Starting' : metricsMode === 'partial' ? 'Partial' : 'Healthy',
      message: metricsMode === 'starting' ? 'Waiting for the first host readings.'
        : metricsMode === 'partial' ? 'Some host readings are unavailable.' : 'Spark is reporting normally.',
      sampleIntervalSeconds: 10, retentionSeconds: 3600, latest: history[history.length - 1] ?? null, history,
    })
  }
  if (path.startsWith('/v1/') || path.startsWith('/api/playground'))
    requests.push({ url: request.url, method: request.method, headers: request.headers })
  if (path === '/v1/models' && modelsMode === 'unavailable') return json({ error: { message: 'Synthetic host unavailable.' } }, 503)
  if (path === '/v1/models' && modelsMode === 'empty') return json({ data: [] })
  if (path === '/v1/models') return json({ data: [{
    id: 'synthetic-model', capabilities: ['chat'], backend: 'ggml_cuda',
    context_length: 8192, native_context_length: 262144, max_output_tokens: 2048,
  }] })
  if (path === '/v1/chat/completions') {
    response.writeHead(200, { 'Content-Type': 'text/event-stream' })
    return response.end('data: {"choices":[{"delta":{"content":"Synthetic authenticated response."},"finish_reason":null}]}\n\ndata: {"choices":[{"delta":{},"finish_reason":"stop"}]}\n\ndata: [DONE]\n\n')
  }
  const file = resolve(root, path === '/' ? 'index.html' : '.' + path)
  if (!file.startsWith(root + sep)) return json({ error: 'not found' }, 404)
  try {
    const original = await readFile(file)
    const data = extname(file) === '.html' ? Buffer.from(original.toString().replace('<body>',
      '<body><div role="note" style="padding:8px 16px;background:#fff2dc;color:#785417;font:14px system-ui;text-align:center">UI test fixture — no live accounts, DNS changes, or certificates. Do not enter real credentials.</div>')) : original
    response.writeHead(200, { 'Content-Type': { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml' }[extname(file)] ?? 'application/octet-stream' })
    response.end(data)
  } catch (error) {
    if (error.code === 'ENOENT') return json({ error: 'not found' }, 404)
    console.error('Fixture file read failed.', error)
    json({ error: 'fixture failure' }, 500)
  }
})
server.listen(4175, '127.0.0.1', () => console.log('Synthetic SSO fixture only: http://127.0.0.1:4175'))
