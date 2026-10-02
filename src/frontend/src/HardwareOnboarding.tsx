import { useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { Icon } from './Icon'
import type { IconName } from './Icon'
import { ownerRequest } from './managementApi'
import { cudaLines, cudaLineUnsupported, formatBytes, formatCountdown, installationBlockers, isInstallableDisk, parseOnboardingSnapshot, parseManagedNodes, requestOnboarding, secondsUntil, validateInstallApproval, visibleDiscoveries } from './onboarding'
import type { CudaLine, DevicePhase, InstallationTask, NodeAction, OnboardingAction, OnboardingDevice, OnboardingSnapshot, TaskPhase, ManagedNodeSummary, NodeRuntimeSummary } from './onboarding'
import { ServerUsage } from './ServerUsage'
import { parseStackList } from './stackManagement'
import './HardwareOnboarding.css'

interface HardwareOnboardingProps {
  session: AuthenticationSession
  refreshSession: () => Promise<void>
  view: 'devices' | 'tasks'
}

const phaseLabels: Record<DevicePhase | TaskPhase, string> = {
  Discovered: 'Needs your approval', Approved: 'Installation approved', GrantIssued: 'Installation authorized',
  Installing: 'Installing', AwaitingEnrollment: 'Waiting for enrollment', Managed: 'Managed',
  Failed: 'Needs attention', Rejected: 'Discovery rejected', Invalidated: 'Approval no longer valid',
}

function Phase({ phase }: { phase: DevicePhase | TaskPhase }) {
  const attention = phase === 'Failed' || phase === 'Invalidated'
  const quiet = phase === 'Rejected'
  return <span className={`status status-${attention || phase === 'Discovered' ? 'amber' : phase === 'Managed' ? 'green' : quiet ? 'muted' : 'accent'}`}>
    <Icon name={attention ? 'attention' : phase === 'Managed' ? 'check' : quiet ? 'close' : 'clock'} />{phaseLabels[phase]}
  </span>
}

function DateTime({ value }: { value: string }) {
  return <time dateTime={value}>{new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })}</time>
}

