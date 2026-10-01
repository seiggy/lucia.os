import { useEffect, useId, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { parseSettings, toolLabel } from './assistant'
import type { AssistantTool } from './assistant'
import './NetworkSettings.css'

const failureText = (failure: unknown, fallback: string) => failure instanceof TypeError
  ? 'Lucia could not reach the host. Check your connection and try again.' : failure instanceof Error ? failure.message : fallback
// A pasted link keeps just its host name.
function siteName(entry: string) {
  try { return /^https?:\/\//i.test(entry) ? new URL(entry).hostname : entry } catch { return entry }
}

export function AssistantSettings({ session, refreshSession }: { session: AuthenticationSession; refreshSession: () => Promise<void> }) {
  const [tools, setTools] = useState<AssistantTool[]>([])
  const [auto, setAuto] = useState<string[]>([])
  const [sites, setSites] = useState('')
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [badSites, setBadSites] = useState(false)
  const [notice, setNotice] = useState('')
  const pending = useRef<AbortController | null>(null)
  const sitesField = useRef<HTMLTextAreaElement>(null)
  const changesHeading = useId()
  const sitesHint = useId()
  const saveAlert = useId()
  // The field is disabled while saving, so it takes focus once the refusal has re-enabled it.
  useEffect(() => { if (badSites && !busy) sitesField.current?.focus() }, [badSites, busy])
  useEffect(() => {
    const controller = new AbortController()
    if (!session.isOwner) { setLoading(false); return }
    void ownerRequest(session, refreshSession, '/api/assistant/settings', 'GET', undefined, controller.signal)
      .then(response => response.json()).then(parseSettings).then(value => {
        if (!controller.signal.aborted) { setTools(value.tools); setAuto(value.autoTools); setSites(value.hosts.join('\n')) }
      }).catch(failure => { if (!controller.signal.aborted) setError(failureText(failure, 'Assistant settings are unavailable.')) })
      .finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => { controller.abort(); pending.current?.abort() }
  }, [session, refreshSession])

  async function save(event: FormEvent) {
    event.preventDefault()
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller
    setBusy(true); setSaveError(null); setBadSites(false); setNotice('')
    try {
      const value = parseSettings(await (await ownerRequest(session, refreshSession, '/api/assistant/settings', 'PUT',
        { autoTools: auto, hosts: sites.split(/[\s,]+/).filter(Boolean).map(siteName) }, controller.signal)).json())
      if (controller.signal.aborted) return
      setTools(value.tools); setAuto(value.autoTools); setSites(value.hosts.join('\n'))
      setNotice('Saved. The assistant follows these from your next message.')
    } catch (failure) {
      if (controller.signal.aborted) return
      setSaveError(failureText(failure, 'Lucia could not save your settings.'))
      setBadSites((failure as { code?: string } | undefined)?.code === 'invalid_sites')
    } finally {
      if (pending.current === controller) pending.current = null
      if (!controller.signal.aborted) setBusy(false)
    }
  }
  const toggle = (name: string) => { setNotice(''); setAuto(current => current.includes(name) ? current.filter(item => item !== name) : [...current, name]) }

  if (!session.isOwner) return <div className="page-intro"><h1>Owner access is needed.</h1><p>Only lab owners can choose what the assistant may do.</p></div>
  const changes = tools.filter(tool => tool.tier === 'change')
  return <>
    <div className="page-intro"><h1>Assistant</h1><p>The assistant checks your servers, apps and logs on its own, and asks in the chat before it changes anything. Choose what it may do without asking.</p></div>
    {error && <p className="network-error" role="alert">{error}</p>}
    <p className="network-notice" role="status">{loading ? 'Reading your settings…' : ''}</p>
    <form onSubmit={event => void save(event)}>
      <section className="surface network-section">
        <h2 id={changesHeading}>Changes it may make without asking</h2>
        <p className="section-note">Anything left unchecked waits for your approval. In Plan mode it changes nothing at all.</p>
        <div className="assistant-settings-tools" role="group" aria-labelledby={changesHeading}>
          {changes.map(tool => <label key={tool.name} className="network-checkbox">
            <input type="checkbox" checked={auto.includes(tool.name)} disabled={busy} onChange={() => toggle(tool.name)} />
            <span>{toolLabel(tool)}</span>
          </label>)}
        </div>
        <p className="section-note">It always asks before it deletes an app, restores a backup, puts an app on the internet or takes it off, updates or restarts a server, or runs a command on one.</p>
      </section>
      <section className="surface network-section">
        <h2>Sites it may read without asking</h2>
        <label className="assistant-settings-sites">Allowed sites<textarea ref={sitesField} className="ssh-key-input" value={sites} rows={5} autoComplete="off"
          spellCheck={false} disabled={busy} aria-invalid={badSites || undefined} aria-describedby={badSites ? `${sitesHint} ${saveAlert}` : sitesHint}
          onChange={event => { setNotice(''); setSites(event.target.value) }} placeholder="docs.docker.com" /></label>
        <p id={sitesHint} className="section-note">One host name per line, like docs.docker.com; a subdomain needs its own line. For any other site it asks first. It reads public sites only, never your own network.</p>
      </section>
      {/* Beside Save, not at the top: the page outgrows a laptop screen. */}
      <div className="network-actions">
        <button className="button primary" disabled={busy || loading || !tools.length}>{busy ? 'Saving…' : 'Save settings'}</button>
        {saveError && <p id={saveAlert} className="network-error" role="alert">{saveError}</p>}
        <p className="network-notice" role="status">{notice}</p>
      </div>
    </form>
  </>
}
