import { useCallback, useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { parseManagedNodes } from './onboarding'
import { Icon } from './Icon'
import { bytes } from './sparkTelemetry'
import { chatContextRange, downloadProgress, parseContextPlan, parseLocalModels, parseModelStatus, parseProviderStatus, parseRepository, parseSearch, parseServing, record } from './modelManagement'
import type { ContextPlan, LocalModel, ModelChoice, ModelKind, ModelStatus, ProviderStatus, RepositoryModels, SearchItem, ServingStatus } from './modelManagement'
import './ModelManager.css'

type Access = { session: AuthenticationSession; refreshSession: () => Promise<void>; view?: 'library' | 'find'; server?: string }
type Action = { label: string; description: string; path: string; method: string; body?: unknown; successMessage?: string }

export function ModelManager(props: Access) {
  if (!props.session.isOwner) return <section className="reading-surface">
    <h1>Owner access is needed.</h1><p>Your Lucia owner manages the models shared by the lab.</p>
    <a className="text-link" href="#/ai">Back to Local AI <Icon name="arrow" /></a>
  </section>
  const { server, ...rest } = props
  return server ? <ServerModels {...rest} server={server} /> : <Models {...rest} />
}

/** A managed server's Local AI, reached through the host at /api/host/nodes/{id}/ai. */
function ServerModels({ server, ...props }: Access & { server: string }) {
  const [nodeId, setNodeId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    const controller = new AbortController()
    void ownerRequest(props.session, props.refreshSession, '/api/host/nodes', 'GET', undefined, controller.signal).then(response => response.json()).then(parseManagedNodes)
      .then(nodes => { if (controller.signal.aborted) return; const node = nodes.find(item => item.hostname === server); if (node) setNodeId(node.nodeId); else setError(`${server} isn't a managed server.`) })
      .catch(failure => { if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'Lucia could not read your servers.') })
    return () => controller.abort()
  }, [props.session, props.refreshSession, server])
  if (nodeId) return <Models {...props} server={{ hostname: server, base: `/api/host/nodes/${encodeURIComponent(nodeId)}/ai`,
    serving: `/api/host/nodes/${encodeURIComponent(nodeId)}/ai-serving` }} />
  return <div className="page-intro"><h1>Models on {server}</h1>{error ? <p className="model-manager-error" role="alert">{error}</p> : <p role="status">Finding {server}…</p>}</div>
}

/** A parameter count the way model cards write it: 600M, 8B, 1.2T. */
function parameterCount(count: number) {
  const [scale, unit] = count >= 1e12 ? [1e12, 'T'] : count >= 1e9 ? [1e9, 'B'] : [1e6, 'M']
  return `${Number((count / scale).toPrecision(2))}${unit}`
}

function ContextEstimate({ plan }: { plan: ContextPlan }) {
  const reserved = plan.osReserveBytes + plan.servicesReserveBytes + plan.runtimeReserveBytes
  return <div className="model-estimate">
    <h3>Estimated context capacity</h3>
    <p><strong>{plan.memoryLimitedContextTokens.toLocaleString()} tokens</strong> fit the current memory plan, up to the model’s {plan.model.nativeContextTokens.toLocaleString()}-token limit.</p>
    <dl className="fact-list">
      <div><dt>Model allocation estimate</dt><dd>{bytes(plan.residentWeightBytes)}</dd></div>
      <div><dt>OS, services, and runtime reserve</dt><dd>{bytes(reserved)}</dd></div>
      <div><dt>Voice reserve</dt><dd>{bytes(plan.voiceReserveBytes)}</dd></div>
      <div><dt>Other loaded model</dt><dd>{bytes(plan.otherResidentBytes)}</dd></div>
      <div><dt>Remaining cache budget</dt><dd>{bytes(plan.cacheBudgetBytes)}</dd></div>
    </dl>
    <p className="section-note">{plan.qualification}</p>
  </div>
}

