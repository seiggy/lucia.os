import { useCallback, useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { parseDomainOperations } from './networkManagement'
import type { DomainOperations, DomainState } from './networkManagement'
import { Icon } from './Icon'
import { PublicAccess } from './PublicAccess'

export function DomainOverview({ state, session, refreshSession, replaceToken }: {
  state: DomainState; session: AuthenticationSession; refreshSession: () => Promise<void>; replaceToken: () => void
}) {
  const [snapshot, setSnapshot] = useState<DomainOperations | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const pending = useRef<AbortController | null>(null)
  const load = useCallback(async () => {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller
    setBusy(true)
    try {
      const response = await ownerRequest(session, refreshSession, '/api/host/domains/overview', 'GET', undefined, controller.signal)
      const next = parseDomainOperations(await response.json())
      if (!controller.signal.aborted) { setSnapshot(next); setError(null) }
    } catch (failure) {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'The current domain configuration could not be read.')
    } finally {
      if (pending.current === controller) pending.current = null
      if (!controller.signal.aborted) setBusy(false)
    }
  }, [session, refreshSession])
  useEffect(() => {
    void load()
    const interval = setInterval(() => { if (document.visibilityState !== 'hidden') void load() }, 60000)
    return () => { clearInterval(interval); pending.current?.abort(); pending.current = null }
  }, [load])
  const job = state.job
  const certificate = job?.certificate
  const expiry = certificate ? Date.parse(certificate.notAfter) : null
  const expiresSoon = expiry !== null && expiry <= Date.now() + 14 * 86400000
  const certificateState = expiry === null ? 'Unavailable' : expiry <= Date.now() ? 'Expired' : expiresSoon ? 'Expiring soon' : 'Valid'
  const issuance = job?.events.find(event => event.phase === 'Issuing')
  const renewal = job?.phase === 'Renewing' ? 'Checking now' : job?.renewalError ? 'Needs attention'
    : job?.nextRenewalAt ? Date.parse(job.nextRenewalAt) <= Date.now() ? 'Check due' : 'Scheduled' : 'Not scheduled'
  const renewalOutcome = job?.renewalOutcome === 'Renewed' ? 'Certificate renewed and deployed'
    : job?.renewalOutcome === 'NotDue' ? 'Checked; renewal was not due'
      : job?.renewalOutcome === 'Failed' ? 'Last check failed' : 'No renewal check recorded yet'
  return <>
    <div className="page-intro network-overview-header"><div><h1>DNS & certificates</h1>
      <p>{snapshot?.namespace ?? 'Your domain'} · Managed addresses, HTTPS, and local DNS.</p></div>
      <button className="button secondary" disabled={busy} onClick={() => void load()}>{busy ? 'Checking…' : 'Refresh status'}<Icon name="refresh" /></button>
    </div>
    <p className="section-note" role="status">{snapshot ? `Configuration checked ${new Date(snapshot.checkedAt).toLocaleString()}.` : 'Reading the published routes and AdGuard records…'}</p>
    {error && <p className="network-error" role="alert">{error}{snapshot && ' The previous snapshot is still shown; it may be out of date.'}</p>}
    {job?.state === 'Activating' && <p className="network-warning">{job.message}</p>}
    <section className="surface network-section">
      <h2>Traefik routes</h2>
      <p className="section-note">Lucia-managed domain routes on HTTPS port 443. “Published” describes the gateway configuration, not a live endpoint health check.</p>
      {snapshot?.gatewayError && <p className="network-error">{snapshot.gatewayError}</p>}
      {snapshot && <table className="network-table"><caption className="network-table-caption">Published Lucia-managed proxy routes</caption>
        <thead><tr><th>Application</th><th>Address</th><th>Destination</th><th>Configuration</th></tr></thead>
        <tbody>{snapshot.routes.map(route => <tr key={route.origin}>
          <th scope="row">{route.name}{route.kind === 'Public' && <span className="network-cell-note">From the internet</span>}</th><td data-label="Address"><a className="text-link" href={route.origin}>{route.origin}</a></td>
          <td data-label="Destination">{route.kind === 'Redirect' ? 'Redirect to ' : ''}{route.target ?? 'Not available'}</td>
          <td data-label="Configuration" className={route.configuration !== 'Published' ? 'network-error' : undefined}>{route.configuration}</td>
        </tr>)}</tbody>
      </table>}
    </section>
    {job && <PublicAccess job={job} ingressAddress={snapshot?.ingressAddress ?? state.defaults.ingressAddress} session={session} refreshSession={refreshSession} />}
    <section className="surface network-section">
      <h2>Let’s Encrypt</h2>
      {certificate ? <dl className="network-facts">
        <div><dt>Certificate</dt><dd className={expiresSoon ? 'network-error' : undefined}>
          {certificateState} · expires {new Date(certificate.notAfter).toLocaleString()}</dd></div>
        <div><dt>Covers</dt><dd>{certificate.dnsNames.join(', ')}</dd></div>
      </dl> : <p>No issued certificate is recorded for this domain.</p>}
      <table className="network-table"><caption className="network-table-caption">Certificate job status</caption>
        <thead><tr><th>Job</th><th>Status</th><th>Timing</th></tr></thead>
        <tbody>
          <tr><th scope="row">Certificate issuance</th><td data-label="Status">{certificate ? 'Issued' : job?.phase === 'Issuing' ? 'Issuing' : 'Not completed'}</td>
            <td data-label="Timing">{issuance ? `Started ${new Date(issuance.at).toLocaleString()}` : 'Start time not recorded'}</td></tr>
          <tr><th scope="row">Renewal check</th><td data-label="Status">{renewal}</td><td data-label="Timing">
            {job?.nextRenewalAt ? `Next check ${new Date(job.nextRenewalAt).toLocaleString()}` : 'No next check recorded'}
            <span className="network-cell-note">{renewalOutcome}{job?.renewalCheckedAt && ` · ${new Date(job.renewalCheckedAt).toLocaleString()}`}</span>
          </td></tr>
        </tbody>
      </table>
      {job?.renewalError && <p className="network-error" role="alert">{job.renewalError}</p>}
      {!state.cloudflareConfigured && <p className="network-warning">The Cloudflare token is disconnected. Reconnect it before the next certificate renewal.</p>}
      {state.cloudflare.expiresAt && <p className="section-note">Cloudflare token expires {new Date(state.cloudflare.expiresAt).toLocaleString()}.</p>}
      <div className="network-actions"><button className="text-link" onClick={replaceToken}>Replace Cloudflare token</button></div>
    </section>
    <section className="surface network-section">
      <h2>AdGuard records</h2><p className="section-note">Only records matching Lucia’s managed endpoints, nodes and app addresses are shown. Refresh reads AdGuard. Node records follow each node’s latest heartbeat address; app addresses point at Lucia’s gateway; other records are never changed here.</p>
      {snapshot?.dnsError && <p className="network-error">{snapshot.dnsError}</p>}
      {snapshot && <table className="network-table"><caption className="network-table-caption">Local DNS records for managed endpoints</caption>
        <thead><tr><th>Hostname</th><th>Expected address</th><th>Records in AdGuard</th><th>Status</th></tr></thead>
        <tbody>{snapshot.dnsRecords.map(record => <tr key={record.hostname}>
          <th scope="row">{record.hostname}<span className="network-cell-note">{record.ownership}</span></th>
          <td data-label="Expected address">{record.expectedAddress}</td>
          <td data-label="Records in AdGuard">{record.state === 'Unavailable' ? 'Could not check' : record.records.length === 0 ? 'No matching record' :
            record.records.map((entry, index) => <span className="network-dns-answer" key={index}>
              {entry.domain !== record.hostname && <>{entry.domain} → </>}{entry.answer}{!entry.enabled && ' (disabled)'}
            </span>)}
            {record.totalRecords > record.records.length && <span className="network-cell-note">And {record.totalRecords - record.records.length} more matching records.</span>}
          </td>
          <td data-label="Status" className={record.state !== 'Matches' ? 'network-error' : undefined}>{record.state === 'Matches' ? 'Matches configuration' : record.state}</td>
        </tr>)}</tbody>
      </table>}
      <div className="network-actions"><a className="text-link" href="#/settings/adguard">AdGuard connection settings</a></div>
    </section>
    {job && <details className="network-technical-details"><summary>Setup history</summary>
      <ol className="network-help">{job.events.map((event, index) => <li key={index}><strong>{event.phase}</strong> · {event.message}<br />
        <small>{new Date(event.at).toLocaleString()}</small></li>)}</ol>
      {certificate && <p className="section-note">Certificate SHA-256: {certificate.certificateSha256}</p>}
    </details>}
  </>
}
