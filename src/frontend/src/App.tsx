import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { Icon } from './Icon'
import { LocalAI } from './LocalAI'
import { HardwareOnboarding } from './HardwareOnboarding'
import { SparkHealth } from './SparkHealth'
import { InferenceKeys } from './InferenceKeys'
import { ModelManager } from './ModelManager'
import { AuthenticationPanel } from './AccountAccess'
import { PortalNavigation } from './PortalNavigation'
import { AdGuardSettings } from './AdGuardSettings'
import { DomainOnboarding } from './DomainOnboarding'
import { routeDestination } from './navigation'
import { useAuthentication } from './useAuthentication'
import { inferenceConnection } from './authentication'
import type { AuthenticationSession } from './authentication'
import { parseModels } from './playground'
import type { HostedModel } from './playground'
import { defaultPreferences, parseRoute, themeColors, themes, validatePreferences } from './dashboard'
import type { Appearance, Preferences } from './dashboard'
import './App.css'

// Retain existing appearance choices while removing the old preview experience.
const preferenceKey = 'lucia.preview.appearance.v1'

function loadPreferences(): { value: Preferences; notice: string | null } {
  try {
    const saved = localStorage.getItem(preferenceKey)
    return { value: saved ? validatePreferences(JSON.parse(saved)) : defaultPreferences, notice: null }
  } catch (error) {
    console.warn('Lucia could not read saved appearance preferences.', error)
    return { value: defaultPreferences, notice: 'Saved appearance settings could not be read. The default theme is being used.' }
  }
}