function useOnboarding(session: AuthenticationSession, refreshSession: () => Promise<void>) {
  const [snapshot, setSnapshot] = useState<OnboardingSnapshot | null>(null)
  const [nodes, setNodes] = useState<ManagedNodeSummary[]>([])
  const [apps, setApps] = useState<Record<string, number> | null>(null)
  const appsReadAt = useRef(0)
  const [nodeError, setNodeError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [notice, setNotice] = useState('')
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const active = useRef(false)
  const reading = useRef<AbortController | null>(null)
  const writing = useRef<AbortController | null>(null)
  const refresh = useRef<(afterMutation?: boolean) => Promise<boolean>>(async () => false)

  useEffect(() => {
    active.current = true
    let alive = true
    let poll: ReturnType<typeof setInterval> | undefined
    setSnapshot(null)
    setLoading(true)
    setBusy(false)
    const load = async (afterMutation = false): Promise<boolean> => {
      if (!alive || document.visibilityState === 'hidden' || reading.current || (writing.current && !afterMutation)) return false
      const controller = new AbortController()
      reading.current = controller
      try {
        const response = await requestOnboarding(session, refreshSession, { kind: 'snapshot' }, controller.signal)
        const next = parseOnboardingSnapshot(await response.json())
        if (!alive || controller.signal.aborted) return false
        setSnapshot(next)
        setError(null)
        if (next.devices.some(device => device.phase === 'Managed')) {
          try {
            const response = await requestOnboarding(session, refreshSession, { kind: 'managed' }, controller.signal)
            const records = parseManagedNodes(await response.json())
            if (alive && !controller.signal.aborted) { setNodes(records); setNodeError(null) }
          } catch (failure) {
            if (alive && !controller.signal.aborted) setNodeError(failure instanceof Error ? failure.message : 'Managed-node status is unavailable.')
          }
          if (Date.now() - appsReadAt.current > 30_000) {
            appsReadAt.current = Date.now()
            try {
              const stacks = parseStackList(await (await ownerRequest(session, refreshSession, '/api/host/stacks', 'GET', undefined, controller.signal)).json())
              const counts: Record<string, number> = {}
              for (const stack of stacks) if (stack.nodeId) counts[stack.nodeId] = (counts[stack.nodeId] ?? 0) + 1
              if (alive && !controller.signal.aborted) setApps(counts)
            } catch {
              // The app count is a nicety on each server card; leave it out rather than fail the page.
            }
          }
        }
        return true
      } catch (failure) {
        if (alive && !controller.signal.aborted)
          setError(failure instanceof Error ? failure.message : 'Hardware status could not be read. Check the connection and try again.')
        return false
      } finally {
        if (reading.current === controller) reading.current = null
        if (alive && !controller.signal.aborted) setLoading(false)
      }
    }
    refresh.current = load
    const visibilityChanged = () => {
      clearInterval(poll)
      if (document.visibilityState === 'hidden') {
        reading.current?.abort()
        reading.current = null
      } else {
        void load()
        poll = setInterval(() => { void load() }, 5000)
      }
    }
    visibilityChanged()
    document.addEventListener('visibilitychange', visibilityChanged)
    return () => {
      alive = false
      active.current = false
      clearInterval(poll)
      document.removeEventListener('visibilitychange', visibilityChanged)
      reading.current?.abort()
      reading.current = null
      writing.current?.abort()
      writing.current = null
    }
  }, [session, refreshSession])

  async function perform(action: OnboardingAction, success: string) {
    if (writing.current || !active.current) return
    const controller = new AbortController()
    writing.current = controller
    reading.current?.abort()
    reading.current = null
    setBusy(true)
    setActionError(null)
    setNotice('')
    try {
      await requestOnboarding(session, refreshSession, action, controller.signal)
      if (active.current && !controller.signal.aborted) {
        setNotice(success)
        setError('The change was accepted. Waiting for refreshed hardware status before allowing another change.')
      }
    } catch (failure) {
      if (active.current && !controller.signal.aborted)
        setActionError(`${failure instanceof Error ? failure.message : 'The request could not be confirmed.'} Check the refreshed status before trying again.`)
    } finally {
      if (active.current && !controller.signal.aborted) {
        await refresh.current(true)
        if (active.current && !controller.signal.aborted) setBusy(false)
      }
      if (writing.current === controller) writing.current = null
    }
  }

  return { snapshot, nodes, apps, nodeError, error, actionError, notice, loading, busy, refresh: () => refresh.current(), perform }
}

export function HardwareOnboarding(props: HardwareOnboardingProps) {
  if (!props.session.authenticated || !props.session.canAccess || !props.session.isOwner) return <div className="hardware-onboarding">
    <div className="page-intro"><h1>{props.view === 'devices' ? 'Your devices.' : 'Your tasks.'}</h1></div>
    <section className="unconnected-feature">
      <span className="icon-tile tone-muted"><Icon name="shield" /></span>
      <h2>Owner access is needed.</h2>
      <p>Only a Lucia Owner can view hardware discovery, approve an installation, or read its tasks. Ask your lab owner for access.</p>
      <a className="text-link" href="#/">Back to Home <Icon name="arrow" /></a>
    </section>
  </div>
  return <OwnerOnboarding key={props.session.username} {...props} />
}

function OwnerOnboarding({ session, refreshSession, view }: HardwareOnboardingProps) {
  const { snapshot, nodes, apps, nodeError, error, actionError, notice, loading, busy, refresh, perform } = useOnboarding(session, refreshSession)
  const [now, setNow] = useState(() => Date.now())
  const [showDismissed, setShowDismissed] = useState(false)
  const [showFinished, setShowFinished] = useState(false)
  const [showHelp, setShowHelp] = useState(false)
  const [selected, setSelected] = useState<string | null>(null)
  const devices = snapshot ? visibleDiscoveries(snapshot.devices, showDismissed) : []
  const servers = showDismissed ? [] : devices.filter(device => device.phase === 'Managed')
  const others = devices.filter(device => !servers.includes(device))
  const chosen = servers.find(device => device.id === selected)
  const title = (device: OnboardingDevice) => snapshot?.tasks.find(task => task.id === device.taskId)?.hostname
    || [device.hardware.manufacturer, device.hardware.model].filter(Boolean).join(' ') || 'Discovered device'
  const dismissedCount = snapshot ? visibleDiscoveries(snapshot.devices, true).length : 0
  const openTasks = snapshot?.tasks.filter(taskOpen) ?? []
  const finishedCount = (snapshot?.tasks.length ?? 0) - openTasks.length
  const tasks = showFinished ? snapshot?.tasks ?? [] : openTasks
  const hasExpiry = !!snapshot && (snapshot.window.isOpen || snapshot.devices.some(device => device.phase === 'Discovered'))
  useEffect(() => {
    if (!hasExpiry) return
    let clock: ReturnType<typeof setInterval> | undefined
    const visibilityChanged = () => {
      clearInterval(clock)
      if (document.visibilityState !== 'hidden') {
        setNow(Date.now())
        clock = setInterval(() => setNow(Date.now()), 1000)
      }
    }
    visibilityChanged()
    document.addEventListener('visibilitychange', visibilityChanged)
    return () => {
      clearInterval(clock)
      document.removeEventListener('visibilitychange', visibilityChanged)
    }
  }, [hasExpiry])
  const remaining = secondsUntil(snapshot?.window.expiresAt ?? null, now)
  const open = !!snapshot?.window.isOpen
  const disabled = busy || loading || !!error

  return <div className="hardware-onboarding">
    <div className="hardware-intro">
      <div className="page-intro">
        <h1>{view === 'devices' ? 'Your devices.' : 'Your tasks.'}</h1>
        <p>{view === 'devices' ? 'Bring new hardware into Lucia, one device at a time. You decide what gets installed.' : 'Follow the installations you have approved. Status comes from your host, not an estimated progress bar.'}</p>
      </div>
      {view === 'devices' && <button className="button primary" disabled={disabled || !snapshot?.readiness.canDiscover || open}
        aria-describedby="hardware-window-description" onClick={() => void perform({ kind: 'open' }, 'Hardware discovery was opened for 30 minutes. No installation was approved.')}>
        <Icon name="devices" />Add hardware
      </button>}
    </div>

    {error && <div className="hardware-message hardware-error" role="alert"><Icon name="attention" /><div>
      <strong>We cannot confirm the latest hardware status.</strong><p>{error}</p>
      {snapshot && <p>Any devices or tasks below are from the last successful check. Changes are disabled until a fresh check succeeds.</p>}
      <button className="text-link" disabled={busy} onClick={() => void refresh()}>Try again <Icon name="refresh" /></button>
    </div></div>}
    {actionError && <div className="hardware-message hardware-error" role="alert"><Icon name="attention" /><p>{actionError}</p></div>}
    {nodeError && <p className="hardware-failure" role="alert">Managed-node readings could not be refreshed. {nodeError} Previous readings may be out of date.</p>}
    <p className="hardware-notice" role="status">{notice}</p>
    {loading && !snapshot && !error && <p className="hardware-loading" role="status"><Icon name="clock" />Reading hardware status from your host…</p>}

    {view === 'devices' && <section className="surface hardware-window" aria-labelledby="hardware-window-heading">
      <h2 id="hardware-window-heading" className="visually-hidden">Hardware discovery</h2>
      <div className="hardware-window-row">
        <p id="hardware-window-description" className={`status status-${error ? 'amber' : open ? 'green' : 'muted'}`}>
          <Icon name={error ? 'attention' : open ? 'clock' : 'shield'} />
          {error ? 'Discovery status unavailable' : !snapshot ? 'Checking discovery' : open ? remaining > 0 ? `Discovery open · ${formatCountdown(remaining)} left` : 'Checking window expiry' : 'Discovery off'}
        </p>
        <p className="hardware-window-note">{open && snapshot?.window.expiresAt
          ? <>Closes <DateTime value={snapshot.window.expiresAt} />. Stopping it doesn’t cancel installations you’ve approved.</>
          : snapshot?.readiness.canInstall ? 'Installation prerequisites confirmed by the host. Each device still needs your approval.'
            : 'Add hardware opens a 30-minute window for read-only discovery.'}</p>
        {snapshot?.window.isOpen && <div className="hardware-actions">
          <button className="button secondary" disabled={disabled || !snapshot.readiness.canDiscover} onClick={() => void perform({ kind: 'open' }, 'The hardware discovery window was extended.')}>Extend 30 minutes</button>
          <button className="button secondary" disabled={disabled} onClick={() => void perform({ kind: 'close' }, 'Hardware discovery was stopped. Previously approved installations were not cancelled.')}><Icon name="stop" />Stop discovery</button>
        </div>}
        <button className="text-link hardware-help-toggle" aria-expanded={showHelp} aria-controls="hardware-help" onClick={() => setShowHelp(value => !value)}>
          How adding hardware works<Icon name={showHelp ? 'down' : 'chevron'} /></button>
      </div>
      {snapshot && !snapshot.readiness.canInstall && <div className="hardware-readiness">
        <h3>{snapshot.readiness.canDiscover ? 'Discovery is ready. Installation is not.' : 'Setup is needed before discovery.'}</h3>
        {snapshot.readiness.reasons.length > 0 ? <ul>{snapshot.readiness.reasons.map(reason => <li key={reason}>{reason}</li>)}</ul>
          : <p>The host has not confirmed its prerequisites. Check the host configuration before continuing.</p>}
        <p>No installation can start until boot, private CA, and directory enrollment are qualified by the host.</p>
      </div>}
      {showHelp && <div id="hardware-help" className="hardware-help">
        <p>Off by default. Add hardware opens a 30-minute window for read-only discovery. It does not erase disks or install anything.</p>
        <p>Stopping the window prevents new discoveries. It does not cancel installations you have already approved.</p>
        <h3>Connect your first device</h3>
        <p>Keep UniFi as your DHCP server. Complete its one-time network-boot settings first: use the Spark’s IP address as the boot server and <code>debian-installer/amd64/bootnetx64.efi</code> as the x86_64 UEFI boot file.</p>
        <p>The controller’s HTTPS hostname must resolve in local DNS. Connect the device to the configured provisioning network, then choose its network boot option.</p>
        <p>This development profile is being qualified on x86_64 UEFI with Secure Boot already off. Lucia reports that setting; it does not change firmware security settings.</p>
        <p>Compare the reported hardware with the physical device before approving a disk. Nothing is automatically reinstalled.</p>
      </div>}
    </section>}

    {snapshot && (view === 'devices'
      ? <>
        {servers.length > 0 && <section className="hardware-list-section" aria-labelledby="servers-heading">
          <div className="hardware-section-heading">
            <div><h2 id="servers-heading">Your servers</h2>
              <p className="hardware-meta">{servers.length} managed · readings every 30 seconds</p></div>
            <div className="hardware-actions">
              {others.length === 0 && dismissedCount > 0 && <button className="text-link" onClick={() => setShowDismissed(true)}>View dismissed ({dismissedCount})</button>}
              <button className="text-link" disabled={busy} onClick={() => void refresh()}><Icon name="refresh" />Refresh</button>
            </div>
          </div>
          <div className="server-grid">{servers.map(device => <ServerCard key={device.id} device={device} title={title(device)}
            node={nodes.find(node => node.nodeId === device.id)} apps={apps?.[device.id]} selected={device.id === selected}
            onManage={() => setSelected(value => value === device.id ? null : device.id)} />)}</div>
          {chosen && <ServerDetail key={chosen.id} device={chosen} title={title(chosen)} node={nodes.find(node => node.nodeId === chosen.id)}
            disabled={disabled} perform={perform} session={session} refreshSession={refreshSession} refresh={refresh}
            onClose={() => { setSelected(null); document.getElementById(`manage-${chosen.id}`)?.focus() }} />}
        </section>}
        {(showDismissed || others.length > 0 || servers.length === 0) && <section className="hardware-list-section" aria-labelledby="hardware-list-heading">
          <div className="hardware-section-heading"><h2 id="hardware-list-heading" tabIndex={-1}>{showDismissed ? 'Dismissed discoveries' : servers.length ? 'Other devices' : 'Reported devices'}</h2>
            <div className="hardware-actions">
              {(dismissedCount > 0 || showDismissed) && <button className="text-link" onClick={() => setShowDismissed(value => !value)}>
                {showDismissed ? 'Back to devices' : `View dismissed (${dismissedCount})`}</button>}
              {servers.length === 0 && <button className="text-link" disabled={busy} onClick={() => void refresh()}><Icon name="refresh" />Refresh</button>}
            </div>
          </div>
          {others.length > 0 ? <div className="surface hardware-list">{others.map(device =>
            <DeviceEntry key={device.id} device={device} title={title(device)} snapshot={snapshot} now={now} disabled={disabled} perform={perform}
              session={session} refreshSession={refreshSession} />)}</div>
            : !error && <div className="hardware-empty"><Icon name="devices" /><h3>{showDismissed ? 'No dismissed discoveries.' : dismissedCount > 0 ? 'No devices to show.' : 'No devices have been reported.'}</h3>
              <p>{showDismissed ? 'Dismissed discoveries stay rejected. Restoring a record only returns it to the device list.'
                : dismissedCount > 0 ? 'Your dismissed discoveries are kept out of this list. You can view or restore them from View dismissed.'
                  : open ? 'The window is open. A device will appear after its read-only discovery reaches the host.' : 'When discovery is ready, choose Add hardware and network-boot the device you want to add.'}</p></div>}
        </section>}
      </>
      : <section aria-labelledby="hardware-tasks-heading">
        <div className="hardware-section-heading"><h2 id="hardware-tasks-heading">{showFinished ? 'All installation tasks' : 'Installation tasks'}</h2>
          <div className="hardware-actions">
            {(finishedCount > 0 || showFinished) && <button className="text-link" onClick={() => setShowFinished(value => !value)}>
              {showFinished ? 'Hide finished' : `Show finished (${finishedCount})`}</button>}
            <button className="text-link" disabled={busy} onClick={() => void refresh()}><Icon name="refresh" />Refresh</button>
          </div>
        </div>
        {tasks.length > 0 ? <><div className="surface hardware-list">{tasks.map(task => <TaskEntry key={task.id} task={task} />)}</div><p className="section-note">These are the timestamps reported by the host. A detailed event history is not available from this connection.</p></>
          : !error && <div className="hardware-empty"><Icon name="tasks" /><h3>{finishedCount > 0 ? 'Nothing needs you right now.' : 'No installation tasks have been reported.'}</h3>
            <p>{finishedCount > 0 ? 'Finished installations are tucked away. An installation shows here while it runs, and stays if it needs your attention.'
              : 'A task appears when you approve an installation for a discovered device. Opening discovery alone does not create a task.'}</p>
            <a className="text-link" href="#/devices">View devices <Icon name="arrow" /></a></div>}
      </section>)}
    {busy && <p className="hardware-working" role="status">Saving your change and checking the host…</p>}
  </div>
}

type Perform = (action: OnboardingAction, success: string) => Promise<void>

/** Running installations, and failures that still need the owner. Managed and invalidated ones are finished. */
const taskOpen = (task: InstallationTask) => task.phase !== 'Managed' && task.phase !== 'Invalidated'

function UpdatesSection({ node, disabled, perform }: { node: ManagedNodeSummary; disabled: boolean; perform: Perform }) {
  const [confirmRestart, setConfirmRestart] = useState(false)
  const updates = node.status?.updates ?? null
  const working = updates?.state === 'Checking' || updates?.state === 'Installing'
  const locked = disabled || working || node.state !== 'Online'
  const id = `updates-${node.nodeId}`
  const act = (action: NodeAction, success: string) => perform({ kind: 'node', nodeId: node.nodeId, action }, success)
  const more = updates ? updates.count - updates.packages.length : 0
  return <section className="hardware-gpus hardware-updates" aria-labelledby={id}>
    <h4 id={id}>Updates</h4>
    {!updates ? node.agentUpdateAvailable === null && node.status
      ? <p className="hardware-meta">This agent is too old to report updates or update itself. Upgrade it once from the host; later versions update from here.</p>
      : <p className="hardware-meta">This agent doesn’t report updates yet. Update the agent to see them here.</p>
      : <>
        <p className={`status status-${updates.state === 'Failed' ? 'amber' : working ? 'accent' : updates.count ? 'amber' : updates.checkedAt ? 'green' : 'muted'}`}>
          <Icon name={updates.state === 'Failed' ? 'attention' : working ? 'clock' : updates.count ? 'attention' : updates.checkedAt ? 'check' : 'clock'} />
          {updates.state === 'Checking' ? 'Checking for updates…' : updates.state === 'Installing' ? 'Installing updates…'
            : updates.state === 'Failed' ? 'The last update didn’t finish'
              : !updates.checkedAt ? 'Not checked yet' : !updates.count ? 'Up to date'
                : `${updates.count} update${updates.count === 1 ? '' : 's'} available${updates.securityCount ? ` · ${updates.securityCount} security` : ''}`}
        </p>
        {updates.state === 'Failed' && updates.message && <p className="hardware-failure">{updates.message}</p>}
        {updates.state === 'Installing' && <p className="hardware-meta">Apps on this server may restart briefly while packages install.</p>}
        {updates.checkedAt && <p className="hardware-meta">Checked <DateTime value={updates.checkedAt} />. Lucia checks every six hours.</p>}
        {updates.packages.length > 0 && <details><summary>Show {updates.count === 1 ? 'the package' : `${updates.count} packages`}</summary>
          <ul className="hardware-detail-list">{updates.packages.map(item => <li key={item.name}>
            <strong>{item.name}</strong>{item.security && <span className="status status-amber hardware-security">Security</span>}
            <span className="hardware-version">{item.current ? `${item.current} → ${item.candidate}` : `New · ${item.candidate}`}</span>
          </li>)}</ul>
          {more > 0 && <p className="hardware-meta">And {more} more.</p>}
        </details>}
        {updates.restartRequired && <p className="status status-amber hardware-gpu-warning"><Icon name="attention" />
          <span>Restart this server to finish installing updates. Its apps stop until it’s back, usually a few minutes.</span></p>}
      </>}
    {node.agentUpdateAvailable && <p className="hardware-meta">A newer Lucia agent is ready for this server.</p>}
    {node.agentUpdateAvailable === null && node.status && updates && <p className="hardware-meta">This agent is too old to update itself. Upgrade it once from the host; later versions update from here.</p>}
    <div className="hardware-gpu-actions">
      {updates && updates.count > 0 && <button className="button primary" type="button" disabled={locked}
        onClick={() => void act('install-updates', 'Installing updates. Progress shows here as the server reports it.')}>Install updates</button>}
      {updates && <button className="button secondary" type="button" disabled={locked}
        onClick={() => void act('check-updates', 'Checking for updates. This takes a minute.')}>Check now</button>}
      {node.agentUpdateAvailable && <button className="button secondary" type="button" disabled={locked}
        onClick={() => void act('update-agent', 'Updating the agent. It reconnects in about a minute.')}>Update agent</button>}
      {updates && (confirmRestart
        ? <><button className="button secondary hardware-danger" type="button" disabled={locked}
          onClick={() => { setConfirmRestart(false); void act('restart', 'Restarting. The server reports back in a few minutes.') }}>Restart {node.hostname}</button>
          <button className="text-link" type="button" onClick={() => setConfirmRestart(false)}>Keep running</button></>
        : <button className={updates.restartRequired ? 'button secondary' : 'text-link'} type="button" disabled={locked}
          onClick={() => setConfirmRestart(true)}>Restart…</button>)}
    </div>
    {node.state !== 'Online' && <p className="hardware-meta">Actions are available while the agent is reporting.</p>}
  </section>
}

function RemoveDevice({ device, title, disabled, perform }: { device: OnboardingDevice; title: string; disabled: boolean; perform: Perform }) {
  const [confirm, setConfirm] = useState(false)
  return <div className="hardware-reject">
    <p>{confirm ? <>Lucia forgets {title}, revokes its agent’s access and drops its DNS name. The machine itself isn’t erased or shut down. Move its apps off first.</>
      : 'Retired this machine? Remove it from Lucia. Nothing on the machine changes.'}</p>
    <div className="hardware-actions">{confirm
      ? <><button className="button secondary hardware-danger" disabled={disabled}
        onClick={() => void perform({ kind: 'remove', deviceId: device.id }, `${title} was removed from Lucia.`)}>Remove {title}</button>
        <button className="text-link" onClick={() => setConfirm(false)}>Keep it</button></>
      : <button className="button secondary" disabled={disabled} onClick={() => setConfirm(true)}>Remove from Lucia…</button>}</div>
  </div>
}

function containerSummary(runtime: NodeRuntimeSummary | null): string {
  if (!runtime) return 'Not reported by this agent version'
  if (runtime.state === 'Preparing') return 'Setting up Docker…'
  if (runtime.state === 'Failed') return `Not ready. ${runtime.message ?? 'Docker setup failed.'} Lucia retries every 15 minutes.`
  return [runtime.dockerVersion && `Docker ${runtime.dockerVersion}`, runtime.composeVersion && `Compose ${runtime.composeVersion}`]
    .filter(Boolean).join(' · ') || 'Docker is running'
}

function GpuSection({ node, runtime, disabled, session, refreshSession, onSaved }: {
  node: ManagedNodeSummary; runtime: NodeRuntimeSummary; disabled: boolean
  session: AuthenticationSession; refreshSession: () => Promise<void>; onSaved: () => Promise<unknown>
}) {
  const saved = node.gpu
  const [cudaLine, setCudaLine] = useState<CudaLine | null>(saved.cudaLine)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState('')
  const pending = useRef<AbortController | null>(null)
  useEffect(() => () => pending.current?.abort(), [])
  const changed = cudaLine !== saved.cudaLine
  const locked = disabled || busy
  const reasons = Object.fromEntries(cudaLines.map(({ line }) => [line, cudaLineUnsupported(line, runtime)])) as Record<CudaLine, string | null>
  // A driver or agent problem rules out every line the same way; say it once instead of under each option.
  const shared = reasons[12] && reasons[12] === reasons[13] ? reasons[12] : null
  const id = `gpus-${node.nodeId}`
  async function save() {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller
    setBusy(true); setError(null); setNotice('')
    try {
      await ownerRequest(session, refreshSession, `/api/host/nodes/${node.nodeId}/gpu`, 'PUT',
        { cudaLine }, controller.signal)
      if (!controller.signal.aborted) { setNotice('GPU settings saved.'); await onSaved() }
    } catch (failure) {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'GPU settings could not be saved.')
    } finally {
      if (pending.current === controller) pending.current = null
      if (!controller.signal.aborted) setBusy(false)
    }
  }
  return <section className="hardware-gpus" aria-labelledby={id}>
    <h4 id={id}>GPUs</h4>
    <ul className="hardware-detail-list">{runtime.gpus.map((gpu, index) => <li key={gpu.uuid ?? index}>
      <strong>{gpu.model}{gpu.memoryBytes !== null && ` · ${formatBytes(gpu.memoryBytes)}`}</strong>
      <span>Compute {gpu.computeCapability ?? 'not reported'} · {runtime.gpuContainers ? 'available to containers' : 'not available to containers yet'}</span>
      {gpu.uuid && <span className="hardware-gpu-uuid">{gpu.uuid}</span>}
    </li>)}</ul>
    <p className="hardware-meta">NVIDIA driver {runtime.driverVersion ?? 'version not reported'}
      {runtime.cudaVersion && ` · supports up to CUDA ${runtime.cudaVersion}`}</p>
    {node.gpuWarning && <p className="status status-amber hardware-gpu-warning" role="alert"><Icon name="attention" />
      <span>CUDA {saved.cudaLine} no longer works here. {node.gpuWarning} Apps that need it won’t be placed or moved here until then.</span></p>}
    <fieldset className="hardware-choice" disabled={locked}><legend>CUDA line</legend>
      <p className="hardware-meta">Apps that need CUDA use this version on this server. Lucia doesn’t change it when the driver updates.</p>
      {shared && <p className="hardware-meta"><strong>{shared}</strong></p>}
      {cudaLines.map(({ line, note }) => {
        const reason = reasons[line]
        return <label className="hardware-checkbox" key={line}>
          <input type="radio" name={`${id}-cuda`} checked={cudaLine === line} disabled={!!reason && cudaLine !== line}
            onChange={() => setCudaLine(line)} />
          <span><strong>CUDA {line}</strong><span className="hardware-choice-note">{shared ? note : reason ?? note}</span></span>
        </label>
      })}
      <label className="hardware-checkbox">
        <input type="radio" name={`${id}-cuda`} checked={cudaLine === null} onChange={() => setCudaLine(null)} />
        <span><strong>Not set</strong><span className="hardware-choice-note">Apps that need a CUDA line won’t be placed here.</span></span>
      </label>
    </fieldset>
    <div className="hardware-choice"><h5>Local AI</h5>
      <p className="hardware-meta">Local AI is an app. Install it from the catalog to serve models on {runtime.gpus.length === 1 ? 'this GPU' : 'the GPUs you choose'}.</p>
      <a className="text-link" href={`#/apps/install/local-ai/${encodeURIComponent(node.hostname)}`}>Set up local AI on {node.hostname}<Icon name="arrow" /></a>
    </div>
    {error && <p className="hardware-failure" role="alert">{error}</p>}
    <div className="hardware-gpu-actions">
      <button className="button secondary" type="button" disabled={locked || !changed} onClick={() => void save()}>
        {busy ? 'Saving…' : 'Save GPU settings'}</button>
      <p className="hardware-meta" role="status">{notice}</p>
    </div>
  </section>
}

