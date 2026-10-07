import { useCallback, useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { Icon } from './Icon'
import { parseAdGuardCertificate, parseAdGuardFleet, parseAdGuardStatus } from './networkManagement'
import type { AdGuardCertificate, AdGuardFleet, AdGuardStatus } from './networkManagement'
import './NetworkSettings.css'

export function AdGuardSettings({ session, refreshSession }: {
  session: AuthenticationSession; refreshSession: () => Promise<void>
}) {
  const [status, setStatus] = useState<AdGuardStatus | null>(null)
  const [url, setUrl] = useState('')
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [allowHttp, setAllowHttp] = useState(false)
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [disconnect, setDisconnect] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState('')
  const pending = useRef<AbortController | null>(null)
  const mounted = useRef(true)
  useEffect(() => {
    mounted.current = true
    const controller = new AbortController()
    if (!session.isOwner) { setLoading(false); return }
    void ownerRequest(session, refreshSession, '/api/host/connections/adguard', 'GET', undefined, controller.signal)
      .then(response => response.json()).then(parseAdGuardStatus).then(value => {
        if (controller.signal.aborted) return
        setStatus(value); setUrl(value.baseUrl ?? ''); setUsername(value.username ?? ''); setAllowHttp(value.allowInsecureHttp)
      }).catch(failure => { if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'AdGuard status is unavailable.') })
      .finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => { mounted.current = false; controller.abort(); pending.current?.abort() }
  }, [session, refreshSession])
  async function change(method: 'PUT' | 'POST' | 'DELETE') {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller
    setBusy(true); setError(null); setNotice('')
    try {
      const response = await ownerRequest(session, refreshSession,
        '/api/host/connections/adguard' + (method === 'POST' ? '/verify' : ''), method,
        method === 'PUT' ? { baseUrl: url.trim(), username, password, allowInsecureHttp: allowHttp } : undefined,
        controller.signal)
      const value = parseAdGuardStatus(await response.json())
      if (mounted.current && !controller.signal.aborted) {
        setStatus(value); setPassword(''); setDisconnect(false)
        setNotice(method === 'DELETE' ? 'AdGuard disconnected. Existing DNS rewrites were not removed.'
          : method === 'POST' ? 'AdGuard is reachable and its DNS rewrite API was verified.' : 'AdGuard connection verified and saved.')
      }
    } catch (failure) {
      if (mounted.current && !controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'The AdGuard operation failed.')
    } finally { if (pending.current === controller) pending.current = null; if (mounted.current) setBusy(false) }
  }
  if (!session.isOwner) return <div className="page-intro"><h1>Owner access is needed.</h1><p>Your lab owner configures DNS connections.</p></div>
  return <>
    <div className="page-intro"><h1>AdGuard connection</h1><p>Connect the AdGuard Home instance you already run. Lucia uses this connection for local DNS without taking over its deployment.</p></div>
    {error && <p className="network-error" role="alert">{error}</p>}
    <p className="network-notice" role="status">{notice || (loading ? 'Reading the saved connection…' : '')}</p>
    {status?.configured && <section className="surface network-section">
      <h2>Configured connection</h2><p>{status.baseUrl}</p><p className="section-note">Account: {status.username} · {status.version ?? 'Version unavailable'}</p>
      <p className="section-note">{status.lastVerifiedAt ? `Last verified ${new Date(status.lastVerifiedAt).toLocaleString()}.` : 'No verification time is available.'} Saved settings do not guarantee current availability.</p>
      {status.allowInsecureHttp && <p className="network-warning">This connection uses HTTP. Its administrator credentials are visible to anyone able to intercept that LAN traffic.</p>}
      <div className="network-actions"><button className="button secondary" disabled={busy} onClick={() => void change('POST')}>Verify connection <Icon name="refresh" /></button>
        <button className="text-link" disabled={busy} onClick={() => setDisconnect(true)}>Disconnect</button>
        <a className="text-link" href="#/settings/domains">Set up DNS & certificates <Icon name="arrow" /></a></div>
      {disconnect && <div className="network-warning"><p>Disconnect AdGuard? Existing rewrites stay in AdGuard, but new DNS onboarding will be blocked.</p>
        <div className="network-actions"><button className="button secondary" disabled={busy} onClick={() => void change('DELETE')}>Disconnect AdGuard</button>
          <button className="text-link" disabled={busy} onClick={() => setDisconnect(false)}>Keep connection</button></div></div>}
    </section>}
    {status?.configured && status.baseUrl && <CertificateSection key={status.baseUrl} host={new URL(status.baseUrl).hostname}
      session={session} refreshSession={refreshSession} />}
    {status?.configured && <FleetSection session={session} refreshSession={refreshSession} />}
    <section className="surface network-section">
      <h2>{status?.configured ? 'Replace connection settings' : 'Connect AdGuard Home'}</h2>
      <form onSubmit={event => { event.preventDefault(); void change('PUT') }}>
        <label>API origin<input type="url" value={url} onChange={event => { setUrl(event.target.value); setAllowHttp(false) }}
          placeholder="https://adguard.example.com:8443" autoComplete="off" required maxLength={512} disabled={busy} /></label>
        <p className="section-note">Use the management interface’s origin, including its port if needed, without <code>/control</code>. Only private LAN addresses are accepted. HTTPS certificates must validate normally.</p>
        <div className="network-fields"><label>Username<input value={username} onChange={event => setUsername(event.target.value)} autoComplete="off" required maxLength={128} disabled={busy} /></label>
          <label>Password<input type="password" value={password} onChange={event => setPassword(event.target.value)} autoComplete="new-password" required maxLength={1024} disabled={busy} /></label></div>
        <p className="section-note">AdGuard uses administrator credentials, not a scoped API token. Lucia encrypts them on the Spark and never saves them in browser storage. Verification reads service/profile/rewrite settings only—not DNS query logs.</p>
        {url.toLowerCase().startsWith('http:') && <label className="network-checkbox"><input type="checkbox" checked={allowHttp} onChange={event => setAllowHttp(event.target.checked)} disabled={busy} />
          <span>I understand HTTP exposes these credentials on the LAN and explicitly allow this trusted-network connection.</span></label>}
        <button className="button primary" disabled={busy || loading || !url || !username || !password || (url.toLowerCase().startsWith('http:') && !allowHttp)}>
          {busy ? 'Verifying…' : 'Verify and save'}<Icon name="shield" /></button>
      </form>
    </section>
  </>
}

