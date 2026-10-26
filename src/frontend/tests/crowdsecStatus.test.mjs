import assert from 'node:assert/strict'
import { consoleSummary, isAppSec, parseAlerts, parseCrowdSecStatus, parseDecisions, remaining, scenarioName, summarize, validEnrollKey } from '../.checks/crowdsecStatus.js'
import { parseCatalog, requirementProblem } from '../.checks/stackManagement.js'
import { parseRoute } from '../.checks/dashboard.js'
import { findDestinations, visibleDestinations } from '../.checks/navigation.js'

const healthy = {
  state: 'healthy', message: null, protecting: true, observabilityInstalled: true, stack: 'crowdsec', node: 'lucialab01', address: '192.168.1.20', name: 'crowdsec.lab.example.com',
  version: 'v1.8.1-909b5157', image: 'crowdsecurity/crowdsec:v1.8.1@sha256:0f25', certificateExpiresAt: '2026-11-02T19:48:07Z',
  capi: { registered: true, authenticated: true, sharing: true, blocklist: true },
  console: { state: 'failed', message: 'the attachment key provided is not valid', at: '2026-10-26T18:58:00Z', plan: null, url: 'https://app.crowdsec.net' },
  bouncer: { name: 'lucia_gateway_0123456789ab', lastPull: '2026-10-26T18:59:50Z', revoked: false },
  metrics: { linesRead: 120, linesParsed: 118, linesUnparsed: 2, appSecProcessed: 40, appSecBlocked: 3 },
  harborEntries: 5, reportedAt: '2026-10-26T19:00:00Z',
}
const status = parseCrowdSecStatus(healthy)
assert.equal(status.metrics.appSecBlocked, 3)
assert.equal(status.name, 'crowdsec.lab.example.com')
assert.equal(status.console.state, 'failed')
assert.deepEqual(summarize(status), { tone: 'good', title: 'Protecting the gateway', detail: 'CrowdSec runs on lucialab01 and the gateway checks every request with it.' })
assert.equal(consoleSummary(status), 'The Console refused the key: the attachment key provided is not valid')
assert.match(consoleSummary(parseCrowdSecStatus({ ...healthy, console: { ...healthy.console, state: 'accepted', message: null, plan: 'premium' } })), /premium plan/)
assert.match(consoleSummary(parseCrowdSecStatus({ ...healthy, console: { ...healthy.console, state: 'pending', message: null } })), /Accept this engine/)

// Absent: the page explains what to install first, Observability before CrowdSec.
const absent = parseCrowdSecStatus({ state: 'absent', message: null, protecting: false, observabilityInstalled: false, stack: null, node: null, address: null,
  version: null, image: null, certificateExpiresAt: null, capi: null, console: { state: 'not-enrolled', message: null, at: null, plan: null, url: 'https://app.crowdsec.net' },
  bouncer: null, metrics: null, harborEntries: null, reportedAt: null })
assert.match(summarize(absent).detail, /install Observability first/)
assert.match(summarize({ ...absent, observabilityInstalled: true }).detail, /Install CrowdSec from Apps/)
assert.equal(summarize({ ...status, state: 'unreachable', message: null }).tone, 'warning')
assert.match(summarize({ ...status, state: 'waiting-certificate', message: null }).title, /certificate/)
const noDomain = parseCrowdSecStatus({ ...healthy, state: 'waiting-domain', name: null, message: null })
assert.equal(noDomain.name, null)
assert.match(summarize(noDomain).detail, /Domains page/)
assert.throws(() => parseCrowdSecStatus({ ...healthy, name: 'x'.repeat(254) }))
// A silent break in the log path is a warning, never "Protecting the gateway".
for (const state of ['needs-observability', 'no-logs']) {
  const warned = summarize(parseCrowdSecStatus({ ...healthy, state, message: null }))
  assert.equal(warned.tone, 'warning')
  assert.notEqual(warned.title, 'Protecting the gateway')
}
assert.equal(summarize(parseCrowdSecStatus({ ...healthy, state: 'needs-observability', message: null })).title, 'Update Observability')

// Strict parsing: unknown states, a foreign Console link, negative counters and oversized lists are refused.
for (const bad of [{ ...healthy, state: 'hacked' }, { ...healthy, console: { ...healthy.console, url: 'https://evil.example' } },
  { ...healthy, metrics: { ...healthy.metrics, linesRead: -1 } }, { ...healthy, protecting: 'yes' }, null, []])
  assert.throws(() => parseCrowdSecStatus(bad))

