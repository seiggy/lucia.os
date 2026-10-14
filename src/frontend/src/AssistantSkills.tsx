import { Component, lazy, Suspense, useCallback, useEffect, useId, useRef, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest, responseError } from './managementApi'
import { isChatId, openAssistantChat } from './assistant'
import { Icon } from './Icon'
import {
  copyName, executionLabel, executions, fileGroups, fileName, languageChoices, languageLabel, languageOf, maxDescriptionLength, maxNameLength, parseSkillCheck,
  parseSkillDetail, parseSkillFile, parseSkillFileContent, parseSkillList, sandboxBlockers, shortHash, sizeText, skillFromHash, skillsChangedEvent, splitSkills, starterScript, tierLabel, tiers,
  validateSkillName, validFileName,
} from './skills'
import type { SkillDetail, SkillDiagnostic, SkillExecution, SkillFile, SkillFileKind, SkillLanguage, SkillNetwork, SkillSummary, SkillTier } from './skills'
import './NetworkSettings.css'
import './MultiSelect.css'
import './Jobs.css'
import './AssistantSkills.css'

const SkillScriptEditor = lazy(() => import('./SkillScriptEditor'))

type Props = { session: AuthenticationSession; refreshSession: () => Promise<void> }
type Request = (path: string, method?: string, body?: unknown, signal?: AbortSignal) => Promise<Response>
type Failure = Error & { code?: string }

const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })
const failureText = (failure: unknown, fallback: string) => failure instanceof TypeError
  ? 'Lucia could not reach the host. Check your connection and try again.' : failure instanceof Error ? failure.message : fallback
const sandboxText = (failure: unknown, fallback: string) => (failure as Failure | undefined)?.code === 'sandbox_languages'
  ? `The sandbox runs JavaScript and Python only. ${failureText(failure, '')}`.trim() : failureText(failure, fallback)
const noKvm = 'No KVM server yet'
const discard = 'Discard your unsaved changes?'
const Hidden = ({ children }: { children: string }) => <span className="visually-hidden"> {children}</span>

class EditorBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false }
  static getDerivedStateFromError() { return { failed: true } }
  render() {
    return this.state.failed ? <p className="network-error" role="alert">The editor could not load. Reload the page to try again.</p> : this.props.children
  }
}

function Editor({ height, ...props }: { height: number } & React.ComponentProps<typeof SkillScriptEditor>) {
  return <div className="skills-editor-frame" style={{ height }}>
    <EditorBoundary><Suspense fallback={<p className="skills-editor-loading" role="status">Loading the editor…</p>}><SkillScriptEditor {...props} /></Suspense></EditorBoundary>
  </div>
}

function ExecutionPill({ value }: { value: SkillExecution }) {
  return <span className={`skills-pill skills-pill-${value}`}>{executionLabel(value)}</span>
}

// Free-text pills for host names, like the pill boxes elsewhere in the console.
function DomainInput({ value, onChange, disabled, labelledBy }: { value: string[]; onChange: (value: string[]) => void; disabled?: boolean; labelledBy: string }) {
  const [text, setText] = useState('')
  const [bad, setBad] = useState(false)
  const input = useRef<HTMLInputElement>(null)
  const hint = useId()
  function add(raw: string) {
    const entries = raw.split(/[\s,]+/).map(item => item.trim().toLowerCase()).filter(Boolean)
    if (!entries.length) return
    const invalid = entries.some(entry => !/^(\*\.)?[a-z0-9]([a-z0-9.-]*[a-z0-9])?$/.test(entry))
    setBad(invalid)
    if (invalid) return
    onChange([...new Set([...value, ...entries])])
    setText('')
  }
  return <div className="multi-select">
    <div className={`multi-control${disabled ? ' disabled' : ''}`} onClick={() => input.current?.focus()}>
      {value.map(domain => <span key={domain} className="multi-pill">{domain}
        <button type="button" disabled={disabled} aria-label={`Remove ${domain}`} onClick={event => { event.stopPropagation(); onChange(value.filter(item => item !== domain)) }}><Icon name="close" /></button></span>)}
      <input ref={input} className="multi-input" aria-labelledby={labelledBy} aria-describedby={hint} aria-invalid={bad || undefined} value={text} disabled={disabled}
        autoComplete="off" spellCheck={false} placeholder={value.length ? '' : 'api.example.com'}
        onChange={event => { setBad(false); setText(event.target.value) }} onBlur={() => add(text)}
        onKeyDown={event => {
          if (event.key === 'Enter' || event.key === ',') { event.preventDefault(); add(text) }
          else if (event.key === 'Backspace' && !text && value.length) onChange(value.slice(0, -1))
        }} />
    </div>
    <span id={hint} className={`ssh-key-hint${bad ? ' skills-bad' : ''}`}>{bad ? 'Use a host name like api.example.com, or *.example.com for every subdomain.'
      : 'Host names the script may reach. Press Enter to add one.'}</span>
  </div>
}

export function AssistantSkills({ session, refreshSession }: Props) {
  const [open, setOpen] = useState<string | null>(() => skillFromHash(location.hash))
  const request: Request = useCallback((path, method = 'GET', body, signal) =>
    ownerRequest(session, refreshSession, `/api/assistant/skills${path}`, method, body, signal), [session, refreshSession])
  // A review link from the chat or a notification adds ?skill=; the address drops it again once the skill closes.
  useEffect(() => {
    const linked = () => { const name = skillFromHash(location.hash); if (name) setOpen(name) }
    window.addEventListener('hashchange', linked)
    return () => window.removeEventListener('hashchange', linked)
  }, [])
  const close = useCallback(() => {
    setOpen(null)
    if (location.hash.includes('?')) history.replaceState(history.state, '', location.hash.split('?')[0])
  }, [])
  if (!session.isOwner) return <div className="page-intro"><h1>Owner access is needed.</h1><p>Only lab owners can manage what the assistant can run.</p></div>
  return open ? <SkillEditor key={open} name={open} request={request} session={session} refreshSession={refreshSession} onClose={close} onOpen={setOpen} />
    : <SkillList request={request} session={session} refreshSession={refreshSession} onOpen={setOpen} />
}

