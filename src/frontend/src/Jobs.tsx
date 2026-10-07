import { useCallback, useEffect, useId, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { isDestructiveTool, openAssistantChat, parseModels, parseSettings, toolLabel } from './assistant'
import type { AssistantModels, AssistantTool } from './assistant'
import { cronOf, emptyJob, frequencies, parseJob, parseJobs, parseNext, parseRun, parseRuns, runStatusText, scheduleOf, scheduleText, scopeText,
  weekdays } from './assistantJobs'
import type { Job, JobDraft, JobRun, Schedule } from './assistantJobs'
import { Icon } from './Icon'
import type { IconName } from './Icon'
import './NetworkSettings.css'
import './Jobs.css'

type Props = { session: AuthenticationSession; refreshSession: () => Promise<void> }
type Request = (path: string, method?: string, body?: unknown, signal?: AbortSignal) => Promise<Response>

const maxPrompts = 10
const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })
const timeFormat = new Intl.DateTimeFormat(undefined, { timeStyle: 'short' })
const when = (value: string) => dateFormat.format(new Date(value))
const localZone = Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'
const zones = (() => { try { return Intl.supportedValuesOf('timeZone') } catch { return [localZone] } })()
const failureText = (failure: unknown, fallback: string) => failure instanceof TypeError
  ? 'Lucia could not reach the host. Check your connection and try again.' : failure instanceof Error ? failure.message : fallback