function Models({ session, refreshSession, view = 'library', server }: Omit<Access, 'server'> & { server?: { hostname: string; base: string; serving: string } }) {
  const base = server?.base ?? '/api/host'
  const home = server ? `#/ai/models/on/${server.hostname}` : '#/ai/models'
  const where = server?.hostname ?? 'your Spark'
  const [models, setModels] = useState<LocalModel[]>([])
  const [status, setStatus] = useState<ModelStatus | null>(null)
  const [serving, setServing] = useState<ServingStatus | null>(null)
  const [serveContext, setServeContext] = useState('')
  // vLLM serves one model the owner picks here; llama.cpp's router serves the whole library; Lucia Inference loads models itself.
  const engine = serving?.engine ?? 'lucia'
  const vllm = engine === 'vllm'
  const router = engine === 'llamacpp'
  const dedicated = engine !== 'lucia'
  const engineName = vllm ? 'vLLM' : 'llama.cpp'
  const [provider, setProvider] = useState<ProviderStatus | null>(null)
  const [providerLoading, setProviderLoading] = useState(true)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  const [token, setToken] = useState('')
  const [query, setQuery] = useState('')
  const [kind, setKind] = useState<ModelKind>('Chat')
  const searchKind: ModelKind = dedicated ? 'Chat' : kind
  const [results, setResults] = useState<SearchItem[] | null>(null)
  const [limitReached, setLimitReached] = useState(false)
  const [searching, setSearching] = useState(false)
  const [repositoryInput, setRepositoryInput] = useState('')
  const [repository, setRepository] = useState<RepositoryModels | null>(null)
  const [file, setFile] = useState('')
  const [advanced, setAdvanced] = useState(false)
  const [preview, setPreview] = useState<ContextPlan | null>(null)
  const [previewMessage, setPreviewMessage] = useState('')
  const [previewing, setPreviewing] = useState(false)
  const [selected, setSelected] = useState<string | null>(null)
  const [plan, setPlan] = useState<ContextPlan | null>(null)
  const [contextTokens, setContextTokens] = useState(8192)
  const contextRange = plan ? chatContextRange(plan) : null
  const [estimating, setEstimating] = useState(false)
  const [confirmation, setConfirmation] = useState<Action | null>(null)
  const reading = useRef<AbortController | null>(null)
  const browsing = useRef<AbortController | null>(null)
  const writing = useRef<AbortController | null>(null)
  const estimate = useRef<AbortController | null>(null)
  const remoteEstimate = useRef<AbortController | null>(null)
  const alive = useRef(true)
  const confirmationPanel = useRef<HTMLElement>(null)
  const previousFocus = useRef<HTMLElement | null>(null)
  const choice = repository?.choices.find(item => item.file === file)
  const selectedModel = models.find(model => model.id === selected)

  const request = useCallback((path: string, method = 'GET', body?: unknown, signal?: AbortSignal) =>
    ownerRequest(session, refreshSession, path, method, body, signal), [session, refreshSession])
  const refresh = useCallback(async (force = false) => {
    if (reading.current || (!force && (writing.current || document.visibilityState === 'hidden'))) return
    const controller = new AbortController()
    reading.current = controller
    try {
      const [inventory, runtime, engine] = await Promise.all([
        request(`${base}/models`, 'GET', undefined, controller.signal),
        request(`${base}/status`, 'GET', undefined, controller.signal),
        server ? request(server.serving, 'GET', undefined, controller.signal) : null,
      ])
      const nextModels = parseLocalModels(await inventory.json())
      const nextStatus = parseModelStatus(await runtime.json())
      const nextServing = engine && parseServing(await engine.json())
      if (alive.current && !controller.signal.aborted) { setModels(nextModels); setStatus(nextStatus); setServing(nextServing); setError(null) }
    } catch (failure) {
      if (alive.current && !controller.signal.aborted)
        setError(failure instanceof Error ? failure.message : 'The model library could not be read.')
    } finally {
      if (reading.current === controller) reading.current = null
      if (alive.current && !controller.signal.aborted) setLoading(false)
    }
  }, [request, base, server])
  useEffect(() => {
    alive.current = true
    const controller = new AbortController()
    if (server) setProviderLoading(false)
    else void request('/api/host/huggingface/credentials', 'GET', undefined, controller.signal)
      .then(response => response.json()).then(parseProviderStatus)
      .then(value => { if (alive.current && !controller.signal.aborted) setProvider(value) })
      .catch(failure => { if (alive.current && !controller.signal.aborted) setActionError(failure instanceof Error ? failure.message : 'Hugging Face status is unavailable.') })
      .finally(() => { if (alive.current && !controller.signal.aborted) setProviderLoading(false) })
    void refresh()
    const poll = setInterval(() => { void refresh() }, 5000)
    const visible = () => { if (document.visibilityState !== 'hidden') void refresh() }
    document.addEventListener('visibilitychange', visible)
    return () => {
      alive.current = false; clearInterval(poll); controller.abort()
      document.removeEventListener('visibilitychange', visible)
      reading.current?.abort(); reading.current = null
      browsing.current?.abort(); writing.current?.abort(); estimate.current?.abort(); remoteEstimate.current?.abort()
    }
  }, [request, refresh, server])
  useEffect(() => {
    if (confirmation) {
      previousFocus.current = document.activeElement instanceof HTMLElement ? document.activeElement : null
      confirmationPanel.current?.focus()
      confirmationPanel.current?.scrollIntoView({ block: 'center', behavior: 'instant' })
    } else if (previousFocus.current?.isConnected) {
      previousFocus.current.focus()
      previousFocus.current = null
    }
  }, [confirmation])
  useEffect(() => { setConfirmation(null) }, [view])
  useEffect(() => {
    remoteEstimate.current?.abort()
    setPreview(null); setPreviewMessage('')
    // The pre-download estimate uses the Spark's memory plan, so it's only shown for the Spark.
    if (!repository || !file || server) { setPreviewing(false); return }
    const controller = new AbortController()
    remoteEstimate.current = controller
    setPreviewing(true)
    const parameters = new URLSearchParams({ repository: repository.repository, revision: repository.revision, file, kind: repository.kind })
    void request('/api/host/huggingface/context-preview?' + parameters, 'GET', undefined, controller.signal)
      .then(response => response.json()).then(value => {
        const result = record(value)
        if (result.available === true) return { plan: parseContextPlan(result.plan), reason: '' }
        if (result.available !== false || typeof result.reason !== 'string') throw new Error('The context preview was invalid.')
        return { plan: null, reason: result.reason }
      }).then(value => {
        if (alive.current && !controller.signal.aborted) { setPreview(value.plan); setPreviewMessage(value.reason) }
      }).catch(failure => {
        if (alive.current && !controller.signal.aborted) setPreviewMessage(failure instanceof Error ? failure.message : 'A pre-download estimate is unavailable.')
      }).finally(() => { if (alive.current && !controller.signal.aborted) setPreviewing(false) })
    return () => controller.abort()
  }, [repository, file, request, server])

  async function mutate(action: Action) {
    if (writing.current) return
    if (action.path.startsWith(`${base}/models`) && (loading || error)) {
      setActionError('Refresh the host status before changing models.')
      return
    }
    const controller = new AbortController()
    writing.current = controller
    reading.current?.abort(); reading.current = null
    setBusy(true); setActionError(null); setNotice('')
    try {
      const response = await request(action.path, action.method, action.body, controller.signal)
      if (action.path.endsWith('/credentials')) {
        const next = parseProviderStatus(await response.json())
        if (alive.current && !controller.signal.aborted) { setProvider(next); setToken(''); setProviderLoading(false) }
      }
      if (alive.current && !controller.signal.aborted) {
        setConfirmation(null); setNotice(action.successMessage ?? action.label + ' completed.'); setSelected(null); setPlan(null)
        await refresh(true)
      }
    } catch (failure) {
      if (alive.current && !controller.signal.aborted) {
        setActionError(failure instanceof Error ? failure.message : 'The host could not complete this change.')
        await refresh(true)
      }
    } finally {
      if (writing.current === controller) writing.current = null
      if (alive.current) setBusy(false)
    }
  }
  async function search(event: React.FormEvent) {
    event.preventDefault()
    browsing.current?.abort()
    const controller = new AbortController()
    browsing.current = controller
    setSearching(true); setActionError(null); setResults(null)
    try {
      const parameters = new URLSearchParams({ query: query.trim(), kind: searchKind, ...(vllm ? { format: 'Safetensors' } : {}) })
      const value = parseSearch(await (await request('/api/host/huggingface/search?' + parameters, 'GET', undefined, controller.signal)).json())
      if (alive.current && !controller.signal.aborted) { setResults(value.items); setLimitReached(value.limitReached) }
    } catch (failure) {
      if (alive.current && !controller.signal.aborted) setActionError(failure instanceof Error ? failure.message : 'Model search failed.')
    } finally { if (alive.current && !controller.signal.aborted) setSearching(false) }
  }
  async function browse(name: string) {
    browsing.current?.abort()
    const controller = new AbortController()
    browsing.current = controller
    setSearching(true); setActionError(null); setRepository(null); setFile(''); setAdvanced(false)
    setRepositoryInput(name)
    try {
      const parameters = new URLSearchParams({ repository: name.trim(), revision: 'main', kind: searchKind, ...(vllm ? { format: 'Safetensors' } : {}) })
      const value = parseRepository(await (await request('/api/host/huggingface/repository?' + parameters, 'GET', undefined, controller.signal)).json())
      // vLLM takes the whole repository, so there is only ever one choice.
      if (alive.current && !controller.signal.aborted) { setRepository(value); if (vllm && value.choices.length === 1) setFile(value.choices[0].file) }
    } catch (failure) {
      if (alive.current && !controller.signal.aborted) setActionError(failure instanceof Error ? failure.message : 'The repository could not be read.')
    } finally { if (alive.current && !controller.signal.aborted) setSearching(false) }
  }
  async function inspect(model: LocalModel) {
    estimate.current?.abort()
    const controller = new AbortController()
    estimate.current = controller
    setSelected(model.id); setPlan(null); setEstimating(true); setActionError(null)
    try {
      const value = parseContextPlan(await (await request(`${base}/models/${model.id}/context`, 'GET', undefined, controller.signal)).json())
      if (alive.current && !controller.signal.aborted) {
        setPlan(value)
        setContextTokens(model.source.kind === 'Chat' ? chatContextRange(value).initial : value.effectiveContextTokens)
      }
    } catch (failure) {
      if (alive.current && !controller.signal.aborted) setActionError(failure instanceof Error ? failure.message : 'Context could not be estimated.')
    } finally { if (alive.current && !controller.signal.aborted) setEstimating(false) }
  }
  function download(selectedChoice: ModelChoice) {
    const what = selectedChoice.download.format === 'Safetensors' ? repository?.repository ?? selectedChoice.download.repository : selectedChoice.file
    setConfirmation({
      label: 'Download model', path: `${base}/models/download`, method: 'POST',
      body: { ...selectedChoice.download, pro: true },
      successMessage: 'Download queued. Its actual state will appear in your library.',
      description: dedicated
        ? `Download ${what} (${bytes(selectedChoice.totalSizeBytes)}) from the pinned Hugging Face revision? Downloading doesn't change what ${engineName} serves.`
        : `Download ${what} (${bytes(selectedChoice.totalSizeBytes)}) from the pinned Hugging Face revision? This model has not been qualified for your host. Downloading does not load or replace either active model.`,
    })
  }
  const title = (model: LocalModel) => model.source.format === 'Safetensors' ? model.source.repository : model.source.file
  const disabled = busy || loading || !!error
  const servedState = !serving || (!serving.model && !router) ? 'none' : serving.desired !== 'Running' ? 'stopped' : serving.ready ? 'ready'
    : serving.service && (serving.service.state !== 'running' || serving.service.health === 'unhealthy') ? 'failed' : 'starting'
  const routerStatus = (value: string) => value === 'loaded' ? 'Loaded' : value === 'loading' ? 'Loading into GPU memory'
    : value === 'unloaded' ? 'Loads when an app asks for it' : value === 'failed' ? 'Failed to load' : value
  return <>
    <div className="page-intro"><h1>{server ? `Models on ${server.hostname}` : view === 'library' ? 'Models' : 'Find models'}</h1><p>{server
      ? router ? `llama.cpp on ${server.hostname}. It serves every downloaded GGUF LLM and loads the one each request names, one at a time.`
        : dedicated ? `${engineName} on ${server.hostname}. It serves one model at a time, with all of its GPU memory.`
        : `Local AI on ${server.hostname}. One LLM and one embedding model, sized to its GPU memory.` : view === 'library'
      ? 'What runs in your lab. One LLM and one embedding model, with room reserved for the rest of your system.'
      : 'Explore Hugging Face, compare quantizations, and choose what to bring home.'}</p></div>
    <nav className="model-library-navigation" aria-label="Model library navigation">
      <a href={home} aria-current={view === 'library' ? 'page' : undefined}>Installed models</a>
      <a href={`${home}/find`} aria-current={view === 'find' ? 'page' : undefined}><Icon name="search" />Find & download</a>
    </nav>
    {(error || actionError) && <div className="model-manager-error" role="alert"><p>{actionError ?? error}</p>
      {error && status && <p>Model state below is from the last successful refresh. Changes are disabled until status is available again.</p>}
      {error && <button className="text-link" disabled={busy} onClick={() => void refresh(true)}>Refresh host status <Icon name="refresh" /></button>}</div>}
    <p className="model-manager-notice" role="status">{busy ? 'The host is working on your request…' : notice}</p>
    {status?.startupError && !dedicated && <p className="model-manager-error" role="alert">{status.startupError}</p>}
    {router && serving && <section hidden={view !== 'library'} className="surface model-manager-section" aria-labelledby="serving-heading">
      <h2 id="serving-heading">Available to apps</h2>
      {servedState === 'ready' ? serving.models?.length
        ? <div className="model-slots">{serving.models.map(item => <div key={item.id}><h3>{item.id}</h3><p className="muted">{routerStatus(item.status)}</p></div>)}</div>
        : <p className="muted">Nothing yet. Each GGUF LLM you download is copied to {server?.hostname ?? 'this server'}’s own disk, then listed here.</p>
        : <p className="muted">{servedState === 'starting' ? 'llama.cpp is starting.'
          : servedState === 'stopped' ? <>Local AI is stopped. <a href={`#/apps/${serving.stack}`}>Start it in Apps</a>.</>
          : <>llama.cpp stopped{serving.service?.exitCode ? ` with exit code ${serving.service.exitCode}` : ''}. <a href={`#/apps/${serving.stack}`}>Read its logs in Apps</a>.</>}</p>}
      <p className="section-note">Apps name the model in their requests exactly as it’s listed here. A new download is listed once it’s copied from the library to this server’s disk, so it loads in seconds rather than over the network. The first request to a model loads it; llama.cpp unloads the previous one to make room.</p>
    </section>}
    {vllm && serving && <section hidden={view !== 'library'} className="surface model-manager-section" aria-labelledby="serving-heading">
      <h2 id="serving-heading">Serving now</h2>
      <div className="model-slots"><div><h3>{engineName}</h3>
        <p className="model-name">{serving.name ?? 'Nothing yet'}</p>
        <p className="muted">{servedState === 'none' ? 'Choose a downloaded model below to serve it.'
          : servedState === 'ready' ? `Ready · ${serving.context === 'auto' || !serving.context ? 'context fitted to GPU memory' : `${Number(serving.context).toLocaleString()} tokens of context`}`
          : servedState === 'starting' ? `Starting. ${engineName} is loading the weights into GPU memory, which can take several minutes.`
          : servedState === 'stopped' ? <>Local AI is stopped. <a href={`#/apps/${serving.stack}`}>Start it in Apps</a>.</>
          : <>{engineName} stopped{serving.service?.exitCode ? ` with exit code ${serving.service.exitCode}` : ''}. <a href={`#/apps/${serving.stack}`}>Read its logs in Apps</a>.</>}</p>
        {serving.model && <button className="text-link" disabled={disabled} onClick={() => setConfirmation({
          label: 'Stop serving', path: server!.serving, method: 'DELETE',
          successMessage: `Saved. ${engineName} stops within about 20 seconds.`,
          description: `Stop serving ${serving.name}? Apps using it get errors until you serve a model again. Its files stay in the library.`,
        })}>Stop serving <Icon name="eject" /></button>}
      </div></div>
      <p className="section-note">Apps use the model’s repository name, such as {serving.name ?? 'Qwen/Qwen3-8B'}, as the model in their requests.</p>
    </section>}
    <section hidden={view !== 'library' || dedicated} className="surface model-manager-section" aria-labelledby="loaded-models-heading">
      <h2 id="loaded-models-heading">Loaded now</h2>
      <div className="model-slots">{(['Chat', 'Embedding'] as const).map(slot => {
        const loaded = slot === 'Chat' ? status?.chat : status?.embedding
        return <div key={slot}><h3>{slot === 'Chat' ? 'LLM' : 'Embedding model'}</h3>
          <p className="model-name">{loading && !status ? 'Reading the host…' : !status ? 'Status unavailable' : loaded?.name ?? 'Not loaded'}</p>
          {loaded && <><p className="muted">{loaded.plan.effectiveContextTokens.toLocaleString()} tokens · {loaded.plan.model.architecture}</p>
            <button className="text-link" disabled={disabled} onClick={() => setConfirmation({
              label: `Unload ${slot === 'Chat' ? 'LLM' : 'embedding model'}`, path: `${base}/models/${slot}/unload`, method: 'POST',
              description: `Unload ${loaded.name}? The other model slot is unchanged. Requests already using inference finish before the host changes models.`,
            })}>Unload <Icon name="eject" /></button></>}
        </div>
      })}</div>
      {!server && <p className="section-note">{status ? `${status.voiceReserveGiB} GiB is reserved for future voice features.` : 'Memory for future voice features is reserved separately.'} Context estimates also include the other loaded model and OS/services headroom.</p>}
    </section>
    <div hidden={view !== 'find'}>
    {server && <p className="section-note">{server.hostname} downloads from Hugging Face without an account, so only public models are available. {vllm
      ? 'vLLM runs safetensors repositories built on an architecture it supports, and search shows only those. Whether a model fits is checked when vLLM starts serving it.'
      : dedicated ? 'llama.cpp serves every GGUF LLM you download here. It fits each one and its context to GPU memory when an app first asks for it.'
      : 'Its memory fit is checked after the download.'}</p>}
    {!server && <details className="surface model-manager-section provider-connection">
      <summary>Hugging Face connection <span>{provider?.configured ? 'Connected' : 'Add or manage an access token'}</span></summary>
      <p className="muted">{providerLoading ? 'Reading connection settings…' : !provider ? 'Connection status is unavailable.' : provider.configured
        ? `Authenticated access${provider.accountName ? ` as ${provider.accountName}` : ' from host configuration'}.`
        : 'Anonymous access. Add an access token for your account’s authenticated download limits.'}</p>
        <form className="provider-token-form" onSubmit={event => {
          event.preventDefault()
          void mutate({ label: 'Save Hugging Face token', description: '', path: '/api/host/huggingface/credentials', method: 'PUT', body: { token } })
        }}>
          <label htmlFor="hf-access-token">Hugging Face access token</label>
          <input id="hf-access-token" type="password" autoComplete="off" spellCheck={false} value={token} onChange={event => setToken(event.target.value)} disabled={busy} placeholder="hf_…" maxLength={512} />
          <p className="section-note">Use a read or appropriately scoped fine-grained token. It is validated with Hugging Face and stored encrypted on your Spark, never in browser storage. Download speed is not guaranteed; gated models may still require approval on Hugging Face.</p>
          <div className="model-actions"><button className="button primary" disabled={busy || !token.trim()}>Save access token <Icon name="shield" /></button>
            {(provider?.configured || (!providerLoading && !provider)) && <button type="button" className="button secondary" disabled={busy} onClick={() => setConfirmation({
              label: 'Disconnect Hugging Face', path: '/api/host/huggingface/credentials', method: 'DELETE',
              description: 'Future searches and downloads will use anonymous access. This also suppresses old implicit CLI credentials. Downloads already running keep the credential they started with.',
            })}>{provider ? 'Remove access token' : 'Reset saved connection'}</button>}</div>
        </form>
    </details>}
    <section className="surface model-manager-section" aria-labelledby="browse-models-heading">
      <h2 id="browse-models-heading">Find a model</h2>
      <form className="model-search" onSubmit={event => void search(event)}>
        {!dedicated && <label>Model type<select value={kind} onChange={event => {
          setKind(event.target.value as ModelKind); setResults(null); setRepository(null); setFile(''); setAdvanced(false)
        }} disabled={busy || searching}><option value="Chat">LLM</option><option value="Embedding">Embedding model</option></select></label>}
        <label>Search Hugging Face<input value={query} onChange={event => setQuery(event.target.value)} maxLength={100} placeholder={vllm ? 'For example, Qwen3' : kind === 'Chat' ? 'For example, Qwen GGUF' : 'For example, BGE GGUF'} disabled={busy || searching} /></label>
        <button className="button secondary" disabled={busy || searching || !query.trim()}>{searching ? 'Reading Hugging Face…' : 'Search'}<Icon name="arrow" /></button>
      </form>
      <form className="model-direct" onSubmit={event => { event.preventDefault(); void browse(repositoryInput) }}>
        <label htmlFor="hf-repository">Or open a repository</label><div><input id="hf-repository" value={repositoryInput} onChange={event => setRepositoryInput(event.target.value)} placeholder={vllm ? 'owner/model' : 'owner/model-GGUF'} maxLength={200} disabled={busy || searching} />
          <button className="text-link" disabled={busy || searching || !repositoryInput.trim()}>Open <Icon name="arrow" /></button></div>
      </form>
      {searchKind === 'Embedding' && <p className="section-note">This host currently supports BERT/XLM-R sentence-encoder GGUFs. A repository’s embedding tag alone does not prove compatibility.</p>}
      {results && <div className="model-search-results">{results.length === 0 ? <p className="muted">{vllm ? 'No matching repositories vLLM can run were returned.' : 'No matching GGUF repositories were returned.'} Try a different query or open a repository directly.</p>
        : results.map(item => <button key={item.repository} className="model-search-result" disabled={busy || searching} onClick={() => void browse(item.repository)}>
          <span><strong>{item.repository}</strong><small>{[item.parameters ? `${parameterCount(item.parameters)} parameters` : null,
            item.downloads === null ? 'Downloads not reported' : `${item.downloads.toLocaleString()} downloads`,
            item.gated ? 'Access approval may be required' : null, item.private ? 'Private' : null].filter(Boolean).join(' · ')}</small></span><Icon name="chevron" /></button>)}
        {limitReached && <p className="section-note">Showing the first 30 matches. Refine the query to narrow the results.</p>}</div>}
      {repository && <div className="model-repository">
        <h3>{repository.repository}</h3><p className="model-revision">Pinned revision {repository.revision}</p>
        {repository.warnings.map(warning => <p className="section-note" key={warning}>{warning}</p>)}
        {repository.choices.length > 0 && vllm && choice ? <>
          <p className="model-name">Whole repository</p>
          <p className="muted">{[bytes(choice.totalSizeBytes), `${choice.files.length} files`, repository.ggufMetadata?.architecture, choice.quantization].filter(Boolean).join(' · ')}</p>
          <label className="model-checkbox"><input type="checkbox" checked={advanced} onChange={event => setAdvanced(event.target.checked)} disabled={busy} /><span>I understand that this model has not been qualified for {server?.hostname ?? 'this server'}. vLLM checks that it fits when it starts serving it.</span></label>
          <button className="button primary" disabled={disabled || !advanced} onClick={() => download(choice)}>Download model <Icon name="arrow" /></button>
        </> : repository.choices.length > 0 ? <>
          <label htmlFor="model-quantization">File and quantization</label>
          <select id="model-quantization" value={file} onChange={event => { setFile(event.target.value); setAdvanced(false) }} disabled={busy}>
            <option value="">Choose a model file</option>{repository.choices.map(item => <option key={item.file} value={item.file}>
              {item.quantization ?? 'Quantization not identified'} · {bytes(item.totalSizeBytes)} · {item.file}
            </option>)}
          </select>
          <p className="section-note">Quantization labels come from filenames. Smaller weights usually leave more room for context, with a potential quality tradeoff. Downloads include all shards of the selected variant.</p>
          {choice && <>
            <p className="model-name">{choice.file}</p><p className="muted">{bytes(choice.totalSizeBytes)} · {choice.files.length} file{choice.files.length === 1 ? '' : 's'}</p>
            {dedicated ? null : previewing ? <p className="section-note" role="status">Reading bounded GGUF metadata for a context estimate…</p>
              : preview ? <ContextEstimate plan={preview} /> : <p className="section-note">{previewMessage || 'Context capacity will be checked after the model metadata is available.'}</p>}
            <label className="model-checkbox"><input type="checkbox" checked={advanced} onChange={event => setAdvanced(event.target.checked)} disabled={busy} /><span>{dedicated
              ? `I understand that this model has not been qualified for ${server?.hostname ?? 'this server'}. ${engineName} checks that it fits when it starts serving it.`
              : 'I understand that this model/quantization has not been qualified for my host. The full file will be inspected before loading.'}</span></label>
            <button className="button primary" disabled={disabled || !advanced || previewing} onClick={() => download(choice)}>Download selected model <Icon name="arrow" /></button>
          </>}
        </> : <p className="muted">{vllm ? 'vLLM can’t run this repository.' : 'No complete standalone GGUF variant is available from this repository.'}</p>}
      </div>}
    </section>
    </div>
    <section hidden={view !== 'library'} className="model-library" aria-labelledby="model-library-heading">
      <div className="model-section-heading"><h2 id="model-library-heading">On {where}</h2><button className="text-link" disabled={busy || loading} onClick={() => void refresh(true)}>Refresh <Icon name="refresh" /></button></div>
      {loading && !models.length && <p className="muted" role="status">Reading the model library…</p>}
      {!loading && !error && models.length === 0 && <div className="model-library-empty"><p>No local models have been downloaded yet.</p><a className="button primary" href={`${home}/find`}>Find your first model <Icon name="arrow" /></a></div>}
      <div className="surface model-library-list">{models.map(model => {
        const loaded = dedicated ? vllm && serving?.model === model.id : status?.chat?.id === model.id || status?.embedding?.id === model.id
        const snapshot = model.source.format === 'Safetensors'
        const servable = vllm && model.source.kind === 'Chat' && snapshot
        return <article key={model.id}>
          <div className="model-library-row"><div><h3>{title(model)}</h3><p className="muted">{snapshot ? 'Safetensors for vLLM' : model.source.repository} · {model.source.kind === 'Chat' ? 'LLM' : 'Embedding'} · {loaded ? (dedicated ? 'Serving' : 'Loaded') : model.state}</p>
            {model.inspection && <p className="model-meta">{bytes(model.inspection.fileBytes)} · {model.inspection.architecture} · {model.inspection.nativeContextTokens.toLocaleString()} model-limit tokens</p>}
            {snapshot && model.state === 'Ready' && model.source.sizeBytes && <p className="model-meta">{bytes(model.source.sizeBytes)}</p>}</div>
            <div className="model-actions">
              {model.state === 'Ready' && !dedicated && !snapshot && <button className="text-link" disabled={disabled} onClick={() => void inspect(model)}>Context & load <Icon name="chevron" /></button>}
              {model.state === 'Ready' && servable && !loaded && <button className="text-link" disabled={disabled} onClick={() => { setSelected(model.id); setServeContext('') }}>Serve <Icon name="chevron" /></button>}
              {['Queued', 'Downloading'].includes(model.state) && <button className="text-link" disabled={disabled} onClick={() => setConfirmation({
                label: 'Cancel download', path: `${base}/models/${model.id}/cancel`, method: 'POST', description: 'Stop this download? Partial files are preserved for a later retry.',
                successMessage: 'Cancellation requested. Partial files are preserved; the library shows when the download has stopped.',
              })}>Cancel</button>}
              {['Failed', 'Canceled', 'Interrupted'].includes(model.state) && <button className="text-link" disabled={disabled} onClick={() => void mutate({
                label: 'Queue download retry', path: `${base}/models/${model.id}/retry`, method: 'POST', description: '',
                successMessage: 'Download retry queued.',
              })}>Retry</button>}
              {!loaded && !['Queued', 'Downloading'].includes(model.state) && <button className="text-link" disabled={disabled} onClick={() => setConfirmation({
                label: 'Delete local model', path: `${base}/models/${model.id}`, method: 'DELETE', description: `Delete the local files for ${title(model)}? You would need to download them again. Other models and provider credentials are unchanged.`,
              })}>Delete</button>}
            </div></div>
          {(() => {
            const progress = downloadProgress(model)
            if (!progress) return model.state === 'Queued' ? <p className="section-note">Waiting for the download ahead of it.</p> : null
            return <div className="model-progress">
              <progress max={100} {...(progress.percent !== null ? { value: progress.percent } : {})} aria-label={`Downloading ${title(model)}`} />
              <p className="muted">{bytes(progress.received)}{progress.total ? ` of ${bytes(progress.total)} · ${progress.percent}%` : ' so far'}</p>
            </div>
          })()}
          {(model.error || model.persistenceError) && <p className="model-manager-error">{model.error ?? model.persistenceError}</p>}
          {selected === model.id && dedicated && <div className="model-local-plan">
            <label className="model-serve-context" htmlFor={`serve-context-${model.id}`}>Context limit
              <input id={`serve-context-${model.id}`} value={serveContext} inputMode="numeric" maxLength={7} placeholder="Auto" autoComplete="off"
                aria-describedby={`serve-help-${model.id}`} disabled={disabled} onChange={event => setServeContext(event.target.value.replace(/\D/g, ''))} /></label>
            <p id={`serve-help-${model.id}`} className="section-note">Leave it blank and {engineName} uses the longest context its GPU memory fits, up to the model’s own limit. Set a lower limit if the model doesn’t start with its full context.</p>
            <div className="model-actions">
              <button className="button primary" disabled={disabled || (serveContext !== '' && Number(serveContext) < 256)} onClick={() => setConfirmation({
                label: 'Serve model', path: server!.serving, method: 'POST', body: { modelId: model.id, context: serveContext || 'auto' },
                successMessage: `Saved. ${engineName} restarts with this model within about 20 seconds, then loads it.`,
                description: serving?.name
                  ? `Serve ${title(model)} instead of ${serving.name}? ${engineName} restarts, so apps get errors until the new model is ready. Loading takes a few minutes.`
                  : `Serve ${title(model)}? ${engineName} starts and loads it, which takes a few minutes.`,
              })}>Serve this model <Icon name="spark" /></button>
              <button className="button secondary" disabled={busy} onClick={() => setSelected(null)}>Not now</button>
            </div>
            {serveContext !== '' && Number(serveContext) < 256 && <p className="section-note">Use at least 256 tokens.</p>}
          </div>}
          {selected === model.id && !dedicated && <div className="model-local-plan">
            {estimating ? <p role="status">Calculating the current memory plan…</p> : plan && selectedModel?.id === model.id && <>
              <ContextEstimate plan={plan} />
              {model.source.kind === 'Chat' && contextRange && (contextRange.available ? <div className="model-context-input">
                <div className="model-context-heading"><label htmlFor={`context-${model.id}`}>Context to serve</label>
                  <output htmlFor={`context-${model.id}`}>{contextTokens.toLocaleString()} tokens</output></div>
                <input id={`context-${model.id}`} type="range" min={contextRange.min} max={contextRange.max} step={contextRange.step}
                  value={contextTokens} aria-valuetext={`${contextTokens.toLocaleString()} tokens`}
                  aria-describedby={`context-help-${model.id}`} disabled={disabled || contextRange.min === contextRange.max}
                  onChange={event => setContextTokens(event.target.valueAsNumber)} />
                <div className="model-context-limits" aria-hidden="true"><span>8K · 8,192 tokens</span><span>{contextRange.max.toLocaleString()} tokens</span></div>
                <p id={`context-help-${model.id}`} className="section-note">From 8,192 tokens to the calculated memory limit, after model allocations and system reserves. The host rechecks this estimate when loading.</p>
              </div> : <p className="model-manager-error" role="status">The calculated limit is {plan.memoryLimitedContextTokens.toLocaleString()} tokens, below the 8,192-token minimum. Choose a smaller model or quantization, or free capacity in the other model slot.</p>)}
              {model.source.kind === 'Embedding' && plan.memoryLimitedContextTokens < 1 && <p className="model-manager-error" role="status">This embedding model doesn’t fit next to the loaded LLM. Unload the LLM or load it with less context, then try again.</p>}
              <button className="button primary" disabled={disabled || plan.memoryLimitedContextTokens < (model.source.kind === 'Chat' ? 8192 : 1)
                || !Number.isInteger(contextTokens) || contextTokens < (model.source.kind === 'Chat' ? 8192 : 1) || contextTokens > plan.memoryLimitedContextTokens
                || (model.source.kind === 'Chat' && contextTokens % 256 !== 0)}
                onClick={() => setConfirmation({
                  label: `Load ${model.source.kind === 'Chat' ? 'LLM' : 'embedding model'}`, path: `${base}/models/${model.id}/load`, method: 'POST',
                  body: { contextTokens },
                  description: `Load ${model.source.file} with ${contextTokens.toLocaleString()} tokens? This replaces the current ${model.source.kind === 'Chat' ? 'LLM' : 'embedding'} slot only. Loading is rechecked against the current memory plan and may take time; a failed replacement can leave that slot unloaded.`,
                })}>Load selected model <Icon name="spark" /></button>
            </>}
          </div>}
        </article>
      })}</div>
    </section>
    {confirmation && <section ref={confirmationPanel} tabIndex={-1} className="surface model-confirmation" aria-labelledby="model-confirmation-heading">
      <h2 id="model-confirmation-heading">{confirmation.label}?</h2><p>{confirmation.description}</p>
      <div className="model-actions"><button className="button primary" disabled={busy || (confirmation.path.startsWith(`${base}/models`) && (loading || !!error))}
        onClick={() => void mutate(confirmation)}>{busy ? 'Working…' : confirmation.label}</button>
        <button className="button secondary" disabled={busy} onClick={() => setConfirmation(null)}>Not now</button></div>
      <p className="section-note">Model changes already running on the host may finish if you leave this page.</p>
    </section>}
  </>
}
