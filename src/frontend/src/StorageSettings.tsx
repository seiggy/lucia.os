import { useCallback, useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { Icon } from './Icon'
import { StateLabel } from './Stacks'
import { mountLabel, nasDraftProblem, parseNasList, shareNameFrom, shareState } from './stackManagement'
import type { NasDraft, NasKind, NasServer } from './stackManagement'
import './NetworkSettings.css'

const endpoint = '/api/host/nas'
const blank = (): NasDraft => ({ id: '', kind: 'nfs', host: '', username: '', password: '', shares: [{ name: '', path: '' }] })
const draftOf = (nas: NasServer): NasDraft => ({ id: nas.id, kind: nas.kind, host: nas.host, username: nas.username ?? '', password: '',
  shares: nas.shares.map(share => ({ name: share.name, path: share.path })) })
const messageOf = (failure: unknown, fallback: string) => failure instanceof Error ? failure.message : fallback

export function StorageSettings({ session, refreshSession }: { session: AuthenticationSession; refreshSession: () => Promise<void> }) {
  const [servers, setServers] = useState<NasServer[] | null>(null)
  const [draft, setDraft] = useState<NasDraft>(blank)
  const [editing, setEditing] = useState<string | null>(null)
  const [removing, setRemoving] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState('')
  const mounted = useRef(true)
  const form = useRef<HTMLHeadingElement>(null)

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const value = parseNasList(await (await ownerRequest(session, refreshSession, endpoint, 'GET', undefined, signal)).json())
      if (mounted.current && !signal?.aborted) setServers(value)
    } catch (failure) { if (mounted.current && !signal?.aborted) setError(messageOf(failure, 'Storage connections are unavailable.')) }
  }, [session, refreshSession])
  useEffect(() => {
    mounted.current = true
    if (!session.isOwner) return
    const controller = new AbortController()
    void load(controller.signal)
    // Servers report their mounts every 20 seconds.
    const timer = window.setInterval(() => void load(controller.signal), 10000)
    return () => { mounted.current = false; controller.abort(); window.clearInterval(timer) }
  }, [session, load])

  const saved = servers?.find(nas => nas.id === editing) ?? null
  const problem = nasDraftProblem(draft, saved)
  const update = (change: Partial<NasDraft>) => { setDraft(current => ({ ...current, ...change })); setError(null) }
  const updateShare = (index: number, path: string, name?: string) => setDraft(current => ({
    ...current, shares: current.shares.map((share, at) => at !== index ? share : name !== undefined ? { ...share, name }
      : { path, name: !share.name || share.name === shareNameFrom(share.path) ? shareNameFrom(path) : share.name }),
  }))

  async function send(method: 'PUT' | 'DELETE', id: string, body: unknown, done: string) {
    setBusy(true); setError(null); setNotice('')
    try {
      await ownerRequest(session, refreshSession, `${endpoint}/${encodeURIComponent(id)}`, method, body)
      if (!mounted.current) return
      setNotice(done); setRemoving(null)
      if (method === 'PUT' || id === editing) { setEditing(null); setDraft(blank()) }
      await load()
    } catch (failure) { if (mounted.current) setError(messageOf(failure, 'Lucia couldn’t save the NAS.')) }
    finally { if (mounted.current) setBusy(false) }
  }
  function submit(event: React.FormEvent) {
    event.preventDefault()
    if (problem) { setError(problem); return }
    void send('PUT', draft.id, {
      kind: draft.kind, host: draft.host.trim(), shares: draft.shares.map(share => ({ name: share.name.trim(), path: share.path.trim() })),
      ...draft.kind === 'smb' ? { username: draft.username.trim(), password: draft.password || null } : {},
    }, `${draft.id} saved. Servers mount its shares within about 20 seconds.`)
  }
  function edit(nas: NasServer) {
    setEditing(nas.id); setDraft(draftOf(nas)); setError(null); setNotice(''); setRemoving(null)
    window.requestAnimationFrame(() => form.current?.focus())
  }

  if (!session.isOwner) return <div className="page-intro"><h1>Owner access is needed.</h1><p>Your lab owner connects network storage.</p></div>
  const nfs = draft.kind === 'nfs'
  return <>
    <div className="page-intro"><h1>Storage</h1><p>Connect a NAS and every server Lucia manages mounts its shares at the same folder, so an app can use them wherever it runs. Lucia only mounts shares; it never changes anything on the NAS.</p></div>
    {error && <p className="network-error" role="alert">{error}</p>}
    <p className="network-notice" role="status">{notice || (!servers && !error ? 'Reading storage connections…' : '')}</p>

    {servers?.map(nas => <section key={nas.id} className="surface network-section" aria-labelledby={`nas-${nas.id}`}>
      <div className="network-overview-header">
        <div><h2 id={`nas-${nas.id}`}>{nas.id}</h2>
          <p className="section-note storage-summary">{nas.kind === 'nfs' ? 'NFS' : `SMB as ${nas.username}`} at <span className="storage-mono">{nas.host}</span></p></div>
        <div className="network-actions storage-header-actions">
          <button className="button secondary" disabled={busy} onClick={() => edit(nas)}>Edit</button>
          <button className="text-link" disabled={busy} onClick={() => setRemoving(nas.id)}>Remove</button></div>
      </div>
      {removing === nas.id && <div className="network-warning" role="alert"><p>{nas.shares.some(share => share.usedBy.length)
        ? `${[...new Set(nas.shares.flatMap(share => share.usedBy))].join(', ')} still use ${nas.id}. Change that first.`
        : `Remove ${nas.id}? Every server unmounts its shares. Nothing on the NAS is deleted.`}</p>
        <div className="network-actions">{!nas.shares.some(share => share.usedBy.length) && <button className="button secondary" disabled={busy}
          onClick={() => void send('DELETE', nas.id, undefined, `${nas.id} removed. Servers unmount its shares within about 20 seconds.`)}>Remove {nas.id}</button>}
          <button className="text-link" disabled={busy} onClick={() => setRemoving(null)}>Keep it</button></div></div>}
      <table className="network-table storage-table">
        <caption className="network-table-caption">Shares on {nas.id}</caption>
        <thead><tr><th scope="col">Share</th><th scope="col">Folder on every server</th><th scope="col">Status</th></tr></thead>
        <tbody>{nas.shares.map(share => {
          const state = shareState(share)
          const trouble = share.mounts.filter(mount => mount.state !== 'Mounted')
          return <tr key={share.name}>
            <th scope="row" data-label="Share">{share.name}
              <span className="network-cell-note storage-mono">{nas.kind === 'nfs' ? share.path : `//${nas.host}/${share.path}`}</span>
              {share.usedBy.length > 0 && <span className="network-cell-note">Used by {share.usedBy.join(', ')}</span>}</th>
            <td data-label="Folder on every server"><code className="storage-path">{share.mountPath}</code></td>
            <td data-label="Status"><StateLabel {...state} />
              {trouble.length > 0 && <ul className="storage-mounts">{trouble.map(mount => <li key={mount.node}>
                <strong>{mount.node}</strong> {mountLabel(mount).toLowerCase()}{mount.message ? `: ${mount.message}` : mount.state === null ? ': it hasn’t reported since the share was added. It may be offline or running an older agent.' : ''}
              </li>)}</ul>}</td>
          </tr>
        })}</tbody>
      </table>
      {nas.kind === 'nfs' && nas.shares.some(share => share.mounts.some(mount => mount.state === 'Failed')) &&
        <p className="section-note">“Access denied” usually means the NAS doesn’t export that path to the server’s address. Allow each Lucia server in the NAS’s NFS settings.</p>}
    </section>)}

    <section className="surface network-section">
      <h2 ref={form} tabIndex={-1} className="network-step-heading">{editing ? `Edit ${editing}` : servers?.length ? 'Connect another NAS' : 'Connect a NAS'}</h2>
      <form onSubmit={submit} noValidate>
        <div className="network-fields">
          <label>Name<input value={draft.id} onChange={event => update({ id: event.target.value.toLowerCase().replace(/[^a-z0-9-]/g, '') })}
            maxLength={32} autoComplete="off" spellCheck={false} placeholder="unas" disabled={busy || editing !== null} aria-describedby="nas-name-hint" />
            <span id="nas-name-hint" className="stack-hint">Shares mount under <code>/mnt/lucia/nas/{draft.id || 'name'}/</code></span></label>
          <label>Address<input value={draft.host} onChange={event => update({ host: event.target.value.trim() })} maxLength={253}
            autoComplete="off" spellCheck={false} placeholder="192.168.1.10" disabled={busy} /></label>
        </div>
        <fieldset className="storage-kind" disabled={busy}>
          <legend>Protocol</legend>
          {(['nfs', 'smb'] as NasKind[]).map(kind => <label key={kind} className="network-checkbox">
            <input type="radio" name="nas-kind" checked={draft.kind === kind} onChange={() => update({ kind })} />
            <span><strong>{kind === 'nfs' ? 'NFS' : 'SMB'}</strong><br />{kind === 'nfs'
              ? 'Most NAS appliances and Linux servers. The NAS decides access by each server’s address.'
              : 'Windows file sharing. Lucia signs in with a NAS account.'}</span></label>)}
        </fieldset>
        {!nfs && <div className="network-fields">
          <label>Username<input value={draft.username} onChange={event => update({ username: event.target.value })} maxLength={64}
            autoComplete="off" spellCheck={false} disabled={busy} /></label>
          <label>Password<input type="password" value={draft.password} onChange={event => update({ password: event.target.value })}
            maxLength={256} autoComplete="new-password" disabled={busy}
            placeholder={saved?.kind === 'smb' && saved.hasPassword ? 'Saved. Leave blank to keep it.' : ''} /></label>
        </div>}
        <fieldset className="storage-shares" disabled={busy}>
          <legend>Shares</legend>
          {draft.shares.map((share, index) => <div key={index} className="storage-share-row">
            <label>{nfs ? 'Export path' : 'Share name'}<input value={share.path} onChange={event => updateShare(index, event.target.value)}
              maxLength={256} autoComplete="off" spellCheck={false} placeholder={nfs ? '/volume1/media' : 'Media'} /></label>
            <label>Folder name<input value={share.name} onChange={event => updateShare(index, share.path, event.target.value)}
              maxLength={64} autoComplete="off" spellCheck={false} placeholder="media" /></label>
            <button type="button" className="text-link storage-remove" disabled={draft.shares.length === 1}
              aria-label={`Remove share ${share.path || index + 1}`}
              onClick={() => setDraft(current => ({ ...current, shares: current.shares.filter((_, at) => at !== index) }))}><Icon name="close" /></button>
          </div>)}
          {draft.shares.length < 32 && <button type="button" className="text-link storage-add"
            onClick={() => setDraft(current => ({ ...current, shares: [...current.shares, { name: '', path: '' }] }))}>Add another share</button>}
        </fieldset>
        <ol className="network-help">
          {nfs
            ? <li>On the NAS, export each path to your Lucia servers’ addresses, with read and write access if apps save files there.</li>
            : <li>Use an account that can open these shares. Lucia encrypts the password and gives it only to your servers, where only root can read it.</li>}
          <li>In an app’s compose file, mount the folder, such as <code>/mnt/lucia/nas/{draft.id || 'name'}/{draft.shares[0]?.name || 'media'}:/media</code>. Lucia then runs that app only on servers that have the share mounted.</li>
        </ol>
        <div className="network-actions">
          <button className="button primary" disabled={busy || !servers}>{busy ? 'Saving…' : editing ? 'Save changes' : 'Save and mount'}<Icon name="drive" /></button>
          {editing && <button type="button" className="text-link" disabled={busy} onClick={() => { setEditing(null); setDraft(blank()); setError(null) }}>Cancel</button>}
        </div>
      </form>
    </section>
  </>
}