function SkillList({ request, session, refreshSession, onOpen }: Props & { request: Request; onOpen: (name: string) => void }) {
  const [skills, setSkills] = useState<SkillSummary[] | null>(null)
  const [kvmNodes, setKvmNodes] = useState<string[]>([])
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState('')
  const [panel, setPanel] = useState<'create' | 'github' | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [removing, setRemoving] = useState<string | null>(null)
  const [enabling, setEnabling] = useState<{ name: string; hash: string; scripts: SkillFile[] } | null>(null)
  const upload = useRef<HTMLInputElement>(null)
  const load = useCallback(async (signal?: AbortSignal) => {
    const value = parseSkillList(await (await request('', 'GET', undefined, signal)).json())
    if (!signal?.aborted) { setSkills(value.skills); setKvmNodes(value.kvmNodes); window.dispatchEvent(new CustomEvent(skillsChangedEvent, { detail: value.needsReview })) }
  }, [request])
  useEffect(() => {
    const controller = new AbortController()
    load(controller.signal).catch(failure => { if (!controller.signal.aborted) setError(failureText(failure, 'Lucia could not list your skills.')) })
    return () => controller.abort()
  }, [load])

  async function setEnabled(name: string, enabled: boolean, hash?: string) {
    setBusy(name); setError(null); setNotice('')
    try {
      await request(`/${name}/enabled`, 'POST', hash ? { enabled, hash } : { enabled })
      await load()
      setEnabling(null)
      setNotice(enabled ? `${name} is on. The assistant sees it from your next message.` : `${name} is off.`)
    } catch (failure) {
      if ((failure as Failure).code === 'skill_changed') {
        setEnabling(null)
        setError('This draft changed since you opened it. Review it again.')
        await load().catch(() => { /* The list refreshes on the next visit. */ })
      } else setError(failureText(failure, 'Lucia could not change this skill.'))
    }
    finally { setBusy(null) }
  }
  async function toggle(skill: SkillSummary) {
    if (skill.enabled) { await setEnabled(skill.name, false); return }
    // Turning a skill on shows its scripts first, since the assistant may run them.
    setBusy(skill.name); setError(null); setNotice('')
    try {
      const detail = parseSkillDetail(await (await request(`/${skill.name}`)).json())
      setEnabling({ name: skill.name, hash: detail.hash, scripts: detail.files.filter(file => file.kind === 'script') })
    } catch (failure) { setError(failureText(failure, 'Lucia could not read this skill.')) }
    finally { setBusy(null) }
  }
  async function importZip(file: File) {
    setBusy('import'); setError(null); setNotice('')
    try {
      const form = new FormData()
      form.append('file', file)
      const response = await fetch('/api/assistant/skills/import', {
        method: 'POST', credentials: 'same-origin', headers: { 'X-CSRF-TOKEN': session.csrfToken ?? '' }, body: form,
      })
      if (response.status === 401) void refreshSession()
      if (!response.ok) throw await responseError(response)
      onOpen(parseSkillDetail(await response.json()).name)
    } catch (failure) { setError(failureText(failure, 'Lucia could not import this file.')) }
    finally { setBusy(null); if (upload.current) upload.current.value = '' }
  }

  async function removeDraft(name: string) {
    setBusy(name); setError(null); setNotice('')
    try {
      await request(`/${name}`, 'DELETE')
      await load()
      setRemoving(null)
      setNotice(`${name} is deleted.`)
    } catch (failure) { setError(failureText(failure, 'Lucia could not delete this skill.')) }
    finally { setBusy(null) }
  }
  const { review, rest } = splitSkills(skills ?? [])
  const confirmEnable = (skill: SkillSummary) => enabling?.name === skill.name && <div className="job-confirm skills-confirm" role="group" aria-label={`Turn on ${skill.name}`}>
    <p>{enabling.scripts.length ? <>Once on, the assistant can run {enabling.scripts.length === 1 ? 'this script' : `these ${enabling.scripts.length} scripts`}, with the approvals you set:</>
      : 'This skill has no scripts, only instructions.'}</p>
    {enabling.scripts.length > 0 && <ul className="skills-script-list">{enabling.scripts.map(file => <li key={file.path}><code>{file.path}</code></li>)}</ul>}
    <button className="button secondary" disabled={busy === skill.name} onClick={() => void setEnabled(skill.name, true, enabling.hash)}>Turn on</button>
    <button className="text-link" autoFocus onClick={() => setEnabling(null)}>Cancel</button>
  </div>

  return <>
    <div className="page-intro network-overview-header job-header">
      <div><h1>Assistant skills</h1><p>A skill is a folder of instructions and scripts the assistant can load when it needs them. Skills start off; turn one on to let the assistant use it.</p></div>
      <div className="skills-header-actions">
        <button id="skill-new" className="button primary" aria-expanded={panel === 'create'} onClick={() => setPanel(panel === 'create' ? null : 'create')}><Icon name="plus" />New skill</button>
        <button className="button secondary" disabled={busy === 'import'} onClick={() => upload.current?.click()}>Upload .zip</button>
        <button className="button secondary" aria-expanded={panel === 'github'} onClick={() => setPanel(panel === 'github' ? null : 'github')}>From GitHub</button>
        <input ref={upload} type="file" accept=".zip,application/zip" className="visually-hidden" tabIndex={-1} aria-label="Skill .zip file"
          onChange={event => { const file = event.target.files?.[0]; if (file) void importZip(file) }} />
      </div>
    </div>
    {error && <p className="network-error" role="alert">{error}</p>}
    <p className="network-notice" role="status">{skills ? notice : 'Reading your skills…'}</p>
    {panel === 'create' && <CreateSkill request={request} kvmNodes={kvmNodes} onDone={name => { setPanel(null); if (name) onOpen(name) }} />}
    {panel === 'github' && <ImportGitHub request={request} onDone={name => { setPanel(null); if (name) onOpen(name) }} />}
    {review.length > 0 && <section className="surface network-section skills-review" aria-labelledby="skills-review-heading">
      <h2 id="skills-review-heading">Needs review <span className="skills-count">{review.length}</span></h2>
      <p className="section-note skills-tight">The assistant drafted {review.length === 1 ? 'this skill' : 'these skills'} from your chats. {review.length === 1 ? 'It stays' : 'They stay'} off until you read and turn {review.length === 1 ? 'it' : 'them'} on.</p>
      <ul className="skills-review-list">{review.map(skill => <li key={skill.name} className="skills-draft">
        <div className="skills-draft-head">
          <button className="text-link skills-name" onClick={() => onOpen(skill.name)}>{skill.name}</button>
          <ExecutionPill value={skill.execution} /><span className="network-cell-note">{tierLabel(skill.tier)} · {skill.scripts} {skill.scripts === 1 ? 'script' : 'scripts'}</span>
        </div>
        <p>{skill.description || 'No description yet.'}</p>
        {skill.rationale && <p className="skills-draft-why"><strong>Why: </strong>{skill.rationale}</p>}
        {skill.sourceChats.length > 0 && <p className="skills-draft-sources"><span>From {skill.sourceChats.length === 1 ? 'this chat' : 'these chats'}:</span>
          {skill.sourceChats.map((chat, index) => isChatId(chat)
            ? <button key={chat} className="text-link" onClick={() => openAssistantChat(chat)}>Chat {index + 1}<Hidden>{`for ${skill.name}`}</Hidden></button> : null)}</p>}
        {skill.warnings.length > 0 && <div className="network-warning" role="note"><strong>Look at these before turning it on:</strong>
          <ul>{skill.warnings.map(warning => <li key={warning}>{warning}</li>)}</ul></div>}
        {skill.problem && <span className="skills-problem" role="note"><Icon name="attention" />{skill.problem}</span>}
        <div className="network-actions">
          <button className="button primary" disabled={busy === skill.name || !!skill.problem} onClick={() => void toggle(skill)}>Enable<Hidden>{skill.name}</Hidden></button>
          <button className="button secondary" disabled={busy === skill.name} onClick={() => onOpen(skill.name)}>Open<Hidden>{skill.name}</Hidden></button>
          <button className="text-link" aria-expanded={removing === skill.name} onClick={() => setRemoving(removing === skill.name ? null : skill.name)}>Delete<Hidden>{skill.name}</Hidden></button>
        </div>
        {confirmEnable(skill)}
        {removing === skill.name && <div className="job-confirm skills-confirm" role="group" aria-label={`Delete ${skill.name}`}>
          <p>Delete this draft and its files? This can’t be undone.</p>
          <button className="button secondary" disabled={busy === skill.name} onClick={() => void removeDraft(skill.name)}>Delete draft</button>
          <button className="text-link" autoFocus onClick={() => setRemoving(null)}>Cancel</button>
        </div>}
      </li>)}</ul>
    </section>}
    {skills && !skills.some(skill => !skill.builtIn && !skill.needsReview) && <section className="surface network-section">
      <h2>No skills of your own yet</h2>
      <p>A skill could explain how to check ZFS pool health on your servers, with a script that does the checking. Create one, upload a package, or import a folder from GitHub. Built-in skills are listed below, and you can duplicate one to start from it.</p>
    </section>}
    {rest.length > 0 && <section className="surface network-section">
      <table className="network-table skills-table">
        <caption className="network-table-caption">Skills</caption>
        <thead><tr><th scope="col">Skill</th><th scope="col">Runs</th><th scope="col">Version</th><th scope="col">On</th></tr></thead>
        <tbody>{rest.map(skill => <tr key={skill.name}>
          <th scope="row">
            <button className="text-link skills-name" onClick={() => onOpen(skill.name)}>{skill.name}</button>
            {skill.builtIn && <> <span className="skills-pill skills-pill-builtin">Built in</span></>}
            <span className="network-cell-note">{skill.description || 'No description yet.'}</span>
            {skill.problem && <span className="skills-problem" role="note"><Icon name="attention" />{skill.problem}</span>}
          </th>
          <td data-label="Runs"><ExecutionPill value={skill.execution} /><span className="network-cell-note">{tierLabel(skill.tier)} · {skill.scripts} {skill.scripts === 1 ? 'script' : 'scripts'}</span></td>
          <td data-label="Version"><code className="skills-hash" title={skill.hash}>{shortHash(skill.hash)}</code><span className="network-cell-note">{dateFormat.format(new Date(skill.updated))}</span></td>
          <td data-label="On">
            {skill.builtIn ? <span className="status status-green"><Icon name="check" />Always on<Hidden>{skill.name}</Hidden></span> : <label className="skills-switch"><input type="checkbox" role="switch" checked={skill.enabled} disabled={busy === skill.name || (!skill.enabled && !!skill.problem)}
              onChange={() => void toggle(skill)} /><span>{skill.enabled ? 'On' : 'Off'}<Hidden>{skill.name}</Hidden></span></label>}
            {confirmEnable(skill)}
          </td>
        </tr>)}</tbody>
      </table>
    </section>}
  </>
}

