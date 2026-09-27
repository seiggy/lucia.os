import { useCallback, useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { Icon } from './Icon'
import { parseDhcpReservations, parseUniFiStatus } from './networkManagement'
import type { DhcpReservations, DhcpState, UniFiStatus } from './networkManagement'
import './NetworkSettings.css'

const endpoint = '/api/host/connections/unifi'
const states: Record<DhcpState, { label: string; tone: string; note: string }> = {
  Reserved: { label: 'Reserved', tone: 'status-green', note: 'UniFi keeps this address for the node.' },
  Reserving: { label: 'Reserving', tone: 'status-accent', note: 'Lucia is adding the reservation now.' },
  ReservedElsewhere: { label: 'Reserved at another address', tone: 'status-amber', note: 'You reserved a different address in UniFi. Lucia leaves it alone; the node moves on its next lease.' },
  AddressTaken: { label: 'Address held by another device', tone: 'status-amber', note: 'Another client already reserves this address. Change one of them in UniFi.' },
  NotSeen: { label: 'Waiting for UniFi', tone: 'status-muted', note: 'UniFi has no live lease for this node at this address yet. Lucia checks every five minutes.' },
  Failed: { label: 'UniFi refused the change', tone: 'status-amber', note: 'Lucia tries again in five minutes. The API key’s admin must be allowed to edit clients.' },
}
const fingerprint = (sha: string) => sha.toUpperCase().match(/../g)!.join(':')
const failureOf = (failure: unknown, fallback: string) => ({
  message: failure instanceof Error ? failure.message : fallback,
  code: (failure as { code?: string } | null)?.code,
  certificateSha256: (failure as { certificateSha256?: string } | null)?.certificateSha256,
})

export function UniFiSettings({ session, refreshSession }: {
  session: AuthenticationSession; refreshSession: () => Promise<void>
}) {
  const [status, setStatus] = useState<UniFiStatus | null>(null)
  const [reservations, setReservations] = useState<DhcpReservations | null>(null)
  const [url, setUrl] = useState('')
  const [site, setSite] = useState('default')
  const [apiKey, setApiKey] = useState('')
  const [untrusted, setUntrusted] = useState<{ sha: string; changed: boolean } | null>(null)
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [disconnect, setDisconnect] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState('')
  const pending = useRef<AbortController | null>(null)
  const mounted = useRef(true)
  const loadReservations = useCallback(async (signal?: AbortSignal) => {
    try {
      const response = await ownerRequest(session, refreshSession, endpoint + '/reservations', 'GET', undefined, signal)
      const value = parseDhcpReservations(await response.json())
      if (mounted.current && !signal?.aborted) setReservations(value)
    } catch { if (mounted.current && !signal?.aborted) setReservations(null) }
  }, [session, refreshSession])
  useEffect(() => {
    mounted.current = true
    const controller = new AbortController()
    if (!session.isOwner) { setLoading(false); return }
    void ownerRequest(session, refreshSession, endpoint, 'GET', undefined, controller.signal)
      .then(response => response.json()).then(parseUniFiStatus).then(value => {
        if (controller.signal.aborted) return
        setStatus(value); setUrl(value.baseUrl ?? ''); setSite(value.site ?? 'default')
        if (value.configured) void loadReservations(controller.signal)
      }).catch(failure => { if (!controller.signal.aborted) setError(failureOf(failure, 'UniFi status is unavailable.').message) })
      .finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => { mounted.current = false; controller.abort(); pending.current?.abort() }
  }, [session, refreshSession, loadReservations])
  // The background check runs right after a change; read its result once it has had time to finish.
  useEffect(() => {
    if (!status?.configured || !reservations?.nodes.some(node => node.state === 'Reserving')) return
    const timer = window.setTimeout(() => void loadReservations(), 4000)
    return () => window.clearTimeout(timer)
  }, [status, reservations, loadReservations])

  async function change(method: 'PUT' | 'POST' | 'DELETE', path = '', body?: unknown, done = '') {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller
    setBusy(true); setError(null); setNotice('')
    try {
      const response = await ownerRequest(session, refreshSession, endpoint + path, method, body, controller.signal)
      const value = parseUniFiStatus(await response.json())
      if (!mounted.current || controller.signal.aborted) return
      setStatus(value); setDisconnect(false); setNotice(done)
      if (path === '' && method === 'PUT') { setApiKey(''); setUntrusted(null) }
      if (value.configured) window.setTimeout(() => void loadReservations(), 1500)
      else setReservations(null)
    } catch (failure) {
      if (!mounted.current || controller.signal.aborted) return
      const { message, code, certificateSha256 } = failureOf(failure, 'The UniFi operation failed.')
      if (certificateSha256 && (code === 'unifi_certificate_untrusted' || code === 'unifi_certificate_changed') && method === 'PUT' && path === '')
        setUntrusted({ sha: certificateSha256, changed: code === 'unifi_certificate_changed' })
      else setError(message)
    } finally { if (pending.current === controller) pending.current = null; if (mounted.current) setBusy(false) }
  }
  const connect = (certificateSha256: string | null) => void change('PUT', '', { baseUrl: url.trim(), apiKey, site: site.trim(), certificateSha256 },
    'UniFi connection verified and saved.')

  if (!session.isOwner) return <div className="page-intro"><h1>Owner access is needed.</h1><p>Your lab owner configures the network connection.</p></div>
  const reserving = status?.reserveNodeAddresses ?? false
  return <>
    <div className="page-intro"><h1>UniFi Network</h1><p>Connect your UniFi gateway so every server Lucia onboards keeps the address it was given. Lucia adds DHCP reservations for managed nodes and never changes anything else on your network.</p></div>
    {error && <p className="network-error" role="alert">{error}</p>}
    <p className="network-notice" role="status">{notice || (loading ? 'Reading the saved connection…' : '')}</p>
    {status?.configured && <>
      <section className="surface network-section">
        <h2>Gateway</h2>
        <dl className="network-facts unifi-facts">
          <div><dt>Address</dt><dd>{status.baseUrl}</dd></div>
          <div><dt>Site</dt><dd>{status.site}</dd></div>
          <div><dt>UniFi Network</dt><dd>{status.networkVersion ?? 'Version unavailable'}</dd></div>
          <div><dt>Certificate</dt><dd>{status.certificateSha256
            ? <span title={fingerprint(status.certificateSha256)}>Trusted by fingerprint <code>{fingerprint(status.certificateSha256).slice(0, 23)}…</code></span>
            : 'Trusted by this system'}</dd></div>
        </dl>
        <p className="section-note">{status.lastVerifiedAt ? `Last verified ${new Date(status.lastVerifiedAt).toLocaleString()}.` : 'Not verified yet.'}</p>
        <div className="network-actions"><button className="button secondary" disabled={busy} onClick={() => void change('POST', '/verify', undefined, 'UniFi is reachable. Reservations were checked again.')}>Verify and check now <Icon name="refresh" /></button>
          <button className="text-link" disabled={busy} onClick={() => setDisconnect(true)}>Disconnect</button></div>
        {disconnect && <div className="network-warning"><p>Disconnect UniFi? Reservations already in UniFi stay there; new nodes won’t get one until you reconnect.</p>
          <div className="network-actions"><button className="button secondary" disabled={busy} onClick={() => void change('DELETE', '', undefined, 'UniFi disconnected. Existing reservations were kept.')}>Disconnect UniFi</button>
            <button className="text-link" disabled={busy} onClick={() => setDisconnect(false)}>Keep connection</button></div></div>}
      </section>
      <section className="surface network-section">
        <div className="network-overview-header">
          <div><h2>Address reservations</h2>
            <p className="section-note">Lucia pins this Spark’s address, and each server’s as soon as it finishes onboarding, to its current lease. Reservations you made yourself are never changed.</p></div>
        </div>
        <label className="network-checkbox"><input type="checkbox" checked={reserving} disabled={busy}
          onChange={event => void change('PUT', '/reservations', { enabled: event.target.checked },
            event.target.checked ? 'Automatic reservations are on.' : 'Automatic reservations are off. Existing reservations were kept.')} />
          <span><strong>Reserve managed-node addresses automatically</strong><br />Recommended. Keeps network boot, DNS names, SSH and service pins pointing at the right machine.</span></label>
        {reserving && reservations?.error && <p className="network-warning">{reservations.error} Lucia tries again every five minutes.</p>}
        {reserving && reservations && !reservations.error && !reservations.nodes.some(node => !node.host) &&
          <p className="section-note">No managed servers yet. <a className="text-link" href="#/devices">Onboard a server <Icon name="arrow" /></a></p>}
        {reserving && reservations && reservations.nodes.length > 0 && <table className="network-table unifi-table">
          <caption className="network-table-caption">Lucia DHCP reservations</caption>
          <thead><tr><th scope="col">Machine</th><th scope="col">Address</th><th scope="col">Status</th></tr></thead>
          <tbody>{reservations.nodes.map(node => <tr key={(node.host ? 'host:' : 'node:') + node.hostname}>
            <th scope="row" data-label="Machine">{node.host ? 'This Spark' : node.hostname}
              {node.host && <span className="network-cell-note">Runs Lucia and network boot</span>}
              {node.mac && <span className="network-cell-note unifi-mac">{node.mac}</span>}</th>
            <td data-label="Address" className="unifi-address">{node.address}</td>
            <td data-label="Status"><strong className={states[node.state].tone}>{states[node.state].label}</strong>
              <span className="network-cell-note">{states[node.state].note}</span></td>
          </tr>)}</tbody>
        </table>}
        {reserving && reservations?.checkedAt && <p className="section-note">Checked {new Date(reservations.checkedAt).toLocaleString()}.</p>}
      </section>
    </>}
    <section className="surface network-section">
      <h2>{status?.configured ? 'Replace connection' : 'Connect your gateway'}</h2>
      <form onSubmit={event => { event.preventDefault(); connect(null) }}>
        <div className="network-fields unifi-fields">
          <label>Gateway address<input type="url" value={url} onChange={event => { setUrl(event.target.value); setUntrusted(null) }}
            placeholder="https://192.168.1.1" autoComplete="off" required maxLength={512} disabled={busy} /></label>
          <label>Site<input value={site} onChange={event => setSite(event.target.value)} autoComplete="off" required maxLength={64}
            pattern="[A-Za-z0-9_\-]+" disabled={busy} /></label>
        </div>
        <label>API key<input type="password" value={apiKey} onChange={event => { setApiKey(event.target.value); setUntrusted(null) }}
          autoComplete="new-password" required minLength={16} maxLength={256} disabled={busy} /></label>
        <ol className="network-help">
          <li>In UniFi Network, open <strong>Settings → Control Plane → Integrations</strong> and create an API key. Use an admin limited to Network if you can.</li>
          <li>Paste it here. Lucia encrypts it on the Spark and only sends it to this gateway over HTTPS on your LAN.</li>
          <li>Leave the site as <code>default</code> unless your console runs more than one.</li>
        </ol>
        {untrusted && <div className="network-warning unifi-trust" role="alert">
          <p><strong>{untrusted.changed ? 'The gateway’s certificate changed.' : 'This gateway uses its own certificate.'}</strong> {untrusted.changed
            ? 'If you replaced it or reset the console, trust the new one. Otherwise stop here: something else may be answering at this address.'
            : 'UniFi consoles ship with a self-signed certificate. Open the gateway in your browser, view its certificate, and check that the SHA-256 fingerprint matches:'}</p>
          <code className="unifi-fingerprint">{fingerprint(untrusted.sha)}</code>
          <div className="network-actions"><button type="button" className="button primary" disabled={busy} onClick={() => connect(untrusted.sha)}>Trust this certificate and connect <Icon name="shield" /></button>
            <button type="button" className="text-link" disabled={busy} onClick={() => setUntrusted(null)}>Cancel</button></div>
        </div>}
        {!untrusted && <button className="button primary" disabled={busy || loading || !url || !site || apiKey.length < 16}>
          {busy ? 'Verifying…' : 'Verify and save'}<Icon name="shield" /></button>}
      </form>
    </section>
  </>
}
