import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode, RefObject } from 'react'
import { useChat } from '@ai-sdk/react'
import { DefaultChatTransport } from 'ai'
import type { UIMessage } from 'ai'
import { ExternalLinkIcon, Loader2Icon } from 'lucide-react'
import { Conversation, ConversationContent, ConversationScrollButton } from './components/ai-elements/conversation'
import { Message, MessageContent, MessageResponse } from './components/ai-elements/message'
import {
  PromptInput, PromptInputBody, PromptInputFooter, PromptInputProvider, PromptInputSelect, PromptInputSelectContent,
  PromptInputSelectItem, PromptInputSelectTrigger, PromptInputSelectValue, PromptInputSubmit, PromptInputTextarea,
  PromptInputTools, usePromptInputController,
} from './components/ai-elements/prompt-input'
import type { PromptInputMessage } from './components/ai-elements/prompt-input'
import { Reasoning, ReasoningContent, ReasoningTrigger } from './components/ai-elements/reasoning'
import { SelectGroup, SelectLabel } from './components/ui/select'
import { chatTitle, chooseModel, isChatId, isGitHubModel, maxMessageLength, messageText, newChatId, pageRoute, parseGitHub, parseModels, parseSessions, parseTranscript, settledText, signInModel } from './assistant'
import type { AssistantMetadata, AssistantMode, AssistantModels, ChatSummary, DockLayout, DockSide, GitHubState, GitHubStatus } from './assistant'
import type { AuthenticationSession } from './authentication'
import { Icon } from './Icon'
import { ownerRequest, responseError } from './managementApi'
import './assistant.css'

type ChatMessage = UIMessage<AssistantMetadata>
type Auth = { session: AuthenticationSession; refreshSession: () => Promise<void> }
type AuthRef = RefObject<Auth>
type Failure = Error & { code?: string }

const chatKey = 'lucia.assistant.chat.v1'
const modelKey = 'lucia.assistant.model.v1'
// Streamdown's fullscreen, download, and link-confirmation overlays portal outside the dock's styles.
const replyControls = { table: false, code: { copy: true, download: false }, mermaid: false }
const noLinkSafety = { enabled: false }
const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches
const starters = ['What can I do on this page?', 'How do custom app stacks work in Lucia?', 'What should I check when an app stops responding?']
const updatedFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

function readStored(key: string): string | null {
  try { return localStorage.getItem(key) } catch { return null }
}

function writeStored(key: string, value: string | null) {
  try {
    if (value === null) localStorage.removeItem(key)
    else localStorage.setItem(key, value)
  } catch { /* The choice still applies until the page closes. */ }
}

const failureCode = (error: unknown) => error instanceof Error ? (error as Failure).code : undefined
const failureMessage = (error: unknown, fallback: string) => error instanceof Error && error.message ? error.message : fallback

function updated(value: string) {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '' : updatedFormat.format(date)
}

function Alert({ children, action, onAction }: { children: ReactNode; action?: string; onAction?: () => void }) {
  return <div className="assistant-alert" role="alert"><Icon name="attention" /><div><p>{children}</p>
    {action && <button type="button" className="text-link" onClick={onAction}>{action}</button>}</div></div>
}

const thinkingLabel = (streaming: boolean, duration?: number) =>
  <span>{streaming ? 'Thinking…' : duration ? `Thought for ${duration} second${duration === 1 ? '' : 's'}` : 'Reasoning'}</span>

function ChatBubble({ message, streaming, onRetry }: { message: ChatMessage; streaming: boolean; onRetry?: () => void }) {
  if (message.role === 'user')
    return <Message from="user"><MessageContent className="assistant-bubble">{messageText(message)}</MessageContent></Message>
  const { stopped, error } = message.metadata ?? {}
  const content = message.parts.map((part, index) => {
    if (part.type === 'reasoning' && part.text)
      return <Reasoning key={index} className="assistant-reasoning" isStreaming={streaming && part.state === 'streaming'}>
        <ReasoningTrigger className="assistant-reasoning-trigger" getThinkingMessage={thinkingLabel} />
        <ReasoningContent className="assistant-thinking">{part.text}</ReasoningContent>
      </Reasoning>
    if (part.type !== 'text') return null
    const text = stopped || error ? settledText(part.text) : part.text
    return text ? <MessageResponse key={index} className="assistant-reply" controls={replyControls} linkSafety={noLinkSafety}
      isAnimating={streaming && part.state === 'streaming'}>{text}</MessageResponse> : null
  })
  if (!content.some(Boolean) && !stopped && !error) return null
  return <Message from="assistant">
    {content}
    {stopped && <p className="status status-amber"><Icon name="stop" />Stopped</p>}
    {error && <div className="assistant-failed"><Icon name="attention" /><div><p>{error}</p>
      {onRetry && <button type="button" className="text-link" onClick={onRetry}>Try again</button>}</div></div>}
  </Message>
}

