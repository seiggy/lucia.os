import { useCallback, useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { parseManagedNodes } from './onboarding'
import type { ManagedNodeSummary } from './onboarding'
import { Icon } from './Icon'
import type { IconName } from './Icon'
import {
  composeExample, containerState, describeUnmet, logLineChoices, moveState, parseInventory, parseStackDetail, parseStackList, parseStackSummary, portRows,
  requirementForm, requirementList, stackNamePattern, stackState, unmetRequirement, validateStackDraft,
} from './stackManagement'
import type { NodeContainer, NodeInventory, RequirementForm, StackPlacement, StackSummary, StackTone } from './stackManagement'
import './NetworkSettings.css'
import './Stacks.css'

type Session = { session: AuthenticationSession; refreshSession: () => Promise<void> }
type View = 'list' | 'containers' | 'new' | 'app'

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

export function Stacks({ session, refreshSession, view, name }: Session & { view: View; name?: string }) {
  if (!session.isOwner) return <div className="page-intro"><h1>Owner access is needed.</h1><p>Only lab owners can run apps on managed servers.</p></div>
  const props = { session, refreshSession }
  return <>
    {view !== 'app' && <>
      <div className="page-intro"><h1>{view === 'new' ? 'Add an app' : view === 'containers' ? 'Containers' : 'Apps'}</h1><p>{view === 'new'
        ? 'Paste a Docker Compose file and say where it can run. Lucia keeps it running there and reports what happens.'
        : view === 'containers' ? 'Everything running on a server, including containers Lucia didn\'t start, and the ports in use.'
          : 'Container apps Lucia runs on your servers. Each one is a Docker Compose file placed on one server.'}</p></div>
      {view !== 'new' && <nav className="model-library-navigation" aria-label="Apps navigation">
        <a href="#/apps" aria-current={view === 'list' ? 'page' : undefined}>Your apps</a>
        <a href="#/apps/containers" aria-current={view === 'containers' ? 'page' : undefined}><Icon name="server" />Containers & ports</a>
      </nav>}
    </>}
    {view === 'list' && <AppList {...props} />}
    {view === 'containers' && <ContainersView {...props} />}
    {view === 'new' && <AppEditor {...props} />}
    {view === 'app' && name && <AppDetail {...props} name={name} />}
  </>
}

function StateLabel({ label, tone }: { label: string; tone: StackTone }) {
  return <span className={`stack-state stack-tone-${tone}`}><Icon name={toneIcon[tone]} />{label}</span>
}

function AppList(props: Session) {
  const { data: stacks, error } = useJson(props, '/api/host/stacks', parseStackList, 10000)
  return <section className="surface network-section">
    <div className="network-overview-header"><h2>Your apps</h2><a className="button primary" href="#/apps/new">Add an app<Icon name="arrow" /></a></div>
    {error && <p className="network-error" role="alert">{error}</p>}
    {!stacks && !error && <p className="section-note" role="status">Reading your apps…</p>}
    {stacks?.length === 0 && <div className="stack-empty">
      <p>No apps yet. Add one by pasting its Docker Compose file. Most self-hosted apps publish one in their README.</p>
      <p className="section-note">Lucia stores named volumes under <code>/srv/lucia/stacks/&lt;app&gt;/volumes</code> on the server, so your data is easy to find and back up.</p>
    </div>}
    {stacks && stacks.length > 0 && <table className="network-table stack-table">
      <caption className="network-table-caption">Apps and their state</caption>
      <thead><tr><th scope="col">App</th><th scope="col">State</th><th scope="col">Server</th><th scope="col"><span className="network-table-caption">Open</span></th></tr></thead>
      <tbody>{stacks.map(stack => {
        const state = stackState(stack)
        return <tr key={stack.name}>
          <th scope="row"><a className="stack-name" href={`#/apps/${stack.name}`}>{stack.name}</a></th>
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
      spellCheck={false} autoComplete="off" required disabled={busy} placeholder={composeExample} /></label>
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

    <AppEditor key={detail.data.stack.revision} {...props} locked={moving}
      existing={{ name, node: stack.node, placement: stack.placement, compose: detail.data.compose, env: detail.data.env, revision: detail.data.stack.revision }}
      onSaved={() => { detail.reload(); live.reload(); setNotice('Saved. The server applies the change within about 20 seconds.') }} />

    <MoveSection {...props} stack={stack} onChanged={done => { setNotice(done); live.reload(); detail.reload() }} />

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
