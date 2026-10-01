import { Component, Suspense, lazy, useCallback, useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore } from 'react'
import type { ErrorInfo, KeyboardEvent as ReactKeyboardEvent, ReactNode } from 'react'
import type { DockLayout, DockSide } from './assistant'
import type { AuthenticationSession } from './authentication'
import { Icon } from './Icon'
import './AssistantDock.css'

const AssistantPanel = lazy(() => import('./AssistantPanel'))
const push = window.matchMedia('(min-width: 1200px)')
const overlay = window.matchMedia('(min-width: 700px)')

function subscribe(change: () => void) {
  push.addEventListener('change', change)
  overlay.addEventListener('change', change)
  return () => {
    push.removeEventListener('change', change)
    overlay.removeEventListener('change', change)
  }
}
const currentLayout = (): DockLayout => push.matches ? 'push' : overlay.matches ? 'overlay' : 'sheet'

class PanelBoundary extends Component<{ children: ReactNode; onClose: () => void }, { failed: boolean }> {
  state = { failed: false }
  static getDerivedStateFromError() {
    return { failed: true }
  }
  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('Lucia could not load the assistant.', error, info.componentStack)
  }
  render() {
    if (!this.state.failed) return this.props.children
    return <div className="assistant-fallback" role="alert"><p>The assistant could not load. Reload the page to try again.</p>
      <button className="button secondary" type="button" onClick={this.props.onClose}>Close</button></div>
  }
}

type DockProps = {
  session: AuthenticationSession
  refreshSession: () => Promise<void>
  page: string
  open: boolean
  side: DockSide
  waiting: number
  onToggle: () => void
  onClose: () => void
  onMove: () => void
  onWaiting: (count: number) => void
}

export function AssistantDock({ session, refreshSession, page, open, side, waiting, onToggle, onClose, onMove, onWaiting }: DockProps) {
  const layout = useSyncExternalStore(subscribe, currentLayout)
  const pushed = open && layout === 'push'
  const dock = useRef<HTMLElement>(null)
  const outside = useRef<HTMLElement | null>(null)
  const wasOpen = useRef(open)
  // The panel loads on first open and then stays mounted so a closed dock keeps its chat and any running answer.
  const [mounted, setMounted] = useState(open)
  const [opened, setOpened] = useState(open)
  const [focusToken, setFocusToken] = useState(0)
  const [heard, setHeard] = useState('')
  if (open && !mounted) setMounted(true)
  if (open !== opened) {
    setOpened(open)
    if (open) setFocusToken(token => token + 1)
  }
  // An open dock's conversation log reads new requests out itself; a closed one is hidden, so name them here.
  const waitingChanged = useCallback((count: number, message?: string) => {
    onWaiting(count)
    if (message && !open) setHeard(message)
  }, [onWaiting, open])

  useEffect(() => {
    const track = (event: FocusEvent) => {
      if (event.target instanceof HTMLElement && !event.target.closest('.assistant')) outside.current = event.target
    }
    document.addEventListener('focusin', track)
    return () => document.removeEventListener('focusin', track)
  }, [])
  useLayoutEffect(() => {
    if (wasOpen.current === open) return
    wasOpen.current = open
    const active = document.activeElement
    if (open || (active && active !== document.body && !dock.current?.contains(active))) return
    const targets = [outside.current, document.querySelector<HTMLElement>('[data-assistant-toggle]'),
      document.querySelector<HTMLElement>('.assistant-bar button'), document.getElementById('main-content')]
    for (const target of targets) {
      if (!target?.isConnected) continue
      target.focus({ preventScroll: true })
      if (document.activeElement === target) return
    }
  }, [open])
  // A pushed page scrolls in its own column (AssistantDock.css), so the reading position moves between it and the window.
  useLayoutEffect(() => {
    const shell = dock.current?.parentElement
    if (!pushed || !shell) return
    const root = document.documentElement
    const top = window.scrollY
    root.dataset.assistantPush = ''
    shell.scrollTop = top
    return () => {
      const top = shell.scrollTop
      delete root.dataset.assistantPush
      window.scrollTo({ top, behavior: 'instant' })
    }
  }, [pushed])
  useEffect(() => {
    const key = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.altKey || event.shiftKey || event.repeat || event.key.toLowerCase() !== 'j') return
      if (document.querySelector('dialog[open]')) return
      event.preventDefault()
      onToggle()
    }
    window.addEventListener('keydown', key)
    return () => window.removeEventListener('keydown', key)
  }, [onToggle])
  // The phone sheet covers the page, so keep the page out of reach while it is open.
  useEffect(() => {
    const element = dock.current
    if (!open || layout !== 'sheet' || !element?.parentElement) return
    const page = [...element.parentElement.children].filter((child): child is HTMLElement =>
      child instanceof HTMLElement && child !== element && child.tagName !== 'DIALOG' && !child.inert)
    page.forEach(child => { child.inert = true })
    return () => page.forEach(child => { child.inert = false })
  }, [open, layout])

  function escape(event: ReactKeyboardEvent<HTMLElement>) {
    if (event.key !== 'Escape' || event.defaultPrevented || event.nativeEvent.isComposing || layout === 'push') return
    event.preventDefault()
    onClose()
  }

  return <>
    {layout === 'sheet' && !open && <div className="assistant-bar">
      <button type="button" onClick={onToggle} aria-expanded={false} aria-controls="assistant-dock" aria-keyshortcuts="Control+J Meta+J"
        aria-label={waiting ? `Ask the assistant, ${waiting} waiting for you` : undefined}>
        <Icon name="chat" /><span>Ask the assistant</span>{waiting > 0 && <span className="assistant-waiting">{waiting} waiting</span>}
      </button>
    </div>}
    <p className="visually-hidden" role="status">{heard}</p>
    <aside id="assistant-dock" ref={dock} className="assistant-dock assistant" data-side={side} data-layout={layout} hidden={!open}
      aria-label="Assistant" onKeyDown={escape}>
      {mounted && <PanelBoundary onClose={onClose}>
        <Suspense fallback={<p className="assistant-fallback" role="status">Loading the assistant…</p>}>
          <AssistantPanel session={session} refreshSession={refreshSession} page={page} side={side} layout={layout}
            onMove={onMove} onClose={onClose} focusToken={focusToken} onWaiting={waitingChanged} />
        </Suspense>
      </PanelBoundary>}
    </aside>
  </>
}
