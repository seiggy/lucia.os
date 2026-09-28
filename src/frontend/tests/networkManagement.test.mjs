import assert from 'node:assert/strict'
import { domainFailureSummary, parseAdGuardCertificate, parseAdGuardStatus, parseDomainPlan, parseDomainState, parseDomainZones, parseDomainOperations, parseUniFiStatus, parseDhcpReservations } from '../.checks/networkManagement.js'

const plan = {
  id: '11111111-1111-4111-8111-111111111111', reviewHash: 'a'.repeat(64), expiresAt: '2026-09-23T18:00:00Z',
  termsUrl: 'https://letsencrypt.org/documents/agreement.pdf', ingressAddress: '192.168.0.222', email: 'owner@example.com',
  naming: { domain: 'example.com', namespace: 'lab.example.com',
    serviceUrls: { lucia: 'https://dashboard.example.com', authentik: 'https://login.lab.example.com', spark: 'https://atlas.lab.example.com' },
    certificateNames: ['lab.example.com', '*.lab.example.com', 'dashboard.example.com'],
    localHostnames: ['dashboard.example.com', 'login.lab.example.com', 'atlas.lab.example.com'] },
  rewrites: [{ domain: 'dashboard.example.com', answer: '192.168.0.222', alreadyPresent: false }], blockers: [], warnings: ['Synthetic review.'],
}
assert.equal(parseDomainPlan(plan).naming.serviceUrls.lucia, 'https://dashboard.example.com')
for (const termsUrl of ['javascript:alert(1)', 'https://letsencrypt.org.evil.example/agreement', 'http://letsencrypt.org/terms'])
  assert.throws(() => parseDomainPlan({ ...plan, termsUrl }))
assert.throws(() => parseDomainPlan({ ...plan, naming: { ...plan.naming, serviceUrls: { ...plan.naming.serviceUrls, lucia: 'javascript:alert(1)' } } }))
assert.throws(() => parseDomainPlan({ ...plan, reviewHash: '' }))
const state = { adguardConfigured: true, cloudflareConfigured: true, workerReady: true, configured: false,
  defaults: { ingressAddress: '192.168.0.222', propagationSeconds: 60 }, plan, job: null,
  cloudflare: { configured: true, accountId: 'b'.repeat(32), expiresAt: null } }
assert.equal(parseDomainState(state).plan.rewrites.length, 1)
assert.throws(() => parseDomainState({ ...state, workerReady: 'yes' }))
assert.throws(() => parseDomainState({ ...state, job: { state: 'PretendActive' } }))
const job = { id: '22222222-2222-4222-8222-222222222222', state: 'Failed', phase: 'Staging',
  message: 'Synthetic certificate setup failure.', serviceUrls: plan.naming.serviceUrls, events: [],
  renewalError: null, nextRenewalAt: null, recoveryRequired: false, pendingRewrites: [], certificate: null }
const diagnosis = { code: 'CertificateWorkspacePermissions', title: 'Certificate workspace could not be read',
  summary: 'The certificate process could not read its managed workspace.',
  evidence: ['Sanitized process output: permission denied.'], nextSteps: ['Review setup before another attempt.'], canRetry: true }
const support = { state: 'Complete', model: 'synthetic-local-chat', explanation: 'Local, read-only advice.',
  error: null, updatedAt: '2026-09-23T17:00:00Z' }
const parseJob = values => parseDomainState({ ...state, job: { ...job, ...values } }).job
assert.equal(parseJob({}).diagnosis, null)
assert.equal(parseJob({}).support, null)
assert.equal(parseJob({ diagnosis: null, support: null }).support, null)
assert.deepEqual(parseJob({ diagnosis, support }).diagnosis, diagnosis)
assert.deepEqual(parseJob({ diagnosis, support }).support, support)
assert.equal(parseJob({ support: { ...support, explanation: '<script>alert("literal text")</script>\n**Not Markdown**' } }).support.explanation,
  '<script>alert("literal text")</script>\n**Not Markdown**')
for (const supportState of ['Queued', 'Running', 'Complete', 'Unavailable'])
  assert.equal(parseJob({ support: { ...support, state: supportState, model: null, explanation: null, error: null } }).support.state, supportState)
assert.equal(parseJob({ support: { ...support, state: 'Unavailable', model: null, explanation: null, error: 'No local chat model is loaded.' } }).support.model, null)
for (const malformed of [undefined, false, [], {}, { ...diagnosis, code: 1 }, { ...diagnosis, summary: null },
  { ...diagnosis, title: {} }, { ...diagnosis, canRetry: 'true' }, { ...diagnosis, nextSteps: null },
  { ...diagnosis, evidence: [null] }, { ...diagnosis, evidence: 'Not a list' }])
  assert.throws(() => parseJob({ diagnosis: malformed }))
for (const key of ['evidence', 'nextSteps']) {
  assert.equal(parseJob({ diagnosis: { ...diagnosis, [key]: Array(100).fill('x'.repeat(4096)) } }).diagnosis[key].length, 100)
  assert.throws(() => parseJob({ diagnosis: { ...diagnosis, [key]: Array(101).fill('x') } }))
  assert.throws(() => parseJob({ diagnosis: { ...diagnosis, [key]: ['x'.repeat(4097)] } }))
}
for (const malformed of [undefined, false, [], {}, { ...support, state: 'Available' }, { ...support, state: 'complete' },
  { ...support, model: {} }, { ...support, explanation: 1 }, { ...support, error: false },
  { ...support, updatedAt: 'not-a-date' }, { ...support, updatedAt: null }, { ...support, explanation: 'x'.repeat(16385) }])
  assert.throws(() => parseJob({ support: malformed }))
