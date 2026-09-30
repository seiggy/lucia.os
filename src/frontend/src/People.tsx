import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { Icon } from './Icon'
import {
  MEMBERS, NAME_PATTERN, OWNERS, describeChange, generatePassword, initials, parseChange, parseDirectory, passwordProblems, pendingFor,
  type Directory, type DirectoryApp, type DirectoryGroup, type DirectoryRequest, type DirectoryUser,
} from './directory'
import './NetworkSettings.css'
import './People.css'

type View = 'people' | 'groups' | 'apps'
type Submit = (request: DirectoryRequest) => Promise<string | null>
type Shared = { username: string; password: string; id: string }

const groupNote = (group: DirectoryGroup) => group.description || 'No description.'

export function People({ session, refreshSession, view }: { session: AuthenticationSession; refreshSession: () => Promise<void>; view: View }) {
  const [directory, setDirectory] = useState<Directory | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState('')
  const [mine, setMine] = useState<Set<string>>(() => new Set())
  const [dismissed, setDismissed] = useState<Set<string>>(() => new Set())
  const announced = useRef(new Set<string>())
  const mounted = useRef(true)

  const load = useCallback(async (signal?: AbortSignal) => {
    const value = parseDirectory(await (await ownerRequest(session, refreshSession, '/api/host/directory', 'GET', undefined, signal)).json())
    if (!signal?.aborted && mounted.current) { setDirectory(value); setError(null) }
  }, [session, refreshSession])

  useEffect(() => {
    mounted.current = true
    const controller = new AbortController()
    if (!session.isOwner) { setLoading(false); return }
    void load(controller.signal)
      .catch(failure => { if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'People are unavailable.') })
      .finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => { mounted.current = false; controller.abort() }
  }, [session, load])

  const pending = directory?.changes.filter(change => change.state === 'pending') ?? []
  useEffect(() => {
    if (pending.length === 0) return
    const timer = window.setTimeout(() => { void load().catch(() => undefined) }, 2000)
    return () => window.clearTimeout(timer)
  }, [directory, pending.length, load])

  useEffect(() => {
    for (const change of directory?.changes ?? [])
      if (mine.has(change.id) && change.state === 'done' && !announced.current.has(change.id)) {
        announced.current.add(change.id)
        setNotice(`${describeChange(change, directory?.apps)}: done.`)
      }
  }, [directory, mine])

  const submit: Submit = useCallback(async request => {
    setError(null); setNotice('')
    try {
      const change = parseChange(await (await ownerRequest(session, refreshSession, '/api/host/directory/changes', 'POST', request)).json())
      if (!mounted.current) return null
      setMine(current => new Set(current).add(change.id))
      setDirectory(current => current && { ...current, changes: [change, ...current.changes] })
      return change.id
    } catch (failure) {
      if (mounted.current) setError(failure instanceof Error ? failure.message : 'The change could not be queued.')
      return null
    }
  }, [session, refreshSession])

  if (!session.isOwner) return <div className="page-intro"><h1>Owner access is needed.</h1><p>Only lab owners can manage people.</p></div>
  const failures = directory?.changes.filter(change => change.state === 'failed' && mine.has(change.id) && !dismissed.has(change.id)) ?? []
  const counts = directory ? { people: directory.users.length, groups: directory.groups.length } : null
  return <>
    <div className="page-intro"><h1>People</h1>
      <p>Everyone who can sign in to your lab. One account and password covers Lucia, the apps their groups can open, and, for owners, your servers.</p></div>
    <nav className="model-library-navigation" aria-label="People navigation">
      <a href="#/settings/people" aria-current={view === 'people' ? 'page' : undefined}><Icon name="user" />People{counts && <span className="people-count">{counts.people}</span>}</a>
      <a href="#/settings/people/groups" aria-current={view === 'groups' ? 'page' : undefined}><Icon name="tasks" />Groups{counts && <span className="people-count">{counts.groups}</span>}</a>
      <a href="#/settings/people/apps" aria-current={view === 'apps' ? 'page' : undefined}><Icon name="shield" />App access</a>
    </nav>
    {error && <p className="network-error" role="alert">{error}</p>}
    {failures.map(change => <div key={change.id} className="people-failure" role="alert">
      <Icon name="attention" />
      <p><strong>{describeChange(change, directory?.apps)} didn't finish.</strong> {change.message ?? 'The identity service refused the change.'}</p>
      <button className="text-link" onClick={() => setDismissed(current => new Set(current).add(change.id))}>Dismiss</button>
    </div>)}
    <p className="network-notice people-status" role="status">
      {pending.length > 0 ? <><span className="people-pulse" aria-hidden="true" />Applying {pending.length === 1 ? 'a change' : `${pending.length} changes`}. This usually takes a few seconds, longer when Authentik resyncs.</>
        : notice || (loading ? 'Reading your directory…' : '')}
    </p>
    {!loading && directory && !directory.ready && directory.users.length === 0
      ? <section className="surface network-section"><h2>Waiting for the identity service</h2>
        <p className="section-note">Lucia's identity service on the Spark publishes your directory about once a minute. If this doesn't clear, check that <code>lucia-domain-activation</code> is running on the Spark.</p></section>
      : directory && (view === 'people' ? <PeopleList directory={directory} submit={submit} />
        : view === 'groups' ? <GroupList directory={directory} submit={submit} />
          : <AppAccess directory={directory} submit={submit} />)}
  </>
}

