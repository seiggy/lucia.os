import { useCallback, useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { parseManagedNodes } from './onboarding'
import type { ManagedNodeSummary } from './onboarding'
import { Icon } from './Icon'
import type { IconName } from './Icon'
import { bytes } from './sparkTelemetry'
import {
  catalogDefaults, catalogReason, composeExample, containerState, describeUnmet, envValue, freeName, logLineChoices, moveState, parseCatalog, parseInventory,
  parseStackDetail, parseStackList, parseStackSummary, portRows, requirementForm, requirementList, settingsProblem, fieldShown, stackNamePattern, stackState, unmetRequirement,
  validateStackDraft,
} from './stackManagement'
import type { CatalogApp, CatalogServer, NodeContainer, NodeInventory, RequirementForm, StackDetail, StackPlacement, StackSummary, StackTone } from './stackManagement'
import './NetworkSettings.css'
import './Stacks.css'

type Session = { session: AuthenticationSession; refreshSession: () => Promise<void> }
type View = 'list' | 'containers' | 'catalog' | 'new' | 'app' | 'install'

const toneIcon: Record<StackTone, IconName> = { green: 'check', amber: 'attention', failed: 'attention', muted: 'stop', accent: 'clock' }
const message = (failure: unknown, fallback: string) => failure instanceof Error ? failure.message : fallback

function useJson<T>({ session, refreshSession }: Session, path: string | null, parse: (value: unknown) => T, refreshMs = 0) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tick, setTick] = useState(0)
  const reload = useCallback(() => setTick(value => value + 1), [])
  useEffect(() => {
    if (!path) return
    const controller = new AbortController()
    void ownerRequest(session, refreshSession, path, 'GET', undefined, controller.signal).then(response => response.json()).then(parse)
      .then(value => { if (!controller.signal.aborted) { setData(value); setError(null) } })
      .catch(failure => { if (!controller.signal.aborted) setError(message(failure, 'Lucia could not read this.')) })
    const timer = refreshMs > 0 ? window.setTimeout(reload, refreshMs) : undefined
    return () => { controller.abort(); window.clearTimeout(timer) }
  }, [session, refreshSession, path, parse, refreshMs, reload, tick])
  return { data, error, reload, setData }
}

export function Stacks({ session, refreshSession, view, name, node }: Session & { view: View; name?: string; node?: string }) {
  if (!session.isOwner) return <div className="page-intro"><h1>Owner access is needed.</h1><p>Only lab owners can run apps on managed servers.</p></div>
  const props = { session, refreshSession }
  return <>
    {view !== 'app' && view !== 'install' && <>
      {view === 'new' && <a className="text-link stack-back" href="#/apps/catalog"><Icon name="back" />Catalog</a>}
      <div className="page-intro"><h1>{view === 'new' ? 'Your own app' : view === 'containers' ? 'Containers' : view === 'catalog' ? 'Add an app' : 'Apps'}</h1><p>{view === 'new'
        ? 'Paste a Docker Compose file and say where it can run. Lucia keeps it running there and reports what happens.'
        : view === 'containers' ? 'Everything running on a server, including containers Lucia didn\'t start, and the ports in use.'
          : view === 'catalog' ? 'Apps Lucia knows how to set up. Pick one and a server; Lucia writes the configuration and keeps it current.'
            : 'Container apps Lucia runs on your servers. Each one is a Docker Compose file placed on one server.'}</p></div>
      {view !== 'new' && <nav className="model-library-navigation" aria-label="Apps navigation">
        <a href="#/apps" aria-current={view === 'list' ? 'page' : undefined}>Your apps</a>
        <a href="#/apps/catalog" aria-current={view === 'catalog' ? 'page' : undefined}><Icon name="search" />Catalog</a>
        <a href="#/apps/containers" aria-current={view === 'containers' ? 'page' : undefined}><Icon name="server" />Containers & ports</a>
      </nav>}
    </>}
    {view === 'list' && <AppList {...props} />}
    {view === 'containers' && <ContainersView {...props} />}
    {view === 'catalog' && <CatalogView {...props} />}
    {view === 'install' && name && <InstallApp {...props} id={name} node={node} />}
    {view === 'new' && <AppEditor {...props} />}
    {view === 'app' && name && <AppDetail {...props} name={name} />}
  </>
}

export function StateLabel({ label, tone }: { label: string; tone: StackTone }) {
  return <span className={`stack-state stack-tone-${tone}`}><Icon name={toneIcon[tone]} />{label}</span>
}

function AppList(props: Session) {
  const { data: stacks, error } = useJson(props, '/api/host/stacks', parseStackList, 10000)
  return <section className="surface network-section">
    <div className="network-overview-header"><h2>Your apps</h2><a className="button primary" href="#/apps/catalog">Add an app<Icon name="arrow" /></a></div>
    {error && <p className="network-error" role="alert">{error}</p>}
    {!stacks && !error && <p className="section-note" role="status">Reading your apps…</p>}
    {stacks?.length === 0 && <div className="stack-empty">
      <p>No apps yet. <a href="#/apps/catalog">Pick one from the catalog</a>, or paste the Docker Compose file of any self-hosted app.</p>
      <p className="section-note">Lucia stores named volumes under <code>/srv/lucia/stacks/&lt;app&gt;/volumes</code> on the server, so your data is easy to find and back up.</p>
    </div>}
    {stacks && stacks.length > 0 && <table className="network-table stack-table">
      <caption className="network-table-caption">Apps and their state</caption>
      <thead><tr><th scope="col">App</th><th scope="col">State</th><th scope="col">Server</th><th scope="col"><span className="network-table-caption">Open</span></th></tr></thead>
      <tbody>{stacks.map(stack => {
        const state = stackState(stack)
        return <tr key={stack.name}>
          <th scope="row"><a className="stack-name" href={`#/apps/${stack.name}`}>{stack.name}</a>{stack.template?.name && stack.template.name !== stack.name
            && <span className="network-cell-note">{stack.template.name}</span>}</th>
          <td data-label="State"><StateLabel {...state} /><span className="network-cell-note">{state.detail}</span></td>
          <td data-label="Server">{stack.move ? <span className="stack-route">{stack.move.from}<Icon name="arrow" />{stack.move.to}</span> : stack.node}</td>
          <td className="stack-open"><a className="text-link" href={`#/apps/${stack.name}`} aria-label={`Open ${stack.name}`}>Open<Icon name="chevron" /></a></td>
        </tr>
      })}</tbody>
    </table>}
  </section>
}

