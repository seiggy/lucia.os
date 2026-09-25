import { useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { Icon } from './Icon'
import { parseAdGuardStatus } from './networkManagement'
import type { AdGuardStatus } from './networkManagement'
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