function OwnerWarning({ name, confirmed, setConfirmed }: { name: string; confirmed: boolean; setConfirmed: (value: boolean) => void }) {
  return <div className="network-warning people-owner-warning">
    <p><strong>Owners have full control.</strong> {name || 'This person'} could change this Spark, sign in to every server with sudo, run and remove apps,
      change your domain, and edit or delete any account, including yours. They also become Authentik administrators.</p>
    <label className="network-checkbox"><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} />
      I trust {name || 'this person'} with all of that.</label>
  </div>
}

function GroupChoices({ groups, chosen, locked, onChange, disabled, titled }: {
  groups: DirectoryGroup[]; chosen: string[]; locked?: string[]; onChange: (value: string[]) => void; disabled?: boolean; titled?: boolean
}) {
  return <fieldset className="people-choices" disabled={disabled}><legend className={titled ? 'people-hidden' : undefined}>Groups</legend>
    {groups.map(group => <label key={group.name} className="network-checkbox people-choice">
      <input type="checkbox" checked={chosen.includes(group.name)} disabled={locked?.includes(group.name)}
        onChange={event => onChange(event.target.checked ? [...chosen, group.name] : chosen.filter(name => name !== group.name))} />
      <span><strong>{group.name}</strong><span>{groupNote(group)}</span></span>
    </label>)}
  </fieldset>
}

function PasswordField({ value, onChange, label, disabled }: { value: string; onChange: (value: string) => void; label: string; disabled?: boolean }) {
  const [shown, setShown] = useState(false)
  const problems = value ? passwordProblems(value) : []
  return <div className="people-password">
    <label>{label}
      <span className="people-password-row">
        <input type={shown ? 'text' : 'password'} value={value} onChange={event => onChange(event.target.value)} autoComplete="new-password"
          spellCheck={false} maxLength={256} required disabled={disabled} aria-describedby="people-password-rule" />
        <button type="button" className="button secondary people-inline-button" disabled={disabled} onClick={() => setShown(!shown)}>{shown ? 'Hide' : 'Show'}</button>
        <button type="button" className="button secondary people-inline-button" disabled={disabled}
          onClick={() => { onChange(generatePassword()); setShown(true) }}><Icon name="refresh" />Generate</button>
      </span>
    </label>
    <p id="people-password-rule" className={`people-hint${problems.length ? ' people-hint-warn' : ''}`}>
      {problems.length ? `Still needs ${problems.join(', ')}.` : '14 or more characters with an uppercase letter, a lowercase letter, a digit and a symbol.'}</p>
  </div>
}