function NodeState({ node }: { node?: ManagedNodeSummary }) {
  const [tone, icon, text]: [string, IconName, string] = node?.state === 'Online' ? ['green', 'check', 'Online']
    : node?.state === 'Stale' ? ['amber', 'attention', 'Readings stale'] : ['muted', 'clock', 'Waiting for agent']
  return <span className={`status status-${tone}`}><Icon name={icon} />{text}</span>
}

/** One line for the card footer: the thing about updates most worth knowing. */
function updatesSummary(node?: ManagedNodeSummary): [string, IconName, string] {
  const updates = node?.status?.updates
  if (!node?.status) return ['muted', 'clock', 'Waiting for readings']
  if (!updates) return ['muted', 'clock', node.agentUpdateAvailable === null ? 'Agent too old to report updates' : 'Updates not reported']
  if (updates.state === 'Checking') return ['accent', 'clock', 'Checking for updates…']
  if (updates.state === 'Installing') return ['accent', 'clock', 'Installing updates…']
  if (updates.state === 'Failed') return ['amber', 'attention', 'The last update didn’t finish']
  if (!updates.checkedAt) return ['muted', 'clock', 'Not checked yet']
  if (updates.count) return ['amber', 'attention', `${updates.count} update${updates.count === 1 ? '' : 's'} available${updates.securityCount ? ` · ${updates.securityCount} security` : ''}`]
  if (updates.restartRequired) return ['amber', 'attention', 'Restart to finish updates']
  if (node.agentUpdateAvailable) return ['amber', 'upgrade', 'Agent update ready']
  return ['green', 'check', 'Up to date']
}

