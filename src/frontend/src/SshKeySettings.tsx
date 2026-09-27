import { useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { Icon } from './Icon'
import './NetworkSettings.css'

export interface OwnerSshKey { id: string; publicKey: string; algorithm: string; fingerprint: string; label: string; addedAt: string }
interface ImportedKey { publicKey: string; algorithm: string; fingerprint: string }

export function parseOwnerSshKeys(data: unknown): { username: string; keys: OwnerSshKey[] } {
  if (!data || typeof data !== 'object' || !('username' in data) || typeof data.username !== 'string'
    || !('keys' in data) || !Array.isArray(data.keys) || data.keys.length > 50)
    throw new Error('SSH key information was incomplete.')
  const keys = data.keys.map((key: unknown) => {
    if (!key || typeof key !== 'object') throw new Error('A saved SSH key was invalid.')
    const value = key as Record<string, unknown>
    if (typeof value.id !== 'string' || typeof value.publicKey !== 'string' || typeof value.algorithm !== 'string'
      || typeof value.fingerprint !== 'string' || typeof value.label !== 'string' || typeof value.addedAt !== 'string')
      throw new Error('A saved SSH key was invalid.')
    return { id: value.id, publicKey: value.publicKey, algorithm: value.algorithm, fingerprint: value.fingerprint, label: value.label, addedAt: value.addedAt }
  })
  return { username: data.username, keys }
}

export function SshKeySettings({ session, refreshSession }: { session: AuthenticationSession; refreshSession: () => Promise<void> }) {
  const [username, setUsername] = useState('')
  const [keys, setKeys] = useState<OwnerSshKey[]>([])
  const [publicKey, setPublicKey] = useState('')
  const [label, setLabel] = useState('')
  const [github, setGithub] = useState('')
  const [imported, setImported] = useState<ImportedKey[]>([])
  const [confirm, setConfirm] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState('')
  const pending = useRef<AbortController | null>(null)
  const mounted = useRef(true)
  useEffect(() => {
    mounted.current = true
    const controller = new AbortController()
    if (!session.isOwner) { setLoading(false); return }
    void ownerRequest(session, refreshSession, '/api/host/ssh-keys', 'GET', undefined, controller.signal)
      .then(response => response.json()).then(parseOwnerSshKeys).then(value => {
        if (!controller.signal.aborted) { setUsername(value.username); setKeys(value.keys) }
      }).catch(failure => { if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'SSH keys are unavailable.') })
      .finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => { mounted.current = false; controller.abort(); pending.current?.abort() }
  }, [session, refreshSession])

  async function run<T>(work: (signal: AbortSignal) => Promise<T>, done: (value: T) => void) {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller
    setBusy(true); setError(null); setNotice('')
    try {
      const value = await work(controller.signal)
      if (mounted.current && !controller.signal.aborted) done(value)
    } catch (failure) {
      if (mounted.current && !controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'The SSH key change failed.')
    } finally { if (pending.current === controller) pending.current = null; if (mounted.current) setBusy(false) }
  }
  const add = (key: string, name: string) => run(async signal => parseOwnerSshKeys(await (await ownerRequest(session, refreshSession,
    '/api/host/ssh-keys', 'POST', { publicKey: key, label: name.trim() || null }, signal)).json()), value => {
    setKeys(value.keys); setPublicKey(''); setLabel('')
    setImported(current => current.filter(item => item.publicKey !== key))
    setNotice('Key added. Managed servers pick it up at their next check-in, usually within a minute.')
  })
  const remove = (id: string) => run(async signal => parseOwnerSshKeys(await (await ownerRequest(session, refreshSession,
    `/api/host/ssh-keys/${encodeURIComponent(id)}`, 'DELETE', undefined, signal)).json()), value => {
    setKeys(value.keys); setConfirm(null)
    setNotice('Key removed. Managed servers stop accepting it at their next check-in.')
  })
  const lookup = () => {
    if (!/^[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,37}[a-zA-Z0-9])?$/.test(github) || github.includes('--')) { setError('Enter a GitHub username, not a URL.'); return }
    void run(async signal => {
      const data: unknown = await (await ownerRequest(session, refreshSession, `/api/host/ssh-keys/github/${encodeURIComponent(github)}`, 'GET', undefined, signal)).json()
      if (!data || typeof data !== 'object' || !('keys' in data) || !Array.isArray(data.keys) || data.keys.length > 100)
        throw new Error('GitHub key information was incomplete. Paste your public key instead.')
      return data.keys.map((key: unknown) => {
        const value = (key ?? {}) as Record<string, unknown>
        if (typeof value.publicKey !== 'string' || typeof value.algorithm !== 'string' || typeof value.fingerprint !== 'string')
          throw new Error('A returned public key was invalid. Paste your public key instead.')
        return { publicKey: value.publicKey, algorithm: value.algorithm, fingerprint: value.fingerprint }
      })
    }, (value: ImportedKey[]) => {
      const saved = new Set(keys.map(key => key.publicKey))
      const fresh = value.filter(key => !saved.has(key.publicKey))
      setImported(fresh)
      setNotice(value.length === 0 ? 'No supported public SSH keys were found on that GitHub account.'
        : fresh.length === 0 ? 'Every supported key on that account is already added.' : 'Check each fingerprint before adding it.')
    })
  }

  if (!session.isOwner) return <div className="page-intro"><h1>Owner access is needed.</h1><p>Only lab owners can sign in to managed servers.</p></div>
  return <>
    <div className="page-intro"><h1>SSH keys</h1><p>Add your public keys once. Every server Lucia manages accepts them for your directory account{username ? <> — sign in with <code>ssh {username}@&lt;server&gt;</code></> : null}.</p></div>
    {error && <p className="network-error" role="alert">{error}</p>}
    <p className="network-notice" role="status">{notice || (loading ? 'Reading your keys…' : '')}</p>
    <section className="surface network-section">
      <h2>Your keys</h2>
      {!loading && keys.length === 0 && <p className="section-note">No keys yet. Until you add one, managed servers ask for your Lucia password.</p>}
      {keys.length > 0 && <ul className="network-review-list">
        {keys.map(key => <li key={key.id} className="ssh-key-row">
          <div><strong>{key.label || key.algorithm}</strong>
            <span>{key.algorithm} · <code>{key.fingerprint}</code> · added {new Date(key.addedAt).toLocaleDateString()}</span></div>
          {confirm === key.id
            ? <div className="network-actions ssh-key-confirm"><button className="button secondary" disabled={busy} onClick={() => void remove(key.id)}>Remove key</button>
              <button className="text-link" disabled={busy} onClick={() => setConfirm(null)}>Keep</button></div>
            : <button className="text-link" disabled={busy} onClick={() => setConfirm(key.id)} aria-label={`Remove ${key.label || key.fingerprint}`}>Remove</button>}
        </li>)}
      </ul>}
      <p className="section-note">Keys apply to your account only. Removing a key also removes it from every managed server. Your Lucia password keeps working either way.</p>
    </section>
    <section className="surface network-section">
      <h2>Add a key</h2>
      <form onSubmit={event => { event.preventDefault(); void add(publicKey, label) }}>
        <label>Public key<textarea className="ssh-key-input" value={publicKey} onChange={event => setPublicKey(event.target.value.trim())} rows={3}
          autoComplete="off" spellCheck={false} maxLength={16384} required disabled={busy} placeholder="ssh-ed25519 AAAA… you@laptop" /></label>
        <label>Label <span className="ssh-key-hint">(optional; defaults to the key comment)</span>
          <input value={label} onChange={event => setLabel(event.target.value)} maxLength={64} autoComplete="off" disabled={busy} placeholder="Work laptop" /></label>
        <p className="section-note">Ed25519, RSA 2048-bit or stronger, or ECDSA P-256. Paste the <code>.pub</code> file, never the private key.</p>
        <button className="button primary" disabled={busy || loading || !publicKey}>{busy ? 'Saving…' : 'Add key'}<Icon name="shield" /></button>
      </form>
      <div className="network-fields ssh-key-github">
        <label>Or import from GitHub<input value={github} onChange={event => { setGithub(event.target.value.trim()); setImported([]) }} maxLength={39}
          autoComplete="off" spellCheck={false} disabled={busy} placeholder="GitHub username"
          onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); lookup() } }} /></label>
        <div><button className="button secondary" type="button" disabled={busy || !github} onClick={lookup}>Read public keys</button></div>
      </div>
      {imported.length > 0 && <ul className="network-review-list">
        {imported.map(key => <li key={key.fingerprint} className="ssh-key-row">
          <div><strong>{key.algorithm}</strong><span><code>{key.fingerprint}</code></span></div>
          <button className="button secondary" disabled={busy} onClick={() => void add(key.publicKey, `${github} (GitHub)`)}>Add</button>
        </li>)}
      </ul>}
      <p className="section-note">Public keys on GitHub don't prove who owns the account, so check the fingerprints. Later changes on GitHub are not synced.</p>
    </section>
  </>
}