function SharePassword({ username, password, passwordChange, onDone }: { username: string; password: string; passwordChange: boolean; onDone: () => void }) {
  const [copied, setCopied] = useState(false)
  return <div className="people-share" role="status">
    <p><strong>Give {username} this password privately.</strong> Lucia won't show it again.
      {passwordChange ? ' They can change it in Authentik under Settings once they sign in.' : ''}</p>
    <div className="people-share-row"><code>{password}</code>
      <button className="button secondary people-inline-button" onClick={() => {
        void navigator.clipboard?.writeText(password).then(() => setCopied(true), () => undefined)
      }}><Icon name={copied ? 'check' : 'copy'} />{copied ? 'Copied' : 'Copy'}</button>
      <button className="text-link" onClick={onDone}>Done</button></div>
  </div>
}

function PeopleList({ directory, submit }: { directory: Directory; submit: Submit }) {
  const [adding, setAdding] = useState(false)
  const [open, setOpen] = useState<string | null>(null)
  const [shared, setShared] = useState<Shared | null>(null)
  return <section className="surface network-section people-section">
    <div className="people-heading"><h2>Accounts</h2>
      {!adding && <button className="button primary" onClick={() => { setAdding(true); setOpen(null) }}><Icon name="user" />Add person</button>}</div>
    {shared && directory.changes.find(change => change.id === shared.id)?.state !== 'failed' && <SharePassword {...shared} passwordChange={directory.passwordChange} onDone={() => setShared(null)} />}
    {adding && <AddPerson directory={directory} submit={submit} onClose={() => setAdding(false)} onShare={setShared} />}
    <ul className="people-list">
      {directory.users.map(user => {
        const saving = pendingFor(directory.changes, user.username)
        const expanded = open === user.username
        return <li key={user.username} className={`people-row${expanded ? ' people-row-open' : ''}${user.active ? '' : ' people-row-off'}`}>
          <div className="people-row-main">
            <span className="people-avatar" aria-hidden="true">{initials(user.name)}</span>
            <div className="people-identity">
              <strong>{user.name}{user.username === directory.actor && <span className="people-you">you</span>}</strong>
              <span>{user.username}{user.email ? ` · ${user.email}` : ''}</span>
            </div>
            <div className="people-tags">
              {!user.active && <span className="people-tag people-tag-off">Disabled</span>}
              {!user.synced && <span className="people-tag">Not in Authentik yet</span>}
              {user.groups.map(name => <span key={name} className={`people-tag${name === OWNERS ? ' people-tag-owner' : ''}`}>{name}</span>)}
            </div>
            {saving ? <span className="people-saving"><span className="people-pulse" aria-hidden="true" />Saving</span>
              : <button className="text-link people-manage" aria-expanded={expanded} onClick={() => { setOpen(expanded ? null : user.username); setAdding(false) }}>
                {expanded ? 'Close' : 'Manage'}<Icon name={expanded ? 'down' : 'chevron'} /></button>}
          </div>
          {expanded && !saving && <PersonEditor key={user.username} user={user} directory={directory} submit={submit}
            onClose={() => setOpen(null)} onShare={setShared} />}
        </li>
      })}
    </ul>
    {directory.users.length === 0 && <p className="section-note">No one is in the directory yet.</p>}
  </section>
}