const alerts = parseAlerts({ alerts: [{ id: 2, scenario: 'crowdsecurity/http-probing', scope: 'Ip', value: '203.0.113.50', country: 'NL', asName: 'Example AS',
  at: '2026-10-26T20:02:14Z', events: 11, decisions: 1, simulated: false, kind: 'crowdsec', path: null }] })
assert.equal(alerts[0].value, '203.0.113.50')
assert.equal(alerts[0].path, null)
const blocks = parseAlerts({ alerts: [{ id: 3, scenario: 'crowdsecurity/vpatch-env-access', scope: 'Ip', value: '192.168.0.184', country: null, asName: null,
  at: '2026-10-26T20:30:20Z', events: 1, decisions: 0, simulated: false, kind: 'waf', path: '/.env' }] })
assert.equal(blocks[0].kind, 'waf')
assert.equal(blocks[0].path, '/.env')
assert.throws(() => parseAlerts({ alerts: [{ ...blocks[0], kind: undefined }] }))
assert.throws(() => parseAlerts({ alerts: [{ ...blocks[0], path: 'x'.repeat(257) }] }))
assert.equal(scenarioName(alerts[0].scenario), 'http-probing')
assert.ok(isAppSec('crowdsecurity/appsec-vpatch') && isAppSec('crowdsecurity/vpatch-env-access') && !isAppSec('crowdsecurity/http-probing'))
assert.throws(() => parseAlerts({ alerts: [{ ...alerts[0], events: 1.5 }] }))
assert.throws(() => parseAlerts({ alerts: Array.from({ length: 201 }, () => alerts[0]) }))

const decisions = parseDecisions({ decisions: [{ id: 14999, value: '203.0.113.50', scope: 'Ip', type: 'ban', origin: 'crowdsec',
  scenario: 'crowdsecurity/http-probing', until: '2026-10-27T20:09:00Z' }] })
assert.equal(decisions[0].id, 14999)
const now = Date.parse('2026-10-26T20:10:00Z')
assert.equal(remaining(decisions[0].until, now), '23 h 59 min left')
assert.equal(remaining('2026-10-26T20:40:00Z', now), '30 min left')
assert.equal(remaining('2026-11-02T20:10:00Z', now), '7 days left')
assert.equal(remaining(null, now), 'until lifted')

assert.ok(validEnrollKey('cmh5r7ygd0001jw08wdqjt4sq'))
for (const bad of ['short', 'has space in it', 'key\nLUCIA_CROWDSEC_TLS_KEY=x', 'a'.repeat(129)]) assert.ok(!validEnrollKey(bad))

// Installing CrowdSec needs Observability first.
assert.equal(requirementProblem({ requires: [{ id: 'observability', name: 'Observability', installed: false, current: true }], requiresWhy: 'CrowdSec reads the gateway’s access log through Observability’s collector.' }),
  'Install Observability first. CrowdSec reads the gateway’s access log through Observability’s collector.')
assert.equal(requirementProblem({ requires: [{ id: 'observability', name: 'Observability', installed: true, current: true }], requiresWhy: null }), null)
// An Observability too old to forward the access log has to be updated first.
assert.equal(requirementProblem({ requires: [{ id: 'observability', name: 'Observability', installed: true, current: false }], requiresWhy: null }),
  'Update Observability first: open it under Apps and choose Update.')
const catalogApp = { id: 'crowdsec', version: 1, name: 'CrowdSec', summary: 's', needs: 'n', require: [], fields: [], serverBound: false, usesAddress: false, servers: [] }
assert.equal(parseCatalog({ apps: [{ ...catalogApp, requires: [{ id: 'observability', name: 'Observability', installed: true, current: false }] }] })[0].requires[0].current, false)
assert.equal(parseCatalog({ apps: [{ ...catalogApp, requires: [{ id: 'observability', name: 'Observability', installed: true }] }] })[0].requires[0].current, true)
assert.throws(() => parseCatalog({ apps: [{ ...catalogApp, requires: [{ id: 'observability', name: 'Observability', installed: true, current: 'no' }] }] }))
assert.equal(requirementProblem({ requires: [], requiresWhy: null }), null)

// The page is owner-only, under Your lab, and found by the words people use.
assert.deepEqual(parseRoute('#/security'), { page: 'security' })
assert.ok(!visibleDestinations(false).some(item => item.page === 'security'))
assert.equal(findDestinations('crowdsec unban', true)[0]?.href, '#/security')
assert.equal(findDestinations('trusted addresses', true)[0]?.href, '#/security')
console.log('CrowdSec page checks passed.')
