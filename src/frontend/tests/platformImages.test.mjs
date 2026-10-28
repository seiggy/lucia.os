import assert from 'node:assert/strict'
import { disruption, kindLabel, parsePlatformImages, patchServices, policyLabel, securityBadge, stepLabel, waiting } from '../.checks/platformImages.js'

const digest = (character) => 'sha256:' + character.repeat(64)
const offer = (tag, kind, extra = {}) => ({ tag, digest: digest('b'), kind, applicable: kind !== 'major', notes: `https://github.com/example/releases/tag/${tag}`,
  upgradeNotes: null, firstSeenAt: '2026-10-01T00:00:00Z', ...extra })
const image = (service, tag, policy, offers, extra = {}) => ({ service, name: service, policy, image: service, tag, digest: digest('a'), override: false,
  security: ['authentik', 'gateway', 'ca'].includes(service), disruption: 'Restarts.', offers, problem: null, ...extra })
const payload = {
  worker: { state: 'ready', installCommand: 'python3 platform_update_worker.py install' },
  recorded: true, recordedAt: '2026-10-27T10:00:00Z', problem: null, checkedAt: '2026-10-28T08:00:00Z',
  images: [
    image('authentik', '2026.8.5', 'authentik', [offer('2026.8.6', 'patch'),
      offer('2026.10.1', 'release-line', { upgradeNotes: 'https://docs.goauthentik.io/releases/2026.10/' })]),
    image('gateway', 'v3.7.16', 'minor', [offer('v4.0.1', 'major')]),
    image('database', '16.15-alpine', 'patch', [offer('16.15-alpine', 'rebuild')]),
    image('node-exporter', 'v1.12.1', 'minor', [offer('v1.13.0', 'minor')]),
    image('directory', '2.6.10', 'patch', [], { override: true }),
  ],
  plugin: { name: 'CrowdSec bouncer plugin', version: 'v1.8.1', policy: 'plugin', latest: null, notes: null, applicable: false },
  status: { generation: 3, state: 'succeeded', step: null, message: 'Updated.', startedAt: '2026-10-28T07:00:00Z', finishedAt: '2026-10-28T07:04:00Z',
    rolledBack: false, backup: null, updates: [], history: [] },
  pending: false,
  securityWaiting: [{ service: 'authentik', name: 'Authentik', tag: '2026.8.6', days: 9 }, { service: 'ca', name: 'step-ca', tag: '0.30.3', days: 8 }],
}
const state = parsePlatformImages(payload)
assert.equal(state.images.length, 5)
assert.equal(state.images[0].offers[1].upgradeNotes, 'https://docs.goauthentik.io/releases/2026.10/')
assert.equal(state.status.state, 'succeeded')

// "Update all patches" takes patches and rebuilds, never minors, release lines or majors.
assert.deepEqual(patchServices(state.images), ['authentik', 'database'])
assert.match(disruption(['database']), /10 to 15 seconds/)
assert.match(disruption(['ca']), /Certificates/)
assert.match(disruption(['node-exporter', 'telemetry-relay']), /Only telemetry/)
assert.equal(kindLabel('release-line'), 'Next release line')
assert.equal(policyLabel('authentik'), 'Patches, then one release line at a time')
assert.equal(stepLabel('health-checking'), 'Waiting for every service to come back')
assert.equal(stepLabel(null), 'Starting')
assert.equal(waiting('2026-10-01T00:00:00Z', Date.parse('2026-10-09T12:00:00Z')), 8)
assert.equal(securityBadge(state), 'Authentik 2026.8.6 has waited 9 days (and 1 more)')
assert.equal(securityBadge({ securityWaiting: [] }), null)

// Release notes open in a new tab, so only https links survive; everything else is parsed strictly.
assert.equal(parsePlatformImages({ ...payload, images: [image('gateway', 'v3.7.16', 'minor', [offer('v3.7.17', 'patch', { notes: 'javascript:alert(1)' })])] })
  .images[0].offers[0].notes, null)
assert.throws(() => parsePlatformImages({ ...payload, images: [image('gateway', 'v3.7.16', 'minor', [offer('v3.7.17', 'downgrade')])] }), /incomplete platform image data/)
assert.throws(() => parsePlatformImages({ ...payload, worker: { state: 'up' } }), /incomplete/)
assert.throws(() => parsePlatformImages({ ...payload, securityWaiting: [{ service: 'ca', name: 'step-ca', tag: '1', days: -1 }] }), /incomplete/)
assert.equal(parsePlatformImages({ ...payload, status: null }).status, null)

console.log('Platform image checks passed.')