function CertificateSection({ host, session, refreshSession }: {
  host: string; session: AuthenticationSession; refreshSession: () => Promise<void>
}) {
  const [certificate, setCertificate] = useState<AdGuardCertificate | null>(null)
  const [draft, setDraft] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const endpoint = '/api/host/connections/adguard/certificate'
  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const value = parseAdGuardCertificate(await (await ownerRequest(session, refreshSession, endpoint, 'GET', undefined, signal)).json())
      if (!signal?.aborted) setCertificate(value)
    } catch (failure) { if (!signal?.aborted) setError(failure instanceof Error ? failure.message : 'Certificate status is unavailable.') }
  }, [session, refreshSession])
  useEffect(() => {
    const controller = new AbortController()
    void load(controller.signal)
    return () => controller.abort()
  }, [load])
  const issuing = certificate?.enabled && !certificate.error && !certificate.pushedAt
  useEffect(() => {
    if (!issuing) return
    const timer = window.setTimeout(() => void load(), 5000)
    return () => window.clearTimeout(timer)
  }, [issuing, certificate, load])
  async function save(enabled: boolean, name?: string) {
    setBusy(true); setError(null)
    try {
      const value = parseAdGuardCertificate(await (await ownerRequest(session, refreshSession, endpoint, 'PUT',
        name === undefined ? { enabled } : { enabled, name: name === host ? '' : name })).json())
      setCertificate(value); setDraft(null)
    }
    catch (failure) { setError(failure instanceof Error ? failure.message : 'The certificate setting was not saved.') }
    finally { setBusy(false) }
  }
  const name = certificate?.name ?? host
  const value = draft ?? name
  const moving = certificate?.enabled && certificate.pushedAt && !certificate.error && name !== host
  return <section className="surface network-section">
    <h2>HTTPS certificate</h2>
    <p className="section-note">Lucia issues a Let’s Encrypt certificate with your domain setup, installs it in AdGuard for HTTPS and encrypted DNS, and renews it before it expires.</p>
    {error && <p className="network-error" role="alert">{error}</p>}
    <label className="network-checkbox"><input type="checkbox" checked={certificate?.enabled ?? false} disabled={busy || !certificate}
      onChange={event => void save(event.target.checked)} />
      <span><strong>Manage AdGuard’s certificate</strong><br />This replaces the certificate AdGuard serves now. Turning it off keeps the last one installed.</span></label>
    {certificate?.enabled && <form className="adguard-name-form" onSubmit={event => { event.preventDefault(); void save(true, value.trim().toLowerCase()) }}>
      <label>Name devices use<input value={value} onChange={event => setDraft(event.target.value)} required maxLength={253}
        autoComplete="off" spellCheck={false} disabled={busy} aria-describedby="adguard-name-hint" /></label>
      <p id="adguard-name-hint" className="section-note">For AdGuard’s web page and encrypted DNS. To move AdGuard to a new name, enter it here first; the certificate keeps covering {host} until you change the connection.</p>
      {draft !== null && draft.trim().toLowerCase() !== name && <div className="network-actions">
        <button className="button primary" disabled={busy || !draft.trim()}>Save name</button>
        <button type="button" className="text-link" disabled={busy} onClick={() => setDraft(null)}>Cancel</button></div>}
    </form>}
    {certificate?.enabled && <p className={certificate.error ? 'network-warning' : 'section-note'} role="status">
      {certificate.error ? `${certificate.error} Lucia tries again in 30 minutes.`
        : certificate.notAfter && certificate.pushedAt
          ? `AdGuard serves ${certificate.coveredNames.join(' and ')} with a certificate valid until ${new Date(certificate.notAfter).toLocaleDateString()}. Installed ${new Date(certificate.pushedAt).toLocaleString()}.`
          : `Issuing a certificate for ${name}. This takes a few minutes while DNS checks propagate.`}</p>}
    {moving && <p className="network-warning">To finish the move, replace the connection’s API origin below with <code>https://{name}</code>. The next renewal then drops {host}.</p>}
  </section>
}