function ServerCard({ device, node, title, apps, selected, onManage }: {
  device: OnboardingDevice; node?: ManagedNodeSummary; title: string; apps: number | undefined; selected: boolean; onManage: () => void
}) {
  const hardware = device.hardware
  const runtime = node?.status?.runtime ?? null
  const [tone, icon, text] = updatesSummary(node)
  const id = `server-${device.id}`
  return <article className={`surface server-card${selected ? ' is-selected' : ''}`} aria-labelledby={id}>
    <div className="server-card-head">
      <div><h3 id={id}>{title}</h3><p className="hardware-meta">{node?.dnsName ?? node?.address ?? 'Address not reported yet'}</p></div>
      <NodeState node={node} />
    </div>
    <p className="server-hardware"><strong>{hardware.cpuModel || 'CPU not reported'}</strong> · <span>{hardware.logicalCpuCount} threads</span> · <span>{formatBytes(hardware.memoryBytes)}</span><br />
      <span>{!runtime ? 'GPU not reported' : runtime.gpus.length ? runtime.gpus.map(gpu => gpu.model).join(', ') : 'No GPU'}</span>
      {apps !== undefined && <> · <span>runs {apps} app{apps === 1 ? '' : 's'}</span></>}</p>
    {node ? <ServerUsage node={node} hasGpu={!runtime || runtime.gpus.length > 0} /> : <p className="server-note">Waiting for the agent’s first readings.</p>}
    <div className="server-card-foot">
      <span className={`status status-${tone}`}><Icon name={icon} />{text}</span>
      <button id={`manage-${device.id}`} className="text-link" aria-expanded={selected} aria-controls={selected ? 'server-detail' : undefined} onClick={onManage}>
        {selected ? 'Hide details' : 'Manage'}<Icon name={selected ? 'down' : 'chevron'} /></button>
    </div>
  </article>
}

