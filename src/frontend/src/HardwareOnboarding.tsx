import { useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { Icon } from './Icon'
import { ownerRequest } from './managementApi'
import { cudaLines, cudaLineUnsupported, formatBytes, formatCountdown, installationBlockers, isInstallableDisk, parseOnboardingSnapshot, parseManagedNodes, requestOnboarding, secondsUntil, validateInstallApproval, visibleDiscoveries } from './onboarding'
import type { CudaLine, DevicePhase, InstallationTask, OnboardingAction, OnboardingDevice, OnboardingSnapshot, TaskPhase, ManagedNodeSummary, NodeRuntimeSummary } from './onboarding'
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

  return { snapshot, nodes, nodeError, error, actionError, notice, loading, busy, refresh: () => refresh.current(), perform }
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
  const { snapshot, nodes, nodeError, error, actionError, notice, loading, busy, refresh, perform } = useOnboarding(session, refreshSession)
  const [now, setNow] = useState(() => Date.now())
  const [showDismissed, setShowDismissed] = useState(false)
  const devices = snapshot ? visibleDiscoveries(snapshot.devices, showDismissed) : []
  const dismissedCount = snapshot ? visibleDiscoveries(snapshot.devices, true).length : 0
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
      <div className="hardware-window-row">
        <div>
          <h2 id="hardware-window-heading">Hardware discovery</h2>
          <p id="hardware-window-description" className={`status status-${error ? 'amber' : open ? 'green' : 'muted'}`}>
            <Icon name={error ? 'attention' : open ? 'clock' : 'shield'} />
            {error ? 'Current status unavailable' : !snapshot ? 'Checking status' : open ? remaining > 0 ? `Open · ${formatCountdown(remaining)} remaining` : 'Checking window expiry' : 'Off'}
          </p>
          {open && snapshot?.window.expiresAt && <p className="hardware-meta">Closes <DateTime value={snapshot.window.expiresAt} /></p>}
        </div>
        {snapshot?.window.isOpen && <div className="hardware-actions">
          <button className="button secondary" disabled={disabled || !snapshot.readiness.canDiscover} onClick={() => void perform({ kind: 'open' }, 'The hardware discovery window was extended.')}>Extend 30 minutes</button>
          <button className="button secondary" disabled={disabled} onClick={() => void perform({ kind: 'close' }, 'Hardware discovery was stopped. Previously approved installations were not cancelled.')}><Icon name="stop" />Stop discovery</button>
        </div>}
      </div>
      <p>Off by default. Add hardware opens a 30-minute window for read-only discovery. It does not erase disks or install anything.</p>
      <p className="hardware-meta">Stopping the window prevents new discoveries. It does not cancel installations you have already approved.</p>
      {snapshot && !snapshot.readiness.canInstall && <div className="hardware-readiness">
        <h3>{snapshot.readiness.canDiscover ? 'Discovery is ready. Installation is not.' : 'Setup is needed before discovery.'}</h3>
        {snapshot.readiness.reasons.length > 0 ? <ul>{snapshot.readiness.reasons.map(reason => <li key={reason}>{reason}</li>)}</ul>
          : <p>The host has not confirmed its prerequisites. Check the host configuration before continuing.</p>}
        <p>No installation can start until boot, private CA, and directory enrollment are qualified by the host.</p>
      </div>}
      {snapshot?.readiness.canInstall && <p className="status status-green"><Icon name="check" />Installation prerequisites confirmed by the host. Each device still needs your approval.</p>}
      <details><summary>Connect your first device</summary>
        <p>Keep UniFi as your DHCP server. Complete its one-time network-boot settings first: use the Spark’s IP address as the boot server and <code>debian-installer/amd64/bootnetx64.efi</code> as the x86_64 UEFI boot file.</p>
        <p>The controller’s HTTPS hostname must resolve in local DNS. Connect the device to the configured provisioning network, then choose its network boot option.</p>
        <p>This development profile is being qualified on x86_64 UEFI with Secure Boot already off. Lucia reports that setting; it does not change firmware security settings.</p>
        <p>Compare the reported hardware with the physical device before approving a disk. Nothing is automatically reinstalled.</p>
      </details>
    </section>}

    {snapshot && (view === 'devices'
      ? <section className="hardware-list-section" aria-labelledby="hardware-list-heading">
        <div className="hardware-section-heading"><h2 id="hardware-list-heading" tabIndex={-1}>{showDismissed ? 'Dismissed discoveries' : 'Reported devices'}</h2>
          <div className="hardware-actions">
            {(dismissedCount > 0 || showDismissed) && <button className="text-link" onClick={() => setShowDismissed(value => !value)}>
              {showDismissed ? 'Back to devices' : `View dismissed (${dismissedCount})`}</button>}
            <button className="text-link" disabled={busy} onClick={() => void refresh()}><Icon name="refresh" />Refresh</button>
          </div>
        </div>
        {devices.length > 0 ? <div className="surface hardware-list">{devices.map(device =>
          <DeviceEntry key={device.id} device={device} snapshot={snapshot} now={now} disabled={disabled} perform={perform}
            session={session} refreshSession={refreshSession} managed={nodes.find(node => node.nodeId === device.id)} refresh={refresh} />)}</div>
          : !error && <div className="hardware-empty"><Icon name="devices" /><h3>{showDismissed ? 'No dismissed discoveries.' : dismissedCount > 0 ? 'No devices to show.' : 'No devices have been reported.'}</h3>
            <p>{showDismissed ? 'Dismissed discoveries stay rejected. Restoring a record only returns it to the device list.'
              : dismissedCount > 0 ? 'Your dismissed discoveries are kept out of this list. You can view or restore them from View dismissed.'
                : open ? 'The window is open. A device will appear after its read-only discovery reaches the host.' : 'When discovery is ready, choose Add hardware and network-boot the device you want to add.'}</p></div>}
      </section>
      : <section aria-labelledby="hardware-tasks-heading">
        <div className="hardware-section-heading"><h2 id="hardware-tasks-heading">Installation tasks</h2><button className="text-link" disabled={busy} onClick={() => void refresh()}><Icon name="refresh" />Refresh</button></div>
        {snapshot.tasks.length > 0 ? <><div className="surface hardware-list">{snapshot.tasks.map(task => <TaskEntry key={task.id} task={task} />)}</div><p className="section-note">These are the timestamps reported by the host. A detailed event history is not available from this connection.</p></>
          : !error && <div className="hardware-empty"><Icon name="tasks" /><h3>No installation tasks have been reported.</h3><p>A task appears when you approve an installation for a discovered device. Opening discovery alone does not create a task.</p><a className="text-link" href="#/devices">View devices <Icon name="arrow" /></a></div>}
      </section>)}
    {busy && <p className="hardware-working" role="status">Saving your change and checking the host…</p>}
  </div>
}

