import { useCallback, useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { Icon } from './Icon'
import { actionLabel, defaultWindow, formatBytes, fromLocalInput, groupUpdates, parseSparkUpdates, progressLabel, progressPercent, restartNeeded, resultLabel, runningLabel, summarize, toLocalInput } from './packageUpdates'
import type { PackageUpdate, RestartPolicy, SparkUpdates as State } from './packageUpdates'
import './NetworkSettings.css'
import './SparkUpdates.css'

type Group = 'everyday' | 'platform'
const dismissKey = 'lucia.sparkUpdates.dismissedTask'
type Target = Group | 'restart' | 'all'
type Panel = { kind: 'install'; group: Group | 'all' } | { kind: 'schedule'; group: Target } | { kind: 'restart-spark' } | null
const everything = { packages: null, includePlatform: true, restart: 'spark' } as const

const when = (value: string) => new Date(value).toLocaleString(undefined, { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })
function ago(value: string): string {
  const minutes = Math.round((Date.now() - Date.parse(value)) / 60000)
  if (minutes < 2) return 'just now'
  if (minutes < 90) return `${minutes} minutes ago`
  const hours = Math.round(minutes / 60)
  return hours < 36 ? `${hours} hours ago` : `${Math.round(hours / 24)} days ago`
}
const restartText: Record<RestartPolicy, string> = {
  spark: 'Restart services, and the Spark if an update needs it',
  services: 'Restart only the services using updated files',
  none: 'Don’t restart anything',
}

export function SparkUpdates({ session, refreshSession }: { session: AuthenticationSession; refreshSession: () => Promise<void> }) {
  const [state, setState] = useState<State | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [selected, setSelected] = useState<{ everyday: Set<string>; platform: Set<string> }>({ everyday: new Set(), platform: new Set() })
  const [panel, setPanel] = useState<Panel>(null)
  const [restart, setRestart] = useState<RestartPolicy>('services')
  const [runAt, setRunAt] = useState(defaultWindow)
  const [acknowledged, setAcknowledged] = useState(false)
  const [changelogs, setChangelogs] = useState<Record<string, { text?: string; error?: string; loading?: boolean }>>({})
  const [copied, setCopied] = useState(false)
  const [dismissed, setDismissed] = useState(() => localStorage.getItem(dismissKey))
  const known = useRef<Set<string>>(new Set())
  const pending = useRef<AbortController | null>(null)

  const load = useCallback(async () => {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller
    try {
      const response = await ownerRequest(session, refreshSession, '/api/host/packages', 'GET', undefined, controller.signal)
      const next = parseSparkUpdates(await response.json())
      if (controller.signal.aborted) return
      setState(next)
      setError(null)
      const { everyday, platform } = groupUpdates(next.updates)
      setSelected(current => ({
        // New everyday updates start selected; platform updates are always opt-in.
        everyday: new Set(everyday.map(item => item.name).filter(name => current.everyday.has(name) || !known.current.has(name))),
        platform: new Set(platform.map(item => item.name).filter(name => current.platform.has(name))),
      }))
      known.current = new Set(next.updates.map(item => item.name))
    } catch (failure) {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'Spark updates could not be read.')
    } finally {
      if (pending.current === controller) pending.current = null
    }
  }, [session, refreshSession])

  const active = !!state && (!!state.operation || state.pendingRequest || !!state.rebootingAt || state.scanning || state.modelsPaused)
  useEffect(() => {
    void load()
    const interval = setInterval(() => { if (document.visibilityState !== 'hidden') void load() }, active ? 4000 : 60000)
    return () => clearInterval(interval)
  }, [load, active])
  useEffect(() => () => { pending.current?.abort(); pending.current = null }, [])

  async function submit(path: string, method: string, body: unknown, done: string) {
    setSubmitting(true)
    setNotice(null)
    try {
      await ownerRequest(session, refreshSession, path, method, body)
      setPanel(null)
      if (done) setNotice(done)
      pending.current?.abort()
      pending.current = null
      await load()
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Lucia could not start this task.')
    } finally { setSubmitting(false) }
  }

  async function changelog(name: string) {
    if (changelogs[name]?.text || changelogs[name]?.loading) {
      setChangelogs(current => { const next = { ...current }; delete next[name]; return next })
      return
    }
    setChangelogs(current => ({ ...current, [name]: { loading: true } }))
    try {
      const response = await ownerRequest(session, refreshSession, '/api/host/packages/changelog', 'POST', { package: name })
      const value: unknown = await response.json()
      const text = value && typeof value === 'object' && 'changelog' in value && typeof value.changelog === 'string' ? value.changelog : ''
      setChangelogs(current => ({ ...current, [name]: { text: text || 'The changelog is empty.' } }))
    } catch (failure) {
      setChangelogs(current => ({ ...current, [name]: { error: failure instanceof Error ? failure.message : 'The changelog could not be read.' } }))
    }
  }

  function open(next: Panel) {
    setPanel(next)
    setAcknowledged(false)
    setRestart(next?.kind === 'schedule' ? 'spark' : 'services')
    if (next?.kind === 'schedule') setRunAt(state?.schedule ? toLocalInput(new Date(state.schedule.runAt)) : defaultWindow())
  }

  if (!state) return <>
    <div className="page-intro"><h1>Spark updates</h1><p>Operating system and platform packages on your DGX Spark.</p></div>
    {error ? <p className="network-error" role="alert">{error}</p> : <p className="section-note" role="status">Reading the Spark’s packages…</p>}
  </>

  const { everyday, platform } = groupUpdates(state.updates)
  const busy = submitting || !!state.operation || state.pendingRequest || !!state.rebootingAt || state.worker.state !== 'ready'
  const pick = (group: Group) => (group === 'everyday' ? everyday : platform).filter(item => selected[group].has(item.name))
  const toggle = (group: Group, name: string) => setSelected(current => {
    const next = new Set(current[group])
    if (next.has(name)) next.delete(name)
    else next.add(name)
    return { ...current, [group]: next }
  })
  const toggleAll = (group: Group, items: PackageUpdate[]) => setSelected(current => ({
    ...current, [group]: current[group].size === items.length ? new Set() : new Set(items.map(item => item.name)),
  }))
  const system = state.system
  const percent = progressPercent(state.operation?.progress ?? null)
  const last = state.history[0]
  const recent = last && last.id !== dismissed && Date.now() - Date.parse(last.finishedAt ?? last.startedAt) < 30 * 60000 ? last : null
  const dismiss = (id: string) => { localStorage.setItem(dismissKey, id); setDismissed(id) }
  const services = state.restart.services

  function installBody(group: Group, policy: RestartPolicy) {
    return { packages: pick(group).map(item => item.name), includePlatform: group === 'platform', restart: policy }
  }
  function scheduleBody(group: Target) {
    const iso = fromLocalInput(runAt)
    if (group === 'all') return { runAt: iso, ...everything }
    if (group === 'restart') return { runAt: iso, packages: [], includePlatform: false, restart: 'spark' }
    const items = pick(group)
    // Everything in the everyday group means "whatever everyday updates exist then".
    const packages = group === 'everyday' && items.length === everyday.length ? null : items.map(item => item.name)
    return { runAt: iso, packages, includePlatform: group === 'platform', restart }
  }

  function panelFor(group: Target) {
    if (!panel || panel.kind === 'restart-spark' || panel.group !== group) return null
    const items = group === 'restart' ? [] : group === 'all' ? state!.updates : pick(group)
    const needs = restartNeeded(items)
    const schedule = panel.kind === 'schedule'
    // A scheduled "everything" window may include platform updates published before it runs.
    const isPlatform = group === 'platform' || (group === 'all' && (schedule || platform.length > 0))
    const minimum = toLocalInput(new Date(Date.now() + 3 * 60000))
    const valid = !schedule || (fromLocalInput(runAt) !== null && runAt >= minimum)
    return <div className="updates-panel" role="group" aria-label={schedule ? 'Schedule updates' : 'Confirm install'}>
      <h3>{group === 'restart' ? 'Schedule a Spark restart' : group === 'all' ? (schedule ? 'Schedule every update' : `Install all ${items.length} update${items.length === 1 ? '' : 's'} now?`) : schedule ? `Schedule ${items.length} update${items.length === 1 ? '' : 's'}` : `Install ${items.length} update${items.length === 1 ? '' : 's'} now?`}</h3>
      {group === 'all' && <p className="section-note">{schedule ? 'Lucia installs every update available at that time, restarts the services using them, and restarts the Spark only if an update needs it.'
        : needs === 'spark' || state!.restart.sparkRequired ? 'When the install finishes, Lucia restarts the services using these files and then restarts the Spark. Everything on the Spark is offline for a few minutes.'
          : needs === 'lucia' ? 'This updates Docker, so Lucia goes offline for a minute while it restarts. The Spark itself keeps running.'
            : 'Lucia restarts the services using these files. None of them need a Spark restart.'}</p>}
      {group !== 'restart' && group !== 'all' && <p className="section-note">{needs === 'spark' ? 'At least one of these finishes only after the Spark restarts.'
        : needs === 'lucia' ? 'This updates Docker, so Lucia goes offline for a minute while it restarts.'
          : 'Services that use these packages can be restarted without restarting the Spark.'}</p>}
      {isPlatform && <p className="network-warning">Lucia unloads Local AI before installing and reloads it when the updates finish{needs === 'spark' ? ' or the Spark restarts' : ''}. Apps using Local AI will get errors until then.</p>}
      {schedule && <label className="updates-field">Start at
        <input type="datetime-local" value={runAt} min={minimum} onChange={event => setRunAt(event.target.value)} />
        <span className="network-cell-note">Your browser’s time zone. Lucia skips the window if it was offline for more than an hour past this time.</span>
      </label>}
      {group !== 'restart' && group !== 'all' && <fieldset className="updates-restart"><legend>Afterwards</legend>
        {(['spark', 'services', 'none'] as const).map(option => <label key={option} className="network-checkbox">
          <input type="radio" name={`restart-${group}`} checked={restart === option} onChange={() => setRestart(option)} />{restartText[option]}
        </label>)}
      </fieldset>}
      {isPlatform && <label className="network-checkbox"><input type="checkbox" checked={acknowledged} onChange={event => setAcknowledged(event.target.checked)} />
        I understand Local AI will be unavailable and the Spark may restart.</label>}
      <div className="network-actions">
        <button className="button primary" disabled={submitting || !valid || (isPlatform && !acknowledged) || (group !== 'restart' && items.length === 0)}
          onClick={() => void (schedule
            ? submit('/api/host/packages/schedule', 'PUT', scheduleBody(group), 'Scheduled. Lucia will start at the chosen time.')
            : submit('/api/host/packages/install', 'POST', group === 'all' ? everything : installBody(group as Group, restart), ''))}>
          {submitting ? 'Sending…' : schedule ? 'Save schedule' : group === 'all' ? 'Update everything' : isPlatform ? 'Install platform updates' : 'Install now'}</button>
        <button className="text-link" onClick={() => setPanel(null)}>Cancel</button>
      </div>
    </div>
  }

  function group(id: Group, items: PackageUpdate[]) {
    const count = selected[id].size
    return <section className="surface network-section" aria-labelledby={`updates-${id}`}>
      <h2 id={`updates-${id}`}>{id === 'everyday' ? 'Everyday updates' : 'Platform updates'}</h2>
      <p className="section-note">{id === 'everyday' ? 'Ubuntu and application packages. Safe to install while you work.'
        : 'Kernel, NVIDIA driver, CUDA, DGX and Docker packages. Install these on their own, ideally after hours.'}</p>
      <div className="updates-list-head">
        <label className="network-checkbox"><input type="checkbox" checked={count === items.length} ref={input => { if (input) input.indeterminate = count > 0 && count < items.length }}
          onChange={() => toggleAll(id, items)} />Select all</label>
        <span className="muted">{count} of {items.length} selected · {formatBytes(pick(id).reduce((sum, item) => sum + item.downloadBytes, 0))} download</span>
      </div>
      <ul className="updates-list">{items.map(item => {
        const log = changelogs[item.name]
        return <li key={item.name}>
          <label className="updates-row">
            <input type="checkbox" checked={selected[id].has(item.name)} onChange={() => toggle(id, item.name)} />
            <span className="updates-name"><strong>{item.name}</strong><span className="muted">{item.summary}</span></span>
            <span className="updates-version">{item.currentVersion ?? 'new'} → {item.candidateVersion}<span className="muted">{item.source}</span></span>
          </label>
          <div className="updates-meta">
            {item.security && <span className="updates-badge badge-security"><Icon name="shield" />Security</span>}
            {item.restart === 'spark' && <span className="updates-badge badge-restart">Needs Spark restart</span>}
            {item.restart === 'lucia' && <span className="updates-badge badge-restart">Restarts Lucia briefly</span>}
            {item.phased && <span className="updates-badge" title="Ubuntu releases this gradually. Installing it now is supported.">Phased rollout</span>}
            {item.keptBack && <span className="updates-badge" title="A plain apt upgrade skips this because it needs new or changed packages.">Held back by apt</span>}
            <button className="text-link" aria-expanded={!!log} onClick={() => void changelog(item.name)}>{log ? 'Hide changelog' : 'Changelog'}</button>
          </div>
          {log && (log.loading ? <p className="section-note" role="status">Downloading the changelog…</p>
            : log.error ? <p className="network-error">{log.error}</p> : <pre className="updates-log" tabIndex={0}>{log.text}</pre>)}
        </li>
      })}</ul>
      {panelFor(id) ?? <div className="network-actions">
        <button className="button secondary" disabled={busy || count === 0} onClick={() => open({ kind: 'install', group: id })}>
          {id === 'everyday' ? (count ? `Install ${count} now` : 'Install now') : 'Review platform install'}</button>
        <button className="button secondary" disabled={busy || count === 0 || !!scheduled} onClick={() => open({ kind: 'schedule', group: id })}><Icon name="clock" />Schedule</button>
        {scheduled && <p className="section-note">Cancel or change the current schedule to plan another window.</p>}
      </div>}
    </section>
  }

  const attention = state.worker.state === 'ready' && !state.rebootingAt
    && (state.restart.sparkRequired || services.length > 0 || !state.packageDatabase.healthy || state.modelsPaused)
  const scheduled = state.schedule
  return <div className="spark-updates">
    <div className="page-intro network-overview-header"><div><h1>Spark updates</h1>
      <p>{system ? [system.os, system.dgx, `kernel ${system.kernel}`].filter(Boolean).join(' · ') : 'Operating system and platform packages on your DGX Spark.'}
        {state.lastUpdateCheckAt && <> · Package lists refreshed {ago(state.lastUpdateCheckAt)}</>}</p></div>
      <button className="button secondary" disabled={busy} onClick={() => void submit('/api/host/packages/check', 'POST', undefined, '')}>
        {state.operation?.action === 'check' ? 'Checking…' : 'Check for updates'}<Icon name="refresh" /></button>
    </div>
    <p className="updates-summary" role="status">{summarize(state)}</p>
    {notice && !error && <p className="network-notice">{notice}</p>}
    {state.operation ? <section className="surface network-section updates-task" aria-labelledby="updates-task">
      <h2 id="updates-task"><Icon name="clock" />{runningLabel(state.operation.action)}</h2>
      <div className="updates-progress" role="status">
        <span><strong>{progressLabel(state.operation.progress)}</strong>{state.operation.progress?.package && <span className="muted"> · {state.operation.progress.package}</span>}</span>
        <span className="muted">{percent !== null ? `${percent}%` : ''}</span>
      </div>
      <progress className="updates-bar" max={100} {...(percent !== null ? { value: percent } : {})} aria-label={runningLabel(state.operation.action)} />
      <p className="section-note">Started {ago(state.operation.startedAt)}. It keeps running if you close this page.</p>
      {state.operation.log.length > 0 && <details className="network-technical-details"><summary>What apt is printing</summary>
        <pre className="updates-log" tabIndex={0}>{state.operation.log.join('\n')}</pre></details>}
    </section> : state.pendingRequest ? <section className="surface network-section updates-task" aria-labelledby="updates-task">
      <h2 id="updates-task"><Icon name="clock" />Starting</h2>
      <p role="status">Waiting for the Spark’s update service to pick this up…</p>
      <progress className="updates-bar" aria-label="Starting" />
    </section> : recent && <section className={`surface network-section updates-task ${recent.state === 'succeeded' ? 'is-done' : 'is-failed'}`} aria-labelledby="updates-task">
      <h2 id="updates-task"><Icon name={recent.state === 'succeeded' ? 'check' : 'attention'} />{resultLabel(recent)}</h2>
      <p role="status">{recent.message ?? (recent.state === 'succeeded' ? 'Finished.' : 'The task stopped before it finished.')}</p>
      <p className="section-note">Finished {ago(recent.finishedAt ?? recent.startedAt)}.</p>
      {recent.log.length > 0 && <details className="network-technical-details" open={recent.state !== 'succeeded'}><summary>Last lines apt printed</summary>
        <pre className="updates-log" tabIndex={0}>{recent.log.join('\n')}</pre></details>}
      <div className="network-actions"><button className="button secondary" onClick={() => dismiss(recent.id)}>Dismiss</button></div>
    </section>}
    {error && <p className="network-error" role="alert">{error}{state.rebootingAt && ' This is expected while the Spark restarts.'}</p>}

    {state.worker.state === 'ready' && state.updates.length > 0 && <section className="surface network-section" aria-labelledby="updates-all">
      <h2 id="updates-all">Update everything</h2>
      <p className="section-note">Install all {state.updates.length} update{state.updates.length === 1 ? '' : 's'} in one pass{platform.length > 0 && `, including ${platform.length} platform update${platform.length === 1 ? '' : 's'}`}. Lucia restarts the services using them
        {restartNeeded(state.updates) === 'spark' ? ', then restarts the Spark because at least one update needs it.' : state.restart.sparkRequired ? ', then restarts the Spark to finish earlier updates.' : '. The Spark keeps running.'}</p>
      {panelFor('all') ?? <div className="network-actions">
        <button className="button primary" disabled={busy} onClick={() => open({ kind: 'install', group: 'all' })}>Update all</button>
        <button className="button secondary" disabled={busy || !!scheduled} onClick={() => open({ kind: 'schedule', group: 'all' })}><Icon name="clock" />Schedule</button>
      </div>}
    </section>}
    {state.worker.state === 'missing' && <section className="surface network-section">
      <h2>Set up Lucia’s update service</h2>
      <p>Installing packages needs administrator rights, so a small update service runs on the Spark as root. It only accepts Lucia’s fixed tasks: check, install listed updates, restart services, restart the Spark, and repair. Run this once on the Spark:</p>
      <div className="updates-command"><code>{state.worker.installCommand}</code>
        <button className="button secondary" onClick={() => void navigator.clipboard.writeText(state.worker.installCommand).then(() => setCopied(true), () => setCopied(false))}>
          <Icon name={copied ? 'check' : 'copy'} />{copied ? 'Copied' : 'Copy'}</button></div>
      <p className="section-note">This page picks it up within a few seconds.</p>
    </section>}
    {state.worker.state === 'stale' && !state.rebootingAt && <p className="network-warning">Lucia’s update service stopped responding. It restarts on its own; if this lasts, run <code>sudo systemctl status lucia-package-updates</code> on the Spark.</p>}

    {attention && <section className="surface network-section" aria-labelledby="updates-attention">
      <h2 id="updates-attention">Needs attention</h2>
      <ul className="updates-attention">
        {state.modelsPaused && <li><Icon name="clock" /><div><strong>Local AI is paused</strong><p>Lucia unloaded models for platform updates and reloads them when the updates finish.</p></div></li>}
        {state.restart.sparkRequired && <li><Icon name="attention" /><div><strong>Restart the Spark to finish updating</strong>
          <p>{state.restart.kernelPending ? 'A new kernel is installed but the Spark is still running the old one.' : 'Recent updates take effect after a restart.'}
            {state.restart.sparkReasons.length > 0 && ` Waiting on ${state.restart.sparkReasons.slice(0, 4).join(', ')}${state.restart.sparkReasons.length > 4 ? ` and ${state.restart.sparkReasons.length - 4} more` : ''}.`}</p>
          {panel?.kind === 'restart-spark' ? <div className="updates-panel">
            <h3>Restart the Spark now?</h3>
            <p>Lucia, Local AI and everything else on the Spark will be offline for a few minutes. Lucia reloads your models when it’s back.</p>
            <div className="network-actions">
              <button className="button primary" disabled={submitting} onClick={() => void submit('/api/host/packages/restart-spark', 'POST', { confirm: true }, 'Restarting the Spark…')}>{submitting ? 'Sending…' : 'Restart Spark'}</button>
              <button className="text-link" onClick={() => setPanel(null)}>Cancel</button></div>
          </div> : panelFor('restart') ?? <div className="network-actions">
            <button className="button secondary" disabled={busy} onClick={() => open({ kind: 'restart-spark' })}>Restart now</button>
            <button className="text-link" disabled={busy || !!scheduled} onClick={() => open({ kind: 'schedule', group: 'restart' })}><Icon name="clock" />Schedule restart</button>
          </div>}
        </div></li>}
        {services.length > 0 && <li><Icon name="server" /><div><strong>{services.length === 1 ? '1 service is' : `${services.length} services are`} still using old files</strong>
          <p>{services.slice(0, 6).join(', ')}{services.length > 6 && ` and ${services.length - 6} more`}. Restarting them takes a few seconds and doesn’t restart the Spark.</p>
          <div className="network-actions"><button className="button secondary" disabled={busy} onClick={() => void submit('/api/host/packages/restart-services', 'POST', undefined, '')}>Restart services</button></div>
        </div></li>}
        {!state.packageDatabase.healthy && <li><Icon name="attention" /><div><strong>The package database has unfinished installs</strong>
          <p>An earlier install stopped part-way. Repair finishes configuring those packages; nothing new is installed.</p>
          <div className="network-actions"><button className="button secondary" disabled={busy} onClick={() => void submit('/api/host/packages/repair', 'POST', undefined, '')}>Repair package database</button></div>
        </div></li>}
      </ul>
    </section>}

    {state.rebootingAt && <section className="surface network-section"><h2>The Spark is restarting</h2>
      <p>Requested {ago(state.rebootingAt)}. This page reconnects on its own when Lucia is back.</p></section>}

    {(scheduled || state.lastSchedule) && <section className="surface network-section" aria-labelledby="updates-schedule">
      <h2 id="updates-schedule">Scheduled window</h2>
      {scheduled ? <>
        <p className="updates-when"><Icon name="clock" /><strong>{when(scheduled.runAt)}</strong></p>
        <p>{scheduled.packages === null ? (scheduled.includePlatform ? 'Install every update available at that time' : 'Install every everyday update available at that time') : scheduled.packages.length === 0 ? 'Restart the Spark if it still needs it'
          : `Install ${scheduled.packages.length} selected update${scheduled.packages.length === 1 ? '' : 's'}`}{scheduled.includePlatform && scheduled.packages !== null && ', including platform updates'}.
          {scheduled.packages?.length !== 0 && ` ${restartText[scheduled.restart]}.`}</p>
        <div className="network-actions"><button className="button secondary" disabled={submitting} onClick={() => void submit('/api/host/packages/schedule', 'DELETE', undefined, 'Schedule canceled.')}>Cancel schedule</button></div>
      </> : <p className="section-note">No window scheduled.</p>}
      {state.lastSchedule && <p className={state.lastSchedule.state === 'started' ? 'section-note' : 'network-error'}>Last window ({when(state.lastSchedule.runAt)}): {state.lastSchedule.message}</p>}
    </section>}

    {state.worker.state !== 'missing' && (state.updates.length > 0 ? <>
      {everyday.length > 0 && group('everyday', everyday)}
      {platform.length > 0 && group('platform', platform)}
    </> : !state.scanning && <section className="surface network-section updates-clear"><Icon name="check" /><div><h2>Up to date</h2>
      <p className="section-note">{state.scannedAt ? `Compared against the package lists ${ago(state.scannedAt)}.` : ''} Use Check for updates to download the latest lists first.</p></div></section>)}

    {state.worker.state !== 'missing' && <section className="surface network-section">
      <h2>Automatic security updates</h2>
      <p>{!state.automaticUpdates ? 'Not reported yet.' : state.automaticUpdates.enabled ? 'On. Ubuntu installs security fixes daily by itself; they appear in the history below only when Lucia installs them.'
        : state.automaticUpdates.installed ? 'Off. unattended-upgrades is installed but not enabled, so security fixes wait for you here.'
          : 'Off. unattended-upgrades isn’t installed, so security fixes wait until you install them here.'}</p>
    </section>}

    {state.history.length > 0 && <details className="network-technical-details"><summary>Update history</summary>
      <ol className="network-help">{state.history.map(item => <li key={item.id}>
        <strong>{actionLabel(item.action)}</strong> · <span className={item.state === 'succeeded' ? undefined : 'updates-failed'}>{item.state === 'succeeded' ? 'Done' : item.state === 'failed' ? 'Failed' : 'Interrupted'}</span>
        {item.message && <> · {item.message}</>}<br /><small>{new Date(item.finishedAt ?? item.startedAt).toLocaleString()}</small>
        {item.state !== 'succeeded' && item.log.length > 0 && <pre className="updates-log" tabIndex={0}>{item.log.join('\n')}</pre>}
      </li>)}</ol>
    </details>}
  </div>
}
