import { useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { Icon } from './Icon'
import { DomainOverview } from './DomainOverview'
import { domainFailureSummary, parseDomainPlan, parseDomainState, parseDomainZones } from './networkManagement'
import type { DomainPlan as Plan, DomainState as State, DomainZone as Zone, ServiceUrls } from './networkManagement'
import './NetworkSettings.css'

export function DomainOnboarding({ session, refreshSession }: {
  session: AuthenticationSession; refreshSession: () => Promise<void>
}) {
  const [state, setState] = useState<State | null>(null)
  const [step, setStep] = useState(0)
  const [accountId, setAccountId] = useState('')
  const [token, setToken] = useState('')
  const [zones, setZones] = useState<Zone[]>([])
  const [zoneId, setZoneId] = useState('')
  const [subdomain, setSubdomain] = useState('homelab')
  const [sparkName, setSparkName] = useState('spark')
  const [ingressAddress, setIngressAddress] = useState('')
  const [email, setEmail] = useState('')
  const [propagation, setPropagation] = useState('60')
  const [customUrls, setCustomUrls] = useState<Partial<ServiceUrls>>({})
  const [plan, setPlan] = useState<Plan | null>(null)
  const [terms, setTerms] = useState(false)
  const [changes, setChanges] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [statusError, setStatusError] = useState<string | null>(null)
  const [supportError, setSupportError] = useState<string | null>(null)
  const [supportBusy, setSupportBusy] = useState(false)
  const [loading, setLoading] = useState(true)
  const [showToken, setShowToken] = useState(false)
  const running = useRef(false)
  const reading = useRef(false)
  const observedJob = useRef<string | null>(null)
  const supportRequest = useRef<{ jobId: string; updatedAt: string | null } | null>(null)
  const alive = useRef(true)
  const heading = useRef<HTMLHeadingElement>(null)
  const zone = zones.find(item => item.id === zoneId)
  const ns = zone ? `${subdomain}.${zone.name}` : 'homelab.example.com'
  const suggested: ServiceUrls = { lucia: `https://lucia.${ns}`, authentik: `https://auth.${ns}`, spark: `https://${sparkName}.${ns}` }
  const urls = { ...suggested, ...customUrls }
  const support = state?.job?.support
  const supportPending = supportBusy || support?.state === 'Queued' || support?.state === 'Running'
  const failure = state?.job?.state === 'Failed' ? domainFailureSummary(state.job) : null
  const localAdvice = failure && state?.job?.diagnosis?.code
    && !['unknown', 'certbot_no_diagnostic'].includes(state.job.diagnosis.code) && support?.state === 'Complete'
    && support.explanation?.trim() !== failure ? support.explanation?.trim() : null
  useEffect(() => {
    heading.current?.focus({ preventScroll: true })
    heading.current?.scrollIntoView({ block: 'start', behavior: 'instant' })
  }, [step])

  useEffect(() => {
    alive.current = true
    const controller = new AbortController()
    if (!session.isOwner) { setLoading(false); return }
    const load = async () => {
      if (running.current || reading.current || document.visibilityState === 'hidden') return
      reading.current = true
      try {
        const response = await ownerRequest(session, refreshSession, '/api/host/domains/status', 'GET', undefined, controller.signal)
        const next = parseDomainState(await response.json())
        if (alive.current && !controller.signal.aborted) {
          setState(next)
          const requested = supportRequest.current
          if (requested && (next.job?.id !== requested.jobId || (next.job.support
            && next.job.support.updatedAt !== requested.updatedAt
            && ['Complete', 'Unavailable'].includes(next.job.support.state)))) {
            supportRequest.current = null
            setSupportBusy(false)
          }
          if (next.cloudflare.accountId) setAccountId(current => current || next.cloudflare.accountId!)
          if (next.defaults.ingressAddress) setIngressAddress(current => current || next.defaults.ingressAddress!)
          if (next.job && observedJob.current !== next.job.id && ['Queued', 'Running', 'Activating', 'Active', 'Failed'].includes(next.job.state)) {
            observedJob.current = next.job.id
            setStep(3)
          }
          setStatusError(null)
        }
      } catch (failure) {
        if (alive.current && !controller.signal.aborted) setStatusError(failure instanceof Error ? failure.message : 'DNS status could not be refreshed.')
      } finally { reading.current = false; if (alive.current) setLoading(false) }
    }
    void load()
    const interval = setInterval(() => { void load() }, 5000)
    return () => { alive.current = false; controller.abort(); clearInterval(interval) }
  }, [session, refreshSession])

  async function perform(action: () => Promise<void>) {
    if (running.current) return
    running.current = true; setBusy(true); setError(null)
    try { await action() }
    catch (failure) { if (alive.current) setError(failure instanceof Error ? failure.message : 'DNS setup could not complete this step.') }
    finally { running.current = false; if (alive.current) setBusy(false) }
  }
  async function readZones() {
    const response = await ownerRequest(session, refreshSession, '/api/host/domains/cloudflare/zones')
    const value = parseDomainZones(await response.json())
    if (alive.current) { setZones(value); setStep(1); setToken('') }
  }
  async function connect() {
    await ownerRequest(session, refreshSession, '/api/host/domains/cloudflare/credentials', 'PUT', { accountId, token })
    if (alive.current) { setToken(''); setShowToken(false) }
    if (state?.configured) { if (alive.current) setStep(3); return }
    await readZones()
  }
  async function prepare() {
    const response = await ownerRequest(session, refreshSession, '/api/host/domains/plan', 'POST', {
      zoneId, subdomain: subdomain.trim(), sparkName: sparkName.trim(), serviceUrls: urls,
      ingressAddress: ingressAddress.trim(), email: email.trim(), propagationSeconds: Number(propagation),
    })
    const value = parseDomainPlan(await response.json())
    if (alive.current) { setPlan(value); setTerms(false); setChanges(false); setStep(2) }
  }
  async function start() {
    if (!plan) return
    await ownerRequest(session, refreshSession, '/api/host/domains/start', 'POST', {
      planId: plan.id, reviewHash: plan.reviewHash, acceptTerms: terms, acceptDnsChanges: changes,
    })
    if (alive.current) setStep(3)
  }
  async function recover() {
    if (!state?.job) return
    await ownerRequest(session, refreshSession, `/api/host/domains/jobs/${state.job.id}/recover`, 'POST')
    if (alive.current) setError(null)
  }
  async function requestExplanation() {
    if (!state?.job || supportRequest.current || supportPending) return
    supportRequest.current = { jobId: state.job.id, updatedAt: support?.updatedAt ?? null }
    setSupportBusy(true); setSupportError(null)
    try {
      await ownerRequest(session, refreshSession, `/api/host/domains/jobs/${state.job.id}/diagnose`, 'POST')
    } catch (failure) {
      supportRequest.current = null
      if (alive.current) {
        setSupportBusy(false)
        setSupportError(failure instanceof Error ? failure.message : 'The local explanation could not be requested. Try again.')
      }
    }
  }
  if (!session.isOwner) return <div className="page-intro"><h1>Owner access is needed.</h1><p>Your lab owner manages DNS and certificates.</p></div>
  if (state?.configured && state.job?.state !== 'Failed' && !showToken) return <>
    {statusError && <p className="network-error" role="alert">{statusError}</p>}
    <DomainOverview state={state} session={session} refreshSession={refreshSession}
      replaceToken={() => { setShowToken(true); setStep(0) }} />
  </>
  return <>
    <div className="page-intro"><h1>DNS & certificates</h1><p>Your domain, your service names. Use Cloudflare for certificate validation and AdGuard for addresses that stay inside your lab.</p></div>
    {(error || statusError) && <p className="network-error" role="alert">{error ?? statusError}</p>}
    {loading && <p className="network-notice" role="status">Checking configured connections…</p>}
    {state && !state.adguardConfigured && <section className="surface network-section"><h2>Connect AdGuard first</h2>
      <p>This workflow uses your configured local DNS provider. Connecting an existing AdGuard instance now also leaves room for a Lucia-managed instance later.</p>
      <a className="button primary" href="#/settings/adguard">Configure AdGuard <Icon name="arrow" /></a></section>}
    {state && (state.adguardConfigured || state.job !== null) && <>
      {!state.configured && <ol className="network-steps" aria-label="DNS setup progress">{['Provider', 'Names', 'Review', 'Set up'].map((label, index) =>
        <li key={label} aria-current={step === index ? 'step' : undefined}>{label}</li>)}</ol>}
      {!state.workerReady && !state.configured && <p className="network-warning">The scoped activation service is not ready. You can prepare connection settings, but setup cannot begin until the service is running.</p>}
      {step === 0 && <section className="surface network-section">
        {state.configured && <button className="text-link" onClick={() => { setToken(''); setShowToken(false); setStep(3) }}>Back to domain overview</button>}
        <h2 className="network-step-heading" tabIndex={-1} ref={heading}>Choose your domain provider</h2><p><strong>Cloudflare</strong> · The supported DNS provider for v1.</p>
        <p className="section-note">Your domain can remain registered elsewhere. Its authoritative DNS must already be an active, full-setup Cloudflare zone.</p>
        {state.cloudflareConfigured && !showToken ? <><p className="section-note">A Cloudflare account token is configured.</p>
          <div className="network-actions"><button className="button primary" disabled={busy} onClick={() => void perform(readZones)}>Choose a domain <Icon name="arrow" /></button>
            <button className="text-link" disabled={busy} onClick={() => setShowToken(true)}>Replace account token</button></div></>
          : <>
            <details open><summary>Create a Cloudflare account API token</summary>
              <p className="section-note">Start with the <strong>Edit zone DNS</strong> template, then make the two changes below. The template alone is not enough: it grants DNS Write to all zones by default.</p>
              <ol className="network-help">
                <li>Open <strong>Manage Account → Account API Tokens → Create Token</strong>, not My Profile’s user tokens. Name it <strong>Lucia DNS & certificates</strong>. Creating an account token requires the account’s Super Administrator role.</li>
                <li>Choose the <strong>Edit zone DNS</strong> template. Under <strong>Permission policies</strong>, you will see a policy with <strong>All zones in your account</strong> and <strong>DNS Write</strong>.</li>
                <li>Open that policy and change its zone selection from <strong>All zones</strong> to <strong>only the domain you want to use</strong>. Keep <strong>DNS Write</strong>. Select the root zone, such as <strong>example.com</strong>, not <strong>homelab.example.com</strong> or a wildcard.</li>
                <li>Select <strong>Add policy</strong>. Grant <strong>Zone Read</strong> and select the same single zone. Your policies should now contain:
                  <dl className="network-permissions">
                    <dt>DNS Write — your selected zone only</dt><dd>Create and remove certificate-validation TXT records.</dd>
                    <dt>Zone Read — the same zone only</dt><dd>Find and verify the domain’s zone details. Choose <strong>Zone Read</strong>, not DNS Read; DNS Write already includes reading DNS records.</dd>
                  </dl>
                </li>
                <li>Choose the <strong>Token expiration</strong> setting that fits your policy. If you set an expiry, replace the token in Lucia before that date so certificate renewal can continue.</li>
                <li>Select <strong>Continue to summary</strong>. Confirm only DNS Write and Zone Read for your chosen domain—no all-domain, account-administration, or registrar access—then select <strong>Create Token</strong>. Copy the token now; Cloudflare shows its secret only once.</li>
                <li>In Cloudflare’s Search, enter <strong>Copy account ID</strong> and select that result. Paste the account ID and token below. Use the account ID, not the zone ID.</li>
              </ol>
              <p className="section-note">Older Cloudflare instructions call these permissions Zone → DNS → Edit and Zone → Zone → Read. Verification here is read-only; the later staging DNS challenge checks actual write access.</p>
            </details>
            <form onSubmit={event => { event.preventDefault(); void perform(connect) }}>
              <label>Cloudflare account ID<input value={accountId} onChange={event => setAccountId(event.target.value.trim())} autoComplete="off" maxLength={32} required disabled={busy} /></label>
              <label>Account API token<input type="password" value={token} onChange={event => setToken(event.target.value)} autoComplete="new-password" maxLength={4096} required disabled={busy} /></label>
              <p className="section-note">The token is encrypted on your Spark, never saved in browser storage or placed in command-line arguments. Zone-scoped DNS Edit can still change unrelated records in that zone; keep its scope narrow.</p>
              <button className="button primary" disabled={busy || !accountId || !token}>{busy ? 'Verifying…' : 'Verify account and continue'}<Icon name="arrow" /></button>
            </form>
          </>}
      </section>}
      {step === 1 && <section className="surface network-section">
        <h2 className="network-step-heading" tabIndex={-1} ref={heading}>Name your lab</h2><form onSubmit={event => { event.preventDefault(); void perform(prepare) }}>
          <label>Cloudflare domain<select value={zoneId} onChange={event => setZoneId(event.target.value)} required disabled={busy}>
            <option value="">Choose an accessible domain</option>{zones.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
          {!zones.length && <p className="network-warning">No active full-setup zones were returned. Check account ID, token permissions, and domain nameservers.</p>}
          <div className="network-fields"><label>Local subdomain<input value={subdomain} onChange={event => setSubdomain(event.target.value)} placeholder="homelab" required maxLength={190} disabled={busy} /></label>
            <label>Spark name<input value={sparkName} onChange={event => setSparkName(event.target.value)} placeholder="atlas" required maxLength={63} disabled={busy} /></label></div>
          <p className="section-note">{zone ? 'Namespace' : 'Example namespace'}: <strong>{ns}</strong>. Enter a relative subdomain such as “homelab,” without “*.” A wildcard certificate covers one level below it; the namespace itself receives a separate certificate name.</p>
          <details><summary>Customize service URLs</summary><p>These are suggestions, not fixed names. Use distinct HTTPS hostnames inside the selected domain. Paths and custom ports are not supported by this gateway profile.</p>
            {(['lucia', 'authentik', 'spark'] as const).map(service => <label className="network-custom-url" key={service}>{service === 'lucia' ? 'Lucia URL' : service === 'authentik' ? 'Authentik URL' : 'Spark alias URL'}
              <input type="url" value={urls[service]} onChange={event => setCustomUrls(current => ({ ...current, [service]: event.target.value }))} disabled={busy} required /></label>)}
            <button type="button" className="text-link" disabled={busy} onClick={() => setCustomUrls({})}>Restore suggested URLs</button>
          </details>
          <div className="network-fields"><label>Spark LAN IPv4 address<input value={ingressAddress} onChange={event => setIngressAddress(event.target.value)} placeholder="192.168.0.222" required disabled={busy} /></label>
            <label>ACME contact email<input type="email" value={email} onChange={event => setEmail(event.target.value)} autoComplete="email" required maxLength={254} disabled={busy} /></label></div>
          <details><summary>DNS propagation wait</summary><label className="network-custom-url">Seconds<input type="number" min={10} max={600} value={propagation} onChange={event => setPropagation(event.target.value)} disabled={busy} /></label></details>
          <div className="network-actions"><button className="button primary" disabled={busy || !zoneId || !subdomain || !sparkName || !email || !ingressAddress}>{busy ? 'Checking DNS…' : 'Review DNS setup'}<Icon name="arrow" /></button>
            <button type="button" className="button secondary" disabled={busy} onClick={() => setStep(0)}>Back</button></div>
        </form>
      </section>}
      {step === 2 && plan && <section className="surface network-section">
        <h2 className="network-step-heading" tabIndex={-1} ref={heading}>Review the exact changes</h2>
        <ul className="network-review-list">{Object.entries(plan.naming.serviceUrls).map(([service, url]) =>
          <li key={service}><strong>{service === 'lucia' ? 'Lucia' : service === 'authentik' ? 'Authentik' : 'Spark alias'}</strong><span>{url}</span></li>)}</ul>
        <h3>Certificate names</h3><ul className="network-help">{plan.naming.certificateNames.map(name => <li key={name}>{name}</li>)}</ul>
        <h3>Local AdGuard rewrites</h3><ul className="network-review-list">{plan.rewrites.map(item => <li key={item.domain}>
          <strong>{item.domain} → {item.answer}</strong><span>{item.alreadyPresent ? 'Already present; preserved rather than adopted.' : 'Add this exact local record.'}</span></li>)}</ul>
        <ul className="network-help">{plan.warnings.map(warning => <li key={warning}>{warning}</li>)}</ul>
        {plan.blockers.length > 0 && <div className="network-warning"><strong>Setup is blocked</strong><ul>{plan.blockers.map(blocker => <li key={blocker}>{blocker}</li>)}</ul></div>}
        <label className="network-checkbox"><input type="checkbox" checked={terms} onChange={event => setTerms(event.target.checked)} disabled={busy} />
          <span>I accept the <a className="text-link" href={plan.termsUrl} target="_blank" rel="noreferrer">Let’s Encrypt subscriber agreement</a> for the ACME account using {plan.email}.</span></label>
        <label className="network-checkbox"><input type="checkbox" checked={changes} onChange={event => setChanges(event.target.checked)} disabled={busy} />
          <span>I approve these local rewrites, staging and production DNS-01 TXT changes, public certificate-name disclosure, and the reviewed service URL activation.</span></label>
        <div className="network-actions"><button className="button primary" disabled={busy || !!statusError || !terms || !changes || plan.blockers.length > 0 || !state.workerReady} onClick={() => void perform(start)}>Set up DNS & certificates <Icon name="shield" /></button>
          <button className="button secondary" disabled={busy} onClick={() => setStep(1)}>Edit names</button></div>
      </section>}
      {step === 3 && <section className="surface network-section">
        <h2 className="network-step-heading" tabIndex={-1} ref={heading}>{state.job?.state === 'Active' ? 'Your lab has its own addresses.'
          : failure ? state.job?.certificate ? 'Certificate issued, but new addresses are not active.' : 'DNS setup did not finish.'
            : 'Setting up your domain'}</h2>
        <p role="status" className={failure ? 'network-failure-text' : undefined}>{failure ?? state.job?.message ?? 'Waiting for the host to report the accepted task…'}</p>
        {state.job?.state === 'Failed' && failure && <>
          {state.job.recoveryRequired ? <div className="network-actions">
            <p>Finish recovery before creating another setup.</p>
            <button className="button secondary" disabled={busy || state.configured} onClick={() => void perform(recover)}>Retry ownership-checked cleanup</button>
          </div> : !state.configured && <div className="network-actions">
            {state.job.diagnosis?.nextSteps[0] && <p>{state.job.diagnosis.nextSteps[0]}</p>}
            <button className="button secondary" disabled={busy} onClick={() => { setStep(0); setPlan(null) }}>Review setup again</button>
          </div>}
          <details className="network-technical-details" key={state.job.id}><summary>Technical details</summary>
            {state.job.diagnosis && <>
              {state.job.diagnosis.evidence.length > 0 && <><h3>What the checks found</h3>
                <ul className="network-help">{state.job.diagnosis.evidence.map((item, index) => <li key={index}>{item}</li>)}</ul></>}
              {state.job.diagnosis.nextSteps.slice(state.job.recoveryRequired || state.configured ? 0 : 1).length > 0 && <><h3>Additional guidance</h3>
                <ul className="network-help">{state.job.diagnosis.nextSteps.slice(state.job.recoveryRequired || state.configured ? 0 : 1).map((item, index) => <li key={index}>{item}</li>)}</ul></>}
            </>}
            {state.job.recoveryRequired && <><p>Uncertain DNS additions are not adopted or deleted automatically.</p>
              {state.job.pendingRewrites.length > 0 && <ul className="network-help">{state.job.pendingRewrites.map(name => <li key={name}>Review the AdGuard rule for {name}.</li>)}</ul>}</>}
            {state.job.certificate && <p className="section-note">Issued certificate expires {new Date(state.job.certificate.notAfter).toLocaleDateString()}.</p>}
            {state.job.renewalError && <p className="network-error">{state.job.renewalError}</p>}
            <h3>Setup progress</h3><ol className="network-help">{state.job.events.map((event, index) =>
              <li key={index}><strong>{event.phase}</strong> · {event.message}<br /><small>{new Date(event.at).toLocaleTimeString()}</small></li>)}</ol>
            {localAdvice && <><h3>Local AI explanation</h3><p className="network-support-text">{localAdvice}</p></>}
            <p className="section-note">Primary explanation: setup checks · {support?.model ?? 'No local model reported'} · AI advice is read-only, not a recovery decision.</p>
            <div role="status" aria-live="polite" aria-atomic="true">
              {supportPending ? <p>{support?.state === 'Running' ? 'Writing a local explanation…' : 'Waiting for a local explanation…'} This can take up to 90 seconds.</p>
                : support?.state === 'Unavailable' ? <p className="network-error">{support.error ?? 'A local explanation is unavailable. Load a local chat model, then retry.'}</p>
                  : support?.state === 'Complete' && !support.explanation ? <p className="section-note">The local model returned no explanation. You can retry.</p> : null}
            </div>
            {supportError && <p className="network-error" role="alert">{supportError}</p>}
            <button className="button secondary" disabled={supportPending || !!statusError} onClick={() => void requestExplanation()}>
              {supportPending ? 'Preparing explanation…' : support ? 'Retry local explanation' : 'Request local explanation'}
            </button>
          </details>
        </>}
        {!failure && state.job?.renewalError && <p className="network-warning">{state.job.renewalError}</p>}
        {state.cloudflare.expiresAt && Date.parse(state.cloudflare.expiresAt) <= Date.now() + 7 * 24 * 60 * 60 * 1000
          && <p className="network-warning">The Cloudflare token expires {new Date(state.cloudflare.expiresAt).toLocaleString()}. Replace it to keep certificate renewal available.</p>}
        {!failure && state.job?.certificate && <p className="section-note">Certificate expires {new Date(state.job.certificate.notAfter).toLocaleDateString()}.
          {['Active', 'Activating'].includes(state.job.state) && state.job.nextRenewalAt && <> Next renewal check: {new Date(state.job.nextRenewalAt).toLocaleString()}.</>}</p>}
        {!failure && state.job?.events && <details open><summary>Setup progress</summary><ol className="network-help">{state.job.events.map((event, index) =>
          <li key={index}><strong>{event.phase}</strong> · {event.message}<br /><small>{new Date(event.at).toLocaleTimeString()}</small></li>)}</ol></details>}
        {state.job?.state === 'Active' && <div className="network-actions"><a className="button primary" href={state.job.serviceUrls.lucia}>Open Lucia <Icon name="arrow" /></a>
          <a className="text-link" href={state.job.serviceUrls.authentik}>Open Authentik <Icon name="arrow" /></a>
          <button className="text-link" onClick={() => { setShowToken(true); setStep(0) }}>Replace Cloudflare token</button></div>}
        {!failure && state.job?.recoveryRequired && <div className="network-warning"><p>Finish recovery before creating another setup. Uncertain DNS additions are not adopted or deleted automatically.</p>
          {state.job.pendingRewrites.length > 0 && <ul>{state.job.pendingRewrites.map(name => <li key={name}>Review the AdGuard rule for {name}.</li>)}</ul>}
          <button className="button secondary" disabled={busy || state.configured || state.job.state !== 'Failed'} onClick={() => void perform(recover)}>Retry ownership-checked cleanup</button></div>}
        {!failure && <p className="section-note">Closing this page does not cancel remote work. The existing private CA, LDAP, and recovery address are kept.</p>}
      </section>}
    </>}
  </>
}