function HomeOverview({ session, refreshSession }: { session: AuthenticationSession; refreshSession: () => Promise<void> }) {
  const [models, setModels] = useState<HostedModel[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const request = useRef<AbortController | null>(null)
  const refresh = useCallback(async () => {
    request.current?.abort()
    const controller = new AbortController()
    request.current = controller
    setLoading(true)
    setError(null)
    try {
      const response = await fetch(inferenceConnection(session).models, { credentials: 'same-origin', signal: controller.signal })
      if (session.enabled && response.status === 401) void refreshSession()
      if (!response.ok) throw new Error(`The inference host returned HTTP ${response.status}.`)
      const available = parseModels(await response.json())
      if (!controller.signal.aborted) setModels(available)
    } catch (failure) {
      if (!controller.signal.aborted) {
        console.error('Could not read local AI status.', failure)
        setError(failure instanceof Error ? failure.message : 'Local AI status is unavailable.')
      }
    } finally {
      if (!controller.signal.aborted) setLoading(false)
    }
  }, [session, refreshSession])
  useEffect(() => {
    void refresh()
    return () => request.current?.abort()
  }, [refresh])
  const model = models.find(item => item.capabilities.includes('chat'))
  return <>
    <div className="page-intro home-intro">
      <h1>Your lab, within reach.</h1>
      <p><strong>{location.hostname}</strong> is your home base. Check in, then choose where to work.</p>
    </div>
    <nav className="home-launches" aria-label="Quick access">
      <a href="#/ai"><Icon name="spark" /><span>Open playground</span><Icon name="arrow" /></a>
      {session.isOwner && <><a href="#/ai/models"><Icon name="drive" /><span>Manage models</span><Icon name="arrow" /></a>
        <a href="#/ai/keys"><Icon name="shield" /><span>Connect an app</span><Icon name="arrow" /></a></>}
    </nav>
    <div className={`home-workspace-grid ${session.isOwner ? '' : 'home-member-grid'}`}>
    <SparkHealth session={session} refreshSession={refreshSession} />
    <section className="surface local-ai-overview" aria-labelledby="home-ai-heading">
      <div className="overview-heading"><span className="icon-tile tone-accent"><Icon name="spark" /></span><div>
        <h2 id="home-ai-heading">Local AI</h2>
        <span className={`status status-${error ? 'amber' : loading || !model ? 'muted' : 'green'}`} role="status">
          <Icon name={loading ? 'clock' : error || !model ? 'unknown' : 'check'} />
          {loading ? 'Checking the host' : error ? 'Status unavailable' : model ? 'Ready to respond' : 'No chat model loaded'}
        </span>
      </div></div>
      {error ? <div className="home-status-error" role="alert"><p>{error} Model availability cannot be confirmed.</p><button className="text-link" onClick={() => void refresh()}>Check again <Icon name="refresh" /></button></div>
        : !loading && model ? <><p className="home-model-name">{model.id}</p><p className="muted">{model.context_length?.toLocaleString() ?? 'Unreported'} token context · {model.backend?.replace(/_/g, ' ').toUpperCase() ?? 'Backend not reported'}</p></>
          : <p className="muted">{loading ? 'Reading current model availability, not a sample status.' : 'Your host is reachable. A model needs to be loaded before it can answer.'}</p>}
      <a className="text-link" href="#/ai">Open playground <Icon name="arrow" /></a>
    </section>
    </div>
    {session.isOwner && <div className="future-sections">
      <section aria-labelledby="home-devices-heading"><Icon name="devices" /><h2 id="home-devices-heading">Devices</h2><p>Prepare for new hardware, review discovered servers, and see what is ready for onboarding.</p><a className="text-link" href="#/devices">View devices <Icon name="chevron" /></a></section>
      <section aria-labelledby="home-tasks-heading"><Icon name="tasks" /><h2 id="home-tasks-heading">Tasks</h2><p>Follow real hardware approvals and installation observations. Nothing runs without the required authorization.</p><a className="text-link" href="#/tasks">View tasks <Icon name="chevron" /></a></section>
    </div>}
  </>
}

function App() {
  const authentication = useAuthentication()
  const [saved] = useState(loadPreferences)
  const [preferences, setPreferences] = useState(saved.value)
  const [storageNotice, setStorageNotice] = useState(saved.notice)
  const [systemDark, setSystemDark] = useState(() => window.matchMedia('(prefers-color-scheme: dark)').matches)
  const [route, setRoute] = useState(() => parseRoute(location.hash))
  const [playgroundVisited, setPlaygroundVisited] = useState(false)
  const main = useRef<HTMLElement>(null)
  const firstRoute = useRef(true)
  const scheme = preferences.appearance === 'system' ? (systemDark ? 'dark' : 'light') : preferences.appearance

  useEffect(() => {
    const query = window.matchMedia('(prefers-color-scheme: dark)')
    const update = () => setSystemDark(query.matches)
    query.addEventListener('change', update)
    return () => query.removeEventListener('change', update)
  }, [])
  useEffect(() => {
    if (route.page === 'ai') setPlaygroundVisited(true)
  }, [route.page])
  useLayoutEffect(() => {
    const colors = themeColors(preferences, scheme)
    const root = document.documentElement
    root.dataset.scheme = scheme
    root.style.setProperty('--accent', colors.accent)
    root.style.setProperty('--on-accent', colors.onAccent)
    root.style.setProperty('--accent-ink', colors.accentInk)
  }, [preferences, scheme])
  useEffect(() => {
    const navigate = () => setRoute(parseRoute(location.hash))
    window.addEventListener('hashchange', navigate)
    return () => window.removeEventListener('hashchange', navigate)
  }, [])
  useEffect(() => {
    document.title = authentication.loading ? 'Connecting · Lucia'
      : authentication.error ? 'Connection needs attention · Lucia'
        : authentication.session?.enabled && !authentication.session.authenticated ? 'Sign in · Lucia'
          : authentication.session?.enabled && !authentication.session.canAccess ? 'Access needed · Lucia'
            : `${routeDestination(route)?.label ?? 'Page not found'} · Lucia`
    if (firstRoute.current) { firstRoute.current = false; return }
    window.scrollTo({ top: 0, behavior: 'instant' })
    main.current?.focus({ preventScroll: true })
  }, [route, authentication.loading, authentication.error, authentication.session])

  function updatePreferences(next: Preferences) {
    setPreferences(next)
    try {
      localStorage.setItem(preferenceKey, JSON.stringify(next))
      setStorageNotice(null)
    } catch (error) {
      console.warn('Lucia could not save your appearance.', error)
      setStorageNotice('This browser could not save your appearance. Changes will last until you close this page.')
    }
  }
  function settings() {
    return <>
      <div className="page-intro"><h1>Make yourself at home.</h1><p>A familiar layout, in colors that feel right for you.</p></div>
      <div className="settings-grid">
        <section className="surface settings-section"><h2>Appearance</h2><p className="muted">Choose a look, or follow your device.</p>
          <fieldset><legend>Color mode</legend><div className="choice-row">{(['light', 'dark', 'system'] as Appearance[]).map(mode => <button key={mode} type="button" aria-pressed={preferences.appearance === mode} onClick={() => updatePreferences({ ...preferences, appearance: mode })}><Icon name={mode === 'light' ? 'sun' : mode === 'dark' ? 'moon' : 'system'} />{mode[0].toUpperCase() + mode.slice(1)}</button>)}</div></fieldset>
          <fieldset><legend>Theme</legend><div className="theme-choices">{themes.map(theme => <button key={theme.id} className="theme-choice" aria-pressed={preferences.theme === theme.id} onClick={() => updatePreferences({ ...preferences, theme: theme.id })}><span className="theme-dot" style={{ backgroundColor: theme[scheme] }} /><span>{theme.name}</span>{preferences.theme === theme.id && <Icon name="check" />}</button>)}</div></fieldset>
          <div className="custom-color"><div><label htmlFor="custom-accent">Or choose your own color</label><p>Button text adjusts to stay readable.</p></div><input id="custom-accent" type="color" value={preferences.customAccent} onChange={event => updatePreferences({ ...preferences, theme: 'custom', customAccent: event.target.value })} /></div>
          <div className="theme-example"><span className="icon-tile tone-accent"><Icon name="home" /></span><div><strong>{preferences.theme === 'custom' ? 'Your custom theme' : themes.find(theme => theme.id === preferences.theme)?.name}</strong><p>Same simple home. Your own feel.</p></div><span className="sample-button" aria-label="Theme color sample">Aa</span></div>
        </section>
        <section className="connection-note"><Icon name="shield" /><div><h2>Your account and connection.</h2><p>{authentication.session?.enabled ? `Signed in through Authentik as ${authentication.session.displayName || authentication.session.username}. Permissions are enforced by your host.` : 'This is a development session. Managed deployments use Authentik sign-in.'}</p><p className="section-note">Appearance is saved on this browser. Account credentials and chat history are not stored here.</p></div></section>
      </div>
    </>
  }

  if (authentication.loading || authentication.error || !authentication.session ||
    (authentication.session.enabled && (!authentication.session.authenticated || !authentication.session.canAccess)))
    return <AuthenticationPanel {...authentication} retry={() => void authentication.refresh()} />
  const session = authentication.session
  const content = route.page === 'home' ? <HomeOverview session={session} refreshSession={authentication.refresh} />
    : route.page === 'devices' || route.page === 'tasks' ? <HardwareOnboarding session={session} refreshSession={authentication.refresh} view={route.page} />
      : route.page === 'settings' ? settings()
        : route.page === 'adguard-settings' ? <AdGuardSettings session={session} refreshSession={authentication.refresh} />
          : route.page === 'domain-settings' ? <DomainOnboarding session={session} refreshSession={authentication.refresh} />
        : route.page === 'ai' ? null
          : route.page === 'ai-keys' ? <InferenceKeys session={session} refreshSession={authentication.refresh} />
            : route.page === 'ai-models' ? <ModelManager session={session} refreshSession={authentication.refresh} view={route.view ?? 'library'} />
          : <div className="empty-state"><h1>This page is not available.</h1><p>Return to Home to see what is connected.</p><a className="button primary" href="#/">Back to Home</a></div>
  return <div className="app-shell portal-workspace">
    <a className="skip-link" href="#main-content" onClick={event => { event.preventDefault(); main.current?.focus() }}>Skip to content</a>
    {!session.enabled && <div className="development-note"><strong>Development mode</strong><span>Browser sign-in is disabled on this development host.</span></div>}
    <PortalNavigation route={route} session={session} />
    {storageNotice && <div className="storage-notice" role="alert"><Icon name="attention" /><p>{storageNotice}</p><button className="icon-button" aria-label="Dismiss appearance notice" onClick={() => setStorageNotice(null)}><Icon name="close" /></button></div>}
    <main id="main-content" ref={main} tabIndex={-1}>
      {content}
      {(playgroundVisited || route.page === 'ai') && <div hidden={route.page !== 'ai'}>
        <LocalAI session={session} refreshSession={authentication.refresh} active={route.page === 'ai'} />
      </div>}
    </main>
    <footer className="app-footer"><span>{session.enabled ? 'Connected through Authentik' : 'Trusted-network development connection'}</span><a className="text-link" href="#/ai">Local AI <Icon name="arrow" /></a></footer>
  </div>
}

export default App