function AddPerson({ directory, submit, onClose, onShare }: {
  directory: Directory; submit: Submit; onClose: () => void; onShare: (value: Shared) => void
}) {
  const [username, setUsername] = useState('')
  const [name, setName] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState(() => generatePassword())
  const [groups, setGroups] = useState<string[]>(directory.groups.some(group => group.name === MEMBERS) ? [MEMBERS] : [])
  const [trusted, setTrusted] = useState(false)
  const [busy, setBusy] = useState(false)
  const taken = directory.users.some(user => user.username === username) || directory.groups.some(group => group.name === username)
  const owner = groups.includes(OWNERS)
  const valid = NAME_PATTERN.test(username) && !taken && name.trim() && passwordProblems(password).length === 0 && (!owner || trusted)
  const send = async (event: FormEvent) => {
    event.preventDefault()
    if (!valid) return
    setBusy(true)
    const id = await submit({ action: 'createUser', username, name: name.trim(), email: email.trim(), password, groups })
    if (id) {
      onShare({ username, password, id }); onClose()
    }
    setBusy(false)
  }
  return <form className="people-editor people-add" onSubmit={send}>
    <h3>Add a person</h3>
    <div className="network-fields">
      <label>Username<input value={username} onChange={event => setUsername(event.target.value.toLowerCase().trim())} maxLength={32}
        autoComplete="off" spellCheck={false} required disabled={busy} placeholder="sam" aria-describedby="people-username-rule" autoFocus /></label>
      <label>Full name<input value={name} onChange={event => setName(event.target.value)} maxLength={64} required disabled={busy} placeholder="Sam Lee" /></label>
    </div>
    <p id="people-username-rule" className={`people-hint${username && (!NAME_PATTERN.test(username) || taken) ? ' people-hint-warn' : ''}`}>
      {taken ? 'That name is already used.' : 'Lowercase letters, digits, - or _, starting with a letter. It can’t be changed later.'}</p>
    <div className="network-fields">
      <label>Email <span className="ssh-key-hint">(optional)</span><input type="email" value={email} onChange={event => setEmail(event.target.value)}
        maxLength={254} disabled={busy} placeholder="sam@example.com" /></label>
      <PasswordField label="Temporary password" value={password} onChange={setPassword} disabled={busy} />
    </div>
    <GroupChoices groups={directory.groups} chosen={groups} onChange={setGroups} disabled={busy} />
    {owner && <OwnerWarning name={name.trim() || username} confirmed={trusted} setConfirmed={setTrusted} />}
    <div className="network-actions">
      <button className="button primary" disabled={busy || !valid}>{busy ? 'Adding…' : 'Add person'}</button>
      <button type="button" className="text-link" disabled={busy} onClick={onClose}>Cancel</button>
    </div>
  </form>
}

function EditorPart({ title, children }: { title: string; children: ReactNode }) {
  return <div className="people-part"><h3>{title}</h3>{children}</div>
}