function CreateSkill({ request, kvmNodes, onDone }: { request: Request; kvmNodes: string[]; onDone: (name?: string) => void }) {
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [execution, setExecution] = useState<SkillExecution>('node')
  const [tier, setTier] = useState<SkillTier>('read')
  const [domains, setDomains] = useState<string[]>([])
  const [touched, setTouched] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const domainsLabel = useId()
  const nameError = touched ? validateSkillName(name) : null
  async function save(event: FormEvent) {
    event.preventDefault()
    setTouched(true)
    if (validateSkillName(name)) return
    setBusy(true); setError(null)
    try {
      const detail = parseSkillDetail(await (await request('', 'POST', { name, description: description.trim(), execution, tier,
        ...(execution === 'sandbox' ? { allowedDomains: domains } : {}) })).json())
      onDone(detail.name)
    } catch (failure) { setError(failureText(failure, 'Lucia could not create this skill.')); setBusy(false) }
  }
  return <form className="surface network-section" onSubmit={event => void save(event)} noValidate>
    <h2>New skill</h2>
    <label className="job-field">Name<input value={name} maxLength={maxNameLength} disabled={busy} autoFocus autoComplete="off" spellCheck={false} aria-invalid={!!nameError || undefined}
      aria-describedby="skill-name-hint" onChange={event => setName(event.target.value)} onBlur={() => setTouched(true)} placeholder="check-zfs-health" />
      <span id="skill-name-hint" className={`ssh-key-hint${nameError ? ' skills-bad' : ''}`}>{nameError ?? 'Lowercase letters, numbers and hyphens. It can’t change later.'}</span></label>
    <label className="job-field">Description<textarea className="ssh-key-input" value={description} rows={2} maxLength={maxDescriptionLength} disabled={busy}
      onChange={event => setDescription(event.target.value)} placeholder="Checks ZFS pool health and scrub status on a server." aria-describedby="skill-description-hint" />
      <span id="skill-description-hint" className="ssh-key-hint">The assistant reads this to decide when to use the skill. Say what it does and when to reach for it.</span></label>
    <div className="network-fields">
      <ExecutionSelect value={execution} onChange={setExecution} disabled={busy} kvmNodes={kvmNodes} />
      <TierSelect value={tier} onChange={setTier} disabled={busy} />
    </div>
    {execution === 'sandbox' && <div className="job-field" role="group" aria-labelledby={domainsLabel}>
      <span id={domainsLabel}>Allowed domains</span><DomainInput value={domains} onChange={setDomains} disabled={busy} labelledBy={domainsLabel} /></div>}
    <div className="network-actions">
      <button className="button primary" disabled={busy}>{busy ? 'Creating…' : 'Create skill'}</button>
      <button type="button" className="text-link" disabled={busy} onClick={() => onDone()}>Cancel</button>
      {error && <p className="network-error" role="alert">{error}</p>}
    </div>
  </form>
}