function useNodes(props: Session) {
  return useJson(props, '/api/host/nodes', parseManagedNodes)
}

function nodeNote(node: ManagedNodeSummary): string {
  const runtime = node.status?.runtime
  if (node.state !== 'Online') return node.state === 'Stale' ? 'not checking in' : 'waiting for first check-in'
  if (!runtime) return 'needs an agent update for apps'
  if (runtime.state !== 'Ready') return runtime.state === 'Preparing' ? 'installing Docker' : 'Docker setup failed'
  return runtime.gpuContainers ? 'Docker ready · GPU' : 'Docker ready'
}

function AppEditor({ existing, locked = false, onSaved, ...props }: Session & {
  existing?: { name: string; node: string; placement: StackPlacement; compose: string; env: string; revision: number }
  locked?: boolean; onSaved?: (stack: StackSummary) => void
}) {
  const { data: nodes, error: nodesError } = useNodes(props)
  const { data: stacks } = useJson(props, existing ? null : '/api/host/stacks', parseStackList)
  const [name, setName] = useState(existing?.name ?? '')
  const [mode, setMode] = useState<'auto' | 'pin'>('auto')
  const [node, setNode] = useState('')
  const [form, setForm] = useState<RequirementForm>(() => requirementForm(existing?.placement.require ?? []))
  const [compose, setCompose] = useState(existing?.compose ?? '')
  const [env, setEnv] = useState(existing?.env ?? '')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const revision = useRef(existing?.revision ?? 0)
  const require = requirementList(form)
  const rows = (nodes ?? []).map(item => ({ node: item, ...eligibility(item, require) }))
  useEffect(() => {
    const first = rows.find(row => row.ok) ?? rows[0]
    if (!node && first) setNode(first.node.hostname)
  }, [rows, node])
  const load = (host: string) => (stacks ?? []).filter(item => item.node === host || item.move?.to === host).length
  const pick = mode === 'auto' && !existing ? rows.filter(row => row.ok)
    .sort((a, b) => load(a.node.hostname) - load(b.node.hostname) || (a.node.hostname < b.node.hostname ? -1 : 1))[0] ?? null : null
  const dirty = !existing || existing.compose !== compose || existing.env !== env || existing.placement.require.join('\n') !== require.join('\n')
  const unplaceable = !existing && mode === 'auto' && nodes !== null && !pick

  async function save(event: React.FormEvent) {
    event.preventDefault()
    const pin = existing ? existing.placement.node : mode === 'pin' ? node : null
    const problem = validateStackDraft(name, pin, compose)
    if (problem) { setError(problem); return }
    setBusy(true); setError(null)
    try {
      const response = await ownerRequest(props.session, props.refreshSession, `/api/host/stacks/${encodeURIComponent(name)}`, 'PUT',
        { compose, env, manifest: { schemaVersion: 1, placement: { node: pin, require } }, expectedRevision: revision.current })
      const saved = parseStackSummary(await response.json())
      revision.current = saved.revision
      if (onSaved) onSaved(saved)
      else window.location.hash = `#/apps/${saved.name}`
    } catch (failure) {
      setError(message(failure, 'The app could not be saved.'))
    } finally { setBusy(false) }
  }

  const update = (change: Partial<RequirementForm>) => setForm(value => ({ ...value, ...change }))
  const placement = <fieldset className="stack-placement" disabled={busy}>
    <legend>Where it runs</legend>
    {existing
      ? <p className="section-note">Runs on <strong>{existing.node}</strong>{existing.placement.node ? ', pinned there' : ''}. To run it on another server, move it below; moving takes its data along.</p>
      : <div className="stack-modes">
        <label className="network-checkbox"><input type="radio" name="stack-placement" checked={mode === 'auto'} onChange={() => setMode('auto')} />
          <span><strong>Any server that fits</strong><span className="network-cell-note">Lucia picks a server that meets the requirements below and is running the fewest apps.</span></span></label>
        <label className="network-checkbox"><input type="radio" name="stack-placement" checked={mode === 'pin'} onChange={() => setMode('pin')} />
          <span><strong>A specific server</strong><span className="network-cell-note">For apps tied to one machine, such as its disks or a USB device.</span></span></label>
      </div>}
    {!existing && mode === 'pin' && <label className="stack-server">Server<select value={node} onChange={event => setNode(event.target.value)} required disabled={busy || !nodes}>
      {!nodes && <option value="">{nodesError ? 'Servers unavailable' : 'Reading servers…'}</option>}
      {nodes?.length === 0 && <option value="">No managed servers yet</option>}
      {rows.map(row => <option key={row.node.nodeId} value={row.node.hostname}>{row.node.hostname} — {row.reason}</option>)}
    </select></label>}

    <div className="stack-requirements">
      <label className="network-checkbox"><input type="checkbox" checked={form.gpu} onChange={event => update({ gpu: event.target.checked })} />
        <span><strong>Needs a GPU</strong><span className="network-cell-note">Only servers whose containers can use a GPU qualify. Add <code>gpus: all</code> under the service's <code>deploy</code> reservations too.</span></span></label>
      {form.gpu && <div className="stack-requirement-fields stack-gpu-fields">
        <label>GPU maker<select value={form.vendor} onChange={event => update({ vendor: event.target.value })}>
          <option value="">Any</option><option value="nvidia">NVIDIA</option><option value="amd">AMD</option><option value="intel">Intel</option>
        </select></label>
        <label>GPU memory, at least<span className="stack-unit"><input inputMode="decimal" value={form.vram} placeholder="Any"
          onChange={event => update({ vram: event.target.value.replace(/[^\d.]/g, '') })} /><span>GB</span></span></label>
        <label>Model contains<input value={form.model} maxLength={64} placeholder="4090" spellCheck={false} autoComplete="off"
          onChange={event => update({ model: event.target.value })} /></label>
        <label>Compute capability, at least<input inputMode="decimal" value={form.compute} placeholder="Any" aria-describedby="stack-compute-hint"
          onChange={event => update({ compute: event.target.value.replace(/[^\d.]/g, '') })} />
          <span id="stack-compute-hint" className="stack-hint">NVIDIA's version for GPU features, such as 8.6 for RTX 30-series.</span></label>
        <label>CUDA line<select value={form.cuda} aria-describedby="stack-cuda-hint" onChange={event => update({ cuda: event.target.value as RequirementForm['cuda'] })}>
          <option value="">Any</option><option value="13">CUDA 13</option><option value="12">CUDA 12</option>
        </select><span id="stack-cuda-hint" className="stack-hint">Only servers set to this line under Devices qualify. Match the image's CUDA version.</span></label>
      </div>}
      <div className="stack-requirement-fields">
        <label>Server memory, at least<span className="stack-unit"><input inputMode="decimal" value={form.memory} placeholder="Any"
          onChange={event => update({ memory: event.target.value.replace(/[^\d.]/g, '') })} /><span>GB</span></span></label>
      </div>
      {form.other.length > 0 && <p className="section-note">Also requires {form.other.map(item => <code key={item}>{item}</code>).reduce<React.ReactNode[]>((all, item, index) => index ? [...all, ', ', item] : [item], [])}.</p>}
    </div>

    {nodes && nodes.length > 0 && <div className="stack-eligibility">
      <p className="stack-eligibility-summary" role="status">{existing
        ? rows.find(row => row.node.hostname === existing.node && !row.ok)
          ? <><Icon name="attention" /><span>{existing.node} doesn't meet these requirements. Saving will be refused until you change them or move the app.</span></>
          : <><Icon name="check" /><span>{existing.node} meets these requirements.</span></>
        : mode === 'auto'
          ? pick ? <><Icon name="check" /><span>Lucia will start it on <strong>{pick.node.hostname}</strong>.</span></> : <><Icon name="attention" /><span>No server qualifies right now. Loosen a requirement, or choose a specific server.</span></>
          : rows.find(row => row.node.hostname === node && !row.ok) ? <><Icon name="attention" /><span>{node} doesn't meet these requirements.</span></> : null}</p>
      <ul className="stack-eligibility-list" aria-label="Servers and whether they qualify">{rows.map(row => <li key={row.node.nodeId} className={row.ok ? '' : 'stack-ineligible'}>
        <Icon name={row.ok ? 'check' : 'close'} /><span>{row.node.hostname}</span><span className="stack-eligibility-reason">{row.reason}</span>
      </li>)}</ul>
    </div>}
  </fieldset>

  const fields = <>
    {!existing && <label>Name<input value={name} onChange={event => setName(event.target.value.toLowerCase())} maxLength={40} required
      autoComplete="off" spellCheck={false} disabled={busy} placeholder="jellyfin" aria-describedby="stack-name-hint"
      aria-invalid={name.length > 0 && !stackNamePattern.test(name)} />
      <span id="stack-name-hint" className="stack-hint">Lowercase letters, digits and hyphens. Containers are named <code>lucia-{name || 'name'}-…</code></span></label>}
    <label>Docker Compose file<textarea className="stack-code" value={compose} onChange={event => setCompose(event.target.value)} rows={16}
      spellCheck={false} autoComplete="off" required disabled={busy} placeholder={composeExample} aria-describedby="stack-compose-hint" />
      <span id="stack-compose-hint" className="stack-hint">To use a NAS share, mount its folder, such as <code>/mnt/lucia/nas/unas/media:/media</code>. Lucia runs the app only on servers that have the share mounted. <a className="text-link" href="#/settings/storage">Connect a NAS in Storage</a></span></label>
    <label>Environment <span className="ssh-key-hint">(optional)</span><textarea className="stack-code stack-env" value={env}
      onChange={event => setEnv(event.target.value)} rows={5} spellCheck={false} autoComplete="off" disabled={busy}
      placeholder={'TZ=America/Chicago\nPUID=1000'} aria-describedby="stack-env-hint" />
      <span id="stack-env-hint" className="stack-hint">Stored encrypted and written to the app's <code>.env</code> file. It fills in <code>{'${NAME}'}</code> values in the compose file; add <code>env_file: .env</code> to a service to pass them into its containers.</span></label>
    {placement}
  </>

  return <section className="surface network-section">
    {existing && <h2>Configuration</h2>}
    <form onSubmit={event => void save(event)}>
      {fields}
      {error && <p className="network-error" role="alert">{error}</p>}
      <div className="network-actions">
        <button className="button primary" disabled={busy || locked || !dirty || !nodes?.length || unplaceable}>{busy ? 'Saving…' : existing ? 'Save and apply' : 'Add and start'}</button>
        {!existing && <a className="text-link" href="#/apps">Cancel</a>}
        {existing && locked && <p className="section-note">Changes can be saved once the move finishes.</p>}
        {existing && !locked && !dirty && <p className="section-note">Saved. Changes apply on the server within about 20 seconds.</p>}
      </div>
    </form>
  </section>
}

function eligibility(node: ManagedNodeSummary, require: string[]): { ok: boolean; reason: string } {
  if (node.state !== 'Online' || node.status?.runtime?.state !== 'Ready') return { ok: false, reason: nodeNote(node) }
  const unmet = unmetRequirement(require, node.status, node)
  return unmet ? { ok: false, reason: describeUnmet(unmet) } : { ok: true, reason: nodeNote(node) }
}

function MoveSection({ stack, onChanged, ...props }: Session & { stack: StackSummary; onChanged: (notice: string) => void }) {
  const { data: nodes } = useNodes(props)
  const [target, setTarget] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const rows = (nodes ?? []).filter(item => item.hostname !== stack.node).map(item => ({ node: item, ...eligibility(item, stack.placement.require) }))
  useEffect(() => {
    if (!target && rows.length) setTarget((rows.find(row => row.ok) ?? rows[0]).node.hostname)
  }, [rows, target])
  const chosen = rows.find(row => row.node.hostname === target)

  async function send(path: string, body: unknown, done: string) {
    setBusy(true); setError(null)
    try {
      await ownerRequest(props.session, props.refreshSession, `/api/host/stacks/${encodeURIComponent(stack.name)}/${path}`, 'POST', body)
      onChanged(done)
    } catch (failure) { setError(message(failure, 'That didn\'t work.')) } finally { setBusy(false) }
  }

  if (stack.move) {
    const state = moveState(stack.move, stack.status)
    return <section className="surface network-section">
      <h2>Moving to {stack.move.to}</h2>
      <p><StateLabel label={state.label} tone={state.tone} /> <span>{state.detail}</span></p>
      <progress className="stack-progress" max={100} {...(state.percent !== null ? { value: state.percent } : {})}
        aria-label={`Moving ${stack.name} to ${stack.move.to}`} />
      <p className="section-note">Started by {stack.move.startedBy} at {new Date(stack.move.startedAt).toLocaleTimeString()}. The app starts on {stack.move.to} once every file has arrived; the copy on {stack.move.from} is kept.</p>
      {error && <p className="network-error" role="alert">{error}</p>}
      <div className="network-actions">
        <button className="button secondary" disabled={busy} onClick={() => void send('cancel-move', undefined, `Move cancelled. ${stack.name} stays on ${stack.move!.from}.`)}>Cancel move</button>
      </div>
    </section>
  }

  return <section className="surface network-section">
    <h2>Move to another server</h2>
    <p className="section-note stack-move-note">Lucia stops the app on {stack.node}, copies <code>/srv/lucia/stacks/{stack.name}/</code> to the new server, and starts it there. It's offline while the data copies. Folders the compose file mounts from elsewhere on {stack.node}, such as <code>/mnt/media</code>, don't move; make sure the new server has them.</p>
    {nodes && rows.length === 0 && <p className="section-note">There's no other managed server to move to yet.</p>}
    {rows.length > 0 && <>
      <div className="network-fields stack-node-picker"><label>Move to<select value={target} onChange={event => setTarget(event.target.value)} disabled={busy}>
        {rows.map(row => <option key={row.node.nodeId} value={row.node.hostname}>{row.node.hostname} — {row.reason}</option>)}
      </select></label></div>
      {chosen && !chosen.ok && <p className="section-note"><StateLabel label="Can't move there" tone="amber" /> {chosen.node.hostname}: {chosen.reason}.</p>}
      {error && <p className="network-error" role="alert">{error}</p>}
      <div className="network-actions">
        <button className="button secondary" disabled={busy || !chosen?.ok}
          onClick={() => void send('move', { node: target }, `Moving to ${target}. ${stack.node} stops the app first.`)}>
          <Icon name="arrow" />{busy ? 'Starting move…' : `Move to ${target || 'server'}`}</button>
      </div>
    </>}
  </section>
}

function AppDetail({ name, ...props }: Session & { name: string }) {
  const detail = useJson(props, `/api/host/stacks/${encodeURIComponent(name)}`, parseStackDetail)
  const live = useJson(props, '/api/host/stacks', parseStackList, 5000)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState('')
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [logs, setLogs] = useState<string | null>(null)
  const stack = live.data?.find(item => item.name === name) ?? detail.data?.stack ?? null

  async function act(action: 'start' | 'stop' | 'restart' | 'update', done: string) {
    setBusy(true); setError(null); setNotice('')
    try {
      await ownerRequest(props.session, props.refreshSession, `/api/host/stacks/${encodeURIComponent(name)}/${action}`, 'POST')
      setNotice(done); live.reload()
    } catch (failure) { setError(message(failure, 'That action failed.')) } finally { setBusy(false) }
  }
  async function remove() {
    setBusy(true); setError(null)
    try {
      await ownerRequest(props.session, props.refreshSession, `/api/host/stacks/${encodeURIComponent(name)}`, 'DELETE')
      window.location.hash = '#/apps'
    } catch (failure) { setError(message(failure, 'The app could not be deleted.')); setBusy(false) }
  }

  if (detail.error && !detail.data) return <>
    <a className="text-link stack-back" href="#/apps"><Icon name="back" />All apps</a>
    <p className="network-error" role="alert">{detail.error}</p>
  </>
  if (!stack || !detail.data) return <p className="section-note" role="status">Reading {name}…</p>
  const state = stackState(stack)
  const containers = stack.containers
  const moving = stack.move !== null
  return <>
    <a className="text-link stack-back" href="#/apps"><Icon name="back" />All apps</a>
    <div className="page-intro stack-heading"><h1>{stack.name}</h1>
      <p><StateLabel {...state} /> <span>{state.detail}</span></p></div>
    {!moving && <div className="stack-actions" role="group" aria-label="App actions">
      {stack.desired === 'Running' ? <>
        <button className="button secondary" disabled={busy || moving} onClick={() => void act('restart', 'Restarting. The server recreates the containers within about 20 seconds.')}><Icon name="refresh" />Restart</button>
        <button className="button secondary" disabled={busy || moving} onClick={() => void act('update', 'Checking for newer images. Containers are recreated only if an image changed.')}><Icon name="down" />Update images</button>
        <button className="button secondary" disabled={busy || moving} onClick={() => void act('stop', 'Stopping. Data is kept.')}><Icon name="stop" />Stop</button>
      </> : <button className="button primary" disabled={busy} onClick={() => void act('start', 'Starting.')}><Icon name="arrow" />Start</button>}
    </div>}
    {error && <p className="network-error" role="alert">{error}</p>}
    <p className="network-notice" role="status">{notice}</p>

    <section className="surface network-section">
      <h2>Containers</h2>
      {containers.length === 0 && <p className="section-note">{stack.desired === 'Stopped' ? 'Stopped apps have no containers.' : 'No containers yet.'}</p>}
      {containers.length > 0 && <ul className="network-review-list stack-containers">{containers.map(container => <ContainerRow key={container.id}
        container={container} open={logs === container.name} onLogs={() => setLogs(logs === container.name ? null : container.name)}>
        {logs === container.name && stack.nodeId && <LogsPanel {...props} nodeId={stack.nodeId} container={container.name} />}
      </ContainerRow>)}</ul>}
    </section>

    {stack.template?.id === 'local-ai' && <LocalAiSection stack={stack} detail={detail.data} />}

    {stack.template
      ? <ManagedSettings key={detail.data.stack.revision} {...props} stack={stack} detail={detail.data} locked={moving}
        onSaved={done => { detail.reload(); live.reload(); setNotice(done) }} />
      : <AppEditor key={detail.data.stack.revision} {...props} locked={moving}
        existing={{ name, node: stack.node, placement: stack.placement, compose: detail.data.compose, env: detail.data.env, revision: detail.data.stack.revision }}
        onSaved={() => { detail.reload(); live.reload(); setNotice('Saved. The server applies the change within about 20 seconds.') }} />}

    {stack.template?.serverBound
      ? <section className="surface network-section"><h2>Move to another server</h2>
        <p className="section-note stack-move-note">{stack.template.name ?? stack.name} is set up for {stack.node}'s own hardware, so it can't move. Install it on the other server from the catalog instead.</p></section>
      : <MoveSection {...props} stack={stack} onChanged={done => { setNotice(done); live.reload(); detail.reload() }} />}

    <section className="surface network-section">
      <h2>Delete this app</h2>
      <p className="section-note">The server takes the containers down and deletes the compose and environment files. Data in <code>/srv/lucia/stacks/{name}/</code> on {stack.node} stays until you remove it.</p>
      <div className="network-actions">{confirmDelete
        ? <><button className="button secondary stack-danger" disabled={busy || moving} onClick={() => void remove()}>Delete {name}</button>
          <button className="text-link" disabled={busy} onClick={() => setConfirmDelete(false)}>Keep it</button></>
        : <button className="button secondary" disabled={busy || moving} onClick={() => setConfirmDelete(true)}>Delete app…</button>}</div>
      <p className="section-note">Last changed by {stack.updatedBy} on {new Date(stack.updatedAt).toLocaleString()}.</p>
    </section>
  </>
}

function ContainerRow({ container, open, onLogs, children, showProject = false }: { container: NodeContainer; open: boolean; onLogs: () => void; children?: React.ReactNode; showProject?: boolean }) {
  const state = containerState(container)
  const app = container.project?.startsWith('lucia-') ? container.project.slice(6) : null
  return <li className="stack-container">
    <div className="stack-container-row">
      <div>
        <strong>{container.service ?? container.name}</strong>
        <span><StateLabel {...state} /> · <code>{container.image}</code></span>
        {container.ports && <span className="stack-ports">{container.ports}</span>}
        {showProject && <span>{app ? <>Lucia app <a href={`#/apps/${app}`}>{app}</a></> : container.project ? `Compose project ${container.project}` : 'Not managed by Lucia'}</span>}
      </div>
      <button className="button secondary" onClick={onLogs} aria-expanded={open}>{open ? 'Hide logs' : 'Logs'}</button>
    </div>
    {children}
  </li>
}

function LogsPanel({ nodeId, container, ...props }: Session & { nodeId: string; container: string }) {
  const [tail, setTail] = useState<number>(200)
  const [text, setText] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [tick, setTick] = useState(0)
  const output = useRef<HTMLPreElement>(null)
  useEffect(() => {
    const controller = new AbortController()
    setLoading(true); setError(null)
    void ownerRequest(props.session, props.refreshSession,
      `/api/host/nodes/${encodeURIComponent(nodeId)}/containers/${encodeURIComponent(container)}/logs?tail=${tail}`, 'GET', undefined, controller.signal)
      .then(response => response.json()).then((value: unknown) => {
        if (!value || typeof value !== 'object' || !('logs' in value) || typeof value.logs !== 'string') throw new Error('The logs could not be read.')
        if (!controller.signal.aborted) setText(value.logs)
      }).catch(failure => { if (!controller.signal.aborted) setError(message(failure, 'The logs could not be read.')) })
      .finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => controller.abort()
  }, [props.session, props.refreshSession, nodeId, container, tail, tick])
  useEffect(() => { if (output.current) output.current.scrollTop = output.current.scrollHeight }, [text])
  return <div className="stack-logs">
    <div className="stack-logs-bar">
      <label>Lines<select value={tail} onChange={event => setTail(Number(event.target.value))}>
        {logLineChoices.map(value => <option key={value} value={value}>Last {value.toLocaleString()}</option>)}</select></label>
      <button className="text-link" onClick={() => setTick(value => value + 1)} disabled={loading}><Icon name="refresh" />{loading ? 'Reading…' : 'Refresh'}</button>
    </div>
    {error && <p className="network-error" role="alert">{error}</p>}
    {text !== null && <pre ref={output} className="stack-log-text" tabIndex={0} aria-label={`Logs for ${container}, newest at the bottom`}>{text || 'No log output.'}</pre>}
  </div>
}

function ContainersView(props: Session) {
  const { data: nodes, error: nodesError } = useNodes(props)
  const [nodeId, setNodeId] = useState<string>('')
  const [logs, setLogs] = useState<string | null>(null)
  useEffect(() => { if (!nodeId && nodes?.length) setNodeId((nodes.find(item => item.state === 'Online') ?? nodes[0]).nodeId) }, [nodes, nodeId])
  const inventory = useJson<NodeInventory>(props, nodeId ? `/api/host/nodes/${encodeURIComponent(nodeId)}/containers` : null, parseInventory, 15000)
  const current = inventory.data
  const ports = current ? portRows(current) : []
  return <>
    <section className="surface network-section">
      <div className="network-fields stack-node-picker"><label>Server<select value={nodeId} onChange={event => { setNodeId(event.target.value); setLogs(null); inventory.setData(null) }} disabled={!nodes?.length}>
        {!nodes && <option value="">{nodesError ? 'Servers unavailable' : 'Reading servers…'}</option>}
        {nodes?.map(item => <option key={item.nodeId} value={item.nodeId}>{item.hostname} — {nodeNote(item)}</option>)}
      </select></label></div>
      {nodes?.length === 0 && <p className="section-note">No managed servers yet. Install one from Devices first.</p>}
      {inventory.error && <p className="network-error" role="alert">{inventory.error}</p>}
      {current && <>
        <h2 className="stack-subheading">Containers <span>{current.containers.length}</span></h2>
        {current.containers.length === 0 && <p className="section-note">Nothing is running on this server yet.</p>}
        <ul className="network-review-list stack-containers">{current.containers.map(container => <ContainerRow key={container.id} container={container} showProject
          open={logs === container.name} onLogs={() => setLogs(logs === container.name ? null : container.name)}>
          {logs === container.name && <LogsPanel {...props} nodeId={nodeId} container={container.name} />}
        </ContainerRow>)}</ul>
        <p className="section-note">Reported {new Date(current.reportedAt).toLocaleTimeString()}. Servers report every 20 seconds.</p>
      </>}
    </section>
    {current && <section className="surface network-section">
      <h2>Ports in use</h2>
      <p className="section-note stack-ports-note">Every port the server is listening on, and what opened it. Most system ports are normal; pick a free port when you publish a new app.</p>
      <table className="network-table stack-port-table">
        <caption className="network-table-caption">Listening ports</caption>
        <thead><tr><th scope="col">Port</th><th scope="col">Used by</th><th scope="col">Protocol</th><th scope="col">Address</th></tr></thead>
        <tbody>{ports.map(item => <tr key={`${item.protocol}-${item.port}-${item.owner.label}`}>
          <th scope="row" className="stack-number">{item.port}</th>
          <td data-label="Used by"><span className={item.owner.known ? 'stack-port-owner' : 'stack-port-owner stack-tone-muted'}>
            {item.owner.app ? <a className="text-link" href={`#/apps/${item.owner.app}`}>{item.owner.label}</a> : item.owner.label}</span>
            <span className="network-cell-note">{item.owner.detail}</span></td>
          <td data-label="Protocol">{item.protocol.toUpperCase()}</td>
          <td data-label="Address"><span className="stack-addresses">{item.addresses.map(address => <code key={address}>{address}</code>)}</span>
            {item.addresses.every(address => address === '127.0.0.1' || address === '::1') ? <span className="network-cell-note">This server only</span> : null}</td>
        </tr>)}</tbody>
      </table>
    </section>}
  </>
}

function CatalogView(props: Session) {
  const { data: apps, error } = useJson(props, '/api/host/catalog', parseCatalog)
  return <section className="surface network-section">
    <h2>Catalog</h2>
    {error && <p className="network-error" role="alert">{error}</p>}
    {!apps && !error && <p className="section-note" role="status">Reading the catalog…</p>}
    {apps && <ul className="stack-catalog">
      {apps.map(app => {
        const ready = app.servers.filter(server => !catalogReason(server))
        const blocked = app.servers.find(server => catalogReason(server))
        return <li key={app.id}>
          <div>
            <h3>{app.name}</h3>
            <p>{app.summary}</p>
            <p className="stack-catalog-needs"><span>Needs</span> {app.needs}</p>
            <p className={ready.length ? 'stack-catalog-fit' : 'stack-catalog-fit stack-tone-muted'}>
              <Icon name={ready.length ? 'check' : 'attention'} />
              {ready.length ? <span>Can run on {ready.map(server => server.hostname).join(', ')}</span>
                : <span>No server can run it yet.{blocked ? ` ${blocked.hostname}: ${catalogReason(blocked)}` : ' Add a managed server from Devices.'}</span>}
            </p>
          </div>
          <a className={ready.length ? 'button primary' : 'button secondary'} href={`#/apps/install/${app.id}`}>Install<Icon name="arrow" /></a>
        </li>
      })}
      <li className="stack-catalog-custom">
        <div>
          <h3>Your own app</h3>
          <p>Any self-hosted app with a Docker Compose file. You write the configuration; Lucia runs it, restarts it and reports on it.</p>
        </div>
        <a className="button secondary" href="#/apps/new">Paste a compose file<Icon name="arrow" /></a>
      </li>
    </ul>}
  </section>
}

function SettingsFields({ app, server, values: given, onChange, disabled }: {
  app: CatalogApp; server: CatalogServer | undefined; values: Record<string, string>; onChange: (id: string, value: string) => void; disabled: boolean
}) {
  // Installs from an older catalog version lack newer settings; the server fills the same defaults when it saves.
  const values = { ...Object.fromEntries(app.fields.map(field => [field.id, field.default ?? ''])), ...given }
  return <div className="stack-catalog-fields">{app.fields.filter(field => fieldShown(field, values)).map(field => {
    const hint = field.help && <span id={`setting-${field.id}-hint`} className="stack-hint">{field.help}</span>
    if (field.kind === 'choice') return <fieldset key={field.id} className="stack-gpu-picks">
      <legend>{field.label}</legend>
      <div className="stack-modes">{field.options.map(option => <label key={option.value} className="network-checkbox">
        <input type="radio" name={`setting-${field.id}`} checked={values[field.id] === option.value} disabled={disabled}
          onChange={() => onChange(field.id, option.value)} />
        <span><strong>{option.label}</strong><span className="network-cell-note">{option.help}</span></span>
      </label>)}</div>
    </fieldset>
    if (field.kind !== 'gpus') return <label key={field.id} className={field.kind === 'text' ? 'stack-field-wide' : undefined}>{field.label}<input value={values[field.id] ?? ''} disabled={disabled} required={!field.optional}
      inputMode={field.kind === 'port' ? 'numeric' : undefined} maxLength={field.kind === 'port' ? 5 : 128} spellCheck={false} autoComplete="off"
      className={field.kind === 'port' ? 'stack-number' : undefined} aria-describedby={field.help ? `setting-${field.id}-hint` : undefined}
      onChange={event => onChange(field.id, field.kind === 'port' ? event.target.value.replace(/\D/g, '') : event.target.value)} />{hint}</label>
    const picked = new Set((values[field.id] ?? '').split(',').filter(Boolean))
    return <fieldset key={field.id} className="stack-gpu-picks" aria-describedby={field.help ? `setting-${field.id}-hint` : undefined}>
      <legend>{field.label}</legend>
      {!server?.gpus?.length ? <p className="section-note">{server ? `${server.hostname} reports no GPUs this app can use.` : 'Choose a server first.'}</p>
        : server.gpus.map(gpu => <label key={gpu.uuid} className="network-checkbox">
          <input type="checkbox" checked={picked.has(gpu.uuid)} disabled={disabled || !!gpu.unsupported} onChange={event => {
            const next = new Set(picked)
            if (event.target.checked) next.add(gpu.uuid); else next.delete(gpu.uuid)
            onChange(field.id, server.gpus!.filter(item => next.has(item.uuid)).map(item => item.uuid).join(','))
          }} />
          <span><strong>{gpu.model}</strong><span className="network-cell-note">{gpu.unsupported
            ?? (gpu.memoryBytes ? `${bytes(gpu.memoryBytes)} of memory` : 'Memory not reported')}</span></span>
        </label>)}
      {hint}
    </fieldset>
  })}</div>
}

function ComposePreview({ id, server, settings, ...props }: Session & { id: string; server: string; settings: Record<string, string> }) {
  const [open, setOpen] = useState(false)
  const [text, setText] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const key = JSON.stringify(settings)
  useEffect(() => {
    if (!open || !server) return
    const controller = new AbortController()
    setError(null)
    void ownerRequest(props.session, props.refreshSession, `/api/host/catalog/${encodeURIComponent(id)}/preview`, 'POST',
      { node: server, settings: JSON.parse(key) as Record<string, string> }, controller.signal)
      .then(response => response.json()).then((value: unknown) => {
        if (!value || typeof value !== 'object' || !('compose' in value) || typeof value.compose !== 'string') throw new Error('The preview could not be read.')
        if (!controller.signal.aborted) setText(value.compose)
      }).catch(failure => { if (!controller.signal.aborted) { setText(null); setError(message(failure, 'The preview could not be made.')) } })
    return () => controller.abort()
  }, [open, id, server, key, props.session, props.refreshSession])
  return <details className="stack-compose-details" onToggle={event => setOpen(event.currentTarget.open)}>
    <summary>The compose file Lucia will write</summary>
    {error ? <p className="section-note">{error}</p> : text === null ? <p className="section-note" role="status">Writing a preview…</p>
      : <pre className="stack-log-text" tabIndex={0}>{text}</pre>}
    <p className="section-note">Keys are generated when you install and kept in the app's encrypted environment.</p>
  </details>
}

function InstallApp({ id, node: wanted, ...props }: Session & { id: string; node?: string }) {
  const catalog = useJson(props, '/api/host/catalog', parseCatalog)
  const { data: stacks } = useJson(props, '/api/host/stacks', parseStackList)
  const [name, setName] = useState<string | null>(null)
  const [picked, setPicked] = useState<string | null>(null)
  const [edits, setEdits] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const app = catalog.data?.find(item => item.id === id)
  const back = <a className="text-link stack-back" href="#/apps/catalog"><Icon name="back" />Catalog</a>
  if (catalog.error) return <>{back}<p className="network-error" role="alert">{catalog.error}</p></>
  if (!catalog.data || !stacks) return <>{back}<p className="section-note" role="status">Reading the catalog…</p></>
  if (!app) return <>{back}<div className="page-intro"><h1>Not in the catalog</h1><p>Lucia doesn't have an app called “{id}”. It may have been renamed.</p></div></>

  const taken = stacks.map(stack => stack.name)
  const installed = (host: string) => app.serverBound ? stacks.find(stack => stack.template?.id === app.id && stack.node === host) : undefined
  const blocked = (server: CatalogServer) => catalogReason(server)
    ?? (installed(server.hostname) ? `It already runs ${app.name} as ${installed(server.hostname)!.name}.` : null)
  const open = app.servers.filter(server => !blocked(server))
  const serverName = picked ?? (open.find(server => server.hostname === wanted) ?? open[0])?.hostname ?? ''
  const server = app.servers.find(item => item.hostname === serverName)
  const settings = { ...catalogDefaults(app, server), ...edits }
  const nameValue = name ?? freeName(app.id, taken)
  const nameProblem = !stackNamePattern.test(nameValue) ? 'Use lowercase letters, digits and hyphens, starting with a letter.'
    : taken.includes(nameValue) ? `You already have an app called ${nameValue}.` : null
  const problem = !server ? 'Choose a server.' : nameProblem ?? settingsProblem(app, settings)

  async function install(event: React.FormEvent) {
    event.preventDefault()
    if (problem) { setError(problem); return }
    setBusy(true); setError(null)
    try {
      const response = await ownerRequest(props.session, props.refreshSession, `/api/host/stacks/${encodeURIComponent(nameValue)}`, 'PUT', {
        compose: null, env: null, expectedRevision: 0,
        manifest: { schemaVersion: 1, placement: { node: serverName, require: [] }, template: { id: app!.id, version: app!.version, settings } },
      })
      window.location.hash = `#/apps/${parseStackSummary(await response.json()).name}`
    } catch (failure) { setError(message(failure, `${app!.name} could not be installed.`)); setBusy(false) }
  }

  return <>
    {back}
    <div className="page-intro"><h1>Install {app.name}</h1><p>{app.summary}</p></div>
    <section className="surface network-section">
      <form onSubmit={event => void install(event)}>
        <fieldset className="stack-install-servers" disabled={busy}>
          <legend>Server</legend>
          {app.servers.length === 0 && <p className="section-note">No managed servers yet. <a href="#/devices">Add one from Devices</a> first.</p>}
          <ul className="stack-eligibility-list">{app.servers.map(item => {
            const reason = blocked(item)
            return <li key={item.nodeId} className={reason ? 'stack-ineligible' : ''}>
              <input type="radio" name="install-server" id={`install-${item.nodeId}`} checked={item.hostname === serverName} disabled={!!reason}
                onChange={() => { setPicked(item.hostname); setEdits(current => Object.fromEntries(Object.entries(current).filter(([key]) =>
                  app.fields.find(field => field.id === key)?.kind !== 'gpus'))) }} />
              <label htmlFor={`install-${item.nodeId}`}>{item.hostname}</label>
              <span className="stack-eligibility-reason">{reason ?? (item.gpus?.length
                ? item.gpus.filter(gpu => !gpu.unsupported).map(gpu => gpu.model).join(', ') : 'Ready')}</span>
            </li>
          })}</ul>
          {app.servers.length > 0 && open.length === 0 && <p className="section-note">No server can run {app.name} right now. The reasons are next to each server. {app.needs}</p>}
        </fieldset>

        <fieldset className="stack-install-settings" disabled={busy || !server}>
          <legend>Settings</legend>
          <div className="stack-catalog-fields">
            <label>Name<input value={nameValue} onChange={event => setName(event.target.value.toLowerCase())} maxLength={40} required autoComplete="off"
              spellCheck={false} aria-invalid={!!nameProblem} aria-describedby="install-name-hint" />
              <span id="install-name-hint" className="stack-hint">{nameProblem ?? <>How it appears in Lucia. Containers are named <code>lucia-{nameValue}-…</code></>}</span></label>
          </div>
          <SettingsFields app={app} server={server} values={settings} disabled={busy} onChange={(field, value) => setEdits(current => ({ ...current, [field]: value }))} />
        </fieldset>

        {server && !settingsProblem(app, settings) && <ComposePreview {...props} id={app.id} server={serverName} settings={settings} />}

        {error && <p className="network-error" role="alert">{error}</p>}
        <div className="network-actions">
          <button className="button primary" disabled={busy || !!problem}>{busy ? 'Installing…' : server ? `Install on ${serverName}` : 'Install'}</button>
          <a className="text-link" href="#/apps/catalog">Cancel</a>
        </div>
        <p className="section-note">The server downloads the app's images before it starts. Large images, like Local AI's, can take several minutes.</p>
      </form>
    </section>
  </>
}

function ManagedSettings({ stack, detail, locked, onSaved, ...props }: Session & {
  stack: StackSummary; detail: StackDetail; locked: boolean; onSaved: (notice: string) => void
}) {
  const template = stack.template!
  const { data: catalog, error: catalogError } = useJson(props, '/api/host/catalog', parseCatalog)
  const [values, setValues] = useState(template.settings)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [converting, setConverting] = useState(false)
  const app = catalog?.find(item => item.id === template.id)
  const server = app?.servers.find(item => item.hostname === stack.node)
  const dirty = JSON.stringify(values) !== JSON.stringify(template.settings)
  const problem = app ? settingsProblem(app, values) : null
  const outdated = template.latest !== null && template.latest > template.version

  async function put(body: unknown, done: string) {
    setBusy(true); setError(null)
    try {
      await ownerRequest(props.session, props.refreshSession, `/api/host/stacks/${encodeURIComponent(stack.name)}`, 'PUT', body)
      onSaved(done)
    } catch (failure) { setError(message(failure, 'The app could not be saved.')); setBusy(false) }
  }
  const save = (settings: Record<string, string>, done: string) => put({
    compose: null, env: null, expectedRevision: stack.revision,
    manifest: { schemaVersion: 1, placement: { node: stack.placement.node, require: [] }, template: { id: template.id, version: app!.version, settings } },
  }, done)

  return <section className="surface network-section">
    <h2>Settings</h2>
    <p className="section-note">Installed from the catalog{template.name ? ` as ${template.name}` : ''}, version {template.version}. Lucia writes its compose file from these settings.</p>
    {outdated && <div className="stack-update-note"><Icon name="down" /><span>Version {template.latest} of {template.name ?? 'this app'} is available.</span>
      <button className="button secondary" disabled={busy || locked || !app} onClick={() => void save(template.settings, `Updating to version ${template.latest}. The server applies it within about 20 seconds.`)}>Update</button></div>}
    {catalogError && <p className="network-error" role="alert">{catalogError}</p>}
    {catalog && !app && <p className="section-note">This app is no longer in Lucia's catalog. Convert it to a custom app below to keep changing it.</p>}
    {app && <form onSubmit={event => { event.preventDefault(); if (problem) setError(problem); else void save(values, 'Saved. The server applies the change within about 20 seconds.') }}>
      <SettingsFields app={app} server={server} values={values} disabled={busy || locked} onChange={(field, value) => setValues(current => ({ ...current, [field]: value }))} />
      {error && <p className="network-error" role="alert">{error}</p>}
      <div className="network-actions">
        <button className="button primary" disabled={busy || locked || !dirty || !!problem}>{busy ? 'Saving…' : 'Save and apply'}</button>
        {dirty && <button type="button" className="text-link" disabled={busy} onClick={() => { setValues(template.settings); setError(null) }}>Undo changes</button>}
      </div>
    </form>}
    <details className="stack-compose-details">
      <summary>The compose file Lucia wrote</summary>
      <pre className="stack-log-text" tabIndex={0}>{detail.compose}</pre>
    </details>
    <div className="stack-convert">
      <h3>Convert to a custom app</h3>
      <p className="section-note">Edit the compose file yourself instead. Lucia stops managing it: settings and catalog updates no longer apply. You can't convert it back.</p>
      <div className="network-actions">{converting
        ? <><button className="button secondary stack-danger" disabled={busy || locked} onClick={() => void put({
          compose: detail.compose, env: detail.env, expectedRevision: stack.revision, manifest: { schemaVersion: 1, placement: stack.placement },
        }, `${stack.name} is now a custom app. Edit its compose file below.`)}>Convert {stack.name}</button>
          <button className="text-link" disabled={busy} onClick={() => setConverting(false)}>Keep it managed</button></>
        : <button className="button secondary" disabled={busy || locked} onClick={() => setConverting(true)}>Convert to a custom app…</button>}</div>
    </div>
  </section>
}

function LocalAiSection({ stack, detail }: { stack: StackSummary; detail: StackDetail }) {
  const [copied, setCopied] = useState('')
  const key = envValue(detail.env, 'LUCIA_INFERENCE_KEY')
  const endpoint = `http://${detail.address ?? stack.node}:${stack.template?.settings.port ?? '8080'}/v1`
  async function copy(value: string, what: string) {
    try { await navigator.clipboard.writeText(value); setCopied(`${what} copied.`) } catch { setCopied(`Your browser blocked copying the ${what.toLowerCase()}.`) }
  }
  return <section className="surface network-section">
    <div className="network-overview-header"><h2>Local AI</h2><a className="button primary" href={`#/ai/models/on/${stack.node}`}>Manage models<Icon name="arrow" /></a></div>
    <p className="section-note">{(stack.template?.settings.engine ?? 'lucia') !== 'lucia'
      ? 'Download a model and choose it to serve on the Models page. Then point apps that speak the OpenAI API at this address, using the key as the bearer token and the model’s repository name as the model.'
      : 'Download and load a model first. Then point apps that speak the OpenAI API at this address, using the key as the bearer token.'}</p>
    <dl className="stack-connect">
      <div><dt>Address</dt><dd><code>{endpoint}</code> <button className="text-link" onClick={() => void copy(endpoint, 'Address')}><Icon name="copy" />Copy</button></dd></div>
      <div><dt>API key</dt><dd>{key ? <><span className="stack-hint">Kept encrypted in the app's environment.</span> <button className="text-link" onClick={() => void copy(key, 'API key')}><Icon name="copy" />Copy key</button></>
        : 'Missing. Save the settings again to generate one.'}</dd></div>
    </dl>
    <p className="network-notice" role="status">{copied}</p>
  </section>
}