function FleetSection({ session, refreshSession }: { session: AuthenticationSession; refreshSession: () => Promise<void> }) {
  const [fleet, setFleet] = useState<AdGuardFleet | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const endpoint = '/api/host/connections/adguard/instances'
  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const value = parseAdGuardFleet(await (await ownerRequest(session, refreshSession, endpoint, 'GET', undefined, signal)).json())
      if (!signal?.aborted) setFleet(value)
    } catch (failure) { if (!signal?.aborted) setError(failure instanceof Error ? failure.message : 'AdGuard servers are unavailable.') }
  }, [session, refreshSession])
  useEffect(() => {
    const controller = new AbortController()
    void load(controller.signal)
    const timer = window.setInterval(() => void load(controller.signal), 30000)
    return () => { controller.abort(); window.clearInterval(timer) }
  }, [load])
  async function promote(name: string) {
    setBusy(name); setError(null)
    try { setFleet(parseAdGuardFleet(await (await ownerRequest(session, refreshSession, `${endpoint}/${encodeURIComponent(name)}/primary`, 'POST')).json())) }
    catch (failure) { setError(failure instanceof Error ? failure.message : 'The primary was not changed.') }
    finally { setBusy(null) }
  }
  const addresses = fleet?.instances.flatMap(item => item.address ? [item.address] : []) ?? []
  return <section className="surface network-section">
    <h2>AdGuard servers</h2>
    <p className="section-note">Lucia runs AdGuard on every server and copies the primary’s settings to the others each minute. Change settings on the primary. If it stops answering for three minutes, the most recently synced server takes over.</p>
    {error && <p className="network-error" role="alert">{error}</p>}
    {fleet?.problem && <p className="network-warning">{fleet.problem}</p>}
    {fleet?.promotedAt && <p className="section-note">{fleet.promotedFrom ?? 'The primary'} stopped answering, so another server became the primary {new Date(fleet.promotedAt).toLocaleString()}.</p>}
    {addresses.length > 0 && <p className="section-note">Give devices every address as a DNS server, in your router’s DHCP settings: <strong>{addresses.join(', ')}</strong>.</p>}
    {fleet && fleet.instances.length > 0 && <table className="network-table"><caption className="network-table-caption">AdGuard servers</caption>
      <thead><tr><th>Server</th><th>Address</th><th>State</th><th>Last synced</th><th aria-label="Actions"></th></tr></thead>
      <tbody>{fleet.instances.map(item => <tr key={item.name}>
        <th>{item.node}<span className="network-cell-note">{item.primary ? 'Primary' : 'Copy'} · {item.name}</span></th>
        <td>{item.address ?? 'None'}{item.hostName && <span className="network-cell-note">{item.hostName}</span>}</td>
        <td>{item.setup ? 'Setting up' : item.healthy ? 'Answering' : 'Not answering'}{item.error && <span className="network-cell-note">{item.error}</span>}</td>
        <td>{item.primary ? 'Source' : item.syncedAt ? new Date(item.syncedAt).toLocaleString() : 'Not yet'}</td>
        <td>{!item.primary && item.healthy && !item.setup && <button className="text-link" disabled={busy !== null} onClick={() => void promote(item.name)}>
          {busy === item.name ? 'Switching…' : 'Make primary'}</button>}</td>
      </tr>)}</tbody></table>}
  </section>
}