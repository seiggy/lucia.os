import { useCallback, useEffect, useRef, useState } from 'react'
import { Icon } from './Icon'
import { externalApiBase, parseCompletionDelta, parseModels, readSseData } from './playground'
import type { HostedModel } from './playground'
import { inferenceConnection } from './authentication'
import type { AuthenticationSession } from './authentication'
import './LocalAI.css'

interface Message {
  id: number
  role: 'user' | 'assistant'
  text: string
  state: 'streaming' | 'done' | 'stopped' | 'error'
  seconds?: number
  finishReason?: string
  outputTokens?: number
}

async function responseError(response: Response): Promise<string> {
  let body: unknown
  try { body = await response.json() } catch { return `The host returned HTTP ${response.status}. Check the development connection and try again.` }
  if (body && typeof body === 'object' && 'error' in body) {
    const error = body.error
    if (typeof error === 'string') return error
    if (error && typeof error === 'object' && 'message' in error && typeof error.message === 'string') return error.message
  }
  return `The host returned HTTP ${response.status}. Try refreshing its status.`
}

async function* textChunks(body: ReadableStream<Uint8Array>): AsyncGenerator<string> {
  const reader = body.getReader()
  const decoder = new TextDecoder()
  try {
    while (true) {
      const { value, done } = await reader.read()
      if (done) break
      yield decoder.decode(value, { stream: true })
    }
    const tail = decoder.decode()
    if (tail) yield tail
  } finally {
    await reader.cancel()
    reader.releaseLock()
  }
}