function PersonEditor({ user, directory, submit, onClose, onShare }: {
  user: DirectoryUser; directory: Directory; submit: Submit; onClose: () => void; onShare: (value: Shared) => void
}) {
  const [name, setName] = useState(user.name)
  const [email, setEmail] = useState(user.email ?? '')
  const [groups, setGroups] = useState(user.groups)
  const [password, setPassword] = useState('')
  const [trusted, setTrusted] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [busy, setBusy] = useState(false)
  const self = user.username === directory.actor
  const guarded = user.protected || self
  const newOwner = groups.includes(OWNERS) && !user.groups.includes(OWNERS)
  const run = async (request: DirectoryRequest, after?: (id: string) => void) => {
    setBusy(true)
    const id = await submit(request)
    if (id) { after?.(id); onClose() }
    setBusy(false)
  }
  const same = (a: string[], b: string[]) => a.length === b.length && a.every(item => b.includes(item))
  return <div className="people-editor">
    {guarded && <p className="people-guard"><Icon name="shield" />
      {user.protected ? 'Lucia’s first owner can’t be disabled, deleted, or removed from lucia-owners.' : 'You can’t disable or delete your own account, or leave lucia-owners.'}</p>}
    <div className="people-parts">
      <EditorPart title="Details">
        <form onSubmit={event => { event.preventDefault(); void run({ action: 'updateUser', username: user.username, name: name.trim(), email: email.trim() }) }}>
          <label>Full name<input value={name} onChange={event => setName(event.target.value)} maxLength={64} required disabled={busy} /></label>
          <label>Email <span className="ssh-key-hint">(optional)</span><input type="email" value={email} onChange={event => setEmail(event.target.value)} maxLength={254} disabled={busy} /></label>
          <button className="button secondary" disabled={busy || !name.trim() || (name.trim() === user.name && email.trim() === (user.email ?? ''))}>Save details</button>
        </form>
      </EditorPart>
      <EditorPart title="Groups">
        <form onSubmit={event => { event.preventDefault(); void run({ action: 'setUserGroups', username: user.username, groups }) }}>
          <GroupChoices groups={directory.groups} chosen={groups} onChange={setGroups} disabled={busy} titled locked={guarded && user.groups.includes(OWNERS) ? [OWNERS] : []} />
          {newOwner && <OwnerWarning name={user.name} confirmed={trusted} setConfirmed={setTrusted} />}
          <button className="button secondary" disabled={busy || same(groups, user.groups) || (newOwner && !trusted)}>Save groups</button>
        </form>
      </EditorPart>
      <EditorPart title="Password">
        <form onSubmit={event => {
          event.preventDefault()
          void run({ action: 'setPassword', username: user.username, password }, id => onShare({ username: user.username, password, id }))
        }}>
          <PasswordField label="New password" value={password} onChange={setPassword} disabled={busy} />
          <button className="button secondary" disabled={busy || !password || passwordProblems(password).length > 0}>Set password</button>
        </form>
        <p className="people-hint">This replaces their password everywhere they sign in.</p>
      </EditorPart>
      <EditorPart title="Access">
        <p className="people-hint">{user.active ? 'Disabling blocks every new sign-in to Lucia, apps and servers. You can enable the account again later.'
          : 'This account can’t sign in anywhere until you enable it.'}</p>
        <div className="network-actions people-access">
          <button className="button secondary" disabled={busy || (guarded && user.active)}
            onClick={() => void run({ action: 'setUserActive', username: user.username, active: !user.active })}>
            {user.active ? 'Disable account' : 'Enable account'}</button>
          {!deleting ? <button className="text-link people-danger" disabled={busy || guarded} onClick={() => setDeleting(true)}>Delete {user.username}</button>
            : <span className="people-confirm"><button className="button people-danger-button" disabled={busy}
              onClick={() => void run({ action: 'deleteUser', username: user.username })}>Delete permanently</button>
              <button className="text-link" disabled={busy} onClick={() => setDeleting(false)}>Keep</button></span>}
        </div>
        {deleting && <p className="people-hint people-hint-warn">Their account, groups and app access go away, along with any SSH keys they added. Files they own on servers stay.</p>}
      </EditorPart>
    </div>
  </div>
}

function GroupList({ directory, submit }: { directory: Directory; submit: Submit }) {
  const [adding, setAdding] = useState(false)
  const [open, setOpen] = useState<string | null>(null)
  const appsFor = (name: string) => directory.apps.filter(app => app.groups.includes(name)).map(app => app.name)
  return <section className="surface network-section people-section">
    <div className="people-heading"><h2>Groups</h2>
      {!adding && <button className="button primary" onClick={() => { setAdding(true); setOpen(null) }}>New group</button>}</div>
    <p className="section-note people-lead">Groups decide who can open which apps. Put people in groups, then choose each group's apps under App access.</p>
    {adding && <AddGroup directory={directory} submit={submit} onClose={() => setAdding(false)} />}
    <ul className="people-list">
      {directory.groups.map(group => {
        const saving = pendingFor(directory.changes, group.name)
        const expanded = open === group.name
        const apps = appsFor(group.name)
        return <li key={group.name} className={`people-row${expanded ? ' people-row-open' : ''}`}>
          <div className="people-row-main people-group-main">
            <span className="people-avatar people-avatar-group" aria-hidden="true"><Icon name="tasks" /></span>
            <div className="people-identity">
              <strong>{group.name}{group.protected && <span className="people-you">Lucia</span>}</strong>
              <span>{groupNote(group)}</span>
            </div>
            <div className="people-group-facts">
              <span><strong>{group.members.length}</strong> {group.members.length === 1 ? 'person' : 'people'}</span>
              <span>{apps.length ? `Opens ${apps.join(', ')}` : 'No apps yet'}</span>
            </div>
            {saving ? <span className="people-saving"><span className="people-pulse" aria-hidden="true" />Saving</span>
              : <button className="text-link people-manage" aria-expanded={expanded} onClick={() => { setOpen(expanded ? null : group.name); setAdding(false) }}>
                {expanded ? 'Close' : 'Manage'}<Icon name={expanded ? 'down' : 'chevron'} /></button>}
          </div>
          {expanded && !saving && <GroupEditor key={group.name} group={group} directory={directory} submit={submit} onClose={() => setOpen(null)} />}
        </li>
      })}
    </ul>
  </section>
}