type GitHub = {
  status: GitHubStatus | null
  notice: string | null
  setNotice: (notice: string | null) => void
  check: () => Promise<void>
  start: () => Promise<GitHubStatus>
  cancel: () => Promise<void>
  disconnect: () => Promise<void>
}

// The host owns the sign-in. This mirrors it, polling only while a code waits to be entered on GitHub.
function useGitHub(auth: AuthRef): GitHub {
  const [status, setStatus] = useState<GitHubStatus | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  // Only the newest request may set the status, so a slow poll can't undo a cancel or a new code.
  const latest = useRef(0)
  const call = useCallback(async (method: 'GET' | 'POST' | 'DELETE', path: string) => {
    const id = ++latest.current
    return { id, response: await ownerRequest(auth.current.session, auth.current.refreshSession, `/api/assistant/github${path}`, method) }
  }, [auth])
  const check = useCallback(async () => {
    try {
      const { id, response } = await call('GET', '')
      const value = parseGitHub(await response.json())
      if (id === latest.current) setStatus(value)
    } catch (failure) {
      console.warn('Lucia could not check the GitHub sign-in.', failure)
    }
  }, [call])
  const start = useCallback(async () => {
    const { id, response } = await call('POST', '/device')
    const value = parseGitHub(await response.json())
    if (id === latest.current) setStatus(value)
    setNotice(null)
    return value
  }, [call])
  const cancel = useCallback(async () => {
    await call('DELETE', '/device')
    await check()
  }, [call, check])
  const disconnect = useCallback(async () => {
    const { id } = await call('DELETE', '')
    if (id === latest.current) setStatus({ state: 'disconnected' })
    setNotice(null)
    void check()
  }, [call, check])

  const state = status?.state
  useEffect(() => { void check() }, [check])
  useEffect(() => {
    if (state && state !== 'pending') return
    const poll = () => { if (document.visibilityState === 'visible') void check() }
    const timer = state === 'pending' ? window.setInterval(poll, 2000) : undefined
    window.addEventListener('focus', poll)
    document.addEventListener('visibilitychange', poll)
    return () => {
      window.clearInterval(timer)
      window.removeEventListener('focus', poll)
      document.removeEventListener('visibilitychange', poll)
    }
  }, [state, check])
  return { status, notice, setNotice, check, start, cancel, disconnect }
}

