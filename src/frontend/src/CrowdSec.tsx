import { useCallback, useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { Icon } from './Icon'
import { TrustedAddresses } from './TrustedAddresses'
import { consoleSummary, isAppSec, parseAlerts, parseCrowdSecStatus, parseDecisions, remaining, scenarioName, summarize, validEnrollKey } from './crowdsecStatus'
import type { CrowdSecAlert, CrowdSecDecision, CrowdSecStatus } from './crowdsecStatus'
import './NetworkSettings.css'

const when = (value: string | null) => value ? new Date(value).toLocaleString() : 'never'
const failed = (failure: unknown, fallback: string) => failure instanceof Error ? failure.message : fallback
const yesNo = (value: boolean | null | undefined, yes: string, no: string) => value === null || value === undefined ? 'Not reported yet' : value ? yes : no

/** The gateway's CrowdSec protection: the engine's health, its bans and alerts, CrowdSec Console enrollment and the safe harbor. */
export function CrowdSec({ session, refreshSession }: { session: AuthenticationSession; refreshSession: () => Promise<void> }) {
  const [status, setStatus] = useState<CrowdSecStatus | null>(null)
  const [alerts, setAlerts] = useState<CrowdSecAlert[] | null>(null)
  const [blocks, setBlocks] = useState<CrowdSecAlert[] | null>(null)
  const [decisions, setDecisions] = useState<CrowdSecDecision[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [listError, setListError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [unbanning, setUnbanning] = useState<string | null>(null)
  const [enrollKey, setEnrollKey] = useState('')
  const [enrollError, setEnrollError] = useState<string | null>(null)
  const [notice, setNotice] = useState('')
  const pending = useRef<AbortController | null>(null)

  const load = useCallback(async () => {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller
    setBusy(true)
    try {
      const next = parseCrowdSecStatus(await (await ownerRequest(session, refreshSession, '/api/host/security/crowdsec', 'GET', undefined, controller.signal)).json())
      if (controller.signal.aborted) return
      setStatus(next)
      setError(null)
      if (next.state === 'healthy') {
        try {
          const [alertList, blockList, decisionList] = await Promise.all([
            ownerRequest(session, refreshSession, '/api/host/security/crowdsec/alerts?kind=scenario&limit=50', 'GET', undefined, controller.signal).then(async response => parseAlerts(await response.json())),
            ownerRequest(session, refreshSession, '/api/host/security/crowdsec/alerts?kind=appsec&limit=50', 'GET', undefined, controller.signal).then(async response => parseAlerts(await response.json())),
            ownerRequest(session, refreshSession, '/api/host/security/crowdsec/decisions', 'GET', undefined, controller.signal).then(async response => parseDecisions(await response.json())),
          ])
          if (!controller.signal.aborted) { setAlerts(alertList); setBlocks(blockList); setDecisions(decisionList); setListError(null) }
        } catch (failure) { if (!controller.signal.aborted) setListError(failed(failure, 'CrowdSec’s alerts and bans could not be read.')) }
      } else { setAlerts(null); setBlocks(null); setDecisions(null) }
    } catch (failure) {
      if (!controller.signal.aborted) setError(failed(failure, 'CrowdSec’s status could not be read.'))
    } finally {
      if (pending.current === controller) pending.current = null
      if (!controller.signal.aborted) setBusy(false)
    }
  }, [session, refreshSession])
  useEffect(() => {
    void load()
    const interval = setInterval(() => { if (document.visibilityState !== 'hidden') void load() }, 30000)
    return () => { clearInterval(interval); pending.current?.abort(); pending.current = null }
  }, [load])

  async function unban(value: string) {
    setUnbanning(null); setNotice(''); setListError(null)
    try {
      const result = await (await ownerRequest(session, refreshSession, '/api/host/security/crowdsec/unban', 'POST', { ip: value })).json() as { deleted?: number }
      setNotice(`Lifted ${result.deleted ?? 0} ban${result.deleted === 1 ? '' : 's'} on ${value}. The gateway lets it through within 15 seconds.`)
      await load()
    } catch (failure) { setListError(failed(failure, `The ban on ${value} could not be lifted.`)) }
  }

  async function enroll(remove = false) {
    setEnrollError(null); setNotice('')
    const key = enrollKey.trim()
    if (!remove && !validEnrollKey(key)) { setEnrollError('Paste the enroll key from app.crowdsec.net: letters and digits, without spaces.'); return }
    try {
      setStatus(parseCrowdSecStatus(await (await ownerRequest(session, refreshSession, '/api/host/security/crowdsec/enrollment', remove ? 'DELETE' : 'PUT', remove ? undefined : { key })).json()))
      setEnrollKey('')
      setNotice(remove ? 'Lucia forgot the enroll key. An engine already in the Console stays there until you remove it there.' : 'Saved. CrowdSec enrolls within a minute; then accept it in the Console.')
    } catch (failure) { setEnrollError(failed(failure, 'The enroll key could not be saved.')) }
  }

  const summary = status ? summarize(status) : null
  const now = Date.now()
  return <>
    <div className="page-intro network-overview-header"><div><h1>Gateway protection</h1>
      <p>CrowdSec bans addresses that attack your apps and blocks known exploit requests before they reach them.</p></div>
      <button className="button secondary" disabled={busy} onClick={() => void load()}>{busy ? 'Checking…' : 'Refresh'}<Icon name="refresh" /></button>
    </div>
    {error && <p className="network-error" role="alert">{error}</p>}
    {notice && <p className="section-note" role="status">{notice}</p>}
    {status && summary && <section className="surface network-section" aria-labelledby="crowdsec-engine">
      <h2 id="crowdsec-engine">{summary.title}</h2>
      <p className={summary.tone === 'warning' ? 'network-warning' : 'section-note'}>{summary.detail}</p>
      {status.state === 'waiting-domain' && <div className="network-actions"><a className="button secondary" href="#/settings/domains">Set up your domain</a></div>}
      {status.state === 'needs-observability' && <div className="network-actions">{status.observabilityInstalled
        ? <a className="button secondary" href="#/apps">Open Apps to update Observability</a>
        : <a className="button secondary" href="#/apps/install/observability">Install Observability</a>}</div>}
      {status.state === 'absent'
        ? <div className="network-actions">{status.observabilityInstalled
          ? <a className="button primary" href="#/apps/install/crowdsec">Install CrowdSec</a>
          : <><a className="button primary" href="#/apps/install/observability">Install Observability</a><a className="text-link" href="#/apps/catalog">See all apps</a></>}</div>
        : <dl className="network-facts">
          <div><dt>Gateway</dt><dd>{status.protecting ? 'Checking every request with CrowdSec' : 'Letting everything through until CrowdSec answers'}</dd></div>
          <div><dt>Engine</dt><dd>{status.version ?? 'Version not reported yet'} on {status.node ?? 'an unknown server'}{status.address ? ` (${status.address})` : ''}{status.name && <>, reached at <code>{status.name}</code></>}{status.stack && <> · <a className="text-link" href={`#/apps/${status.stack}`}>Manage the app</a></>}</dd></div>
          <div><dt>Certificate</dt><dd>{status.certificateExpiresAt ? `From Lucia’s authority, renewed automatically; current one expires ${when(status.certificateExpiresAt)}` : 'Not issued yet'}</dd></div>
          <div><dt>Last report</dt><dd>{status.reportedAt ? when(status.reportedAt) : 'The engine hasn’t reported yet'}</dd></div>
        </dl>}
    </section>}
    {status && status.state !== 'absent' && <>
      <section className="surface network-section" aria-labelledby="crowdsec-activity">
        <h2 id="crowdsec-activity">Activity since the engine started</h2>
        {status.metrics ? <dl className="network-facts">
          <div><dt>Access-log lines</dt><dd>{status.metrics.linesRead.toLocaleString()} read, {status.metrics.linesParsed.toLocaleString()} understood{status.metrics.linesUnparsed > 0 ? `, ${status.metrics.linesUnparsed.toLocaleString()} not` : ''}</dd></div>
          <div><dt>AppSec</dt><dd>{status.metrics.appSecProcessed.toLocaleString()} requests checked, {status.metrics.appSecBlocked.toLocaleString()} blocked</dd></div>
          <div><dt>Gateway’s last ban update</dt><dd>{status.bouncer ? status.bouncer.revoked ? 'The gateway’s key was revoked' : when(status.bouncer.lastPull) : 'Not reported yet'}</dd></div>
          <div><dt>Trusted addresses in the engine</dt><dd>{status.harborEntries ?? 'Not reported yet'}</dd></div>
        </dl> : <p className="section-note">The engine reports its numbers once a minute after it starts.</p>}
      </section>
      <section className="surface network-section" aria-labelledby="crowdsec-capi">
        <h2 id="crowdsec-capi">CrowdSec network</h2>
        <p className="section-note">The Central API shares what your gateway sees, anonymously, and sends back the community blocklist of known attackers.</p>
        <dl className="network-facts">
          <div><dt>Central API</dt><dd>{status.capi ? status.capi.registered ? yesNo(status.capi.authenticated, 'Registered and connected', 'Registered, but not connected right now') : 'Not registered' : 'Not reported yet'}</dd></div>
          <div><dt>Signal sharing</dt><dd>{yesNo(status.capi?.sharing, 'On', 'Off')}</dd></div>
          <div><dt>Community blocklist</dt><dd>{yesNo(status.capi?.blocklist, 'On', 'Off')}</dd></div>
        </dl>
      </section>
      <section className="surface network-section" aria-labelledby="crowdsec-console">
        <h2 id="crowdsec-console">CrowdSec Console</h2>
        <p className={status.console.state === 'failed' ? 'network-error' : 'section-note'}>{consoleSummary(status)}</p>
        {status.console.state !== 'accepted' && <form onSubmit={event => { event.preventDefault(); void enroll() }}>
          <label>Enroll key from <a className="text-link" href={status.console.url} target="_blank" rel="noreferrer">app.crowdsec.net</a>
            <input type="password" value={enrollKey} onChange={event => setEnrollKey(event.target.value)} autoComplete="off" spellCheck={false} maxLength={128} /></label>
          <p className="section-note">Lucia keeps the key as the app’s secret and never shows it again.</p>
          <div className="network-actions">
            <button className="button primary" disabled={enrollKey.trim().length === 0}>Enroll</button>
            {(status.console.state === 'failed' || status.console.state === 'pending' || status.console.state === 'sending') &&
              <button type="button" className="text-link" onClick={() => void enroll(true)}>Forget the key</button>}
          </div>
        </form>}
        {status.console.state === 'accepted' && <div className="network-actions"><a className="button secondary" href={status.console.url} target="_blank" rel="noreferrer">Open the Console</a></div>}
        {enrollError && <p className="network-error" role="alert">{enrollError}</p>}
      </section>
      {status.state === 'healthy' && <section className="surface network-section" aria-labelledby="crowdsec-bans">
        <h2 id="crowdsec-bans">Bans</h2>
        <p className="section-note">Addresses the gateway refuses now. The community blocklist’s own bans aren’t listed.</p>
        {listError && <p className="network-error" role="alert">{listError}</p>}
        {decisions && (decisions.length === 0 ? <p className="section-note">Nobody is banned.</p>
          : <table className="network-table"><caption className="network-table-caption">Active bans</caption>
            <thead><tr><th>Address</th><th>Why</th><th>Time left</th><th><span className="visually-hidden">Action</span></th></tr></thead>
            <tbody>{decisions.map(decision => <tr key={decision.id}>
              <th scope="row"><code>{decision.value}</code>{decision.scope !== 'Ip' && <span className="network-cell-note">{decision.scope}</span>}</th>
              <td data-label="Why" title={decision.scenario ?? undefined}>{decision.scenario ? scenarioName(decision.scenario) : decision.origin}{decision.origin !== 'crowdsec' && <span className="network-cell-note">From {decision.origin}</span>}</td>
              <td data-label="Time left">{remaining(decision.until, now)}</td>
              <td data-label="Action">{unbanning === decision.value
                ? <><button className="button secondary" onClick={() => void unban(decision.value)}>Unban {decision.value}</button> <button className="text-link" onClick={() => setUnbanning(null)}>Keep the ban</button></>
                : <button className="text-link" onClick={() => setUnbanning(decision.value)}>Unban…</button>}</td>
            </tr>)}</tbody>
          </table>)}
      </section>}
      {status.state === 'healthy' && <section className="surface network-section" aria-labelledby="crowdsec-alerts">
        <h2 id="crowdsec-alerts">Recent alerts</h2>
        <p className="section-note">What CrowdSec noticed, newest first. Devices on your network are recorded but never banned automatically.</p>
        {alerts && (alerts.length === 0 ? <p className="section-note">Nothing yet.</p>
          : <table className="network-table"><caption className="network-table-caption">Recent alerts</caption>
            <thead><tr><th>What</th><th>From</th><th>When</th><th>Events</th></tr></thead>
            <tbody>{alerts.map(alert => <tr key={alert.id}>
              <th scope="row" title={alert.scenario}>{scenarioName(alert.scenario)}{isAppSec(alert.scenario) && <span className="network-cell-note">AppSec</span>}{alert.simulated && <span className="network-cell-note">Simulated</span>}</th>
              <td data-label="From"><code>{alert.value ?? 'unknown'}</code>{(alert.country || alert.asName) && <span className="network-cell-note">{[alert.country, alert.asName].filter(Boolean).join(' · ')}</span>}</td>
              <td data-label="When">{when(alert.at)}</td>
              <td data-label="Events">{alert.events}{alert.decisions > 0 ? ' · banned' : ''}</td>
            </tr>)}</tbody>
          </table>)}
      </section>}
      {status.state === 'healthy' && <section className="surface network-section" aria-labelledby="crowdsec-appsec">
        <h2 id="crowdsec-appsec">AppSec blocks</h2>
        <p className="section-note">Requests the gateway refused because they matched an AppSec rule, newest first. A block from the internet counts towards a ban; one from your network is only recorded.</p>
        {blocks && (blocks.length === 0 ? <p className="section-note">Nothing blocked yet.</p>
          : <table className="network-table"><caption className="network-table-caption">AppSec blocks</caption>
            <thead><tr><th>Rule</th><th>From</th><th>Path</th><th>When</th></tr></thead>
            <tbody>{blocks.map(block => <tr key={block.id}>
              <th scope="row" title={block.scenario}>{scenarioName(block.scenario)}</th>
              <td data-label="From"><code>{block.value ?? 'unknown'}</code>{(block.country || block.asName) && <span className="network-cell-note">{[block.country, block.asName].filter(Boolean).join(' · ')}</span>}</td>
              <td data-label="Path">{block.path ? <code>{block.path}</code> : 'not recorded'}</td>
              <td data-label="When">{when(block.at)}</td>
            </tr>)}</tbody>
          </table>)}
      </section>}
    </>}
    <TrustedAddresses session={session} refreshSession={refreshSession} />
  </>
}