function ImportGitHub({ request, onDone }: { request: Request; onDone: (name?: string) => void }) {
  const [url, setUrl] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function save(event: FormEvent) {
    event.preventDefault()
    setBusy(true); setError(null)
    try { onDone(parseSkillDetail(await (await request('/import-github', 'POST', { url: url.trim() })).json()).name) }
    catch (failure) { setError(failureText(failure, 'Lucia could not import this folder.')); setBusy(false) }
  }
  return <form className="surface network-section" onSubmit={event => void save(event)}>
    <h2>Import from GitHub</h2>
    <label className="job-field">Folder link<input type="url" value={url} required disabled={busy} autoFocus autoComplete="off" spellCheck={false} aria-describedby="skill-github-hint"
      onChange={event => setUrl(event.target.value)} placeholder="https://github.com/owner/repo/tree/main/skills/my-skill" />
      <span id="skill-github-hint" className="ssh-key-hint">Link to the folder that holds SKILL.md. Lucia fetches it from GitHub, and github.com must be on your allowed sites in Assistant settings. The skill starts off.</span></label>
    <div className="network-actions">
      <button className="button primary" disabled={busy || !url.trim()}>{busy ? 'Importing…' : 'Import skill'}</button>
      <button type="button" className="text-link" disabled={busy} onClick={() => onDone()}>Cancel</button>
      {error && <p className="network-error" role="alert">{error}</p>}
    </div>
  </form>
}

function ExecutionSelect({ value, onChange, disabled, kvmNodes }: { value: SkillExecution; onChange: (value: SkillExecution) => void; disabled?: boolean; kvmNodes: string[] }) {
  const hint = useId()
  return <label>Where scripts run<select value={value} disabled={disabled} aria-describedby={hint} onChange={event => onChange(event.target.value as SkillExecution)}>
    {executions.map(item => <option key={item.value} value={item.value} disabled={item.value === 'sandbox' && !kvmNodes.length && value !== 'sandbox'}>
      {item.label}{item.value === 'sandbox' && !kvmNodes.length ? ` (${noKvm})` : ''}</option>)}
  </select><span id={hint} className="ssh-key-hint">{value === 'sandbox' && !kvmNodes.length ? `${noKvm}, so sandbox scripts can’t run yet. ` : ''}
    {executions.find(item => item.value === value)?.hint}{value === 'sandbox' && kvmNodes.length ? ` It runs on ${kvmNodes[0]}.` : ''}</span></label>
}

function TierSelect({ value, onChange, disabled }: { value: SkillTier; onChange: (value: SkillTier) => void; disabled?: boolean }) {
  const hint = useId()
  return <label>What its scripts do<select value={value} disabled={disabled} aria-describedby={hint} onChange={event => onChange(event.target.value as SkillTier)}>
    {tiers.map(item => <option key={item.value} value={item.value}>{item.label}</option>)}
  </select><span id={hint} className="ssh-key-hint">{tiers.find(item => item.value === value)?.hint}</span></label>
}

type Draft = { description: string; execution: SkillExecution; tier: SkillTier; network: SkillNetwork; allowedDomains: string[]; body: string }
const draftOf = (detail: SkillDetail): Draft => ({ description: detail.description, execution: detail.execution, tier: detail.tier, network: detail.network, allowedDomains: detail.allowedDomains, body: detail.body })
const sameDraft = (a: Draft, b: Draft) => a.description === b.description && a.execution === b.execution && a.tier === b.tier && a.network === b.network && a.body === b.body
  && a.allowedDomains.join('\n') === b.allowedDomains.join('\n')

type OpenFile = { path: string; content: string; language: SkillLanguage | null; editable: boolean; size: number }

