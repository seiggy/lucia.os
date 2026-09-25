const zoneId = 'a'.repeat(32)
let adguard = { configured: false, baseUrl: null, username: null, allowInsecureHttp: false, version: null, lastVerifiedAt: null }
let cloudflare = { configured: false, accountId: null, verifiedAt: null, expiresAt: null, zoneCount: 0 }
let plan = null
let job = null
let supportReads = null
let configured = false
let operationsMode = 'ready'
const explanation = 'The certificate check stopped because its local workspace was not readable. This is not evidence of a bad Cloudflare token.\n\nReview the confirmed next steps before another setup attempt. I have not changed DNS, run commands, or completed recovery.\n\nSynthetic literal log sample: <strong>permission denied</strong>. **This is plain text, not Markdown.**'
function completedSupport() {
  return { state: 'Complete', model: 'synthetic-local-chat',
    explanation: job?.diagnosis?.code === 'gateway_configuration_ignored'
      ? 'The certificate was issued, but the gateway ignored the generated configuration file. The next reviewed attempt can use the corrected format and reload notification.'
      : job?.diagnosis?.code === 'unknown' ? 'No errors were found. Retry is blocked because canRetry is false.' : explanation,
    error: null, updatedAt: new Date().toISOString() }
}

export function resetDnsFixture() {
  adguard = { configured: false, baseUrl: null, username: null, allowInsecureHttp: false, version: null, lastVerifiedAt: null }
  cloudflare = { configured: false, accountId: null, verifiedAt: null, expiresAt: null, zoneCount: 0 }
  plan = null; job = null; supportReads = null; configured = false; operationsMode = 'ready'
}
export async function dnsFixture(request, path, mode, json) {
  if (!path.startsWith('/api/host/connections/adguard') && !path.startsWith('/api/host/domains')) return false
  if (mode !== 'owner') { json({ error: { message: 'Synthetic Owner access required.' } }, 403); return true }
  let body
  if (request.method !== 'GET') {
    if (request.headers['x-csrf-token'] !== 'fixture-csrf-request') { json({ error: { message: 'Synthetic CSRF required.' } }, 403); return true }
    let content = ''
    for await (const chunk of request) { content += chunk; if (content.length > 32768) throw new Error('Fixture body limit') }
    body = content ? JSON.parse(content) : null
  }
  if (path === '/api/host/domains/__fixture/active' && request.method === 'POST') {
    configured = true
    operationsMode = body?.mode ?? 'ready'
    adguard = { configured: true, baseUrl: 'https://adguard.example.com', username: 'synthetic-owner', allowInsecureHttp: false, version: 'v0.107.79', lastVerifiedAt: new Date().toISOString() }
    cloudflare = { configured: true, accountId: 'b'.repeat(32), verifiedAt: new Date().toISOString(), expiresAt: null, zoneCount: 1 }
    job = { id: '22222222-2222-4222-8222-222222222222', state: 'Active', phase: operationsMode === 'renewing' ? 'Renewing' : 'Active',
      message: 'Synthetic active domain.', serviceUrls: { lucia: 'https://lucia.lab.example.com', authentik: 'https://auth.lab.example.com', spark: 'https://atlas.lab.example.com' },
      certificate: { notAfter: new Date(Date.now() + 60 * 86400000).toISOString(), dnsNames: ['lab.example.com', '*.lab.example.com'], certificateSha256: 'c'.repeat(64) },
      nextRenewalAt: new Date(Date.now() + 12 * 3600000).toISOString(), renewalError: operationsMode === 'renewal-failed' ? 'Synthetic certificate renewal could not reach Cloudflare.' : null,
      renewalCheckedAt: operationsMode === 'renewal-failed' ? new Date().toISOString() : null, renewalOutcome: operationsMode === 'renewal-failed' ? 'Failed' : null,
      events: [{ at: new Date().toISOString(), phase: 'Issuing', message: 'Synthetic certificate issuance.' }, { at: new Date().toISOString(), phase: 'Active', message: 'Synthetic addresses activated.' }],
      recoveryRequired: false, pendingRewrites: [], diagnosis: null, support: null }
    plan = null; supportReads = null
    json(job)
  } else if (path === '/api/host/domains/overview') {
    if (!configured || !job) { json({ error: { message: 'No synthetic active domain.' } }, 409); return true }
    const names = ['Lucia', 'Authentik', 'Spark alias']
    const origins = Object.values(job.serviceUrls)
    json({ checkedAt: new Date().toISOString(), namespace: 'lab.example.com', ingressAddress: '192.168.0.222',
      gatewayError: null, dnsError: operationsMode === 'dns-unavailable' ? 'Synthetic AdGuard connection is unavailable.' : null,
      routes: origins.map((origin, index) => ({ name: names[index], origin, kind: index === 2 ? 'Redirect' : 'Proxy',
        target: index === 2 ? origins[0] + '/' : index === 0 ? 'http://lucia-host:8080' : 'http://identity-server:9000', configuration: 'Published' })),
      dnsRecords: origins.map((origin, index) => ({ hostname: new URL(origin).hostname, expectedAddress: '192.168.0.222',
        state: operationsMode === 'dns-unavailable' ? 'Unavailable' : operationsMode === 'drift' && index === 0 ? 'Conflict' : operationsMode === 'drift' && index === 1 ? 'Missing' : 'Matches',
        ownership: index === 2 ? 'Pre-existing record' : 'Created by Lucia during setup',
        records: operationsMode === 'dns-unavailable' || operationsMode === 'drift' && index === 1 ? [] :
          [{ domain: new URL(origin).hostname, answer: operationsMode === 'drift' && index === 0 ? '192.168.0.99' : '192.168.0.222', enabled: true }],
        totalRecords: operationsMode === 'dns-unavailable' || operationsMode === 'drift' && index === 1 ? 0 : 1 })) })
  } else if (path === '/api/host/domains/__fixture/failure' && request.method === 'POST') {
    adguard = { configured: true, baseUrl: 'http://192.0.2.10:3000', username: 'synthetic-owner',
      allowInsecureHttp: true, version: 'v0.107.79', lastVerifiedAt: new Date().toISOString() }
    cloudflare = { configured: true, accountId: 'b'.repeat(32), verifiedAt: new Date().toISOString(), expiresAt: null, zoneCount: 1 }
    const recoveryRequired = body?.recoveryRequired === true
    configured = body?.configured === true
    job = { id: '22222222-2222-4222-8222-222222222222', state: 'Failed', phase: 'Staging',
      message: 'Synthetic staging certificate check failed. No live DNS changes were made.',
      serviceUrls: { lucia: 'https://lucia.lab.example.com', authentik: 'https://auth.lab.example.com', spark: 'https://atlas.lab.example.com' },
      events: [{ at: new Date().toISOString(), phase: 'Staging', message: 'Synthetic sanitized Certbot output: permission denied reading workspace.' }],
      renewalError: null, nextRenewalAt: null, certificate: null, recoveryRequired,
      pendingRewrites: recoveryRequired ? ['lucia.lab.example.com'] : [],
      diagnosis: { code: 'CertificateWorkspacePermissions', title: 'Certificate workspace could not be read',
        summary: 'The certificate process could not read its managed workspace. DNS setup has not completed.',
        evidence: ['Sanitized Certbot output: permission denied reading workspace.', 'The managed workspace has a restrictive folder mode.'],
        nextSteps: ['Review setup before another attempt.', 'Finish any ownership-checked recovery shown below before creating a new setup.'],
        canRetry: true },
      support: null }
    if (body?.scenario === 'gateway') {
      job.phase = 'Ingress'
      job.message = 'Synthetic gateway probe failed.'
      job.events = [{ at: new Date().toISOString(), phase: 'Ingress', message: 'Synthetic HTTPS probe: domain configuration was not loaded.' }]
      job.diagnosis = { code: 'gateway_configuration_ignored', title: 'The certificate is ready, but Lucia did not load it',
        summary: 'Your certificate was issued successfully. Lucia saved the HTTPS configuration in a file format the gateway ignores, so the new addresses never became ready. Your existing addresses were kept.',
        evidence: ['The gateway accepts yml, yaml, and toml, not json.', 'Nested configuration changes need a reload notification.'],
        nextSteps: ['The configuration format and reload notification are corrected. Review setup again and approve the next attempt.',
          'Keep your existing Cloudflare token. This is not a token error.'], canRetry: true }
    }
    if (body?.scenario === 'unknown') job.diagnosis = { code: 'unknown', title: 'Lucia did not capture enough detail',
      summary: 'Setup reported an error, but its specific cause was not retained. This is a diagnostics gap in Lucia, not evidence that your DNS credentials are wrong.',
      evidence: ['The failure was recorded without a specific diagnostic cause.'],
      nextSteps: ['Keep your existing credentials. Open Technical details to see where setup stopped.'], canRetry: false }
    if (body?.issuedCertificate) job.certificate = { notAfter: '2026-12-22T18:00:00Z',
      dnsNames: ['lab.example.com', '*.lab.example.com'], certificateSha256: 'c'.repeat(64) }
    if (['Active', 'Activating', 'Running'].includes(body?.jobState)) job.state = body.jobState
    if (body?.renewalScheduled) job.nextRenewalAt = '2026-09-24T06:00:00Z'
    job.support = completedSupport()
    supportReads = null
    if (body?.supportState === 'Unavailable') job.support = { state: 'Unavailable', model: null, explanation: null,
      error: 'No local chat model is loaded. Load one in Models, then retry the explanation. No model was loaded automatically.', updatedAt: new Date().toISOString() }
    if (body?.supportState === 'Queued' || body?.supportState === 'Running') {
      job.support = { ...job.support, state: body.supportState, explanation: null }
      supportReads = body.supportState === 'Queued' ? 0 : 1
    }
    if (body?.supportState === 'Missing') { delete job.support; delete job.diagnosis }
    json(job)
  } else if (path.startsWith('/api/host/connections/adguard')) {
    if (request.method === 'PUT') adguard = { configured: true, baseUrl: body.baseUrl, username: body.username,
      allowInsecureHttp: body.allowInsecureHttp, version: 'v0.107.79', lastVerifiedAt: new Date().toISOString() }
    if (request.method === 'DELETE') adguard = { configured: false, baseUrl: null, username: null, allowInsecureHttp: false, version: null, lastVerifiedAt: null }
    json(adguard)
  } else if (path === '/api/host/domains/cloudflare/credentials') {
    if (request.method === 'PUT') cloudflare = { configured: true, accountId: body.accountId, verifiedAt: new Date().toISOString(), expiresAt: null, zoneCount: 1 }
    json(cloudflare)
  } else if (path === '/api/host/domains/cloudflare/zones') {
    json([{ id: zoneId, name: 'example.com', status: 'active', nameServers: ['a.ns.cloudflare.com', 'b.ns.cloudflare.com'] }])
  } else if (path === '/api/host/domains/status') {
    if (supportReads !== null && job) {
      if (supportReads >= 2) { job.support = completedSupport(); supportReads = null }
      else { job.support.state = supportReads === 0 ? 'Queued' : 'Running'; supportReads++ }
    }
    json({ adguardConfigured: adguard.configured, cloudflareConfigured: cloudflare.configured, workerReady: true, configured,
      defaults: { ingressAddress: '192.168.0.222', propagationSeconds: 60 }, cloudflare, plan, job, active: null })
  } else if (path === `/api/host/domains/jobs/${job?.id}/diagnose` && request.method === 'POST') {
    if (job.state !== 'Failed' || body !== null) { json({ error: { message: 'Expected a failed job and no request body.' } }, 400); return true }
    job.support = { state: 'Queued', model: null, explanation: null, error: null, updatedAt: new Date().toISOString() }
    supportReads = 0
    json(job, 202)
  } else if (path === '/api/host/domains/plan') {
    const namespace = body.subdomain + '.example.com'
    const names = Object.values(body.serviceUrls).map(url => new URL(url).hostname)
    plan = { id: '11111111-1111-4111-8111-111111111111', reviewHash: 'b'.repeat(64), expiresAt: new Date(Date.now() + 1800000).toISOString(),
      termsUrl: 'https://letsencrypt.org/documents/synthetic-agreement.pdf', ingressAddress: body.ingressAddress, email: body.email,
      naming: { domain: 'example.com', namespace, serviceUrls: body.serviceUrls, certificateNames: [namespace, '*.' + namespace, ...names.filter(name => !name.endsWith('.' + namespace))], localHostnames: names },
      rewrites: names.map(domain => ({ domain, answer: body.ingressAddress, alreadyPresent: false })),
      blockers: [], warnings: ['Synthetic review only. No DNS provider, ACME account, or real certificate is changed.'] }
    json(plan)
  } else if (path === '/api/host/domains/start') {
    if (!body.acceptTerms || !body.acceptDnsChanges) { json({ error: { message: 'Explicit consent missing.' } }, 400); return true }
    job = { id: '22222222-2222-4222-8222-222222222222', state: 'Queued', phase: 'Queued', message: 'Synthetic task accepted; no live DNS changes.',
      serviceUrls: plan.naming.serviceUrls, events: [{ at: new Date().toISOString(), phase: 'Queued', message: 'Synthetic review approved.' }],
      renewalError: null, nextRenewalAt: null, certificate: null, recoveryRequired: false, pendingRewrites: [] }
    json(job, 202)
  } else json({ error: { message: 'Unknown synthetic DNS endpoint.' } }, 404)
  return true
}
