import { useCallback, useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { Icon } from './Icon'
import { parseRegistryList, registryDraftProblem, registryHost } from './stackManagement'
import type { RegistryDraft, RegistryLogin } from './stackManagement'
import './NetworkSettings.css'

const endpoint = '/api/host/registries'
const blank = (): RegistryDraft => ({ host: '', username: '', secret: '' })
const nameOf = (host: string) => host === 'docker.io' ? 'Docker Hub' : host
const messageOf = (failure: unknown, fallback: string) => failure instanceof Error ? failure.message : fallback

export function RegistrySettings({ session, refreshSession }: { session: AuthenticationSession; refreshSession: () => Promise<void> }) {
  const [registries, setRegistries] = useState<RegistryLogin[] | null>(null)
  const [draft, setDraft] = useState<RegistryDraft>(blank)
  const [editing, setEditing] = useState<string | null>(null)
  const [removing, setRemoving] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState('')
  const mounted = useRef(true)
  const form = useRef<HTMLHeadingElement>(null)

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const value = parseRegistryList(await (await ownerRequest(session, refreshSession, endpoint, 'GET', undefined, signal)).json())
      if (mounted.current && !signal?.aborted) setRegistries(value)
    } catch (failure) { if (mounted.current && !signal?.aborted) setError(messageOf(failure, 'Registry sign-ins are unavailable.')) }
  }, [session, refreshSession])
  useEffect(() => {
    mounted.current = true
    if (!session.isOwner) return
    const controller = new AbortController()
    void load(controller.signal)
    return () => { mounted.current = false; controller.abort() }
  }, [session, load])

  const saved = registries?.find(registry => registry.host === editing) ?? null
  const problem = registryDraftProblem(draft, saved)
  const update = (change: Partial<RegistryDraft>) => { setDraft(current => ({ ...current, ...change })); setError(null) }

  async function send(method: 'PUT' | 'DELETE', host: string, body: unknown, done: string) {
    setBusy(true); setError(null); setNotice('')
    try {
      await ownerRequest(session, refreshSession, `${endpoint}/${encodeURIComponent(host)}`, method, body)
      if (!mounted.current) return
      setNotice(done); setRemoving(null)
      if (method === 'PUT' || host === editing) { setEditing(null); setDraft(blank()) }
      await load()
    } catch (failure) { if (mounted.current) setError(messageOf(failure, 'Lucia couldn’t save the sign-in.')) }
    finally { if (mounted.current) setBusy(false) }
  }
  function submit(event: React.FormEvent) {
    event.preventDefault()
    if (problem) { setError(problem); return }
    const host = registryHost(draft.host)
    void send('PUT', host, { username: draft.username.trim(), secret: draft.secret || null },
      `Signed in to ${nameOf(host)}. Servers have it within about 20 seconds, and apps pull private images on their next apply or update.`)
  }
  function edit(registry: RegistryLogin) {
    setEditing(registry.host); setDraft({ host: registry.host, username: registry.username, secret: '' }); setError(null); setNotice(''); setRemoving(null)
    window.requestAnimationFrame(() => form.current?.focus())
  }

  if (!session.isOwner) return <div className="page-intro"><h1>Owner access is needed.</h1><p>Your lab owner signs in to image registries.</p></div>
  return <>
    <div className="page-intro"><h1>Registries</h1><p>Sign in to a private image registry, such as Docker Hub, and every server Lucia manages can pull your private images. Lucia also uses the sign-in to check those images for updates.</p></div>
    {error && <p className="network-error" role="alert">{error}</p>}
    <p className="network-notice" role="status">{notice || (!registries && !error ? 'Reading registry sign-ins…' : '')}</p>

    <section className="surface network-section">
      <h2>Signed in</h2>
      {registries?.length === 0 && <p className="section-note">No registries yet. Servers pull public images only.</p>}
      {registries && registries.length > 0 && <ul className="network-review-list">
        {registries.map(registry => <li key={registry.host} className="ssh-key-row">
          <div><strong>{nameOf(registry.host)}</strong>
            <span>As <code>{registry.username}</code> · updated {new Date(registry.updatedAt).toLocaleDateString()} by {registry.updatedBy}</span></div>
          {removing === registry.host
            ? <div className="network-actions ssh-key-confirm"><button className="button secondary" disabled={busy}
              onClick={() => void send('DELETE', registry.host, undefined, `Signed out of ${nameOf(registry.host)}. Servers forget the sign-in within about 20 seconds.`)}>Remove sign-in</button>
              <button className="text-link" disabled={busy} onClick={() => setRemoving(null)}>Keep</button></div>
            : <div className="network-actions ssh-key-confirm"><button className="text-link" disabled={busy} onClick={() => edit(registry)}>Edit</button>
              <button className="text-link" disabled={busy} onClick={() => setRemoving(registry.host)} aria-label={`Remove ${nameOf(registry.host)}`}>Remove</button></div>}
        </li>)}
      </ul>}
      <p className="section-note">Removing a sign-in leaves running apps alone, but they can’t pull that registry’s private images again until you add it back.</p>
    </section>

    <section className="surface network-section">
      <h2 ref={form} tabIndex={-1} className="network-step-heading">{saved ? `Update ${nameOf(saved.host)}` : 'Add a registry'}</h2>
      <form onSubmit={submit} noValidate>
        <div className="network-fields">
          <label>Registry<input value={draft.host} onChange={event => update({ host: event.target.value })} maxLength={260}
            autoComplete="off" spellCheck={false} placeholder="docker.io" disabled={busy || editing !== null} aria-describedby="registry-host-hint" />
            <span id="registry-host-hint" className="ssh-key-hint">docker.io for Docker Hub, ghcr.io for GitHub, or your registry’s host and port.</span></label>
          <label>Username<input value={draft.username} onChange={event => update({ username: event.target.value })} maxLength={256}
            autoComplete="off" spellCheck={false} disabled={busy} /></label>
          <label>Access token<input type="password" value={draft.secret} onChange={event => update({ secret: event.target.value })}
            maxLength={4096} autoComplete="new-password" disabled={busy} placeholder={saved ? 'Saved. Leave blank to keep it.' : ''} /></label>
        </div>
        <ol className="network-help">
          <li>For Docker Hub, create a personal access token with read-only access under Account settings → Personal access tokens. A password works too, but a token can be revoked on its own.</li>
          <li>Lucia signs in to check the token, encrypts it, and gives it only to your servers, where only root can read it. Signing in also raises Docker Hub’s pull limit.</li>
          <li>If the token is revoked or expires, pulls from that registry fail until you update or remove it here.</li>
        </ol>
        <div className="network-actions">
          <button className="button primary" disabled={busy || !registries}>{busy ? 'Signing in…' : saved ? 'Save changes' : 'Sign in'}<Icon name="shield" /></button>
          {editing && <button type="button" className="text-link" disabled={busy} onClick={() => { setEditing(null); setDraft(blank()); setError(null) }}>Cancel</button>}
        </div>
      </form>
    </section>
  </>
}