function SkillEditor({ name, request, refreshSession, onClose, onOpen }: Props & { name: string; request: Request; onClose: () => void; onOpen: (name: string) => void }) {
  const [detail, setDetail] = useState<SkillDetail | null>(null)
  const [duplicate, setDuplicate] = useState<string | null>(null)
  const [owned, setOwned] = useState<string[]>([])
  const [kvmNodes, setKvmNodes] = useState<string[]>([])
  const [draft, setDraft] = useState<Draft | null>(null)
  const [saved, setSaved] = useState<Draft | null>(null)
  // The editor opens the body once; edits flow through the draft so the editor never reloads under the user.
  const [bodyStart, setBodyStart] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [file, setFile] = useState<OpenFile | null>(null)
  const [fileText, setFileText] = useState('')
  const [fileSaved, setFileSaved] = useState('')
  const [diagnostics, setDiagnostics] = useState<SkillDiagnostic[]>([])
  const [check, setCheck] = useState<{ ok: boolean; checkedOn: string | null; problem: string | null; count: number } | null>(null)
  const [fileBusy, setFileBusy] = useState<'save' | 'check' | null>(null)
  const [fileError, setFileError] = useState<string | null>(null)
  const [checkJs, setCheckJs] = useState(false)
  const [renaming, setRenaming] = useState<{ path: string; to: string } | null>(null)
  const [deleting, setDeleting] = useState<string | null>(null)
  const heading = useRef<HTMLHeadingElement>(null)
  const domainsLabel = useId()

  const skillDirty = !!draft && !!saved && !sameDraft(draft, saved)
  const fileDirty = !!file && file.editable && fileText !== fileSaved
  const dirty = skillDirty || fileDirty
  useEffect(() => {
    if (!dirty) return
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault() }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty])

  const refresh = useCallback(async (signal?: AbortSignal) => {
    const value = parseSkillDetail(await (await request(`/${name}`, 'GET', undefined, signal)).json())
    if (!signal?.aborted) setDetail(value)
    return value
  }, [request, name])
  useEffect(() => {
    const controller = new AbortController()
    refresh(controller.signal).then(value => {
      if (controller.signal.aborted) return
      const start = draftOf(value)
      setDraft(start); setSaved(start); setBodyStart(value.body)
    }).catch(failure => { if (!controller.signal.aborted) setError(failureText(failure, 'Lucia could not read this skill.')) })
    request('', 'GET', undefined, controller.signal).then(response => response.json()).then(parseSkillList)
      .then(value => { if (!controller.signal.aborted) { setKvmNodes(value.kvmNodes); setOwned(value.skills.map(skill => skill.name)) } }).catch(() => { /* sandbox simply stays off */ })
    return () => controller.abort()
  }, [refresh, request])
  useEffect(() => heading.current?.focus(), [detail?.name])

  const leave = () => { if (!dirty || window.confirm(discard)) onClose() }
  const update = (change: Partial<Draft>) => { setNotice(''); setDraft(current => current && { ...current, ...change }) }

  async function saveSkill(event?: FormEvent) {
    event?.preventDefault()
    if (!draft || busy) return
    setBusy(true); setError(null); setNotice('')
    try {
      const value = parseSkillDetail(await (await request(`/${name}`, 'PUT', { ...draft, description: draft.description.trim(),
        allowedDomains: draft.execution === 'sandbox' ? draft.allowedDomains : [], network: draft.execution === 'spark' ? draft.network : 'none' })).json())
      setDetail(value)
      const next = draftOf(value)
      setSaved(next); setDraft(next)
      setNotice('Saved. The assistant sees the change from your next message.')
    } catch (failure) { setError(sandboxText(failure, 'Lucia could not save this skill.')) }
    finally { setBusy(false) }
  }

  async function openFile(path: string, force = false) {
    if (file?.path === path && !force) return
    if (!force && fileDirty && !window.confirm(discard)) return
    setFileError(null); setDiagnostics([]); setCheck(null)
    try {
      const value = parseSkillFileContent(await (await request(`/${name}/file?path=${encodeURIComponent(path)}`)).json())
      setFile(value); setFileText(value.content); setFileSaved(value.content)
    } catch (failure) { setFileError(failureText(failure, 'Lucia could not open this file.')) }
  }

  async function saveFile() {
    if (!file || !file.editable || fileBusy || fileText === fileSaved) return
    const content = fileText
    setFileBusy('save'); setFileError(null); setNotice('')
    try {
      parseSkillFile(await (await request(`/${name}/file?path=${encodeURIComponent(file.path)}`, 'PUT', { content })).json())
      setFileSaved(content)
      await refresh()
      setNotice(`${file.path} is saved. Its version changed, so earlier “always allow” choices for this skill no longer apply.`)
    } catch (failure) { setFileError(sandboxText(failure, 'Lucia could not save this file.')) }
    finally { setFileBusy(null) }
  }

  async function runCheck() {
    if (!file || fileBusy) return
    setFileBusy('check'); setFileError(null); setCheck(null)
    try {
      const value = parseSkillCheck(await (await request(`/${name}/check`, 'POST', { path: file.path, content: fileText })).json())
      setDiagnostics(value.diagnostics)
      setCheck({ ok: value.ok, checkedOn: value.checkedOn, problem: value.problem, count: value.diagnostics.length })
    } catch (failure) { setFileError(failureText(failure, 'Lucia could not check this file.')) }
    finally { setFileBusy(null) }
  }

  async function removeFile(path: string) {
    setFileError(null); setNotice('')
    try {
      await request(`/${name}/file?path=${encodeURIComponent(path)}`, 'DELETE')
      if (file?.path === path) { setFile(null); setDiagnostics([]); setCheck(null) }
      setDeleting(null)
      await refresh()
      setNotice(`${path} is deleted.`)
    } catch (failure) { setFileError(failureText(failure, 'Lucia could not delete this file.')) }
  }

  async function renameFile() {
    if (!renaming) return
    const folder = renaming.path.slice(0, renaming.path.lastIndexOf('/') + 1)
    if (!validFileName(renaming.to)) { setFileError('Use letters, numbers, dots, hyphens and underscores in a file name.'); return }
    if (file?.path === renaming.path && fileDirty && !window.confirm(discard)) return
    setFileError(null); setNotice('')
    try {
      const moved = parseSkillFile(await (await request(`/${name}/rename`, 'POST', { from: renaming.path, to: folder + renaming.to })).json())
      const wasOpen = file?.path === renaming.path
      setRenaming(null)
      await refresh()
      if (wasOpen) { setFile(null); setDiagnostics([]); setCheck(null); await openFile(moved.path, true) }
      setNotice(`Renamed to ${moved.path}.`)
    } catch (failure) { setFileError(sandboxText(failure, 'Lucia could not rename this file.')) }
  }

  async function addFile(path: string, content: string) {
    if (fileDirty && !window.confirm(discard)) return false
    setFileError(null); setNotice('')
    try {
      await request(`/${name}/file?path=${encodeURIComponent(path)}`, 'PUT', { content })
      await refresh()
      setFile(null)
      await openFile(path, true)
      return true
    } catch (failure) { setFileError(sandboxText(failure, 'Lucia could not add this file.')); return false }
  }

  async function exportZip() {
    setError(null)
    try {
      const response = await fetch(`/api/assistant/skills/${name}/export`, { credentials: 'same-origin', cache: 'no-store' })
      if (response.status === 401) void refreshSession()
      if (!response.ok) throw await responseError(response)
      const url = URL.createObjectURL(await response.blob())
      const link = document.createElement('a')
      link.href = url; link.download = `${name}.zip`
      link.click()
      URL.revokeObjectURL(url)
    } catch (failure) { setError(failureText(failure, 'Lucia could not export this skill.')) }
  }

  async function duplicateSkill(event: FormEvent) {
    event.preventDefault()
    const copy = (duplicate ?? '').trim()
    const invalid = validateSkillName(copy)
    if (invalid) { setError(invalid); return }
    setBusy(true); setError(null)
    try { onOpen(parseSkillDetail(await (await request(`/${name}/duplicate`, 'POST', { name: copy })).json()).name) }
    catch (failure) { setError(failureText(failure, 'Lucia could not copy this skill.')); setBusy(false) }
  }

  async function deleteSkill() {
    setBusy(true); setError(null)
    try { await request(`/${name}`, 'DELETE'); window.dispatchEvent(new CustomEvent(skillsChangedEvent)); onClose() }
    catch (failure) { setError(failureText(failure, 'Lucia could not delete this skill.')); setBusy(false) }
  }

  if (!detail || !draft) return <>
    <div className="page-intro"><h1 ref={heading} tabIndex={-1}>{name}</h1><p>{error ? '' : 'Reading this skill…'}</p></div>
    {error && <p className="network-error" role="alert">{error}</p>}
    <button className="text-link" onClick={onClose}><Icon name="back" />All skills</button>
  </>

  const builtIn = detail.builtIn
  const locked = busy || builtIn
  const blockers = draft.execution === 'sandbox' ? sandboxBlockers(detail.files) : []
  const editorLanguage = file ? file.language ?? languageOf(file.path) : null
  const isScript = !!file && file.path.startsWith('scripts/')
  const canCheck = isScript && editorLanguage !== null && editorLanguage !== 'markdown'
  return <>
    <div className="page-intro network-overview-header job-header">
      <div>
        <button className="text-link skills-back" onClick={leave}><Icon name="back" />All skills</button>
        <h1 ref={heading} tabIndex={-1}>{detail.name}</h1>
        <p>{builtIn ? <span className="skills-pill skills-pill-builtin">Built in</span>
          : <span className={`status status-${detail.enabled ? 'green' : 'muted'}`}><Icon name={detail.enabled ? 'check' : 'clock'} />{detail.enabled ? 'On' : 'Off'}</span>}
          {' · '}<ExecutionPill value={detail.execution} />{' · '}version <code className="skills-hash" title={detail.hash}>{shortHash(detail.hash)}</code></p>
        {builtIn && <p className="ssh-key-hint">Built-in skills ship with Lucia, are always on, and can’t be changed. Duplicate one to make your own version.</p>}
        {detail.author === 'assistant' && <p className="ssh-key-hint">Drafted by the assistant{detail.needsReview ? '. It stays off until you review it and turn it on from the skills list.' : '.'}</p>}
        {detail.needsReview && detail.rationale && <p className="ssh-key-hint"><strong>Why: </strong>{detail.rationale}</p>}
        {detail.enabled && detail.nodes.length > 0 && <p className="ssh-key-hint">Downloaded to {detail.nodes.filter(item => item.synced).length} of {detail.nodes.length} servers: {detail.nodes.map(item => `${item.node} (${item.synced ? 'ready' : 'waiting'})`).join(', ')}.</p>}
      </div>
    </div>
    {detail.problem && <p className="network-warning" role="note"><strong>The assistant can’t use this skill yet.</strong> {detail.problem}</p>}
    {detail.needsReview && detail.warnings.length > 0 && <div className="network-warning" role="note"><strong>Look at these before turning it on:</strong>
      <ul>{detail.warnings.map(warning => <li key={warning}>{warning}</li>)}</ul></div>}
    {error && <p className="network-error" role="alert">{error}</p>}
    <p className="network-notice" role="status">{notice}</p>

    <form className="surface network-section" onSubmit={event => void saveSkill(event)}>
      <h2>Settings</h2>
      <label className="job-field">Description<textarea className="ssh-key-input" value={draft.description} rows={2} maxLength={maxDescriptionLength} disabled={locked}
        onChange={event => update({ description: event.target.value })} aria-describedby="skill-description-hint" />
        <span id="skill-description-hint" className="ssh-key-hint">The assistant reads this to decide when to use the skill.</span></label>
      <div className="network-fields">
        <ExecutionSelect value={draft.execution} onChange={execution => update({ execution })} disabled={locked} kvmNodes={kvmNodes} />
        <TierSelect value={draft.tier} onChange={tier => update({ tier })} disabled={locked} />
      </div>
      {draft.execution === 'spark' && <label className="network-checkbox"><input type="checkbox" checked={draft.network === 'bridge'} disabled={locked}
        onChange={event => update({ network: event.target.checked ? 'bridge' : 'none' })} />
        <span>Allow network access. Scripts run in a throwaway container with no network unless you allow it; C# <code>#:package</code> restores need it.</span></label>}
      {draft.execution === 'sandbox' && <div className="job-field" role="group" aria-labelledby={domainsLabel}>
        <span id={domainsLabel}>Allowed domains</span>
        <DomainInput value={draft.allowedDomains} onChange={allowedDomains => update({ allowedDomains })} disabled={locked} labelledBy={domainsLabel} /></div>}
      {blockers.length > 0 && <p className="network-warning" role="alert"><strong>The sandbox runs JavaScript and Python only.</strong> Change or remove these scripts first: {blockers.join(', ')}.</p>}
      <h2 className="skills-subhead">Instructions <code>SKILL.md</code></h2>
      <p className="section-note skills-tight">What the assistant reads once it loads the skill. Keep it short and concrete, and point to scripts and references by path.</p>
      <Editor height={360} modelKey="SKILL.md" value={bodyStart} language="markdown" label="SKILL.md instructions" readOnly={locked}
        onChange={body => update({ body })} onSave={() => void saveSkill()} />
      {!builtIn && <div className="network-actions">
        <button className="button primary" disabled={busy || !skillDirty || blockers.length > 0}>{busy ? 'Saving…' : 'Save skill'}</button>
        {skillDirty && <span className="status status-amber"><Icon name="attention" />Unsaved changes</span>}
      </div>}
    </form>

    <section className="surface network-section">
      <h2>Files</h2>
      <p className="section-note skills-tight">{builtIn ? 'The files this built-in skill ships with.' : <>Scripts go in <code>scripts/</code>. Saving a file changes the skill’s version.</>}</p>
      {fileError && <p className="network-error" role="alert">{fileError}</p>}
      <div className="skills-files">
        <div className="skills-tree">
          {fileGroups.map(group => {
            const files = detail.files.filter(item => item.kind === group.kind)
            return <div key={group.kind} className="skills-group">
              <h3>{group.label}</h3>
              {!files.length ? <p className="section-note skills-tight">None{builtIn ? '' : ' yet'}.</p> : <ul>{files.map(item => {
                const unsupported = detail.execution === 'sandbox' && item.kind === 'script' && !/\.(js|mjs|py)$/i.test(item.path)
                return <li key={item.path} className={file?.path === item.path ? 'skills-open' : undefined}>
                  {renaming?.path === item.path ? <form className="skills-rename" onSubmit={event => { event.preventDefault(); void renameFile() }}>
                    <input value={renaming.to} autoFocus aria-label={`New name for ${item.path}`} autoComplete="off" spellCheck={false}
                      onChange={event => setRenaming({ path: item.path, to: event.target.value })} onKeyDown={event => { if (event.key === 'Escape') setRenaming(null) }} />
                    <button className="text-link">Rename</button>
                    <button type="button" className="text-link" onClick={() => setRenaming(null)}>Cancel</button>
                  </form> : <>
                    <button className="skills-file" aria-current={file?.path === item.path ? 'true' : undefined} onClick={() => void openFile(item.path)}>
                      <span>{fileName(item.path)}</span><span className="network-cell-note">{languageLabel(item.language)} · {sizeText(item.size)}{!item.editable ? ' · read-only' : ''}</span></button>
                    {unsupported && <span className="skills-problem"><Icon name="attention" />Not supported in the sandbox</span>}
                    <span className="skills-file-actions">
                      {!builtIn && <button className="text-link" onClick={() => setRenaming({ path: item.path, to: fileName(item.path) })}>Rename<Hidden>{item.path}</Hidden></button>}
                      {!builtIn && <button className="text-link" aria-expanded={deleting === item.path} onClick={() => setDeleting(deleting === item.path ? null : item.path)}>Delete<Hidden>{item.path}</Hidden></button>}
                    </span>
                    {deleting === item.path && <span className="job-confirm skills-confirm" role="group" aria-label={`Delete ${item.path}`}>
                      <span>Delete {item.path}?</span>
                      <button className="button secondary" onClick={() => void removeFile(item.path)}>Delete file</button>
                      <button className="text-link" autoFocus onClick={() => setDeleting(null)}>Cancel</button></span>}
                  </>}
                </li>
              })}</ul>}
            </div>
          })}
          {!builtIn && <AddFile execution={detail.execution} files={detail.files} onAdd={addFile} />}
        </div>
        <div className="skills-pane">
          {!file ? <p className="skills-empty">Pick a file to view or edit it.</p> : <>
            <div className="skills-pane-bar">
              <div><strong>{file.path}</strong><span className="network-cell-note">{languageLabel(editorLanguage)}{!file.editable ? ' · Too large or not text, so it opens read-only' : ''}</span></div>
              <div className="skills-pane-actions">
                {(editorLanguage === 'javascript') && <label className="skills-inline-check"><input type="checkbox" checked={checkJs} onChange={event => setCheckJs(event.target.checked)} />Type-check JavaScript</label>}
                {canCheck && <button className="button secondary" disabled={fileBusy !== null} onClick={() => void runCheck()}>{fileBusy === 'check' ? 'Checking…' : 'Check'}</button>}
                {file.editable && !builtIn && <button className="button primary" disabled={fileBusy !== null || !fileDirty} onClick={() => void saveFile()}>{fileBusy === 'save' ? 'Saving…' : 'Save file'}</button>}
              </div>
            </div>
            <Editor height={420} modelKey={`${detail.name}/${file.path}`} value={file.content} language={editorLanguage} label={`Editing ${file.path}`} readOnly={!file.editable || builtIn}
              checkJs={checkJs} diagnostics={diagnostics} onChange={setFileText} onSave={() => void saveFile()} />
            <p className="section-note skills-tight">{fileDirty ? <span className="status status-amber"><Icon name="attention" />Unsaved changes. Press Ctrl+S or ⌘S to save.</span>
              : file.editable && !builtIn ? 'Press Ctrl+S or ⌘S to save.' : ''}</p>
            <p className="section-note skills-tight" role="status">{check === null ? '' : check.problem ? check.problem
              : check.ok ? `No problems found${check.checkedOn ? ` (checked on ${check.checkedOn})` : ''}.`
                : `${check.count} ${check.count === 1 ? 'problem' : 'problems'} found${check.checkedOn ? ` (checked on ${check.checkedOn})` : ''}. They’re marked in the editor.`}</p>
            {diagnostics.length > 0 && <ul className="skills-diagnostics">{diagnostics.map((item, index) => <li key={index} className={item.severity === 'warning' ? 'skills-warning' : 'skills-bad'}>
              Line {item.line}: {item.message}</li>)}</ul>}
          </>}
        </div>
      </div>
    </section>

    <section className="surface network-section">
      <h2>Package</h2>
      <p className="section-note skills-tight">Export a standard Agent Skills package that works in other tools too.{detail.source ? ` Source: ${detail.source}.` : ''}</p>
      <div className="network-actions">
        <button className="button secondary" onClick={() => void exportZip()}>Export .zip</button>
        <button className="button secondary" aria-expanded={duplicate !== null} disabled={busy}
          onClick={() => setDuplicate(duplicate === null ? copyName(detail.name, owned) : null)}>{builtIn ? 'Duplicate to edit' : 'Duplicate'}</button>
        {!builtIn && <button className="button secondary" aria-expanded={confirmDelete} disabled={busy} onClick={() => setConfirmDelete(!confirmDelete)}><Icon name="trash" />Delete skill</button>}
      </div>
      {duplicate !== null && <form className="job-confirm skills-confirm" aria-label={`Duplicate ${detail.name}`} onSubmit={event => void duplicateSkill(event)}>
        <label className="job-field">Name for the copy<input value={duplicate} maxLength={maxNameLength} disabled={busy} autoFocus autoComplete="off" spellCheck={false}
          aria-describedby="skill-copy-hint" onChange={event => setDuplicate(event.target.value)} />
          <span id="skill-copy-hint" className="ssh-key-hint">The copy is yours to edit. It starts off, like any new skill.</span></label>
        <button className="button secondary" disabled={busy}>{busy ? 'Copying…' : 'Create copy'}</button>
        <button type="button" className="text-link" disabled={busy} onClick={() => setDuplicate(null)}>Cancel</button>
      </form>}
      {!builtIn && confirmDelete && <div className="job-confirm skills-confirm" role="group" aria-label={`Delete ${detail.name}`}>
        <p>Delete {detail.name} and all its files? This can’t be undone.</p>
        <button className="button secondary" disabled={busy} onClick={() => void deleteSkill()}>Delete skill</button>
        <button className="text-link" autoFocus onClick={() => setConfirmDelete(false)}>Cancel</button>
      </div>}
    </section>
  </>
}