function formatUptime(seconds: number): string {
  const days = Math.floor(seconds / 86400), hours = Math.floor(seconds / 3600) % 24, minutes = Math.floor(seconds / 60) % 60
  return days ? `${days} day${days === 1 ? '' : 's'}, ${hours} hour${hours === 1 ? '' : 's'}` : `${hours} hour${hours === 1 ? '' : 's'}, ${minutes} minute${minutes === 1 ? '' : 's'}`
}

/** Everything about one server: its readings, updates, GPUs, the hardware it reported at discovery, and removal. */
function ServerDetail({ device, node, title, disabled, perform, session, refreshSession, refresh, onClose }: {
  device: OnboardingDevice; node?: ManagedNodeSummary; title: string; disabled: boolean; perform: Perform
  session: AuthenticationSession; refreshSession: () => Promise<void>; refresh: () => Promise<unknown>; onClose: () => void
}) {
  const heading = useRef<HTMLHeadingElement>(null)
  useEffect(() => {
    heading.current?.focus({ preventScroll: true })
    heading.current?.closest('section')?.scrollIntoView({ block: 'nearest', behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' })
  }, [])
  const hardware = device.hardware
  const status = node?.status
  return <section id="server-detail" className="surface server-detail" aria-labelledby="server-detail-heading">
    <div className="server-detail-head">
      <div><h2 id="server-detail-heading" ref={heading} tabIndex={-1}>{title}</h2>
        <p className="hardware-meta">{node?.state === 'Online' ? 'Agent is reporting.' : node?.state === 'Stale'
          ? 'Agent readings are stale. Check the server and its connection.' : 'Waiting for current agent readings.'}
          {status && ` Up ${formatUptime(status.uptimeSeconds)}.`}</p>
        {device.statusMessage && <p className="hardware-meta">{device.statusMessage}</p>}
      </div>
      <button className="text-link" onClick={onClose}><Icon name="close" />Close</button>
    </div>
    <div className="server-detail-grid">
      <section aria-labelledby="server-detail-machine">
        <h4 id="server-detail-machine">Machine</h4>
        <dl className="fact-list">
          <div><dt>Address</dt><dd>{node?.dnsName ? <>{node.dnsName} · {node.address}</> : node?.address ?? 'Not reported'}</dd></div>
          {status && <>
            <div><dt>Operating system</dt><dd>{status.osVersion}</dd></div>
            <div><dt>Uptime</dt><dd>{formatUptime(status.uptimeSeconds)}</dd></div>
            <div><dt>Available memory</dt><dd>{formatBytes(status.memoryAvailableBytes)} / {formatBytes(status.memoryTotalBytes)}</dd></div>
            <div><dt>Available root storage</dt><dd>{status.storageAvailableBytes === null || status.storageTotalBytes === null ? 'Not reported'
              : `${formatBytes(status.storageAvailableBytes)} / ${formatBytes(status.storageTotalBytes)}`}</dd></div>
            <div><dt>Load average</dt><dd>{status.loadAverage?.toFixed(2) ?? 'Not reported'}</dd></div>
            <div><dt>Containers</dt><dd>{containerSummary(status.runtime)}</dd></div>
          </>}
          <div><dt>Architecture</dt><dd>{hardware.architecture}</dd></div>
          <div><dt>CPU</dt><dd>{hardware.cpuModel || 'Not reported'} · {hardware.logicalCpuCount} logical CPUs</dd></div>
          <div><dt>Installed memory</dt><dd>{formatBytes(hardware.memoryBytes)}</dd></div>
          <div><dt>Secure Boot</dt><dd>{hardware.secureBoot === null ? 'Unknown — not reported' : hardware.secureBoot ? 'On' : 'Off'}</dd></div>
          {node && <div><dt>Node certificate expires</dt><dd><DateTime value={node.certificateExpiresAt} /></dd></div>}
          <div><dt>Last seen</dt><dd><DateTime value={device.lastSeenAt} /></dd></div>
        </dl>
      </section>
      {node && <UpdatesSection node={node} disabled={disabled} perform={perform} />}
      {node && status?.runtime?.gpus.length ? <GpuSection node={node} runtime={status.runtime}
        disabled={disabled} session={session} refreshSession={refreshSession} onSaved={refresh} /> : null}
    </div>
    <HardwareDetails device={device} />
    <RemoveDevice device={device} title={title} disabled={disabled} perform={perform} />
  </section>
}

function HardwareDetails({ device }: { device: OnboardingDevice }) {
  const hardware = device.hardware
  const managed = device.phase === 'Managed'
  return <details className="hardware-details"><summary>Hardware and network details</summary>
    {managed && <p className="hardware-meta">Recorded when the device was discovered. Its current address is with the agent readings above.</p>}
    <dl className="fact-list">
      <div><dt>Device ID</dt><dd>{device.id}</dd></div>
      <div><dt>Serial number</dt><dd>{hardware.serialNumber || 'Not reported'}</dd></div>
      <div><dt>Hardware UUID</dt><dd>{hardware.hardwareUuid || 'Not reported'}</dd></div>
      <div><dt>Boot mode</dt><dd>{hardware.bootMode}</dd></div>
      <div><dt>Discovered</dt><dd><DateTime value={device.discoveredAt} /></dd></div>
      <div><dt>Last heartbeat</dt><dd>{device.lastHeartbeatAt ? <DateTime value={device.lastHeartbeatAt} /> : 'Not reported'} · {device.heartbeatFreshness.toLowerCase()}</dd></div>
    </dl>
    <h4>{managed ? 'Network interfaces at discovery' : 'Network interfaces'}</h4>
    <ul className="hardware-detail-list">{hardware.interfaces.map(nic => <li key={nic.name}><strong>{nic.name}</strong> · {nic.macAddress ?? 'MAC address not reported'}<span>{nic.addresses.join(', ') || 'No address reported'}</span></li>)}</ul>
    <h4>Reported disks</h4>
    {hardware.disks.length ? <ul className="hardware-detail-list">{hardware.disks.map(disk => <li key={disk.path}>
      <strong>{disk.model || 'Model not reported'} · {formatBytes(disk.sizeBytes)}</strong>
      <span>Serial: {disk.serial || 'Not reported'} · {disk.path}</span>{disk.id && <span>{disk.id}</span>}
      <span>{disk.id === null ? 'Cannot safely identify this disk' : disk.isReadOnly ? 'Read-only — cannot install' : disk.isRemovable ? 'Removable — cannot install' : 'Writable, nonremovable disk'}</span>
    </li>)}</ul> : <p>No disks were reported.</p>}
  </details>
}

function DeviceEntry({ device, title, snapshot, now, disabled, perform, session, refreshSession }: {
  device: OnboardingDevice; title: string; snapshot: OnboardingSnapshot; now: number; disabled: boolean; perform: Perform
  session: AuthenticationSession; refreshSession: () => Promise<void>
}) {
  const hardware = device.hardware
  const task = snapshot.tasks.find(task => task.id === device.taskId)
  return <article className="hardware-entry">
    <div className="hardware-entry-heading"><div><h3>{title}</h3>
      {task && <p className="hardware-meta">{[hardware.manufacturer, hardware.model].filter(Boolean).join(' ') || 'Model not reported'}</p>}
    </div><Phase phase={device.phase} /></div>
    {device.phase === 'Discovered' && <p className="hardware-meta">Verification code: <strong>{device.verificationCode}</strong>. Match this code on the physical device’s console before approving.</p>}
    <p className="hardware-meta">Last seen <DateTime value={device.lastSeenAt} /></p>
    {device.statusMessage && <p className={device.phase === 'Failed' ? 'hardware-failure' : 'hardware-meta'}>{device.statusMessage}</p>}
    <dl className="hardware-specs">
      <div><dt>Architecture</dt><dd>{hardware.architecture}</dd></div>
      <div><dt>CPU</dt><dd>{hardware.cpuModel || 'Not reported'} · {hardware.logicalCpuCount} logical CPUs</dd></div>
      <div><dt>Memory</dt><dd>{formatBytes(hardware.memoryBytes)}</dd></div>
      <div><dt>Secure Boot</dt><dd>{hardware.secureBoot === null ? 'Unknown — not reported' : hardware.secureBoot ? 'On' : 'Off'}</dd></div>
    </dl>
    <HardwareDetails device={device} />
    {device.phase === 'Discovered' && <InstallForm key={`${device.id}:${device.inventoryRevision}`} device={device} snapshot={snapshot}
      now={now} disabled={disabled} perform={perform} session={session} refreshSession={refreshSession} />}
    {device.phase === 'Rejected' && <div className="hardware-reject">
      <p>{device.dismissedAt ? <>Dismissed <DateTime value={device.dismissedAt} />. Restoring this record does not undo its rejection.</>
        : 'Finished with this discovery? Dismiss it to hide it from your device list. Its rejection history is kept.'}</p>
      <button className="button secondary" disabled={disabled} onClick={async () => {
        await perform({ kind: device.dismissedAt ? 'restore' : 'dismiss', deviceId: device.id },
          device.dismissedAt ? 'Discovery restored to the list. It remains rejected.' : 'Discovery dismissed. You can find it under View dismissed.')
        document.getElementById('hardware-list-heading')?.focus({ preventScroll: true })
      }}>{device.dismissedAt ? 'Restore to list' : 'Dismiss discovery'}</button>
    </div>}
    {(device.phase === 'Managed' || device.phase === 'Failed')
      && <RemoveDevice key={device.id} device={device} title={title} disabled={disabled} perform={perform} />}
    {task && taskOpen(task) && <a className="text-link" href="#/tasks">Follow installation <Icon name="arrow" /></a>}
  </article>
}

function InstallForm({ device, snapshot, now, disabled, perform, session, refreshSession }: {
  device: OnboardingDevice; snapshot: OnboardingSnapshot; now: number; disabled: boolean; perform: Perform
  session: AuthenticationSession; refreshSession: () => Promise<void>
}) {
  const [hostname, setHostname] = useState('')
  const [diskId, setDiskId] = useState('')
  const [confirmation, setConfirmation] = useState('')
  const [recoveryPublicKey, setRecoveryPublicKey] = useState('')
  const [acknowledged, setAcknowledged] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const errorSummary = useRef<HTMLParagraphElement>(null)
  const blockers = installationBlockers(snapshot, device, now)
  const disks = device.hardware.disks.filter(isInstallableDisk)
  const selectedDisk = disks.find(disk => disk.id === diskId)
  const dataDisks = selectedDisk ? device.hardware.disks.filter(disk => !disk.isRemovable && !disk.isReadOnly && disk.path !== selectedDisk.path) : []
  const prefix = `hardware-${device.id}`
  return <details className="hardware-approval"><summary>Review installation or reject this device</summary>
    <p>Match the verification code above with the physical device’s console, then compare its model, serial number, and disks. An installation replaces all data on the one disk you choose.</p>
    {blockers.length > 0 && <div className="hardware-blockers"><strong>Installation cannot be approved yet.</strong><ul>{blockers.map(reason => <li key={reason}>{reason}</li>)}</ul></div>}
    <form onSubmit={event => {
      event.preventDefault()
      setError(null)
      try {
        const approval = validateInstallApproval(snapshot, device, { hostname, diskId, confirmation, recoveryPublicKey }, acknowledged)
        void perform({ kind: 'approve', deviceId: device.id, approval }, `Installation was approved for ${hostname}. Follow its real status in Tasks.`)
      } catch (failure) {
        setError(failure instanceof Error ? failure.message : 'Check the installation details.')
        requestAnimationFrame(() => errorSummary.current?.focus())
      }
    }}>
      <fieldset disabled={disabled || blockers.length > 0}>
        <legend>Approve this device only</legend>
        <div className="hardware-fields">
          <label htmlFor={`${prefix}-hostname`}>Device hostname
            <input id={`${prefix}-hostname`} value={hostname} onChange={event => setHostname(event.target.value)} autoComplete="off" autoCapitalize="none" spellCheck={false} maxLength={63} required pattern="[a-z]([a-z0-9-]{0,61}[a-z0-9])?" aria-describedby={`${prefix}-hostname-help`} />
            <span id={`${prefix}-hostname-help`}>Start with a lowercase letter; numbers and hyphens are allowed. For example, dev-server.</span>
          </label>
          <label htmlFor={`${prefix}-disk`}>Install Debian on
            <select id={`${prefix}-disk`} value={diskId} required onChange={event => { setDiskId(event.target.value); setAcknowledged(false); setConfirmation('') }}>
              <option value="">Choose a disk — none is selected</option>
              {disks.map(disk => <option key={disk.id} value={disk.id}>{disk.model || 'Unknown model'} · {formatBytes(disk.sizeBytes)} · {disk.serial || disk.path}</option>)}
            </select>
          </label>
        </div>
        <RecoveryKeyInput session={session} refreshSession={refreshSession} value={recoveryPublicKey}
          disabled={disabled || blockers.length > 0} prefix={prefix}
          onChange={value => { setRecoveryPublicKey(value); setAcknowledged(false); setConfirmation('') }} />
        {selectedDisk && <p className="hardware-selected-disk"><strong>Selected disk:</strong> {selectedDisk.model || 'Model not reported'} · {formatBytes(selectedDisk.sizeBytes)} · Serial: {selectedDisk.serial || 'Not reported'}<span>{selectedDisk.id}</span></p>}
        {selectedDisk && dataDisks.length > 0 && <p className="hardware-selected-disk"><strong>Data volume at /srv/data:</strong> {dataDisks.map(disk => `${disk.model || disk.path} · ${formatBytes(disk.sizeBytes)}`).join(', ')}</p>}
        <div className="hardware-erasure" id={`${prefix}-warning`}><Icon name="attention" /><p><strong>All data on every disk in this machine will be erased</strong>{dataDisks.length > 0 && <>, including the {dataDisks.length === 1 ? 'other disk' : `${dataDisks.length} other disks`}, which become one data volume</>}. This cannot be undone. Back up anything you want to keep before approving.</p></div>
        <label className="hardware-checkbox"><input type="checkbox" checked={acknowledged} required onChange={event => setAcknowledged(event.target.checked)} />I have checked this physical device, matched its verification code, and approve erasing all of its disks.</label>
        <label className="hardware-confirmation" htmlFor={`${prefix}-confirmation`}>Type ERASE to confirm
          <input id={`${prefix}-confirmation`} value={confirmation} onChange={event => setConfirmation(event.target.value)} autoComplete="off" autoCapitalize="characters" spellCheck={false} required pattern="ERASE" aria-describedby={`${prefix}-warning`} />
        </label>
        <button className="button primary" type="submit" disabled={!selectedDisk || !acknowledged || confirmation !== 'ERASE' || !hostname || !recoveryPublicKey}>{dataDisks.length > 0 ? 'Erase all disks and install' : 'Erase disk and install'}</button>
      </fieldset>
      {error && <p className="hardware-failure" role="alert" tabIndex={-1} ref={errorSummary}>{error}</p>}
    </form>
    <div className="hardware-reject"><p>Not the device you meant to add? Rejecting this discovery does not erase its disks.</p><button className="button secondary" disabled={disabled} onClick={() => void perform({ kind: 'reject', deviceId: device.id }, 'This discovery was rejected. No installation was approved.')}>Reject discovery</button></div>
  </details>
}

function RecoveryKeyInput({ session, refreshSession, value, onChange, disabled, prefix }: {
  session: AuthenticationSession; refreshSession: () => Promise<void>; value: string; onChange: (value: string) => void; disabled: boolean; prefix: string
}) {
  const [username, setUsername] = useState('')
  const [keys, setKeys] = useState<{ publicKey: string; algorithm: string; fingerprint: string }[]>([])
  const [notice, setNotice] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const pending = useRef<AbortController | null>(null)
  useEffect(() => () => pending.current?.abort(), [])
  async function loadKeys() {
    if (disabled || pending.current) return
    if (!/^[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,37}[a-zA-Z0-9])?$/.test(username) || username.includes('--')) {
      setError('Enter a GitHub username, not a URL.')
      return
    }
    const controller = new AbortController()
    pending.current = controller
    setBusy(true); setError(null); setNotice(''); setKeys([])
    try {
      const response = await ownerRequest(session, refreshSession, `/api/host/ssh-keys/github/${encodeURIComponent(username)}`, 'GET', undefined, controller.signal)
      const data: unknown = await response.json()
      if (!data || typeof data !== 'object' || !('keys' in data) || !Array.isArray(data.keys)
        || !('unsupportedCount' in data) || typeof data.unsupportedCount !== 'number' || data.keys.length > 100)
        throw new Error('GitHub key information was incomplete. Paste your public key instead.')
      const loaded = data.keys.map((key: unknown) => {
        if (!key || typeof key !== 'object' || !('publicKey' in key) || typeof key.publicKey !== 'string'
          || key.publicKey.length > 16384 || !('fingerprint' in key) || typeof key.fingerprint !== 'string'
          || !/^SHA256:[A-Za-z0-9+/]{43}$/.test(key.fingerprint)
          || !('algorithm' in key) || typeof key.algorithm !== 'string')
          throw new Error('A returned public key was invalid. Paste your public key instead.')
        return { publicKey: key.publicKey, fingerprint: key.fingerprint, algorithm: key.algorithm }
      })
      if (!controller.signal.aborted) {
        setKeys(loaded)
        setNotice(loaded.length ? `Choose one key after checking its fingerprint.${data.unsupportedCount ? ` ${data.unsupportedCount} unsupported keys were skipped.` : ''}`
          : 'No supported public SSH keys were found. Add a key to your GitHub account or paste one below.')
      }
    } catch (failure) {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'GitHub keys could not be read.')
    } finally {
      if (pending.current === controller) pending.current = null
      if (!controller.signal.aborted) setBusy(false)
    }
  }
  return <div className="hardware-recovery">
    <h4>Recovery administrator</h4>
    <p className="hardware-meta">The local <strong>lucia-recovery</strong> account uses this SSH key for administrator access if LDAP is unavailable. Its password is locked.</p>
    <div className="hardware-fields"><label htmlFor={`${prefix}-github`}>GitHub username
      <input id={`${prefix}-github`} value={username} autoComplete="off" spellCheck={false} maxLength={39} disabled={busy || disabled}
        onChange={event => { setUsername(event.target.value.trim()); setKeys([]); setNotice('') }}
        onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); void loadKeys() } }} />
    </label><button className="button secondary" type="button" disabled={busy || disabled || !username} onClick={() => void loadKeys()}>
      {busy ? 'Reading public keys…' : 'Import from GitHub'}</button></div>
    <p className="hardware-meta">No GitHub login is needed. Check the account and fingerprint: public-key lookup does not prove account ownership. The selected key is saved with your approval; later GitHub changes are not synced.</p>
    <p className="hardware-meta" role="status">{notice}</p>
    {error && <p className="hardware-failure" role="alert">{error}</p>}
    {keys.map(key => <label className="hardware-checkbox" key={key.fingerprint}>
      <input type="radio" name={`${prefix}-imported-key`} checked={value === key.publicKey} disabled={disabled} onChange={() => onChange(key.publicKey)} />
      <span>{key.algorithm}<br /><span className="hardware-key-fingerprint">{key.fingerprint}</span></span>
    </label>)}
    <label className="hardware-recovery-key" htmlFor={`${prefix}-public-key`}>SSH public key
      <textarea id={`${prefix}-public-key`} value={value} onChange={event => onChange(event.target.value.trim())} rows={3}
        autoComplete="off" spellCheck={false} maxLength={16384} required disabled={disabled} placeholder="ssh-ed25519 AAAA…" />
    </label>
    <p className="hardware-meta">Use one Ed25519, RSA 2048-bit or stronger, or ECDSA P-256 public key. Never paste a private key.</p>
  </div>
}

