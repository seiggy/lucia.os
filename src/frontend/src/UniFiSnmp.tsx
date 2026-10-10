import { useCallback, useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { Icon } from './Icon'

const endpoint = '/api/host/unifi/snmp'
const authProtocols = ['SHA', 'SHA256', 'SHA512', 'MD5'] as const
const privProtocols = ['AES', 'AES192', 'AES256', 'DES'] as const
interface SnmpStatus { configured: boolean; username: string | null; authProtocol: string | null; privProtocol: string | null; lastScrapeAt: string | null; message: string | null }
const text = (value: unknown) => typeof value === 'string' && value ? value : null
function parse(value: unknown): SnmpStatus {
  const root = value && typeof value === 'object' ? value as Record<string, unknown> : {}
  return { configured: root.configured === true, username: text(root.username), authProtocol: text(root.authProtocol), privProtocol: text(root.privProtocol),
    lastScrapeAt: text(root.lastScrapeAt), message: text(root.message) }
}

export function UniFiSnmp({ session, refreshSession }: { session: AuthenticationSession; refreshSession: () => Promise<void> }) {
  const [status, setStatus] = useState<SnmpStatus | null>(null)
  const [username, setUsername] = useState('')
  const [authProtocol, setAuthProtocol] = useState<string>('SHA')
  const [privProtocol, setPrivProtocol] = useState<string>('AES')
  const [passphrase, setPassphrase] = useState('')
  const [privPassphrase, setPrivPassphrase] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState('')
  const mounted = useRef(true)
  const load = useCallback(async (signal?: AbortSignal) => {
    const value = parse(await (await ownerRequest(session, refreshSession, endpoint, 'GET', undefined, signal)).json())
    if (!mounted.current || signal?.aborted) return
    setStatus(value)
    if (value.username) setUsername(value.username)
    if (value.authProtocol) setAuthProtocol(value.authProtocol)
    if (value.privProtocol) setPrivProtocol(value.privProtocol)
  }, [session, refreshSession])
  useEffect(() => {
    mounted.current = true
    const controller = new AbortController()
    load(controller.signal).catch(failure => { if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'SNMP status is unavailable.') })
    return () => { mounted.current = false; controller.abort() }
  }, [load])

  async function change(method: 'PUT' | 'DELETE', body: unknown, done: string) {
    setBusy(true); setError(null); setNotice('')
    try {
      await ownerRequest(session, refreshSession, endpoint, method, body)
      if (!mounted.current) return
      setPassphrase(''); setPrivPassphrase(''); setNotice(done)
      await load()
    } catch (failure) { if (mounted.current) setError(failure instanceof Error ? failure.message : 'The SNMP change failed.') }
    finally { if (mounted.current) setBusy(false) }
  }
  const privacy = privPassphrase || passphrase
  const ready = username.trim() && passphrase.length >= 8 && privacy.length >= 8

  return <section className="surface network-section unifi-snmp" aria-labelledby="unifi-snmp">
    <h2 id="unifi-snmp">Device traffic (SNMPv3)</h2>
    <p className="section-note">The lab map reads per-port traffic from your gateway and switches over SNMPv3. In UniFi Network, open <strong>Settings → System → Advanced</strong>, turn on SNMPv3, and enter the same username and password here.</p>
    {error && <p className="network-error" role="alert">{error}</p>}
    <p className="network-notice" role="status">{notice}</p>
    {status?.configured && <dl className="network-facts">
      <div><dt>Username</dt><dd>{status.username}</dd></div>
      <div><dt>Protocols</dt><dd>{status.authProtocol} authentication, {status.privProtocol} privacy</dd></div>
      <div><dt>Last reading</dt><dd>{status.lastScrapeAt ? new Date(status.lastScrapeAt).toLocaleString() : 'Waiting for the first poll'}</dd></div>
    </dl>}
    {status?.message && <p className="network-warning">{status.message}</p>}
    <form onSubmit={event => { event.preventDefault(); if (ready) void change('PUT', { username: username.trim(), authProtocol, authPassphrase: passphrase, privProtocol, privPassphrase: privacy },
      'SNMPv3 credentials saved. The first reading arrives within a minute.') }}>
      <div className="network-fields unifi-fields">
        <label>Username<input value={username} onChange={event => setUsername(event.target.value)} autoComplete="off" required maxLength={64} disabled={busy} /></label>
        <label>Password<input type="password" value={passphrase} onChange={event => setPassphrase(event.target.value)} autoComplete="new-password"
          required minLength={8} maxLength={128} disabled={busy} placeholder={status?.configured ? 'Saved; enter to replace' : undefined} /></label>
      </div>
      <details>
        <summary>Advanced</summary>
        <div className="network-fields unifi-fields">
          <label>Authentication<select value={authProtocol} onChange={event => setAuthProtocol(event.target.value)} disabled={busy}>
            {authProtocols.map(item => <option key={item}>{item}</option>)}</select></label>
          <label>Privacy<select value={privProtocol} onChange={event => setPrivProtocol(event.target.value)} disabled={busy}>
            {privProtocols.map(item => <option key={item}>{item}</option>)}</select></label>
        </div>
        <label>Separate privacy password<input type="password" value={privPassphrase} onChange={event => setPrivPassphrase(event.target.value)} autoComplete="new-password"
          minLength={8} maxLength={128} disabled={busy} placeholder="Same as password" /></label>
      </details>
      <div className="network-actions">
        <button className="button primary" disabled={busy || !ready}>{busy ? 'Saving…' : status?.configured ? 'Replace credentials' : 'Save credentials'}<Icon name="shield" /></button>
        {status?.configured && <button type="button" className="text-link" disabled={busy} onClick={() => void change('DELETE', undefined, 'SNMPv3 credentials removed. The map falls back to UniFi rates.')}>Remove credentials</button>}
      </div>
    </form>
  </section>
}