function AddFile({ execution, files, onAdd }: { execution: SkillExecution; files: SkillFile[]; onAdd: (path: string, content: string) => Promise<boolean | undefined> }) {
  const [kind, setKind] = useState<SkillFileKind>('script')
  const choices = languageChoices(execution)
  const [language, setLanguage] = useState<SkillLanguage>('javascript')
  const [text, setText] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const folder = fileGroups.find(group => group.kind === kind)!.folder
  // A sandbox skill offers JavaScript and Python only; fall back when the chosen language isn't allowed.
  const active = choices.find(item => item.id === language) ?? choices[0]
  const hint = useId()
  async function add(event: FormEvent) {
    event.preventDefault()
    const stem = text.trim()
    const base = kind === 'script' ? `${stem.replace(/\.[A-Za-z0-9]+$/, '')}${active.extension}` : stem
    if (!validFileName(base)) { setError('Use letters, numbers, dots, hyphens and underscores in a file name.'); return }
    if (kind !== 'script' && !/\.[A-Za-z0-9]+$/.test(base)) { setError('Add an extension, like notes.md.'); return }
    const path = folder + base
    if (files.some(item => item.path === path)) { setError(`${path} already exists.`); return }
    setBusy(true); setError(null)
    const done = await onAdd(path, kind === 'script' ? starterScript(active.id) : '')
    setBusy(false)
    if (done) setText('')
  }
  return <form className="skills-add" onSubmit={event => void add(event)}>
    <h3>Add a file</h3>
    <label>Type<select value={kind} disabled={busy} onChange={event => { setKind(event.target.value as SkillFileKind); setError(null) }}>
      {fileGroups.map(group => <option key={group.kind} value={group.kind}>{group.label.replace(/s$/, '')}</option>)}</select></label>
    {kind === 'script' && <label>Language<select value={active.id} disabled={busy} aria-describedby={hint} onChange={event => setLanguage(event.target.value as SkillLanguage)}>
      {choices.map(item => <option key={item.id} value={item.id}>{item.label}</option>)}</select>
      {execution === 'sandbox' && <span id={hint} className="ssh-key-hint">The sandbox runs JavaScript and Python only.</span>}</label>}
    <label>Name<input value={text} disabled={busy} autoComplete="off" spellCheck={false} aria-invalid={!!error || undefined}
      onChange={event => { setText(event.target.value); setError(null) }} placeholder={kind === 'script' ? 'check' : 'notes.md'} />
      <span className="ssh-key-hint">{folder}{kind === 'script' ? `${(text.trim().replace(/\.[A-Za-z0-9]+$/, '') || 'name')}${active.extension}` : text.trim() || 'name.md'}</span></label>
    {error && <p className="network-error" role="alert">{error}</p>}
    <button className="button secondary" disabled={busy || !text.trim()}><Icon name="plus" />Add file</button>
  </form>
}
