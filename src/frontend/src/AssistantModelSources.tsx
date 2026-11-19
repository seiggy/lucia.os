import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { modelSourcesChanged, parseSourceSettings } from './assistant'
import type { AssistantConnection, AssistantSource } from './assistant'
import './NetworkSettings.css'

type ConnectionDraft = AssistantConnection & { apiKey?: string }
const failureText = (failure: unknown) => failure instanceof TypeError
  ? 'Lucia could not reach the host. Check your connection and try again.'
  : failure instanceof Error ? failure.message : 'Model sources are unavailable.'

export function AssistantModelSources({ session, refreshSession }: { session: AuthenticationSession; refreshSession: () => Promise<void> }) {
  const [connections, setConnections] = useState<ConnectionDraft[]>([])
  const [sources, setSources] = useState<AssistantSource[]>([])
  const [disabled, setDisabled] = useState<string[]>([])
  const [query, setQuery] = useState('')
  const [loading, setLoading] = useState(true)
  const [loaded, setLoaded] = useState(false)
  const [busy, setBusy] = useState(false)
  const [dirty, setDirty] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [round, setRound] = useState(0)
  const pending = useRef<AbortController | null>(null)

  useEffect(() => {
    if (!session.isOwner) return
    const controller = new AbortController()
    setLoading(true); setError('')
    void ownerRequest(session, refreshSession, '/api/assistant/sources', 'GET', undefined, controller.signal)
      .then(response => response.json()).then(parseSourceSettings).then(value => {
        if (controller.signal.aborted) return
        setConnections(value.connections); setSources(value.sources); setDisabled(value.disabledModels)
        setLoaded(true); setDirty(false)
      }).catch(failure => { if (!controller.signal.aborted) setError(failureText(failure)) })
      .finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => { controller.abort(); pending.current?.abort() }
  }, [session, refreshSession, round])

  function changeConnection(id: string, change: Partial<ConnectionDraft>) {
    setConnections(current => current.map(connection => connection.id === id ? { ...connection, ...change } : connection))
    setDirty(true); setNotice('')
  }

  function enableModels(ids: string[], enabled: boolean) {
    setDisabled(current => enabled ? current.filter(id => !ids.includes(id)) : [...new Set([...current, ...ids])])
    setDirty(true); setNotice('')
  }

  async function save(event: FormEvent) {
    event.preventDefault()
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller
    setBusy(true); setError(''); setNotice('')
    try {
      await ownerRequest(session, refreshSession, '/api/assistant/sources', 'PUT', {
        connections: connections.map(({ id, name, baseUrl, apiKey }) => ({ id, name, baseUrl, ...(apiKey !== undefined ? { apiKey } : {}) })),
        disabledModels: disabled,
      }, controller.signal)
      if (controller.signal.aborted) return
      // Discard typed credentials as soon as the host has saved them, even if discovery fails.
      setConnections(current => current.map(({ apiKey, ...connection }) => ({
        ...connection, hasKey: apiKey === undefined ? connection.hasKey : !!apiKey.trim(),
      })))
      setDirty(false)
      setNotice('Saved. Model choices apply to the next message and future job runs. Existing turns keep running.')
      window.dispatchEvent(new Event(modelSourcesChanged))
      setRound(current => current + 1)
    } catch (failure) {
      if (!controller.signal.aborted) setError(failureText(failure))
    } finally {
      if (pending.current === controller) pending.current = null
      if (!controller.signal.aborted) setBusy(false)
    }
  }

  if (!session.isOwner) return <div className="page-intro"><h1>Owner access is needed.</h1><p>Only lab owners can configure assistant model sources.</p></div>
  const locked = loading || busy || !loaded
  return <>
    <div className="page-intro">
      <h1>Assistant model sources</h1>
      <p>Connect Heimdall or another OpenAI-compatible API, then choose which models your assistant may use. These settings belong to your Lucia account. GitHub connections and models stay in the assistant panel.</p>
      <p><a className="text-link" href="#/settings/assistant">Assistant permissions and reasoning</a></p>
    </div>
    <p className="network-notice" role="status">{loading ? 'Reading model sources...' : ''}</p>
    {!loaded && error && <p className="network-error" role="alert">{error}</p>}
    <div className="network-actions">
      <button type="button" className="button secondary" disabled={loading || busy || dirty} onClick={() => setRound(current => current + 1)}>Refresh models</button>
      {dirty && <p className="section-note">You have unsaved changes. Save to refresh models from your connections.</p>}
    </div>
    {loaded && <form onSubmit={event => void save(event)}>
      <section className="surface network-section">
        <h2>Connections</h2>
        <p className="section-note">Use the API base URL including its version path, such as <code>https://gateway.example.com/v1</code>. For Heimdall, use a client API key, not your portal sign-in. Upstream provider credentials stay in the gateway.</p>
        <p className="section-note">Keys are encrypted on the Lucia host and never returned to this page. Only connect services you trust: selected models receive chat messages and tool results. HTTP sends these and your key without encryption.</p>
        {connections.map(connection => <fieldset className="assistant-source-connection" key={connection.id} disabled={locked}>
          <legend>{connection.name.trim() || 'New connection'}</legend>
          <div className="network-fields">
            <label>Name<input value={connection.name} maxLength={80} required autoComplete="off"
              onChange={event => changeConnection(connection.id, { name: event.target.value })} /></label>
            <label>API base URL<input type="url" value={connection.baseUrl} maxLength={2048} required autoComplete="off" spellCheck={false}
              placeholder="https://gateway.example.com/v1" onChange={event => changeConnection(connection.id, { baseUrl: event.target.value })} /></label>
          </div>
          <label className="network-custom-url">API key{connection.hasKey ? ' (saved; leave blank to keep)' : ' (optional for unauthenticated APIs)'}
            <input type="password" value={connection.apiKey ?? ''} maxLength={4096} autoComplete="new-password" spellCheck={false}
              disabled={connection.apiKey === ''} onChange={event => changeConnection(connection.id, { apiKey: event.target.value || undefined })} />
          </label>
          {connection.hasKey && <label className="network-checkbox">
            <input type="checkbox" checked={connection.apiKey === ''} onChange={event => changeConnection(connection.id, { apiKey: event.target.checked ? '' : undefined })} />
            <span>Clear the saved key when saving</span>
          </label>}
          <div className="network-actions"><button type="button" className="text-link" onClick={() => {
            setConnections(current => current.filter(item => item.id !== connection.id)); setDirty(true); setNotice('')
          }}>Remove connection</button></div>
        </fieldset>)}
        {!connections.length && <p className="section-note">No extra connections yet. LiteLLM and Local AI are discovered from your lab below.</p>}
        <button type="button" className="button secondary" disabled={locked || connections.length >= 20} onClick={() => {
          setConnections(current => [...current, { id: crypto.randomUUID().replaceAll('-', '').slice(0, 12), name: '', baseUrl: '', hasKey: false }])
          setDirty(true); setNotice('')
        }}>Add connection</button>
        <p className="section-note">Save a new connection to load its models. Changing a URL requires entering its key again or clearing the saved key. Removing a connection takes effect only when you save. Manage the built-in LiteLLM and Local AI connections in <a className="text-link" href="#/apps">Apps</a>.</p>
      </section>
      <section className="surface network-section">
        <h2>Available models</h2>
        <p className="section-note">Checked models appear in chats and jobs. Uncheck one to block future turns, including saved jobs that use it. New models are enabled when the source first lists them.</p>
        <label className="network-custom-url">Find a model<input type="search" value={query} onChange={event => setQuery(event.target.value)} /></label>
        {sources.filter(source => !source.id.startsWith('byok:') || connections.some(connection => source.id === `byok:${connection.id}`)).map(source => {
          const known = new Set(source.models.map(model => model.id))
          const missing = disabled.filter(id => id.startsWith(source.id + (source.id.startsWith('byok:') ? '/' : ':')) && !known.has(id))
          const models = [...source.models, ...missing.map(id => ({ id, name: `${id} (unavailable)` }))]
            .filter(model => `${model.name} ${model.id}`.toLowerCase().includes(query.toLowerCase()))
          return <fieldset key={source.id} className="assistant-source-models" disabled={locked}>
            <legend>{source.name}</legend>
            {source.reason && <p className="section-note" role="status">{source.reason}</p>}
            {!models.length ? <p className="section-note">{query ? 'No matching models.' : 'No chat models available.'}</p> : <>
              <div className="network-actions">
                <button type="button" className="text-link" onClick={() => enableModels(models.map(model => model.id), true)}>Enable shown</button>
                <button type="button" className="text-link" onClick={() => enableModels(models.map(model => model.id), false)}>Disable shown</button>
              </div>
              {models.map(model => <label key={model.id} className="network-checkbox">
                <input type="checkbox" checked={!disabled.includes(model.id)} onChange={event => enableModels([model.id], event.target.checked)} />
                <span>{model.name}</span>
              </label>)}
            </>}
          </fieldset>
        })}
      </section>
      <div className="network-actions">
        <button className="button primary" disabled={locked || !dirty}>{busy ? 'Saving...' : 'Save model sources'}</button>
        <p className="network-notice" role="status">{!loading && notice}</p>
        {error && <p className="network-error" role="alert">{error}</p>}
      </div>
    </form>}
  </>
}
