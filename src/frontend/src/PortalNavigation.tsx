import { useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import type { Route } from './dashboard'
import { Icon } from './Icon'
import type { IconName } from './Icon'
import { AccountActions } from './AccountAccess'
import { areas, findDestinations, routeDestination, visibleDestinations } from './navigation'
import type { AreaId } from './navigation'
import './PortalNavigation.css'

const areaIcons: Record<AreaId, IconName> = { overview: 'home', lab: 'devices', ai: 'spark', settings: 'settings' }

export function PortalNavigation({ route, session }: { route: Route; session: AuthenticationSession }) {
  const [panel, setPanel] = useState<'areas' | 'search' | 'account' | null>(null)
  const [query, setQuery] = useState('')
  const dialog = useRef<HTMLDialogElement>(null)
  const opener = useRef<HTMLElement | null>(null)
  const search = useRef<HTMLInputElement>(null)
  const current = routeDestination(route)
  const area = areas.find(item => item.id === current?.area) ?? areas[0]
  const visible = visibleDestinations(session.isOwner)
  const results = findDestinations(query, session.isOwner)
  const siblings = visible.filter(item => item.area === area.id)
  const title = panel === 'search' ? 'Find a tool' : panel === 'account' ? 'Your account' : 'Explore your workspace'

  function open(next: NonNullable<typeof panel>) {
    opener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null
    setQuery('')
    setPanel(next)
  }
  function close(restoreFocus = true) {
    dialog.current?.close()
    setPanel(null)
    if (restoreFocus && opener.current?.isConnected) opener.current.focus()
  }
  useEffect(() => {
    if (!panel || !dialog.current) return
    dialog.current.showModal()
    if (panel === 'search') search.current?.focus()
    else dialog.current.querySelector<HTMLButtonElement>('.portal-dialog-close')?.focus()
    const previous = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => { document.body.style.overflow = previous }
  }, [panel])
  useEffect(() => {
    const changed = () => {
      dialog.current?.close()
      setPanel(null)
    }
    const key = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault()
        opener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null
        setQuery('')
        setPanel('search')
      }
    }
    window.addEventListener('hashchange', changed)
    window.addEventListener('keydown', key)
    return () => { window.removeEventListener('hashchange', changed); window.removeEventListener('keydown', key) }
  }, [])
  function follow(event: React.MouseEvent<HTMLAnchorElement>) {
    if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return
    close(false)
    if (event.currentTarget.hash === location.hash) document.getElementById('main-content')?.focus()
  }
  return <>
    <header className="portal-header">
      <a className="brand" href="#/" aria-label="Lucia Home">Lucia<span className="portal-brand-mark" aria-hidden="true">.</span></a>
      <span className="portal-header-divider" aria-hidden="true" />
      <button className="area-switch" onClick={() => open('areas')} aria-label={`Switch workspace, current: ${area.label}`}
        aria-haspopup="dialog" aria-expanded={panel === 'areas'}>
        <Icon name={areaIcons[area.id]} /><span>{area.label}</span><Icon name="down" />
      </button>
      <div className="portal-header-tools">
        <button className="portal-search-button" onClick={() => open('search')} aria-haspopup="dialog" aria-label="Find a tool">
          <Icon name="search" /><span>Find a tool</span><kbd>⌘ / Ctrl K</kbd>
        </button>
        <button className="portal-account-button" onClick={() => open('account')} aria-haspopup="dialog" aria-label="Account and appearance">
          <Icon name="user" /><span>{session.displayName || session.username || 'Development'}</span>
        </button>
      </div>
    </header>
    <div className="portal-context">
      <nav className="portal-breadcrumb" aria-label="Current location">
        <span>{area.label}</span><Icon name="chevron" /><strong>{current?.label ?? 'Page not found'}</strong>
      </nav>
      <nav className="workspace-navigation" aria-label={`${area.label} navigation`}>
        {siblings.map(item => <a key={item.page} href={item.href} aria-current={route.page === item.page ? 'page' : undefined}>{item.label}</a>)}
      </nav>
    </div>
    <dialog className={`portal-dialog ${panel === 'account' ? 'portal-account-dialog' : ''}`} ref={dialog}
      aria-labelledby="portal-dialog-title" onCancel={event => { event.preventDefault(); close() }}
      onClick={event => {
        if (event.target !== event.currentTarget) return
        const rect = event.currentTarget.getBoundingClientRect()
        if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) close()
      }}>
      <div className="portal-dialog-heading"><h2 id="portal-dialog-title">{title}</h2>
        <button className="portal-dialog-close" aria-label="Close navigation" onClick={() => close()}><Icon name="close" /></button></div>
      {panel === 'search' && <>
        <label className="visually-hidden" htmlFor="tool-query">Search available tools</label>
        <div className="tool-search-field"><Icon name="search" /><input id="tool-query" ref={search} value={query}
          placeholder="Models, API keys, hardware…" autoComplete="off" onChange={event => setQuery(event.target.value)}
          onKeyDown={event => {
            if (event.key === 'ArrowDown') { event.preventDefault(); dialog.current?.querySelector<HTMLAnchorElement>('.tool-result')?.focus() }
          }} /></div>
        <p className="portal-dialog-note">Jump to an available tool. This searches navigation, not your private data.</p>
        <div className="tool-results">{results.map(item => <a className="tool-result" key={item.page} href={item.href} onClick={follow}>
          <div><strong>{item.label}</strong><span>{item.description}</span></div><span className="tool-result-area">{areas.find(area => area.id === item.area)?.label}</span><Icon name="arrow" />
        </a>)}</div>
        {results.length === 0 && <p className="tool-empty" role="status">No matching tools. Try “models,” “keys,” or “appearance.”</p>}
      </>}
      {panel === 'areas' && <>
        <p className="portal-dialog-note">A place for each kind of work. Choose a workspace or go straight to a tool.</p>
        <div className="workspace-directory">{areas.map(area => {
          const tools = visible.filter(item => item.area === area.id)
          if (!tools.length) return null
          return <section className="workspace-directory-section" key={area.id}>
            <div className="workspace-directory-heading"><Icon name={areaIcons[area.id]} /><div><h3>{area.label}</h3><p>{area.description}</p></div></div>
            <nav aria-label={`${area.label} tools`}>{tools.map(item => <a key={item.page} href={item.href} onClick={follow}
              aria-current={route.page === item.page ? 'page' : undefined}>{item.label}<Icon name={route.page === item.page ? 'check' : 'arrow'} /></a>)}</nav>
          </section>
        })}</div>
      </>}
      {panel === 'account' && <div className="portal-account-content">
        <p>Signed in to <strong>{location.hostname}</strong>{session.enabled ? ' through Authentik.' : ' in development mode.'}</p>
        <AccountActions session={session} />
        <a className="tool-result" href="#/settings" onClick={follow}><Icon name="settings" /><div><strong>Appearance</strong><span>Color mode, themes, and custom accent</span></div><Icon name="arrow" /></a>
      </div>}
    </dialog>
  </>
}