for (const field of Object.keys(support)) {
  const incomplete = { ...support }
  delete incomplete[field]
  assert.throws(() => parseJob({ support: incomplete }))
}
assert.equal(parseJob({ support: { ...support, explanation: 'x'.repeat(16384) } }).support.explanation.length, 16384)
assert.equal(domainFailureSummary(parseJob({ diagnosis, support })), diagnosis.summary)
assert.equal(domainFailureSummary(parseJob({ diagnosis, support: { ...support, state: 'Running' } })), diagnosis.summary)
assert.equal(domainFailureSummary(parseJob({ diagnosis, support: { ...support, explanation: '  ' } })), diagnosis.summary)
for (const code of ['unknown', 'certbot_no_diagnostic']) {
  const summary = 'Setup failed, but the specific error could not be captured.'
  assert.equal(domainFailureSummary(parseJob({ diagnosis: { ...diagnosis, code, summary, canRetry: false },
    support: { ...support, explanation: 'There was no error. You cannot retry.' } })), summary)
}
assert.equal(domainFailureSummary(parseJob({ diagnosis: { ...diagnosis, canRetry: false }, support })), diagnosis.summary)
assert.equal(domainFailureSummary(parseJob({ support })),
  'Lucia could not capture the specific error. This does not mean your DNS credentials are wrong.')
assert.equal(parseAdGuardStatus({ configured: false, baseUrl: null, username: null, allowInsecureHttp: false, version: null, lastVerifiedAt: null }).configured, false)
assert.throws(() => parseAdGuardStatus({ configured: true }))
assert.equal(parseAdGuardCertificate({ enabled: true, name: 'adguard.example.com', notAfter: '2026-09-01T00:00:00Z', checkedAt: null, pushedAt: null, error: null, coveredNames: ['adguard.example.com'] }).coveredNames[0], 'adguard.example.com')
assert.throws(() => parseAdGuardCertificate({ enabled: 'yes' }))
assert.equal(parseDomainZones([{ id: 'b'.repeat(32), name: 'example.com', status: 'active', nameServers: ['a.ns.cloudflare.com'] }]).length, 1)
assert.throws(() => parseDomainZones([{ id: 'bad' }]))
const operations = { checkedAt: '2026-09-23T19:10:00Z', namespace: 'lab.example.com', ingressAddress: '192.168.0.222',
  gatewayError: null, dnsError: null,
  routes: [{ name: 'Lucia', origin: 'https://lucia.lab.example.com', kind: 'Proxy', target: 'http://lucia-host:8080', configuration: 'Published' }],
  dnsRecords: [{ hostname: 'lucia.lab.example.com', expectedAddress: '192.168.0.222', state: 'Matches', ownership: 'Created by Lucia during setup',
    records: [{ domain: 'lucia.lab.example.com', answer: '192.168.0.222', enabled: true }], totalRecords: 1 }] }
assert.deepEqual(parseDomainOperations(operations), operations)
assert.equal(parseJob({}).renewalCheckedAt, null)
assert.equal(parseJob({ renewalOutcome: 'NotDue', renewalCheckedAt: '2026-09-23T19:00:00Z' }).renewalOutcome, 'NotDue')
assert.throws(() => parseJob({ renewalOutcome: 'SuccessMaybe' }))
assert.throws(() => parseDomainOperations({ ...operations, checkedAt: 'not a date' }))
for (const change of [{ origin: 'javascript:alert(1)' }, { kind: 'Command' }, { configuration: 'Healthy' }])
  assert.throws(() => parseDomainOperations({ ...operations, routes: [{ ...operations.routes[0], ...change }] }))
for (const change of [{ state: 'ProbablyFine' }, { totalRecords: 0 }, { records: [{ domain: 'x', answer: 'y', enabled: 'yes' }] }])
  assert.throws(() => parseDomainOperations({ ...operations, dnsRecords: [{ ...operations.dnsRecords[0], ...change }] }))
assert.equal(parseDomainOperations({ ...operations, dnsError: 'Unavailable',
  dnsRecords: [{ ...operations.dnsRecords[0], state: 'Unavailable', records: [], totalRecords: 0 }] }).dnsRecords[0].state, 'Unavailable')
console.log('DNS UI checks passed: safe review state, bounded support parsing, and one failure explanation without speculative unknown-cause advice.')

const unifi = { configured: true, baseUrl: 'https://192.168.0.1', site: 'default', certificateSha256: 'c'.repeat(64),
  networkVersion: '10.0.106', reserveNodeAddresses: true, lastVerifiedAt: '2026-09-23T18:00:00Z' }
assert.equal(parseUniFiStatus(unifi).certificateSha256, 'c'.repeat(64))
assert.throws(() => parseUniFiStatus({ ...unifi, certificateSha256: 'C'.repeat(64) }))
assert.throws(() => parseUniFiStatus({ ...unifi, reserveNodeAddresses: 'yes' }))
const dhcp = { enabled: true, checkedAt: '2026-09-23T18:00:00Z', error: null,
  nodes: [{ hostname: 'lab01', address: '192.168.0.241', mac: 'a0:36:bc:ad:d8:29', state: 'Reserved', host: false }] }
assert.equal(parseDhcpReservations(dhcp).nodes[0].state, 'Reserved')
assert.throws(() => parseDhcpReservations({ ...dhcp, nodes: [{ ...dhcp.nodes[0], state: 'Pretend' }] }))