const siteName = (entry: string) => { try { return /^https?:\/\//i.test(entry) ? new URL(entry).hostname : entry } catch { return entry } }
const Hidden = ({ children }: { children: string }) => <span className="visually-hidden"> {children}</span>

function RunStatus({ run, waiting }: { run: JobRun; waiting?: string }) {
  const [tone, icon, label]: [string, IconName, string] = waiting && run.status === 'running' ? ['amber', 'attention', 'Waiting for you']
    : run.status === 'running' ? ['accent', 'clock', 'Running'] : run.status === 'succeeded' ? ['green', 'check', 'Finished']
      : run.status === 'stopped' ? ['muted', 'stop', 'Stopped'] : ['amber', 'attention', runStatusText(run)]
  return <span className={`status status-${tone}`}><Icon name={icon} />{label}</span>
}

const toDraft = ({ name, prompts, cron, timeZone, model, enabled, tools, hosts }: Job): JobDraft =>
  ({ name, prompts, cron, timeZone, model, enabled, tools, hosts })

export function Jobs({ session, refreshSession }: Props) {
  const [jobs, setJobs] = useState<Job[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState('')
  const [editing, setEditing] = useState<{ id?: string; draft: JobDraft } | null>(null)
  const [historyId, setHistoryId] = useState<string | null>(null)
  const [confirming, setConfirming] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  // The control to focus once the page shows it again, such as a row's Edit after its editor closes.
  const focusNext = useRef<string | null>(null)
  const request: Request = useCallback((path, method = 'GET', body, signal) =>
    ownerRequest(session, refreshSession, `/api/assistant/jobs${path}`, method, body, signal), [session, refreshSession])
  const load = useCallback(async (signal?: AbortSignal) => {
    const list = parseJobs(await (await request('', 'GET', undefined, signal)).json())
    if (!signal?.aborted) setJobs(list)
  }, [request])
  useEffect(() => {
    if (!session.isOwner) return
    const controller = new AbortController()
    load(controller.signal).catch(failure => { if (!controller.signal.aborted) setError(failureText(failure, 'Lucia could not list your jobs.')) })
    return () => controller.abort()
  }, [load, session.isOwner])
  // A running job finishes on its own, so the list checks back until it has.
  const running = jobs?.some(job => job.running)
  useEffect(() => {
    if (!running) return
    const timer = setInterval(() => void load().catch(() => undefined), 10_000)
    return () => clearInterval(timer)
  }, [running, load])
  useEffect(() => {
    const target = focusNext.current && document.getElementById(focusNext.current)
    if (target) { focusNext.current = null; target.focus() }
  }, [editing, jobs, historyId])

  async function act(job: Job, work: () => Promise<unknown>, done: string, fallback: string) {
    setBusy(job.id); setError(null); setNotice('')
    try { await work(); await load(); setNotice(done) }
    catch (failure) { setError(failureText(failure, fallback)) }
    finally { setBusy(null) }
  }
  const runNow = (job: Job) => act(job, async () => {
    const run = parseRun(await (await request(`/${job.id}/run`, 'POST')).json())
    openAssistantChat(run.sessionId)
  }, `${job.name} started. Its chat is open in the assistant.`, 'Lucia could not start this job.')
  const remove = (job: Job) => act(job, async () => {
    await request(`/${job.id}`, 'DELETE')
    setConfirming(null)
    if (historyId === job.id) setHistoryId(null)
    focusNext.current = 'job-new'
  }, `${job.name} is deleted. Its past chats stay in your chat history.`, 'Lucia could not delete this job.')
  const showHistory = (job: Job) => {
    focusNext.current = historyId === job.id ? null : 'job-history-heading'
    setHistoryId(current => current === job.id ? null : job.id)
  }
  const history = jobs?.find(job => job.id === historyId)

  if (!session.isOwner) return <div className="page-intro"><h1>Owner access is needed.</h1><p>Only lab owners can schedule the assistant.</p></div>
  if (editing) return <JobEditor key={editing.id ?? 'new'} id={editing.id} initial={editing.draft} request={request} session={session} refreshSession={refreshSession}
    onDone={saved => {
      focusNext.current = saved ? `job-edit-${saved.id}` : editing.id ? `job-edit-${editing.id}` : 'job-new'
      setEditing(null)
      if (saved) { setNotice(`${saved.name} is saved.`); void load().catch(() => undefined) }
    }} />
  return <>
    <div className="page-intro network-overview-header job-header">
      <div><h1>Assistant jobs</h1><p>Saved prompts the assistant runs on a schedule, on its own. Each run is a chat you can open, and it notifies you when it needs you.</p></div>
      <button id="job-new" className="button primary" onClick={() => { setNotice(''); setEditing({ draft: emptyJob(localZone) }) }}><Icon name="plus" />New job</button>
    </div>
    {error && <p className="network-error" role="alert">{error}</p>}
    <p className="network-notice" role="status">{jobs ? notice : 'Reading your jobs…'}</p>
    {jobs && !jobs.length && <section className="surface network-section">
      <h2>No jobs yet</h2>
      <p>A job could check every morning for apps that stopped when they shouldn’t have, read their logs and fix what it can, or tell you what it couldn’t.</p>
    </section>}
    {jobs && jobs.length > 0 && <section className="surface network-section">
      <table className="network-table job-table">
        <caption className="network-table-caption">Your jobs</caption>
        <thead><tr><th scope="col">Job</th><th scope="col">Schedule</th><th scope="col">Last run</th><th scope="col"><span className="visually-hidden">Actions</span></th></tr></thead>
        <tbody>{jobs.map(job => <tr key={job.id}>
          <th scope="row">{job.name}<span className="network-cell-note">{scopeText(job, isDestructiveTool)}</span></th>
          <td data-label="Schedule">{scheduleText(job.cron)}<span className="network-cell-note">{!job.enabled ? 'Paused' : job.next ? `Next ${when(job.next)}` : 'No upcoming run'}</span></td>
          <td data-label="Last run">{job.last ? <LastRun run={job.last} waiting={job.waiting} /> : <span className="status status-muted">Not run yet</span>}</td>
          <td><div className="job-row-actions">
            <button className="button secondary" disabled={busy === job.id || job.running} onClick={() => void runNow(job)}>
              {job.running ? 'Running…' : 'Run now'}<Hidden>{job.name}</Hidden></button>
            <button id={`job-edit-${job.id}`} className="text-link" disabled={busy === job.id}
              onClick={() => { setNotice(''); setEditing({ id: job.id, draft: toDraft(job) }) }}>Edit<Hidden>{job.name}</Hidden></button>
            <button className="text-link" aria-pressed={historyId === job.id} onClick={() => showHistory(job)}>History<Hidden>{`of ${job.name}`}</Hidden></button>
            <button className="text-link" disabled={busy === job.id} aria-expanded={confirming === job.id}
              onClick={() => setConfirming(current => current === job.id ? null : job.id)}>Delete<Hidden>{job.name}</Hidden></button>
          </div>
          {confirming === job.id && <div className="job-confirm" role="group" aria-label={`Delete ${job.name}`}>
            <p>Delete {job.name}? Its past chats stay in your chat history.</p>
            <button className="button secondary" disabled={busy === job.id || job.running} onClick={() => void remove(job)}>Delete job</button>
            <button className="text-link" autoFocus onClick={() => setConfirming(null)}>Cancel</button>
          </div>}</td>
        </tr>)}</tbody>
      </table>
    </section>}
    {history && <JobHistory key={history.id} job={history} request={request} />}
  </>
}

function LastRun({ run, waiting }: { run: JobRun; waiting?: string }) {
  const live = waiting && run.status === 'running'
  return <><RunStatus run={run} waiting={waiting} /><span className="network-cell-note">
    {live ? `Approve by ${timeFormat.format(new Date(waiting))}` : when(run.started)}
    {' · '}<button className="text-link job-inline-link" onClick={() => openAssistantChat(run.sessionId)}>{live ? 'Review' : 'Open chat'}</button></span></>
}

function JobHistory({ job, request }: { job: Job; request: Request }) {
  const [runs, setRuns] = useState<JobRun[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const last = job.last?.id, status = job.last?.status
  useEffect(() => {
    const controller = new AbortController()
    request(`/${job.id}/runs`, 'GET', undefined, controller.signal).then(response => response.json()).then(parseRuns)
      .then(value => { if (!controller.signal.aborted) setRuns(value) })
      .catch(failure => { if (!controller.signal.aborted) setError(failureText(failure, 'Lucia could not read this job’s runs.')) })
    return () => controller.abort()
  }, [job.id, request, last, status])
  return <section className="surface network-section" aria-labelledby="job-history-heading">
    <h2 id="job-history-heading" tabIndex={-1}>Runs of {job.name}</h2>
    {error ? <p className="network-error" role="alert">{error}</p> : !runs ? <p className="section-note">Reading runs…</p>
      : !runs.length ? <p className="section-note">This job hasn’t run yet.</p>
        : <ul className="network-review-list">{runs.map(run => {
          const waiting = run.id === job.last?.id ? job.waiting : undefined
          return <li key={run.id} className="ssh-key-row">
            <div><strong>{when(run.started)}</strong><span><RunStatus run={run} waiting={waiting} /> · {run.trigger === 'manual' ? 'Run by hand' : 'On schedule'}
              {waiting && run.status === 'running' && ` · approve by ${timeFormat.format(new Date(waiting))}`}
              {run.finished && ` · ended ${when(run.finished)}`}{run.error && ` · ${run.error}`}</span></div>
            <button className="text-link" onClick={() => openAssistantChat(run.sessionId)}>
              {waiting && run.status === 'running' ? 'Review' : 'Open chat'}<Hidden>{`from ${when(run.started)}`}</Hidden></button>
          </li>
        })}</ul>}
  </section>
}

type EditorProps = Props & { id?: string; initial: JobDraft; request: Request; onDone: (saved?: Job) => void }

function JobEditor({ id, initial, request, session, refreshSession, onDone }: EditorProps) {
  const [draft, setDraft] = useState<JobDraft>(initial)
  const [sites, setSites] = useState(initial.hosts.join('\n'))
  const [schedule, setSchedule] = useState<Schedule>(() => scheduleOf(initial.cron))
  const [tools, setTools] = useState<AssistantTool[]>([])
  const [inherited, setInherited] = useState<string[]>([])
  const [models, setModels] = useState<AssistantModels | null>(null)
  const [next, setNext] = useState<{ times?: string[]; error?: string }>({})
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const heading = useRef<HTMLHeadingElement>(null)
  const changesHeading = useId()
  const dangerHeading = useId()
  const update = (change: Partial<JobDraft>) => setDraft(current => ({ ...current, ...change }))
  const pick = (change: Partial<Schedule>) => {
    const value = { ...schedule, ...change }
    setSchedule(value)
    const cron = cronOf(value)
    if (cron) update({ cron })
  }

  useEffect(() => heading.current?.focus(), [])
  useEffect(() => {
    const controller = new AbortController()
    const get = (path: string) => ownerRequest(session, refreshSession, path, 'GET', undefined, controller.signal).then(response => response.json())
    get('/api/assistant/settings').then(parseSettings).then(value => { if (!controller.signal.aborted) { setTools(value.tools); setInherited(value.autoTools) } })
      .catch(failure => { if (!controller.signal.aborted) setError(failureText(failure, 'Lucia could not read the assistant’s tools.')) })
    get('/api/assistant/models').then(parseModels).then(value => { if (!controller.signal.aborted) setModels(value) })
      .catch(() => { if (!controller.signal.aborted) setModels({ connected: false, sources: [], models: [] }) })
    return () => controller.abort()
  }, [session, refreshSession])
  // The host reads the schedule, so the preview matches when the job really runs.
  useEffect(() => {
    const controller = new AbortController()
    const timer = setTimeout(() => {
      request('/schedule', 'POST', { cron: draft.cron, timeZone: draft.timeZone }, controller.signal)
        .then(response => response.json()).then(parseNext)
        .then(times => { if (!controller.signal.aborted) setNext({ times }) })
        .catch(failure => { if (!controller.signal.aborted) setNext({ error: failureText(failure, 'Lucia could not read this schedule.') }) })
    }, 400)
    return () => { clearTimeout(timer); controller.abort() }
  }, [draft.cron, draft.timeZone, request])

  async function save(event: FormEvent) {
    event.preventDefault()
    setBusy(true); setError(null)
    try {
      const body = { ...draft, model: draft.model || null, prompts: draft.prompts.map(prompt => prompt.trim()).filter(Boolean),
        hosts: sites.split(/[\s,]+/).filter(Boolean).map(siteName) }
      const saved = parseJob(await (await request(id ? `/${id}` : '', id ? 'PUT' : 'POST', body)).json())
      onDone(saved)
    } catch (failure) {
      setError(failureText(failure, 'Lucia could not save this job.'))
      setBusy(false)
    }
  }
  const toggle = (name: string) => update({ tools: draft.tools.includes(name) ? draft.tools.filter(tool => tool !== name) : [...draft.tools, name] })
  const setPrompt = (index: number, text: string) => update({ prompts: draft.prompts.map((prompt, at) => at === index ? text : prompt) })
  const changes = tools.filter(tool => tool.tier === 'change')
  const dangers = tools.filter(tool => tool.tier === 'destructive')
  const granted = dangers.filter(tool => draft.tools.includes(tool.name))
  const zoneList = zones.includes(draft.timeZone) ? zones : [draft.timeZone, ...zones]
  const custom = schedule.frequency === 'custom'
  const timed = !custom && schedule.frequency !== 'hourly' && schedule.frequency !== 'six-hours'
  const toolBox = (tool: AssistantTool) => {
    const always = inherited.includes(tool.name)
    return <label key={tool.name} className="network-checkbox">
      <input type="checkbox" checked={always || draft.tools.includes(tool.name)} disabled={busy || always} onChange={() => toggle(tool.name)}
        aria-describedby={always ? 'job-inherited' : undefined} />
      <span>{toolLabel(tool)}{always && <span className="network-cell-note">Allowed in Assistant settings</span>}</span></label>
  }

  return <>
    <div className="page-intro"><h1 ref={heading} tabIndex={-1}>{id ? `Edit ${initial.name}` : 'New job'}</h1><p>The job runs as you, in a new chat each time. Anything you haven’t allowed here or in Assistant settings waits for your approval, and you’ll get a notification.</p></div>
    <form onSubmit={event => void save(event)}>
      <section className="surface network-section">
        <h2>What it does</h2>
        <label className="job-field">Name<input value={draft.name} maxLength={80} required disabled={busy} onChange={event => update({ name: event.target.value })}
          placeholder="Morning app check" /></label>
        {draft.prompts.map((prompt, index) => <div key={index} className="job-prompt">
          <label className="job-field">{draft.prompts.length > 1 ? `Prompt ${index + 1}` : 'Prompt'}
            <textarea className="ssh-key-input job-prose" value={prompt} rows={5} maxLength={32768} required disabled={busy}
              onChange={event => setPrompt(index, event.target.value)} aria-describedby="job-prompts-hint"
              placeholder="Scan all apps for containers that are offline but shouldn’t be stopped. Read their logs, find the cause and fix it if no app change is needed. Otherwise, notify me." /></label>
          {draft.prompts.length > 1 && <button type="button" className="text-link" disabled={busy}
            onClick={() => update({ prompts: draft.prompts.filter((_, at) => at !== index) })}>Remove prompt {index + 1}</button>}
        </div>)}
        {draft.prompts.length < maxPrompts && <button type="button" className="text-link job-add" disabled={busy}
          onClick={() => update({ prompts: [...draft.prompts, ''] })}><Icon name="plus" />Add a follow-up prompt</button>}
        <p id="job-prompts-hint" className="section-note">Prompts run in order in the same chat, each after the last answer ends. If one fails or is stopped, the rest don’t run.</p>
        <label className="job-field">Model<select value={draft.model ?? ''} disabled={busy || !models} onChange={event => update({ model: event.target.value || undefined })}>
          <option value="">Your default model</option>
          {models?.sources.filter(source => source.models.length).map(source => <optgroup key={source.id} label={source.name}>
            {source.models.map(model => <option key={model.id} value={model.id}>{model.name}</option>)}</optgroup>)}
          {draft.model && models && !models.models.some(model => model.id === draft.model) && <option value={draft.model}>{draft.model}</option>}
        </select></label>
      </section>
      <section className="surface network-section">
        <h2>When it runs</h2>
        <div className="network-fields job-schedule">
          <label>Repeats<select value={schedule.frequency} disabled={busy} onChange={event => pick({ frequency: event.target.value as Schedule['frequency'] })}>
            {frequencies.map(item => <option key={item.value} value={item.value}>{item.label}</option>)}
          </select></label>
          {schedule.frequency === 'weekly' && <label>On<select value={schedule.day} disabled={busy} onChange={event => pick({ day: Number(event.target.value) })}>
            {weekdays.map((day, index) => <option key={day} value={index}>{day}</option>)}
          </select></label>}
          {timed && <label>At<input type="time" value={schedule.time} required disabled={busy} onChange={event => pick({ time: event.target.value })} /></label>}
          <label>Time zone<select value={draft.timeZone} disabled={busy} onChange={event => update({ timeZone: event.target.value })}>
            {zoneList.map(zone => <option key={zone} value={zone}>{zone.replace(/_/g, ' ')}</option>)}
          </select></label>
        </div>
        {custom && <label className="job-field">Cron expression<input value={draft.cron} required disabled={busy} spellCheck={false} autoComplete="off"
          className="job-mono" onChange={event => update({ cron: event.target.value })} aria-describedby="job-cron-hint job-next"
          aria-invalid={next.error ? true : undefined} />
          <span id="job-cron-hint" className="ssh-key-hint">Minute, hour, day of month, month, day of week. 30 6 * * 1-5 runs at 6:30 on weekdays.</span></label>}
        <p id="job-next" className={`section-note${draft.enabled ? '' : ' job-paused'}`} role="status">{next.error ? next.error : !next.times ? 'Reading the schedule…'
          : `${draft.enabled ? 'Next runs' : 'Paused. Once resumed, it would run'}: ${next.times.map(when).join(' · ')}`}</p>
        <label className="network-checkbox"><input type="checkbox" checked={draft.enabled} disabled={busy} onChange={event => update({ enabled: event.target.checked })} />
          <span>Run on this schedule. Turn off to pause it; you can still run it by hand.</span></label>
      </section>
      <section className="surface network-section">
        <h2 id={changesHeading}>What it may do without asking</h2>
        <p id="job-inherited" className="section-note">Only for this job, on top of what Assistant settings allow. When it needs something else it waits up to 30 minutes for your approval. Approving “for this job” there adds it here.</p>
        <div className="assistant-settings-tools" role="group" aria-labelledby={changesHeading}>{changes.map(toolBox)}</div>
        <h3 id={dangerHeading} className="job-danger-heading">Risky changes</h3>
        <div className="assistant-settings-tools" role="group" aria-labelledby={dangerHeading}>{dangers.map(toolBox)}</div>
        {granted.length > 0 && <p className="network-warning" role="note"><strong>Nobody checks these before they happen.</strong> This job may {granted.map(tool => toolLabel(tool).toLowerCase()).join(', ')} while no one is watching. Only allow what its prompts truly need.</p>}
        <label className="job-field">Sites it may read<textarea className="ssh-key-input" value={sites} rows={3} autoComplete="off" spellCheck={false} disabled={busy}
          onChange={event => setSites(event.target.value)} placeholder="docs.docker.com" aria-describedby="job-sites-hint" /></label>
        <p id="job-sites-hint" className="section-note">One host name per line, on top of the allowed sites in Assistant settings.</p>
      </section>
      <div className="network-actions">
        <button className="button primary" disabled={busy || !tools.length}>{busy ? 'Saving…' : id ? 'Save job' : 'Create job'}</button>
        <button type="button" className="text-link" disabled={busy} onClick={() => onDone()}>Cancel</button>
        {error && <p className="network-error" role="alert">{error}</p>}
      </div>
    </form>
  </>
}
