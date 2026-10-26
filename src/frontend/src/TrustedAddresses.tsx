import { useCallback, useEffect, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'

interface SafeHarbor { enabled: boolean; addresses: string[]; lucia: string[]; updatedAt: string | null; updatedBy: string | null }

const strings = (value: unknown, limit: number) => Array.isArray(value) && value.length <= limit && value.every(item => typeof item === 'string')

function parseSafeHarbor(data: unknown): SafeHarbor {
  if (!data || typeof data !== 'object') throw new Error('The trusted addresses were incomplete.')
  const value = data as Record<string, unknown>
  if (typeof value.enabled !== 'boolean' || !strings(value.addresses, 32) || !strings(value.lucia, 64)
    || (value.updatedAt !== null && typeof value.updatedAt !== 'string') || (value.updatedBy !== null && typeof value.updatedBy !== 'string'))
    throw new Error('The trusted addresses were incomplete.')
  return { enabled: value.enabled, addresses: value.addresses as string[], lucia: value.lucia as string[],
    updatedAt: value.updatedAt as string | null, updatedBy: value.updatedBy as string | null }
}

const failed = (failure: unknown, fallback: string) => failure instanceof Error ? failure.message : fallback

/** Devices the gateway's CrowdSec protection never bans or checks, such as the owner's own computer. */
export function TrustedAddresses({ session, refreshSession }: { session: AuthenticationSession; refreshSession: () => Promise<void> }) {
  const [harbor, setHarbor] = useState<SafeHarbor | null>(null)
  const [draft, setDraft] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState('')

  const load = useCallback(async (signal?: AbortSignal) => {
    try { setHarbor(parseSafeHarbor(await (await ownerRequest(session, refreshSession, '/api/host/security/safe-harbor', 'GET', undefined, signal)).json())) }
    catch (failure) { if (!signal?.aborted) setError(failed(failure, 'The trusted addresses could not be read.')) }
  }, [session, refreshSession])
  useEffect(() => {
    const controller = new AbortController()
    void load(controller.signal)
    return () => controller.abort()
  }, [load])

  async function save() {
    if (draft === null) return
    setBusy(true); setError(null); setNotice('')
    try {
      const addresses = draft.split(/[\s,]+/).map(item => item.trim()).filter(Boolean)
      setHarbor(parseSafeHarbor(await (await ownerRequest(session, refreshSession, '/api/host/security/safe-harbor', 'PUT', { addresses })).json()))
      setDraft(null)
      setNotice('Saved. The gateway and CrowdSec follow the new list within a minute.')
    } catch (failure) { setError(failed(failure, 'The trusted addresses could not be saved.')) } finally { setBusy(false) }
  }

  return <section className="surface network-section" aria-labelledby="trusted-addresses">
    <h2 id="trusted-addresses">Trusted addresses</h2>
    <p className="section-note">Lucia’s gateway runs CrowdSec. It bans internet addresses that probe or attack your apps, records what devices on your
      network do without banning them, and blocks known exploit requests to your apps. Addresses here are never banned or checked, so add your own
      computer.{harbor && !harbor.enabled && ' CrowdSec isn’t installed on this gateway yet; the list applies once it is.'}</p>
    {harbor && draft === null && <>
      {harbor.addresses.length > 0
        ? <ul className="network-review-list">{harbor.addresses.map(address => <li key={address}><code>{address}</code></li>)}</ul>
        : <p className="section-note">Nothing yet. Only Lucia’s own addresses are trusted.</p>}
      <p className="section-note">Always trusted: {harbor.lucia.map((address, index) => <span key={address}>{index > 0 && ', '}<code>{address}</code></span>)} (Lucia itself).
        {harbor.updatedAt && ` Changed ${new Date(harbor.updatedAt).toLocaleString()}${harbor.updatedBy ? ` by ${harbor.updatedBy}` : ''}.`}</p>
      <div className="network-actions"><button className="button secondary" onClick={() => { setDraft(harbor.addresses.join('\n')); setNotice(''); setError(null) }}>Edit trusted addresses…</button></div>
    </>}
    {draft !== null && <form onSubmit={event => { event.preventDefault(); void save() }}>
      <label>One address or network per line
        <textarea className="ssh-key-input" value={draft} onChange={event => setDraft(event.target.value)} rows={5} disabled={busy} spellCheck={false}
          autoComplete="off" placeholder={'192.168.1.50\n192.168.1.0/24'} /></label>
      <p className="section-note">Private networks can be as wide as you like. Internet addresses are limited to a single site (/24 or narrower), and Cloudflare’s addresses can’t be trusted because every visitor comes through them.</p>
      <div className="network-actions">
        <button className="button primary" disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
        <button type="button" className="text-link" disabled={busy} onClick={() => { setDraft(null); setError(null) }}>Cancel</button>
      </div>
    </form>}
    {notice && <p className="section-note" role="status">{notice}</p>}
    {error && <p className="network-error" role="alert">{error}</p>}
  </section>
}