function AddGroup({ directory, submit, onClose }: { directory: Directory; submit: Submit; onClose: () => void }) {
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [busy, setBusy] = useState(false)
  const taken = directory.groups.some(group => group.name === name) || directory.users.some(user => user.username === name)
  const valid = NAME_PATTERN.test(name) && !taken
  return <form className="people-editor people-add" onSubmit={async event => {
    event.preventDefault()
    if (!valid) return
    setBusy(true)
    if (await submit({ action: 'createGroup', group: name, description: description.trim() })) onClose()
    setBusy(false)
  }}>
    <h3>New group</h3>
    <div className="network-fields">
      <label>Name<input value={name} onChange={event => setName(event.target.value.toLowerCase().trim())} maxLength={32} autoComplete="off"
        spellCheck={false} required disabled={busy} placeholder="family" autoFocus /></label>
      <label>Description <span className="ssh-key-hint">(optional)</span><input value={description} onChange={event => setDescription(event.target.value)}
        maxLength={200} disabled={busy} placeholder="Photos and media for the household" /></label>
    </div>
    <p className={`people-hint${name && !valid ? ' people-hint-warn' : ''}`}>
      {taken ? 'That name is already used.' : 'Lowercase letters, digits, - or _. Servers see this name too, so it can’t be one they already use, like sudo.'}</p>
    <div className="network-actions">
      <button className="button primary" disabled={busy || !valid}>{busy ? 'Creating…' : 'Create group'}</button>
      <button type="button" className="text-link" disabled={busy} onClick={onClose}>Cancel</button>
    </div>
  </form>
}

