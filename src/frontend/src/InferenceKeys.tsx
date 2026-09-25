import { useCallback, useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { Icon } from './Icon'
import './InferenceKeys.css'

interface KeyInfo { id: string; name: string; hint: string; createdAt: string; expiresAt: string | null }
function parseKey(value: unknown): KeyInfo {
  if (!value || typeof value !== 'object') throw new Error('Lucia returned invalid API-key metadata.')
  const key = value as Record<string, unknown>
  if (typeof key.id !== 'string' || !/^[a-f\d]{8}-(?:[a-f\d]{4}-){3}[a-f\d]{12}$/i.test(key.id) || typeof key.name !== 'string'
    || typeof key.hint !== 'string' || !key.hint.startsWith('lucia_inf_')
    || typeof key.createdAt !== 'string' || !Number.isFinite(Date.parse(key.createdAt))
    || (key.expiresAt !== null && (typeof key.expiresAt !== 'string' || !Number.isFinite(Date.parse(key.expiresAt)))))
    throw new Error('Lucia returned invalid API-key metadata.')
  return { id: key.id, name: key.name, hint: key.hint, createdAt: key.createdAt, expiresAt: key.expiresAt }
}

export function InferenceKeys(props: { session: AuthenticationSession; refreshSession: () => Promise<void> }) {
  if (!props.session.isOwner) return <section className="reading-surface">
    <h1>Owner access is needed.</h1><p>Ask your Lucia owner to create an inference key for your application.</p>
    <a className="text-link" href="#/ai">Back to Local AI <Icon name="arrow" /></a>
  </section>
  return <KeyManager {...props} />
}

function KeyManager({ session, refreshSession }: { session: AuthenticationSession; refreshSession: () => Promise<void> }) {
  const [keys, setKeys] = useState<KeyInfo[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [expires, setExpires] = useState('')
  const [secret, setSecret] = useState<{ value: string; keyId: string } | null>(null)
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  const [revoke, setRevoke] = useState<string | null>(null)
  const [showCreate, setShowCreate] = useState(false)
  const nameInput = useRef<HTMLInputElement>(null)
  const input = useRef<HTMLInputElement>(null)
  const mounted = useRef(true)
  const read = useRef<AbortController | null>(null)
  const write = useRef<AbortController | null>(null)
  const refresh = useCallback(async () => {
    read.current?.abort()
    const controller = new AbortController()
    read.current = controller
    setLoading(true)
    try {
      const response = await ownerRequest(session, refreshSession, '/api/host/inference-keys', 'GET', undefined, controller.signal)
      const value: unknown = await response.json()
      if (!Array.isArray(value) || value.length > 1000) throw new Error('Lucia returned an invalid API-key list.')
      const next = value.map(parseKey)
      if (mounted.current && !controller.signal.aborted) { setKeys(next); setLoadError(null) }
    } catch (failure) {
      if (mounted.current && !controller.signal.aborted) setLoadError(failure instanceof Error ? failure.message : 'API keys could not be read.')
    } finally {
      if (mounted.current && !controller.signal.aborted) setLoading(false)
    }
  }, [session, refreshSession])
  useEffect(() => {
    mounted.current = true
    void refresh()
    return () => { mounted.current = false; read.current?.abort(); write.current?.abort() }
  }, [refresh])
  useEffect(() => {
    if (secret) { input.current?.focus(); input.current?.select() }
  }, [secret])
  useEffect(() => { if (showCreate) nameInput.current?.focus() }, [showCreate])
  async function create() {
    if (write.current || secret) return
    const controller = new AbortController()
    write.current = controller
    setBusy(true); setError(null); setNotice('')
    try {
      const expiresAt = expires ? new Date(`${expires}T23:59:59`).toISOString() : null
      const response = await ownerRequest(session, refreshSession, '/api/host/inference-keys', 'POST',
        { name: name.trim(), expiresAt }, controller.signal)
      const value: unknown = await response.json()
      if (!value || typeof value !== 'object' || !('secret' in value) || !('key' in value) || typeof value.secret !== 'string'
        || !/^lucia_inf_[A-Za-z0-9_-]{43}$/.test(value.secret))
        throw new Error('The secret could not be read. Refresh the list, revoke the new key, and create another.')
      const created = parseKey(value.key)
      if (mounted.current && !controller.signal.aborted) {
        setSecret({ value: value.secret, keyId: created.id }); setName(''); setExpires(''); setShowCreate(false); await refresh()
      }
    } catch (failure) {
      if (mounted.current && !controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'The key could not be created.')
        await refresh()
      }
    } finally { if (write.current === controller) write.current = null; if (mounted.current) setBusy(false) }
  }
  async function remove(id: string) {
    if (write.current) return
    const controller = new AbortController()
    write.current = controller
    setBusy(true); setError(null)
    try {
      await ownerRequest(session, refreshSession, `/api/host/inference-keys/${id}`, 'DELETE', undefined, controller.signal)
      if (mounted.current && !controller.signal.aborted) {
        setSecret(current => current?.keyId === id ? null : current)
        setRevoke(null); setNotice('The key was revoked. New requests using it will be rejected.'); await refresh()
      }
    } catch (failure) {
      if (mounted.current && !controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'The key could not be revoked.')
    } finally { if (write.current === controller) write.current = null; if (mounted.current) setBusy(false) }
  }
  async function copy() {
    if (!secret) return
    try { await navigator.clipboard.writeText(secret.value); setNotice('API key copied. Save it in your application now.') }
    catch { setNotice('Copy was unavailable. Select and copy the key manually.'); input.current?.select() }
  }
  return <>
    <div className="key-page-heading"><div className="page-intro"><h1>API keys</h1><p>A separate key for each application. Allow inference without giving away control of your host.</p></div>
      {!showCreate && <button className="button primary" disabled={busy || loading || !!loadError || !!secret} onClick={() => setShowCreate(true)}>New API key <Icon name="arrow" /></button>}</div>
    {(error || loadError) && <div className="key-error" role="alert"><p>{error ?? loadError}</p>
      {loadError && <button className="text-link" disabled={busy} onClick={() => void refresh()}>Refresh key list <Icon name="refresh" /></button>}</div>}
    <p className="key-notice" role="status">{notice}</p>
    {secret && <section className="surface key-secret" aria-labelledby="key-secret-heading">
      <h2 id="key-secret-heading">Copy this key now.</h2><p>It is shown only once. Lucia stores its hash, not a recoverable copy.</p>
      <label htmlFor="new-inference-key">New inference API key</label>
      <input id="new-inference-key" ref={input} value={secret.value} readOnly autoComplete="off" spellCheck={false} onFocus={event => event.target.select()} />
      <div className="key-actions"><button className="button primary" onClick={() => void copy()}><Icon name="copy" />Copy key</button>
        <button className="button secondary" onClick={() => { setSecret(null); setNotice('The one-time secret has been dismissed.') }}>I have saved it</button></div>
      <p className="section-note">Use this as the API key with <strong>{location.origin}/v1</strong>. The secret is not saved in browser storage.</p>
    </section>}
    <section hidden={!showCreate} className="surface key-create" aria-labelledby="key-create-heading">
      <h2 id="key-create-heading">New key</h2>
      <form onSubmit={event => { event.preventDefault(); void create() }}>
        <div className="key-fields"><label>Name<input ref={nameInput} value={name} onChange={event => setName(event.target.value)} maxLength={80} required placeholder="For example, Home Assistant" disabled={busy || !!secret} /></label>
          <label>Optional expiry date<input type="date" value={expires} onChange={event => setExpires(event.target.value)} disabled={busy || !!secret} /><small>Leave blank for no expiry. A selected date ends in your local timezone.</small></label></div>
        <div className="key-actions"><button className="button primary" disabled={busy || loading || !!loadError || !!secret || !name.trim()}>{busy ? 'Saving…' : 'Create inference key'}<Icon name="shield" /></button>
          <button className="button secondary" type="button" disabled={busy} onClick={() => setShowCreate(false)}>Not now</button></div>
      </form>
    </section>
    <section className="key-list-section" aria-labelledby="key-list-heading">
      <div className="key-list-heading"><h2 id="key-list-heading">Application keys</h2><button className="text-link" disabled={busy || loading} onClick={() => void refresh()}><Icon name="refresh" />Refresh</button></div>
      {loading && <p className="muted" role="status">Reading your keys…</p>}
      {!loading && !loadError && keys.length === 0 && <p className="muted">No application keys have been created yet.</p>}
      {keys.length > 0 && <div className="surface key-list">{keys.map(key => <article key={key.id}>
        <div className="key-row"><div><h3>{key.name}</h3><p className="key-hint">{key.hint}</p>
          <p className="key-meta">Created {new Date(key.createdAt).toLocaleDateString()} · {key.expiresAt ? `Expires ${new Date(key.expiresAt).toLocaleString()}` : 'Does not expire'}</p></div>
          <button className="text-link" disabled={busy || !!loadError} onClick={() => setRevoke(key.id)}>Revoke<span className="visually-hidden"> {key.name}</span></button></div>
        {revoke === key.id && <div className="key-revoke"><p>Revoke <strong>{key.name}</strong>? Connected apps will lose access on their next request. Requests already running are not cancelled.</p>
          <div className="key-actions"><button className="button secondary" disabled={busy} onClick={() => void remove(key.id)}>Revoke key</button><button className="text-link" disabled={busy} onClick={() => setRevoke(null)}>Keep key</button></div></div>}
      </article>)}</div>}
      <p className="section-note">Existing installation-managed credentials are separate and are not displayed or changed here. Your dashboard continues to use Authentik sign-in.</p>
    </section>
  </>
}
