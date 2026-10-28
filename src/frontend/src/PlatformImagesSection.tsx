import { useCallback, useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { Icon } from './Icon'
import { disruption, kindLabel, parsePlatformImages, patchServices, policyLabel, stepLabel, waiting } from './platformImages'
import type { PlatformImages as State, PlatformOffer } from './platformImages'

type Confirm = { kind: 'one'; service: string; offer: PlatformOffer } | { kind: 'patches'; services: string[] } | null

/** The Spark's third-party images, on the Spark updates page. Applying goes through the Spark's own update service. */
export function PlatformImagesSection({ session, refreshSession, gpuStale }: {
  session: AuthenticationSession; refreshSession: () => Promise<void>; gpuStale: boolean
}) {
  const [state, setState] = useState<State | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<Confirm>(null)
  const [submitting, setSubmitting] = useState(false)
  const [copied, setCopied] = useState(false)
  const pending = useRef<AbortController | null>(null)

  const load = useCallback(async () => {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller
    try {
      const response = await ownerRequest(session, refreshSession, '/api/host/platform-images', 'GET', undefined, controller.signal)
      const next = parsePlatformImages(await response.json())
      if (!controller.signal.aborted) { setState(next); setError(null) }
    } catch (failure) {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'Platform images could not be read.')
    } finally {
      if (pending.current === controller) pending.current = null
    }
  }, [session, refreshSession])

  const working = !!state && (state.pending || state.status?.state === 'working')
  useEffect(() => {
    void load()
    const interval = setInterval(() => { if (document.visibilityState !== 'hidden') void load() }, working ? 4000 : 60000)
    return () => clearInterval(interval)
  }, [load, working])
  useEffect(() => () => { pending.current?.abort(); pending.current = null }, [])

  async function send(path: string, body: unknown) {
    setSubmitting(true)
    try {
      await ownerRequest(session, refreshSession, path, 'POST', body)
      setConfirm(null)
      pending.current?.abort()
      pending.current = null
      await load()
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Lucia could not start this update.')
    } finally { setSubmitting(false) }
  }

  if (!state) return <section className="surface network-section" aria-labelledby="platform-images">
    <h2 id="platform-images">Platform images</h2>
    {error ? <p className="network-error" role="alert">{error}</p> : <p className="section-note" role="status">Reading what the Spark runs…</p>}
  </section>

  const busy = submitting || working || state.worker.state !== 'ready' || gpuStale
  const patches = patchServices(state.images)
  const status = state.status
  const finished = status && status.state !== 'working' && status.state !== 'idle' && !state.pending ? status : null

  function panel(services: string[], offer: PlatformOffer | null) {
    return <div className="updates-panel" role="group" aria-label="Confirm platform update">
      <h3>{offer ? `Update to ${offer.tag} now?` : `Update ${services.length} service${services.length === 1 ? '' : 's'} to their patches now?`}</h3>
      <p className={services.some(service => ['authentik', 'gateway', 'database', 'directory'].includes(service)) ? 'network-warning' : 'section-note'}>{disruption(services)}</p>
      {offer?.kind === 'release-line' && <p className="section-note">Authentik migrates its database to the new release line. The Spark backs the database up first and restores it if the update fails.
        {offer.upgradeNotes && <> <a className="text-link" href={offer.upgradeNotes} target="_blank" rel="noreferrer">Upgrade notes</a></>}</p>}
      {state!.qwenRunning && <p className="network-warning">Qwen3.8-Flash-Next is running on the Spark. An update can still go ahead, but pulling images and restarting services while it holds most of the Spark’s memory is slower and riskier. Stopping it on the AI page first is safer.</p>}
      <p className="section-note">The Spark checks each image with its registry, updates, and waits up to five minutes for every service to come back. If one doesn’t, it puts the previous images back.</p>
      <div className="network-actions">
        <button className="button primary" disabled={submitting} onClick={() => void send('/api/host/platform-images/update', offer
          ? { updates: [{ service: services[0], tag: offer.tag }] } : { services, patchOnly: true })}>{submitting ? 'Sending…' : 'Update now'}</button>
        <button className="text-link" onClick={() => setConfirm(null)}>Cancel</button>
      </div>
    </div>
  }

  return <section className="surface network-section platform-images" aria-labelledby="platform-images">
    <div className="network-overview-header"><div>
      <h2 id="platform-images"><Icon name="server" />Platform images</h2>
      <p className="section-note">Authentik, the gateway, the certificate authority, the directory, the database and the telemetry relay. Lucia pins these itself, so app updates never touch them. Release notes sit next to every version.</p>
    </div>
      <button className="button secondary" disabled={submitting} onClick={() => void send('/api/host/platform-images/check', undefined)}>Check again<Icon name="refresh" /></button>
    </div>
    {error && <p className="network-error" role="alert">{error}</p>}
    {state.problem && <p className="section-note">{state.problem}</p>}
    {state.worker.state === 'missing' && <div>
      <p>Updating these needs the Spark’s platform update service, which runs as your setup account and never gives Lucia’s web host Docker access. Run this once on the Spark:</p>
      <div className="updates-command"><code>{state.worker.installCommand}</code>
        <button className="button secondary" onClick={() => void navigator.clipboard.writeText(state.worker.installCommand).then(() => setCopied(true), () => setCopied(false))}>
          <Icon name={copied ? 'check' : 'copy'} />{copied ? 'Copied' : 'Copy'}</button></div>
    </div>}
    {state.worker.state === 'stale' && <p className="network-warning">The Spark’s platform update service stopped responding. It restarts on its own; if this lasts, run <code>systemctl --user status lucia-platform-update</code> on the Spark.</p>}
    {gpuStale && <p className="network-warning">Platform updates wait until the NVIDIA device list is refreshed (see Needs attention above).</p>}

    {working ? <div className="updates-task" role="status">
      <p><strong>{state.pending ? 'Waiting for the Spark’s platform update service…' : stepLabel(status?.step ?? null)}</strong>
        {status?.message && !state.pending && <span className="muted"> · {status.message}</span>}</p>
      <progress className="updates-bar" aria-label="Updating platform images" />
      <p className="section-note">It keeps running if you close this page. Lucia may be unreachable for a few seconds while the gateway restarts.</p>
    </div> : finished && <p className={finished.state === 'succeeded' ? 'network-notice' : 'network-error'} role="status">
      {finished.message ?? (finished.state === 'succeeded' ? 'Updated.' : 'The update didn’t finish.')}
      {finished.backup && ` Database backup: ${finished.backup}.`}</p>}

    {patches.length > 0 && (confirm?.kind === 'patches' ? panel(confirm.services, null) : <div className="network-actions">
      <button className="button primary" disabled={busy} onClick={() => setConfirm({ kind: 'patches', services: patches })}>Update all patches ({patches.length})</button>
    </div>)}

    <ul className="updates-list">{state.images.map(item => <li key={item.service}>
      <div className="platform-row">
        <span className="updates-name"><strong>{item.name}</strong><span className="muted">{item.image}</span></span>
        <span className="updates-version">{item.tag}{item.override && <span className="muted">Updated from Lucia</span>}<span className="muted">{policyLabel(item.policy)}</span></span>
      </div>
      {item.problem && <p className="section-note">{item.problem}</p>}
      {item.offers.length === 0 ? <p className="section-note platform-current">{state.checkedAt ? <><Icon name="check" />Up to date</> : 'Not checked with its registry yet'}</p>
        : <ul className="platform-offers">{item.offers.map(entry => {
          const open = confirm?.kind === 'one' && confirm.service === item.service && confirm.offer.tag === entry.tag && confirm.offer.digest === entry.digest
          return <li key={entry.tag + entry.digest}>
            <div className="updates-meta">
              <strong>{entry.kind === 'rebuild' ? `${entry.tag} (rebuilt)` : entry.tag}</strong>
              <span className={`updates-badge ${entry.kind === 'major' ? 'badge-restart' : entry.kind === 'patch' || entry.kind === 'rebuild' ? 'badge-security' : ''}`}>{kindLabel(entry.kind)}</span>
              {entry.kind === 'major' && <span className="updates-badge badge-restart">Breaking</span>}
              {entry.notes && <a className="text-link" href={entry.notes} target="_blank" rel="noreferrer">Release notes</a>}
              {entry.upgradeNotes && <a className="text-link" href={entry.upgradeNotes} target="_blank" rel="noreferrer">Upgrade notes</a>}
              {entry.applicable ? <button className="button secondary" disabled={busy} onClick={() => setConfirm({ kind: 'one', service: item.service, offer: entry })}>Update</button>
                : <span className="muted">Needs a new Lucia release</span>}
              <span className="muted">{waiting(entry.firstSeenAt) > 0 ? `Waiting ${waiting(entry.firstSeenAt)} day${waiting(entry.firstSeenAt) === 1 ? '' : 's'}` : 'New today'}</span>
            </div>
            {open && panel([item.service], entry)}
          </li>
        })}</ul>}
    </li>)}
      <li>
        <div className="platform-row">
          <span className="updates-name"><strong>{state.plugin.name}</strong><span className="muted">Gateway plugin</span></span>
          <span className="updates-version">{state.plugin.version}<span className="muted">Shown only</span></span>
        </div>
        {state.plugin.latest ? <div className="updates-meta"><strong>{state.plugin.latest}</strong>
          {state.plugin.notes && <a className="text-link" href={state.plugin.notes} target="_blank" rel="noreferrer">Release notes</a>}
          <span className="muted">Needs a new Lucia release: its download is pinned by hash.</span></div>
          : <p className="section-note platform-current"><Icon name="check" />Up to date</p>}
      </li>
    </ul>
    {state.checkedAt && <p className="section-note">Checked with each registry {new Date(state.checkedAt).toLocaleString()}. Lucia checks again every six hours.</p>}
  </section>
}