function GroupEditor({ group, directory, submit, onClose }: { group: DirectoryGroup; directory: Directory; submit: Submit; onClose: () => void }) {
  const [description, setDescription] = useState(group.description)
  const [members, setMembers] = useState(group.members)
  const [trusted, setTrusted] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [busy, setBusy] = useState(false)
  const locked = group.name === OWNERS ? directory.users.filter(user => user.protected || user.username === directory.actor).map(user => user.username) : []
  const added = group.name === OWNERS ? members.filter(name => !group.members.includes(name)) : []
  const changed = members.length !== group.members.length || members.some(name => !group.members.includes(name))
  const run = async (request: DirectoryRequest) => {
    setBusy(true)
    if (await submit(request)) onClose()
    setBusy(false)
  }
  const addedNames = added.map(name => directory.users.find(user => user.username === name)?.name ?? name).join(' and ')
  return <div className="people-editor">
    <div className="people-parts">
      <EditorPart title="Members">
        <form onSubmit={event => { event.preventDefault(); void run({ action: 'setGroupMembers', group: group.name, members }) }}>
          <fieldset className="people-choices" disabled={busy}><legend className="people-hidden">Members of {group.name}</legend>
            {directory.users.map(user => <label key={user.username} className="network-checkbox people-choice">
              <input type="checkbox" checked={members.includes(user.username)} disabled={locked.includes(user.username)}
                onChange={event => setMembers(event.target.checked ? [...members, user.username] : members.filter(name => name !== user.username))} />
              <span><strong>{user.name}</strong><span>{user.username}</span></span>
            </label>)}
          </fieldset>
          {added.length > 0 && <OwnerWarning name={addedNames} confirmed={trusted} setConfirmed={setTrusted} />}
          <button className="button secondary" disabled={busy || !changed || (added.length > 0 && !trusted)}>Save members</button>
        </form>
      </EditorPart>
      {group.kind === 'directory' && !group.protected && <EditorPart title="About">
        <form onSubmit={event => { event.preventDefault(); void run({ action: 'updateGroup', group: group.name, description: description.trim() }) }}>
          <label>Description<input value={description} onChange={event => setDescription(event.target.value)} maxLength={200} disabled={busy} /></label>
          <button className="button secondary" disabled={busy || description.trim() === group.description}>Save description</button>
        </form>
        {!group.protected && <div className="network-actions">
          {!deleting ? <button className="text-link people-danger" disabled={busy} onClick={() => setDeleting(true)}>Delete {group.name}</button>
            : <span className="people-confirm"><button className="button people-danger-button" disabled={busy}
              onClick={() => void run({ action: 'deleteGroup', group: group.name })}>Delete group</button>
              <button className="text-link" disabled={busy} onClick={() => setDeleting(false)}>Keep</button></span>}
        </div>}
        {deleting && <p className="people-hint people-hint-warn">Members stay, but lose the apps this group opened for them.</p>}
      </EditorPart>}
      {group.name === OWNERS && <p className="people-hint">Owners have full control of Lucia and open every onboarded app. Lucia's first owner always stays in this group.</p>}
      {group.kind === 'lucia' && <p className="people-hint">Members can open Lucia and use its local AI. Lucia keeps this group in Authentik rather than the directory.</p>}
    </div>
  </div>
}

function AppAccess({ directory, submit }: { directory: Directory; submit: Submit }) {
  const [draft, setDraft] = useState<Record<string, string[]>>({})
  const pending = useMemo(() => new Set(directory.changes.filter(change => change.state === 'pending').map(change => change.target)), [directory.changes])
  useEffect(() => {
    // Drop local choices once the published directory has caught up.
    setDraft(current => Object.fromEntries(Object.entries(current).filter(([slug]) => pending.has(slug))))
  }, [pending])
  const groups = directory.groups
  const toggle = async (app: DirectoryApp, name: string, on: boolean) => {
    const current = draft[app.slug] ?? app.groups
    const next = on ? [...new Set([...current, name])] : current.filter(item => item !== name)
    setDraft(value => ({ ...value, [app.slug]: next }))
    if (!await submit({ action: 'setAppGroups', app: app.slug, groups: next.filter(item => item !== OWNERS) }))
      setDraft(value => ({ ...value, [app.slug]: current }))
  }
  const onboarded = directory.apps.filter(app => !app.fixed)
  return <section className="surface network-section people-section">
    <h2>App access</h2>
    <p className="section-note people-lead">Tick a group to let its members open an app. Owners can always open everything. Only apps that sign in with Lucia appear here.</p>
    <div className="people-matrix-wrap">
      <table className="people-matrix">
        <caption className="network-table-caption">Which groups can open each app</caption>
        <thead><tr><th scope="col">App</th>{groups.map(group => <th key={group.name} scope="col">{group.name}</th>)}</tr></thead>
        <tbody>
          {directory.apps.map(app => {
            const chosen = draft[app.slug] ?? app.groups
            const saving = pending.has(app.slug)
            return <tr key={app.slug} className={app.fixed ? 'people-matrix-fixed' : ''}>
              <th scope="row">
                <span className="people-app-name">{app.launchUrl ? <a href={app.launchUrl} target="_blank" rel="noreferrer">{app.name}</a> : app.name}</span>
                <span className="people-app-note">{app.fixed ? 'Lucia decides these' : saving ? <><span className="people-pulse" aria-hidden="true" />Saving</> : `${chosen.length} ${chosen.length === 1 ? 'group' : 'groups'}`}</span>
              </th>
              {groups.map(group => {
                const locked = app.fixed || group.name === OWNERS
                return <td key={group.name} data-label={group.name}>
                  <label className="people-cell">
                    <input type="checkbox" checked={chosen.includes(group.name)} disabled={locked}
                      aria-label={`${group.name} can open ${app.name}`} onChange={event => void toggle(app, group.name, event.target.checked)} />
                    <span className="people-hidden">{group.name}</span>
                  </label>
                </td>
              })}
            </tr>
          })}
        </tbody>
      </table>
    </div>
    {onboarded.length === 0 && <p className="section-note">No apps sign in with Lucia yet. Apps that support it, like Grafana and Immich, appear here once you turn on Lucia sign-in when installing them and your domain is active.</p>}
  </section>
}