const gateCopy: Record<Exclude<GitHubState, 'pending'>, [message: string, action: string]> = {
  connected: ['', 'Sign in again'],
  disconnected: ['Sign in with GitHub to use Copilot’s models. They run on your GitHub Copilot plan.', 'Sign in with GitHub'],
  expired: ['The code expired before it was entered on GitHub.', 'Get a new code'],
  denied: ['Access was declined on GitHub.', 'Try again'],
  error: ['Lucia could not finish the sign-in.', 'Try again'],
}
const shownPage = (uri: string) => uri.replace(/^https:\/\//, '').replace(/\/$/, '')

function GitHubGate({ github, onFocusInput }: { github: GitHub; onFocusInput: () => void }) {
  const { status, notice } = github
  const box = useRef<HTMLDivElement>(null)
  const [working, setWorking] = useState(false)
  const [failure, setFailure] = useState<string | null>(null)
  const [copied, setCopied] = useState<'copied' | 'blocked' | null>(null)
  const [announcement, setAnnouncement] = useState('')
  const seen = useRef(status?.state)
  const refocus = useRef(false)
  const copyTimer = useRef<number>(undefined)

  // Finishing on GitHub removes the code card, so say what happened and keep focus out of the page body.
  useEffect(() => {
    const before = seen.current
    seen.current = status?.state
    if (!status || before === status.state) return
    const asked = refocus.current
    refocus.current = false
    if (before === 'pending' && !asked) {
      if (status.state === 'connected') setAnnouncement(status.login ? `Signed in to GitHub as @${status.login}.` : 'Signed in to GitHub.')
      else if (status.message) setAnnouncement(status.message)
    }
    const lost = !document.activeElement || document.activeElement === document.body
    if (!asked && !(before === 'pending' && lost)) return
    const target = box.current?.querySelector<HTMLElement>('[data-autofocus]') ?? box.current?.querySelector<HTMLElement>('button')
    if (target) target.focus()
    else onFocusInput()
  }, [status, onFocusInput])

  async function act(action: () => Promise<unknown>, fallback: string) {
    if (working) return
    setWorking(true)
    setFailure(null)
    refocus.current = true
    try {
      await action()
    } catch (error) {
      refocus.current = false
      setFailure(failureMessage(error, fallback))
    } finally {
      setWorking(false)
    }
  }
  const begin = () => act(async () => {
    setCopied(null)
    const value = await github.start()
    if (value.userCode && value.verificationUri) setAnnouncement(`Enter the code ${value.userCode} at ${shownPage(value.verificationUri)}.`)
  }, 'Lucia could not start a GitHub sign-in.')
  const cancel = () => act(github.cancel, 'Lucia could not cancel this sign-in.')
  function copy() {
    const code = status?.userCode
    if (!code) return
    window.clearTimeout(copyTimer.current)
    // Write before the link's new tab takes focus; a missing clipboard (plain HTTP) counts as blocked.
    let writing: Promise<void>
    try { writing = navigator.clipboard.writeText(code) } catch (error) { writing = Promise.reject(error) }
    writing.then(() => {
      setCopied('copied')
      setAnnouncement('Code copied.')
      copyTimer.current = window.setTimeout(() => setCopied(null), 2000)
    }, () => setCopied('blocked'))
  }

  let content: ReactNode = null
  if (status?.state === 'pending' && status.userCode && status.verificationUri)
    content = <div className="assistant-signin" role="group" aria-labelledby="assistant-signin-title">
      <p id="assistant-signin-title">Enter this code at <strong>{shownPage(status.verificationUri)}</strong></p>
      <p className="assistant-code"><code>{status.userCode}</code>
        <button type="button" className="assistant-icon-button" aria-label="Copy code" title="Copy code" onClick={copy}>
          <Icon name={copied === 'copied' ? 'check' : 'copy'} /></button></p>
      {copied === 'blocked' && <p className="assistant-copy-blocked">Your browser blocked copying, so copy the code yourself.</p>}
      <div className="assistant-signin-actions">
        <a className="button secondary" href={status.verificationUri} target="_blank" rel="noreferrer" data-autofocus onClick={copy}>
          <ExternalLinkIcon aria-hidden="true" />Copy code and open GitHub</a>
        <button type="button" className="text-link" aria-disabled={working} onClick={() => void cancel()}>Cancel</button>
      </div>
      <p className="assistant-working"><Loader2Icon className="animate-spin" />Waiting for GitHub…</p>
    </div>
  else if (status && status.state !== 'pending' && (status.state !== 'connected' || notice)) {
    const [message, action] = gateCopy[status.state]
    content = <div className="assistant-connect" data-tone={status.state === 'error' ? 'failed' : undefined}>
      <Icon name="attention" />
      <div><p>{notice ?? status.message ?? message}</p>
        <button type="button" className="button secondary" aria-disabled={working} onClick={() => void begin()}>
          {working ? <><Loader2Icon className="animate-spin" />Getting a code…</> : action}</button></div>
    </div>
  }
  return <div ref={box} className="assistant-gate">
    {content}
    {failure && <Alert>{failure}</Alert>}
    <p className="visually-hidden" role="status">{announcement}</p>
  </div>
}

type ChatViewProps = {
  id: string
  fresh: boolean
  hidden: boolean
  auth: AuthRef
  page: string
  github: GitHub
  models: AssistantModels | null
  model?: string
  onModel: (model: string) => void
  mode: AssistantMode
  onMode: (mode: AssistantMode) => void
  focusKey: number
  onStarted: (text: string) => void
  onTitle: (title: string) => void
  onReload: () => void
  onFailed: () => void
}

function ChatView({ id, fresh, hidden, auth, page, github, models, model, onModel, mode, onMode, focusKey, onStarted, onTitle, onReload, onFailed }: ChatViewProps) {
  const draft = usePromptInputController()
  const input = useRef<HTMLTextAreaElement>(null)
  const composer = useRef<HTMLDivElement>(null)
  const picked = useRef<string>(undefined)
  const [phase, setPhase] = useState<'loading' | 'resuming' | 'ready'>(fresh ? 'ready' : 'loading')
  const [loadError, setLoadError] = useState<Error | null>(null)
  const [sent, setSent] = useState(false)
  const transport = useMemo(() => new DefaultChatTransport<ChatMessage>({
    api: '/api/assistant/chat',
    fetch: async (resource, init) => {
      let response: Response
      try {
        response = await fetch(resource, { ...init, cache: 'no-store' })
      } catch (failure) {
        if (failure instanceof Error && failure.name === 'AbortError') throw failure
        throw new Error('Lucia could not reach the host. Check your connection and try again.')
      }
      if (response.ok) return response
      const failure = await responseError(response)
      if (response.status === 401 || failure.code === 'invalid_csrf_token') void auth.current.refreshSession()
      throw failure
    },
    prepareSendMessagesRequest: ({ id: sessionId, messages, body }) => {
      const last = messages.at(-1)
      return { body: { ...body, sessionId, messageId: last?.id, text: last ? messageText(last) : '' } }
    },
    prepareReconnectToStreamRequest: ({ id: sessionId }) => ({ api: `/api/assistant/sessions/${sessionId}/stream` }),
  }), [auth])
  const { messages, setMessages, sendMessage, regenerate, resumeStream, stop, status, error } = useChat<ChatMessage>({ id, transport })

  // Load the saved transcript first; the host's stream replays an in-flight answer from its start.
  useEffect(() => {
    if (fresh) return
    const controller = new AbortController()
    const load = async () => {
      try {
        const response = await ownerRequest(auth.current.session, auth.current.refreshSession, `/api/assistant/sessions/${id}`, 'GET', undefined, controller.signal)
        const transcript = parseTranscript(await response.json())
        if (controller.signal.aborted) return
        setMessages(transcript.messages)
        onTitle(transcript.title)
        if (transcript.running) {
          setPhase('resuming')
          await resumeStream()
        }
      } catch (failure) {
        if (controller.signal.aborted) return
        if (failureCode(failure) === 'session_not_found') onTitle('New chat')
        else {
          console.error('Lucia could not open this assistant chat.', failure)
          setLoadError(failure instanceof Error ? failure : new Error('Lucia could not open this chat.'))
        }
      }
      if (!controller.signal.aborted) setPhase('ready')
    }
    void load()
    return () => controller.abort()
  }, [auth, fresh, id, onTitle, resumeStream, setMessages])
  useEffect(() => {
    if (focusKey > 0) input.current?.focus()
  }, [focusKey])
  // Copilot refusals arrive mid-answer without a code, so any failure re-checks the sign-in and the models.
  useEffect(() => {
    if (status === 'error') onFailed()
  }, [status, onFailed])
  const focusInput = useCallback(() => input.current?.focus(), [])

  const busy = status === 'submitted' || status === 'streaming' || phase === 'resuming'
  // Only Copilot's models need the GitHub sign-in; LiteLLM and Local AI answer without it.
  const needsGitHub = isGitHubModel(model)
  const connected = !needsGitHub || (model !== signInModel && (!github.status || (github.status.state === 'connected' && !github.notice)))
  const ready = phase === 'ready' && !loadError
  const canAsk = ready && connected && !busy
  const last = messages.at(-1)
  const answering = busy && !(last?.role === 'assistant' && last.parts.some(part => (part.type === 'text' || part.type === 'reasoning') && part.text))
  const request = () => ({
    body: { mode, model: model === signInModel ? undefined : model, route: pageRoute(location.hash) },
    headers: { 'X-CSRF-TOKEN': auth.current.session.csrfToken ?? '' },
  })
  function send(text: string) {
    if (!messages.length) onStarted(text)
    setSent(true)
    void sendMessage({ text }, request())
  }
  function submit({ text }: PromptInputMessage) {
    // Throwing keeps the draft in the composer.
    if (!canAsk || !text.trim()) throw new Error('This message cannot be sent yet.')
    send(text.trimEnd())
  }
  function retry() {
    setSent(true)
    void regenerate(request())
  }
  function stopAnswer() {
    ownerRequest(auth.current.session, auth.current.refreshSession, `/api/assistant/sessions/${id}/stop`, 'POST').catch(failure => {
      console.warn('Lucia could not stop this answer on the host.', failure)
      void stop()
    })
  }
  function reload() {
    // The host refused this message because another answer is running, so keep its text for resending.
    if (failureCode(error) === 'run_active' && last?.role === 'user' && !draft.textInput.value.trim()) draft.textInput.setInput(messageText(last))
    onReload()
  }
  function pick(id: string) {
    // Inside a form, Radix's hidden select reports '' when the value and its options change together.
    if (!id) return
    picked.current = id
    onModel(id)
  }
  // Picking the GitHub sign-in moves focus to it rather than back to the picker.
  function focusSignIn(event: Event) {
    const signIn = picked.current === signInModel
    picked.current = undefined
    const gate = composer.current
    const target = signIn && (gate?.querySelector<HTMLElement>('.assistant-gate [data-autofocus]') ?? gate?.querySelector<HTMLElement>('.assistant-gate button'))
    if (!target) return
    event.preventDefault()
    target.focus()
  }

  let notice: ReactNode = null
  if (status === 'error' && error) {
    if (error instanceof TypeError) notice = <Alert action="Reload chat" onAction={reload}>Lost connection to this answer.</Alert>
    else if (failureCode(error) === 'run_active')
      notice = <p className="assistant-note">This chat is already answering somewhere else. Reload it to follow or stop that answer.
        <button type="button" className="text-link" onClick={reload}>Reload chat</button></p>
    else if (last?.role === 'user' && sent) notice = <Alert action="Try again" onAction={retry}>{error.message}</Alert>
    else if (!(last?.role === 'assistant' && last.metadata?.error)) notice = <Alert action="Reload chat" onAction={reload}>{error.message}</Alert>
  } else if (ready && status === 'ready' && last?.role === 'user')
    notice = <p className="assistant-note">This message didn’t get an answer.<button type="button" className="text-link" onClick={retry}>Try again</button></p>

  return <div className="assistant-chat" hidden={hidden}>
    <Conversation className="assistant-conversation" initial="instant" resize={reducedMotion ? 'instant' : 'smooth'}
      aria-label="Conversation" aria-busy={busy || phase === 'loading'}>
      <ConversationContent className="assistant-messages">
        {phase === 'loading' && <p className="assistant-loading"><Loader2Icon className="animate-spin" />Opening this chat…</p>}
        {loadError && <Alert action={failureCode(loadError) === 'session_too_large' ? undefined : 'Reload chat'} onAction={onReload}>{loadError.message}</Alert>}
        {ready && !messages.length && <div className="assistant-empty">
          <p className="assistant-page">On: <strong>{page}</strong></p>
          <p className="assistant-intro">Ask about this page or how Lucia works. For now the assistant only gives advice: it can’t read or change anything in your lab.</p>
          <div className="assistant-starters">{starters.map(text => <button key={text} type="button" className="assistant-starter"
            disabled={!canAsk} onClick={() => send(text)}><span>{text}</span><Icon name="arrow" /></button>)}</div>
        </div>}
        {messages.map((message, index) => <ChatBubble key={message.id} message={message} streaming={busy && index === messages.length - 1}
          onRetry={canAsk && index === messages.length - 1 ? retry : undefined} />)}
        {answering && <p className="assistant-working"><Loader2Icon className="animate-spin" />Answering…</p>}
        {notice}
      </ConversationContent>
      <ConversationScrollButton aria-label="Scroll to the latest message" />
    </Conversation>
    <div className="assistant-composer" ref={composer}>
      {models && needsGitHub && <GitHubGate github={github} onFocusInput={focusInput} />}
      <PromptInput onSubmit={submit}>
        <PromptInputBody>
          <PromptInputTextarea ref={input} aria-label="Message the assistant" placeholder="Ask about this page or your lab…" maxLength={maxMessageLength}
            onKeyDown={event => { if (event.key === 'Enter' && !event.shiftKey && busy) event.preventDefault() }} />
        </PromptInputBody>
        <PromptInputFooter className="assistant-footer">
          <PromptInputTools>
            <div className="assistant-mode" role="group" aria-label="Answer mode">
              <button type="button" aria-pressed={mode === 'plan'} title="Get a short plan to review first" onClick={() => onMode('plan')}>Plan</button>
              <button type="button" aria-pressed={mode === 'execute'} title="Get a direct answer" onClick={() => onMode('execute')}>Execute</button>
            </div>
            {model && models && <PromptInputSelect value={model} onValueChange={pick}>
              <PromptInputSelectTrigger className="assistant-model" aria-label="Model" title={models.models.find(item => item.id === model)?.name}>
                <PromptInputSelectValue /></PromptInputSelectTrigger>
              <PromptInputSelectContent className="assistant" position="popper" side="top" align="start" collisionPadding={16} onCloseAutoFocus={focusSignIn}>
                {models.sources.map(source => {
                  const signIn = source.id === 'github' && !models.connected
                  return (signIn || source.models.length > 0 || source.reason) && <SelectGroup key={source.id}>
                    <SelectLabel>{source.name}</SelectLabel>
                    {source.models.map(item => <PromptInputSelectItem key={item.id} value={item.id}>{item.name}</PromptInputSelectItem>)}
                    {signIn ? <PromptInputSelectItem value={signInModel}>Sign in with GitHub…</PromptInputSelectItem>
                      : source.reason && <p className="assistant-model-reason">{source.reason}</p>}
                  </SelectGroup>
                })}
              </PromptInputSelectContent>
            </PromptInputSelect>}
          </PromptInputTools>
          <PromptInputSubmit className="assistant-send" status={busy ? 'streaming' : undefined} onStop={stopAnswer}
            disabled={!busy && !(canAsk && draft.textInput.value.trim())} aria-label={busy ? 'Stop answering' : 'Send message'}>
            <Icon name={busy ? 'stop' : 'send'} />
          </PromptInputSubmit>
        </PromptInputFooter>
      </PromptInput>
    </div>
  </div>
}

type HistoryProps = { auth: AuthRef; current: string; focusKey: number; onOpen: (chat: ChatSummary) => void; onForget: (id: string) => void }

function ChatHistory({ auth, current, focusKey, onOpen, onForget }: HistoryProps) {
  const heading = useRef<HTMLHeadingElement>(null)
  const [chats, setChats] = useState<ChatSummary[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [confirming, setConfirming] = useState<string | null>(null)
  const [deleting, setDeleting] = useState(false)
  const [deleteError, setDeleteError] = useState<string | null>(null)
  useEffect(() => { heading.current?.focus() }, [focusKey])
  useEffect(() => {
    const controller = new AbortController()
    ownerRequest(auth.current.session, auth.current.refreshSession, '/api/assistant/sessions', 'GET', undefined, controller.signal)
      .then(response => response.json())
      .then(value => { if (!controller.signal.aborted) setChats(parseSessions(value)) })
      .catch(failure => {
        if (controller.signal.aborted) return
        console.error('Lucia could not list assistant chats.', failure)
        setLoadError(failureMessage(failure, 'Lucia could not list your chats.'))
      })
    return () => controller.abort()
  }, [auth])

  function ask(id: string) {
    setConfirming(value => value === id ? null : id)
    setDeleteError(null)
  }
  async function remove(id: string) {
    setDeleting(true)
    setDeleteError(null)
    try {
      await ownerRequest(auth.current.session, auth.current.refreshSession, `/api/assistant/sessions/${id}`, 'DELETE')
      setChats(items => items && items.filter(item => item.id !== id))
      setConfirming(null)
      onForget(id)
      heading.current?.focus()
    } catch (failure) {
      setDeleteError(failureMessage(failure, 'Lucia could not delete this chat.'))
    } finally {
      setDeleting(false)
    }
  }

  return <section className="assistant-history" aria-labelledby="assistant-history-heading">
    <h3 id="assistant-history-heading" ref={heading} tabIndex={-1}>Your chats</h3>
    {loadError ? <Alert>{loadError}</Alert>
      : !chats ? <p className="assistant-loading"><Loader2Icon className="animate-spin" />Loading your chats…</p>
        : !chats.length ? <p className="assistant-empty-history">No saved chats yet. Chats you start are kept on your host.</p>
          : <ul className="assistant-history-list">{chats.map(chat => <li key={chat.id}>
            <button type="button" className="assistant-history-open" aria-current={chat.id === current ? 'true' : undefined} onClick={() => onOpen(chat)}>
              <strong>{chat.title}</strong><span>{chat.running ? 'Answering now' : updated(chat.updated)}</span>
            </button>
            <button type="button" className="assistant-icon-button" aria-label={`Delete “${chat.title}”`} title="Delete chat"
              aria-expanded={confirming === chat.id} onClick={() => ask(chat.id)}><Icon name="trash" /></button>
            {confirming === chat.id && <div className="assistant-confirm">
              <p className={deleteError ? 'assistant-failed' : undefined} aria-live="polite">{deleteError ?? 'Delete this chat from your host? This can’t be undone.'}</p>
              <button type="button" className="button secondary assistant-danger" disabled={deleting} onClick={() => void remove(chat.id)}>Delete chat</button>
              <button type="button" className="text-link" onClick={() => setConfirming(null)}>Cancel</button>
            </div>}
          </li>)}</ul>}
  </section>
}

// Signing out here only drops the host's token; revoking needs the App's client secret, so GitHub keeps its record.
function GitHubAccount({ github }: { github: GitHub }) {
  const [confirming, setConfirming] = useState(false)
  const [working, setWorking] = useState(false)
  const [failure, setFailure] = useState<string | null>(null)
  const [done, setDone] = useState(false)
  const result = useRef<HTMLParagraphElement>(null)
  useEffect(() => { if (done) result.current?.focus() }, [done])
  const { status } = github
  if (done) return <div className="assistant-account">
    <p ref={result} tabIndex={-1}><Icon name="user" /><span>Signed out of GitHub on this host.</span></p></div>
  if (status?.state !== 'connected') return null

  async function signOut() {
    if (working) return
    setWorking(true)
    setFailure(null)
    try {
      await github.disconnect()
      setDone(true)
    } catch (error) {
      setFailure(failureMessage(error, 'Lucia could not sign out of GitHub.'))
    } finally {
      setWorking(false)
    }
  }

  return <div className="assistant-account">
    <p><Icon name="user" /><span>Signed in to GitHub{status.login && <> as <strong>@{status.login}</strong></>}</span></p>
    <button type="button" className="text-link" aria-expanded={confirming} onClick={() => { setConfirming(value => !value); setFailure(null) }}>Disconnect</button>
    {confirming && <div className="assistant-confirm">
      <p className={failure ? 'assistant-failed' : undefined} aria-live="polite">{failure ?? <>Lucia will forget your GitHub sign-in on this host.
        GitHub keeps listing Lucia under <a href="https://github.com/settings/apps/authorizations" target="_blank" rel="noreferrer">Authorized GitHub Apps</a> until
        you revoke it there.</>}</p>
      <button type="button" className="button secondary assistant-danger" aria-disabled={working} onClick={() => void signOut()}>Disconnect GitHub</button>
      <button type="button" className="text-link" onClick={() => setConfirming(false)}>Cancel</button>
    </div>}
  </div>
}

type PanelProps = {
  session: AuthenticationSession
  refreshSession: () => Promise<void>
  page: string
  side: DockSide
  layout: DockLayout
  onMove: () => void
  onClose: () => void
  focusToken: number
}

function savedChat() {
  const id = readStored(chatKey)
  return isChatId(id) ? { id, fresh: false, nonce: 0 } : { id: newChatId(), fresh: true, nonce: 0 }
}

export default function AssistantPanel({ session, refreshSession, page, side, layout, onMove, onClose, focusToken }: PanelProps) {
  const auth = useRef<Auth>({ session, refreshSession })
  useLayoutEffect(() => { auth.current = { session, refreshSession } }, [session, refreshSession])
  const [chat, setChat] = useState(savedChat)
  const [view, setView] = useState<'chat' | 'history'>('chat')
  const [title, setTitle] = useState(() => chat.fresh ? 'New chat' : 'Chat')
  const [models, setModels] = useState<AssistantModels | null>(null)
  const [model, setModel] = useState<string>()
  const [mode, setMode] = useState<AssistantMode>('execute')
  const [focusCount, setFocusCount] = useState(0)
  const [modelsRound, setModelsRound] = useState(0)
  const github = useGitHub(auth)
  const { check: checkGitHub, setNotice } = github
  const linked = github.status ? github.status.state === 'connected' : null

  // Every source lists its own models, so Copilot's sign-in gates only Copilot's; a refusal explains itself above the composer.
  useEffect(() => {
    const controller = new AbortController()
    ownerRequest(auth.current.session, auth.current.refreshSession, '/api/assistant/models', 'GET', undefined, controller.signal)
      .then(response => response.json())
      .then(value => {
        if (controller.signal.aborted) return
        const available = parseModels(value)
        setModels(available)
        setModel(chooseModel(available, readStored(modelKey)))
        if (available.connected) setNotice(null)
        else {
          // When GitHub ends the sign-in, its explanation stays after the status turns disconnected.
          if (linked) setNotice(available.sources.find(source => source.id === 'github')?.reason ?? null)
          void checkGitHub()
        }
      })
      .catch(failure => {
        if (controller.signal.aborted) return
        console.warn('Lucia could not list assistant models.', failure)
        setModels(current => current ?? { connected: false, sources: [], models: [] })
      })
    return () => controller.abort()
  }, [linked, modelsRound, checkGitHub, setNotice])
  const failed = useCallback(() => {
    void checkGitHub()
    setModelsRound(round => round + 1)
  }, [checkGitHub])

  function startChat() {
    setChat(current => ({ id: newChatId(), fresh: true, nonce: current.nonce + 1 }))
    setTitle('New chat')
    writeStored(chatKey, null)
  }
  function newChat() {
    startChat()
    setView('chat')
    setFocusCount(count => count + 1)
  }
  function openChat(summary: ChatSummary) {
    setChat(current => current.id === summary.id ? current : { id: summary.id, fresh: false, nonce: current.nonce + 1 })
    setTitle(summary.title)
    setView('chat')
    setFocusCount(count => count + 1)
    writeStored(chatKey, summary.id)
  }
  function toggleHistory() {
    if (view === 'chat') return setView('history')
    setView('chat')
    setFocusCount(count => count + 1)
  }
  function chooseModelId(id: string) {
    setModel(id)
    writeStored(modelKey, id)
  }
  const started = useCallback((text: string) => {
    writeStored(chatKey, chat.id)
    setTitle(chatTitle(text))
  }, [chat.id])
  // A fresh Chat avoids appending the host's from-the-start replay onto a partly streamed answer.
  const reload = useCallback(() => setChat(current => ({ ...current, fresh: false, nonce: current.nonce + 1 })), [])

  return <PromptInputProvider>
    <div className="assistant-panel">
      <header className="assistant-header">
        <div className="assistant-heading"><h2>Assistant</h2><p>{title}</p></div>
        <div className="assistant-actions">
          <button type="button" className="assistant-icon-button" aria-label="New chat" title="New chat" onClick={newChat}><Icon name="plus" /></button>
          <button type="button" className="assistant-icon-button" aria-label="Chat history" title="Chat history" aria-pressed={view === 'history'} onClick={toggleHistory}><Icon name="history" /></button>
          {layout !== 'sheet' && <button type="button" className="assistant-icon-button" aria-label={`Move to the ${side === 'right' ? 'left' : 'right'}`}
            title={`Move to the ${side === 'right' ? 'left' : 'right'}`} onClick={onMove}><Icon name={side === 'right' ? 'dock-left' : 'dock-right'} /></button>}
          <button type="button" className="assistant-icon-button" aria-label="Close assistant" title="Close assistant" onClick={onClose}><Icon name="close" /></button>
        </div>
      </header>
      <ChatView key={`${chat.id}:${chat.nonce}`} id={chat.id} fresh={chat.fresh} hidden={view === 'history'} auth={auth} page={page}
        github={github} models={models} model={model} onModel={chooseModelId} mode={mode} onMode={setMode}
        focusKey={focusToken + focusCount} onStarted={started} onTitle={setTitle} onReload={reload} onFailed={failed} />
      {view === 'history' && <ChatHistory auth={auth} current={chat.id} focusKey={focusToken} onOpen={openChat}
        onForget={id => { if (id === chat.id) startChat() }} />}
      {view === 'history' && <GitHubAccount github={github} />}
    </div>
  </PromptInputProvider>
}