export function LocalAI({ session, refreshSession, active = true }: { session: AuthenticationSession; refreshSession: () => Promise<void>; active?: boolean }) {
  const [models, setModels] = useState<HostedModel[]>([])
  const [loading, setLoading] = useState(true)
  const [connectionError, setConnectionError] = useState<string | null>(null)
  const [requestError, setRequestError] = useState<string | null>(null)
  const [messages, setMessages] = useState<Message[]>([])
  const [prompt, setPrompt] = useState('')
  const [systemPrompt, setSystemPrompt] = useState('')
  const [temperature, setTemperature] = useState(0.2)
  const [limit, setLimit] = useState(256)
  const [busy, setBusy] = useState(false)
  const [copyNotice, setCopyNotice] = useState('')
  const [lastRequest, setLastRequest] = useState<object | null>(null)
  const abort = useRef<AbortController | null>(null)
  const discovery = useRef<AbortController | null>(null)
  const mounted = useRef(true)
  const nextId = useRef(0)
  const input = useRef<HTMLTextAreaElement>(null)
  const transcript = useRef<HTMLDivElement>(null)
  const followOutput = useRef(true)
  const endpointInput = useRef<HTMLInputElement>(null)
  const [apiBase] = useState(() => session.enabled ? `${location.origin}/v1` : externalApiBase(import.meta.env.VITE_LUCIA_API_URL, location.href))
  const model = models.find(item => item.capabilities.includes('chat'))
  const embedding = models.find(item => item.capabilities.includes('embedding'))
  const responseLimit = model?.max_output_tokens
  const canSend = !!model && !!responseLimit && !loading && !connectionError && !busy && !!prompt.trim()

  const refresh = useCallback(async () => {
    discovery.current?.abort()
    const controller = new AbortController()
    discovery.current = controller
    setLoading(true)
    setConnectionError(null)
    try {
      const response = await fetch(inferenceConnection(session).models, { credentials: 'same-origin', signal: controller.signal })
      if (session.enabled && response.status === 401) void refreshSession()
      if (!response.ok) throw new Error(await responseError(response))
      const available = parseModels(await response.json())
      if (!controller.signal.aborted && mounted.current) setModels(available)
    } catch (error) {
      if (!controller.signal.aborted && mounted.current) {
        console.error('Could not read hosted model details.', error)
        setConnectionError(error instanceof Error ? error.message : 'Could not connect to the inference host.')
      }
    } finally {
      if (!controller.signal.aborted && mounted.current) setLoading(false)
    }
  }, [session, refreshSession])

  useEffect(() => {
    mounted.current = true
    return () => {
      mounted.current = false
      discovery.current?.abort()
      abort.current?.abort()
    }
  }, [refresh])
  useEffect(() => {
    if (active) void refresh()
  }, [active, refresh])

  useEffect(() => {
    if (transcript.current && followOutput.current) transcript.current.scrollTop = transcript.current.scrollHeight
  }, [messages])

  async function send() {
    if (!canSend || !model || !responseLimit) return
    const text = prompt.trim()
    const controller = new AbortController()
    abort.current = controller
    const assistantId = ++nextId.current
    const history = messages.filter(message => message.text && message.state !== 'error')
      .map(message => ({ role: message.role, content: message.text }))
    const payload = {
      model: model.id,
      messages: [
        ...(systemPrompt.trim() ? [{ role: 'system', content: systemPrompt.trim() }] : []),
        ...history,
        { role: 'user', content: text },
      ],
      temperature,
      max_tokens: Math.min(limit, responseLimit),
      think: false,
      stream: true,
      stream_options: { include_usage: true },
    }
    setLastRequest(payload)
    setMessages(current => [...current, { id: ++nextId.current, role: 'user', text, state: 'done' }, { id: assistantId, role: 'assistant', text: '', state: 'streaming' }])
    setPrompt('')
    setRequestError(null)
    setBusy(true)
    followOutput.current = true
    const started = performance.now()
    let finishReason: string | undefined
    let outputTokens: number | undefined
    let receivedDone = false
    const update = (patch: Partial<Message>) => {
      if (mounted.current) setMessages(current => current.map(message => message.id === assistantId ? { ...message, ...patch } : message))
    }
    try {
      const connection = inferenceConnection(session)
      const response = await fetch(connection.chat, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', ...connection.headers },
        body: JSON.stringify(payload),
        signal: controller.signal,
      })
      if (session.enabled && response.status === 401) void refreshSession()
      if (!response.ok) throw new Error(await responseError(response))
      if (!response.body || !response.headers.get('Content-Type')?.includes('text/event-stream'))
        throw new Error('The host did not return a streaming response.')
      let answer = ''
      for await (const data of readSseData(textChunks(response.body))) {
        if (controller.signal.aborted) throw new DOMException('Stopped by the user.', 'AbortError')
        if (data.trim() === '[DONE]') {
          receivedDone = true
          break
        }
        const delta = parseCompletionDelta(data)
        answer += delta.text
        finishReason = delta.finishReason ?? finishReason
        outputTokens = delta.outputTokens ?? outputTokens
        update({ text: answer })
      }
      if (!receivedDone) throw new Error('The connection ended before the model finished. Any partial response is shown below.')
      if (finishReason !== 'stop' && finishReason !== 'length') throw new Error(`The host did not report a normal completion (${finishReason ?? 'no finish reason'}).`)
      update({ state: 'done', finishReason, outputTokens, seconds: (performance.now() - started) / 1000 })
    } catch (error) {
      if (controller.signal.aborted) update({ state: 'stopped', seconds: (performance.now() - started) / 1000 })
      else {
        console.error('Local model request failed.', error)
        update({ state: 'error' })
        if (mounted.current) setRequestError(error instanceof Error ? error.message : 'The request failed. Try again.')
      }
    } finally {
      if (mounted.current) setBusy(false)
      abort.current = null
    }
  }

  async function copyAddress() {
    if (!apiBase) return
    try {
      if (navigator.clipboard && window.isSecureContext) await navigator.clipboard.writeText(apiBase)
      else {
        endpointInput.current?.focus()
        endpointInput.current?.select()
        if (!document.execCommand('copy')) throw new Error('Clipboard unavailable.')
      }
      setCopyNotice('Endpoint address copied.')
    } catch (error) {
      console.warn('Could not copy the endpoint address.', error)
      setCopyNotice('Select the address and press Ctrl+C or Command+C to copy it.')
    }
  }

  return <>
    <div className="page-intro ai-intro"><h1>Playground</h1><p>A conversation with your hosted model. Explore, compare, and try an idea.</p>
      <p className="playground-session-note">Your draft and conversation stay here while you explore Lucia. Refreshing the browser or signing out clears them.</p></div>
    <div className="ai-layout">
      <section className="surface playground-panel" aria-labelledby="playground-heading">
        <div className="playground-heading"><div><h2 id="playground-heading">Try a conversation</h2><p>{loading ? 'Connecting to your host…' : connectionError ? 'Connection needs attention' : model ? 'Responses run on your Spark, not a cloud service.' : 'No chat model is loaded.'}</p></div>
          <button className="text-link" disabled={busy || messages.length === 0} onClick={() => { setMessages([]); setRequestError(null); setLastRequest(null); input.current?.focus() }}><Icon name="refresh" />New chat</button>
        </div>
        {connectionError && <div className="ai-error" role="alert"><Icon name="attention" /><div><strong>We could not connect.</strong><p>{connectionError}</p><button className="text-link" onClick={() => void refresh()}>Try again <Icon name="refresh" /></button></div></div>}
        {!loading && !connectionError && !model && <div className="ai-error"><Icon name="unknown" /><div><strong>There is no model to talk to yet.</strong><p>Load a chat model on the host, then refresh its status here.</p><button className="text-link" onClick={() => void refresh()}>Refresh model status</button></div></div>}
        <div className="conversation" ref={transcript} aria-label="Conversation" aria-busy={busy} onScroll={event => {
          const element = event.currentTarget
          followOutput.current = element.scrollHeight - element.scrollTop - element.clientHeight < 80
        }}>
          {messages.length === 0 ? <div className="conversation-empty"><span className="icon-tile tone-accent"><Icon name="spark" /></span><h3>A small question is a good start.</h3><p>This is a live connection check. Your message goes to the hosted model.</p><button className="suggested-prompt" disabled={!model || !!connectionError} onClick={() => { setPrompt('Say hello in one short sentence.'); input.current?.focus() }}>Say hello in one sentence <Icon name="arrow" /></button></div> : messages.map(message => <article key={message.id} className={`chat-message message-${message.role}`}>
            <div className="message-speaker"><Icon name={message.role === 'assistant' ? 'spark' : 'user'} /><strong>{message.role === 'assistant' ? 'Your model' : 'You'}</strong></div>
            <div className="message-text">{message.text || (message.state === 'streaming' ? 'Waiting for the first words…' : message.state === 'stopped' ? 'Stopped before a response arrived.' : 'No response was received.')}</div>
            {message.role === 'assistant' && <p className="message-state">{message.state === 'streaming' ? 'Generating…' : message.state === 'stopped' ? 'Stopped by you' : message.state === 'error' ? 'Request interrupted' : message.finishReason === 'length' ? 'Response limit reached — this answer may be incomplete.' : 'Complete'}{message.seconds !== undefined && ` · ${message.seconds.toFixed(1)}s`}{message.outputTokens !== undefined && ` · ${message.outputTokens.toLocaleString()} output tokens`}</p>}
          </article>)}
        </div>
        {requestError && <div className="ai-error" role="alert"><Icon name="attention" /><p>{requestError}</p></div>}
        <form className="composer" onSubmit={event => { event.preventDefault(); void send() }}>
          <label htmlFor="ai-prompt">Your message</label>
          <textarea id="ai-prompt" ref={input} value={prompt} onChange={event => setPrompt(event.target.value)} placeholder="What would you like to ask?" rows={3} maxLength={32000} disabled={loading || !!connectionError || !model || busy} onKeyDown={event => {
            if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') { event.preventDefault(); void send() }
          }} />
          <div className="composer-actions"><span>Ctrl / ⌘ + Enter to send</span>{busy ? <button className="button secondary" type="button" onClick={() => abort.current?.abort()}>Stop response <Icon name="stop" /></button> : <button className="button primary" type="submit" disabled={!canSend}>Send message <Icon name="arrow" /></button>}</div>
        </form>
        <details className="generation-settings"><summary>Response settings</summary><div className="generation-fields"><label>Temperature <input type="number" min={0} max={2} step={0.1} value={temperature} disabled={busy} onChange={event => {
          const value = event.target.valueAsNumber
          if (Number.isFinite(value) && value >= 0 && value <= 2) setTemperature(value)
        }} /></label><label>Maximum response tokens <input type="number" min={1} max={responseLimit ?? 256} value={Math.min(limit, responseLimit ?? limit)} disabled={busy} onChange={event => {
          const value = event.target.valueAsNumber
          if (Number.isInteger(value) && value > 0 && value <= (responseLimit ?? 256)) setLimit(value)
        }} /></label></div><label className="system-prompt-label" htmlFor="ai-system">Optional system instruction</label><textarea id="ai-system" value={systemPrompt} onChange={event => setSystemPrompt(event.target.value)} rows={2} maxLength={8000} disabled={busy} placeholder="For example: keep answers brief." /><p>These settings apply to the next message. Conversation text stays in this view, not browser storage.</p></details>
        {lastRequest && <details className="request-details"><summary>View the request sent to the host</summary><pre>{JSON.stringify(lastRequest, null, 2)}</pre></details>}
      </section>
      <aside className="ai-details">
        <section className="surface reading-surface"><div className="model-heading"><span className="icon-tile tone-accent"><Icon name="spark" /></span><div><h2>Hosted model</h2><span className={`status status-${connectionError ? 'amber' : model ? 'green' : 'muted'}`}><Icon name={loading ? 'clock' : connectionError ? 'attention' : model ? 'check' : 'unknown'} />{loading ? 'Checking status' : connectionError ? 'Unavailable' : model ? 'Ready to respond' : 'Not loaded'}</span></div></div>
          {model && <><p className="model-identifier">{model.id}</p><dl className="fact-list"><div><dt>Served context</dt><dd>{model.context_length?.toLocaleString() ?? 'Not reported'} tokens</dd></div><div><dt>Response limit</dt><dd>{model.max_output_tokens?.toLocaleString() ?? 'Not reported'} tokens</dd></div><div><dt>Backend</dt><dd>{model.backend?.replace(/_/g, ' ').toUpperCase() ?? 'Not reported'}</dd></div><div><dt>Embeddings</dt><dd>{embedding ? embedding.id : 'No encoder loaded'}</dd></div></dl><details><summary>Model limit, not a memory guarantee</summary><p>The file declares {model.native_context_length?.toLocaleString() ?? 'an unspecified number of'} tokens. The served context above is the host’s current limit.</p></details></>}
          <div className="model-panel-actions"><button className="text-link" disabled={loading || busy} onClick={() => void refresh()}><Icon name="refresh" />Refresh status</button>
          {session.isOwner && <a className="text-link" href="#/ai/models">Manage models <Icon name="arrow" /></a>}</div>
        </section>
        <section className="surface reading-surface endpoint-panel"><h2>Connect another app</h2><p>The host speaks the OpenAI-compatible API.</p><label htmlFor="endpoint-url">Base URL</label><input id="endpoint-url" ref={endpointInput} readOnly value={apiBase ?? 'Address not advertised by this dev host'} onFocus={event => event.target.select()} /><button className="button secondary" disabled={!apiBase} onClick={() => void copyAddress()}><Icon name="copy" />Copy base URL</button><p className="copy-notice" role="status">{copyNotice}</p>
          <dl className="endpoint-list"><div><dt>Chat</dt><dd>/chat/completions</dd></div><div><dt>Responses</dt><dd>/responses</dd></div><div><dt>Models</dt><dd>/models</dd></div><div><dt>Embeddings</dt><dd>/embeddings</dd></div></dl><p className="section-note">Other clients need an inference API key. An embedding encoder must be loaded before using embeddings.</p>
          {session.isOwner && <a className="text-link" href="#/ai/keys">Create or manage API keys <Icon name="arrow" /></a>}
          {apiBase && !session.enabled && <a className="text-link" href={`${apiBase.slice(0, -3)}/scalar/v1`} target="_blank" rel="noreferrer">Open API reference <Icon name="arrow" /><span className="visually-hidden"> (new tab)</span></a>}
        </section>
        <div className="connection-note"><Icon name="shield" /><div><h2>{session.enabled ? 'Signed in with your home account.' : 'Temporary development connection.'}</h2><p>{session.enabled ? 'This browser uses your secure sign-in session. Requests go directly to your Lucia host, with no shared API key in the browser. Your account permissions are enforced by the host.' : 'The dev server holds an inference-only key. It cannot manage models or devices through this bridge. Keep the dev server on a trusted network; Authentik sign-in comes next.'}</p></div></div>
      </aside>
    </div>
    <div className="visually-hidden" role="status" aria-live="polite">{busy ? 'The model is responding. Stop response is available.' : messages.length > 0 ? 'The model request has ended.' : ''}</div>
  </>
}