type Perform = (action: OnboardingAction, success: string) => Promise<void>

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

function DeviceEntry({ device, snapshot, now, disabled, perform, session, refreshSession, managed, refresh }: {
  device: OnboardingDevice; snapshot: OnboardingSnapshot; now: number; disabled: boolean; perform: Perform
  session: AuthenticationSession; refreshSession: () => Promise<void>
  managed?: ManagedNodeSummary; refresh: () => Promise<unknown>
}) {
  const hardware = device.hardware
  const task = snapshot.tasks.find(task => task.id === device.taskId)
  const title = task?.hostname || [hardware.manufacturer, hardware.model].filter(Boolean).join(' ') || 'Discovered device'
  return <article className="hardware-entry">
    <div className="hardware-entry-heading"><div><h3>{title}</h3>
      {task && <p className="hardware-meta">{[hardware.manufacturer, hardware.model].filter(Boolean).join(' ') || 'Model not reported'}</p>}
    </div><Phase phase={device.phase} /></div>
    {device.phase === 'Discovered' && <p className="hardware-meta">Verification code: <strong>{device.verificationCode}</strong>. Match this code on the physical device’s console before approving.</p>}
    <p className="hardware-meta">Last seen <DateTime value={device.lastSeenAt} /></p>
    {device.phase === 'Managed' && <p className="hardware-meta">{managed?.state === 'Online' ? 'Agent is reporting.' : managed?.state === 'Stale'
      ? 'Agent readings are stale. Check the server and its connection.' : 'Waiting for current agent readings.'}</p>}
    {device.statusMessage && <p className={device.phase === 'Failed' ? 'hardware-failure' : 'hardware-meta'}>{device.statusMessage}</p>}
    <dl className="hardware-specs">
      <div><dt>Architecture</dt><dd>{hardware.architecture}</dd></div>
      <div><dt>CPU</dt><dd>{hardware.cpuModel || 'Not reported'} · {hardware.logicalCpuCount} logical CPUs</dd></div>
      <div><dt>Memory</dt><dd>{formatBytes(hardware.memoryBytes)}</dd></div>
      <div><dt>Secure Boot</dt><dd>{hardware.secureBoot === null ? 'Unknown — not reported' : hardware.secureBoot ? 'On' : 'Off'}</dd></div>
    </dl>
    {device.phase === 'Managed' && managed?.status && <dl className="hardware-specs">
      <div><dt>Operating system</dt><dd>{managed.status.osVersion}</dd></div>
      <div><dt>Uptime</dt><dd>{Math.floor(managed.status.uptimeSeconds / 3600)} hours, {Math.floor(managed.status.uptimeSeconds / 60) % 60} minutes</dd></div>
      <div><dt>Available memory</dt><dd>{formatBytes(managed.status.memoryAvailableBytes)} / {formatBytes(managed.status.memoryTotalBytes)}</dd></div>
      <div><dt>Available root storage</dt><dd>{managed.status.storageAvailableBytes === null || managed.status.storageTotalBytes === null ? 'Not reported'
        : `${formatBytes(managed.status.storageAvailableBytes)} / ${formatBytes(managed.status.storageTotalBytes)}`}</dd></div>
      <div><dt>Load average</dt><dd>{managed.status.loadAverage?.toFixed(2) ?? 'Not reported'}</dd></div>
      <div><dt>Containers</dt><dd>{containerSummary(managed.status.runtime)}</dd></div>
      <div><dt>Node certificate expires</dt><dd><DateTime value={managed.certificateExpiresAt} /></dd></div>
    </dl>}
    {device.phase === 'Managed' && managed?.status?.runtime?.gpus.length ? <GpuSection node={managed} runtime={managed.status.runtime}
      disabled={disabled} session={session} refreshSession={refreshSession} onSaved={refresh} /> : null}
    <details><summary>Hardware and network details</summary>
      {device.phase === 'Managed' && <p className="hardware-meta">Hardware and network addresses below were recorded during discovery. Agent readings above are reported separately.</p>}
      <dl className="fact-list">
        <div><dt>Device ID</dt><dd>{device.id}</dd></div>
        <div><dt>Serial number</dt><dd>{hardware.serialNumber || 'Not reported'}</dd></div>
        <div><dt>Hardware UUID</dt><dd>{hardware.hardwareUuid || 'Not reported'}</dd></div>
        <div><dt>Boot mode</dt><dd>{hardware.bootMode}</dd></div>
        <div><dt>Discovered</dt><dd><DateTime value={device.discoveredAt} /></dd></div>
        <div><dt>Last heartbeat</dt><dd>{device.lastHeartbeatAt ? <DateTime value={device.lastHeartbeatAt} /> : 'Not reported'} · {device.heartbeatFreshness.toLowerCase()}</dd></div>
      </dl>
      <h4>Network interfaces</h4>
      <ul className="hardware-detail-list">{hardware.interfaces.map(nic => <li key={nic.name}><strong>{nic.name}</strong> · {nic.macAddress ?? 'MAC address not reported'}<span>{nic.addresses.join(', ') || 'No address reported'}</span></li>)}</ul>
      <h4>Reported disks</h4>
      {hardware.disks.length ? <ul className="hardware-detail-list">{hardware.disks.map(disk => <li key={disk.path}>
        <strong>{disk.model || 'Model not reported'} · {formatBytes(disk.sizeBytes)}</strong>
        <span>Serial: {disk.serial || 'Not reported'} · {disk.path}</span>{disk.id && <span>{disk.id}</span>}
        <span>{disk.id === null ? 'Cannot safely identify this disk' : disk.isReadOnly ? 'Read-only — cannot install' : disk.isRemovable ? 'Removable — cannot install' : 'Writable, nonremovable disk'}</span>
      </li>)}</ul> : <p>No disks were reported.</p>}
    </details>
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
    {task && <a className="text-link" href="#/tasks">Follow installation <Icon name="arrow" /></a>}
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
          <label htmlFor={`${prefix}-disk`}>Disk to erase
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
        <div className="hardware-erasure" id={`${prefix}-warning`}><Icon name="attention" /><p><strong>All data on this disk will be erased.</strong> This cannot be undone. Back up anything you want to keep before approving.</p></div>
        <label className="hardware-checkbox"><input type="checkbox" checked={acknowledged} required onChange={event => setAcknowledged(event.target.checked)} />I have checked this physical device, matched its verification code, and approve erasing the selected disk.</label>
        <label className="hardware-confirmation" htmlFor={`${prefix}-confirmation`}>Type ERASE to confirm
          <input id={`${prefix}-confirmation`} value={confirmation} onChange={event => setConfirmation(event.target.value)} autoComplete="off" autoCapitalize="characters" spellCheck={false} required pattern="ERASE" aria-describedby={`${prefix}-warning`} />
        </label>
        <button className="button primary" type="submit" disabled={!selectedDisk || !acknowledged || confirmation !== 'ERASE' || !hostname || !recoveryPublicKey}>Erase selected disk and install</button>
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