function TaskEntry({ task }: { task: InstallationTask }) {
  return <article className="hardware-entry">
    <div className="hardware-entry-heading"><h3>{task.hostname}</h3><Phase phase={task.phase} /></div>
    {task.statusMessage && <p className={task.phase === 'Failed' || task.phase === 'Invalidated' ? 'hardware-failure' : 'hardware-meta'}>{task.statusMessage}</p>}
    <ol className="hardware-timeline">
      <li><strong>Installation approved</strong><DateTime value={task.approvedAt} /></li>
      {task.grantIssuedAt && <li><strong>Installation grant issued</strong><DateTime value={task.grantIssuedAt} /></li>}
      <li><strong>Latest update · {phaseLabels[task.phase]}</strong><DateTime value={task.updatedAt} /></li>
    </ol>
    <details><summary>Approval details</summary><dl className="fact-list">
      <div><dt>Task ID</dt><dd>{task.id}</dd></div><div><dt>Device ID</dt><dd>{task.deviceId}</dd></div>
      <div><dt>Approved by</dt><dd>{task.approvedBy}</dd></div><div><dt>Selected disk</dt><dd>{task.diskId}</dd></div>
      <div><dt>Approval authority expires</dt><dd><DateTime value={task.authorityExpiresAt} /></dd></div>
    </dl><p>Closing hardware discovery does not cancel this approved installation. Reinstallation is not available here.</p></details>
  </article>
}
