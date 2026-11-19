import { useEffect, useRef, useState, useSyncExternalStore } from 'react'
import type { AuthenticationSession } from './authentication'
import type { Route } from './dashboard'
import { Icon } from './Icon'
import type { IconName } from './Icon'
import { AccountActions } from './AccountAccess'
import { areas, breadcrumbs, findDestinations, routeDestination, visibleAreas, workspaceLinks } from './navigation'
import type { AreaId } from './navigation'
import './PortalNavigation.css'

const areaIcons: Record<AreaId, IconName> = { lab: 'devices', apps: 'layers', network: 'map', ai: 'spark', assistant: 'chat', settings: 'settings' }
const narrowNavigation = window.matchMedia('(max-width: 1000px)')
const constrainedPush = window.matchMedia('(min-width: 1200px) and (max-width: 1464px)')
const collapsedKey = 'lucia.portal.navigation.collapsed.v1'

function subscribeViewport(change: () => void) {
  narrowNavigation.addEventListener('change', change)
  constrainedPush.addEventListener('change', change)
  return () => {
    narrowNavigation.removeEventListener('change', change)
    constrainedPush.removeEventListener('change', change)
  }
}

function navigationViewport() {
  return narrowNavigation.matches ? 'narrow' : constrainedPush.matches ? 'constrained' : 'wide'
}

function loadCollapsed() {
  try {
    const saved = localStorage.getItem(collapsedKey)
    if (saved === null || saved === 'false') return false
    if (saved === 'true') return true
    console.warn('Lucia found an invalid navigation preference.')
    return false
  } catch (error) {
    console.warn('Lucia could not read the navigation preference.', error)
    return false
  }
}

// A small count beside a destination, such as the skills waiting for review.
function Badge({ count, what }: { count?: number; what: string }) {
  return count ? <><span className="nav-badge" aria-hidden="true">{count}</span><span className="visually-hidden"> ({count} {what})</span></> : null
}

export function PortalNavigation({ route, session, assistant, skillsToReview = 0 }: { route: Route; session: AuthenticationSession; assistant?: { open: boolean; toggle: () => void; waiting: number }; skillsToReview?: number }) {
  const [panel, setPanel] = useState<'navigation' | 'search' | 'account' | null>(null)
  const [query, setQuery] = useState('')
  const dialog = useRef<HTMLDialogElement>(null)
  const opener = useRef<HTMLElement | null>(null)
  const search = useRef<HTMLInputElement>(null)
  const breadcrumbNav = useRef<HTMLElement>(null)
  const current = routeDestination(route)
  const area = areas.find(item => item.id === current?.area)
  const areaId = area?.id
  const [expandedAreas, setExpandedAreas] = useState<AreaId[]>(() => areaId ? [areaId] : [])
  const [collapsed, setCollapsed] = useState(loadCollapsed)
  const viewport = useSyncExternalStore(subscribeViewport, navigationViewport)
  const useDrawer = viewport === 'narrow' || (viewport === 'constrained' && !!assistant?.open)
  const availableAreas = visibleAreas(session.isOwner)
  const results = findDestinations(query, session.isOwner)
  const crumbs = breadcrumbs(route)
  const title = panel === 'search' ? 'Find a tool' : panel === 'account' ? 'Your account' : 'Navigation'

  function changeCollapsed(next: boolean) {
    setCollapsed(next)
    try {
      localStorage.setItem(collapsedKey, String(next))
    } catch (error) {
      console.warn('Lucia could not save the navigation preference.', error)
    }
  }
  function toggleArea(id: AreaId, compact: boolean) {
    if (compact) {
      changeCollapsed(false)
      setExpandedAreas(previous => previous.includes(id) ? previous : [...previous, id])
      return
    }
    setExpandedAreas(previous => previous.includes(id) ? previous.filter(item => item !== id) : [...previous, id])
  }
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
    if (!dialog.current.open) dialog.current.showModal()
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
  useEffect(() => {
    if (areaId) setExpandedAreas(previous => previous.includes(areaId) ? previous : [...previous, areaId])
  }, [areaId, route])
  useEffect(() => {
    const nav = breadcrumbNav.current
    if (nav) nav.scrollLeft = nav.scrollWidth
  }, [route, useDrawer])
  useEffect(() => {
    if (useDrawer || panel !== 'navigation') return
    dialog.current?.close()
    setPanel(null)
    document.querySelector<HTMLButtonElement>('.portal-sidebar-toggle')?.focus()
  }, [useDrawer, panel])
  function follow(event: React.MouseEvent<HTMLAnchorElement>) {
    if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return
    close(false)
    if (event.currentTarget.hash === location.hash) document.getElementById('main-content')?.focus()
  }
  function navigation(compact: boolean, source: 'sidebar' | 'drawer') {
    return <nav className="portal-navigation" id={`portal-navigation-${source}`} aria-label="Main navigation">
      <a className="portal-navigation-home" href="#/" onClick={follow} aria-current={route.page === 'home' ? 'page' : undefined}
        title={compact ? 'Home' : undefined}>
        <Icon name="home" /><span className={compact ? 'visually-hidden' : ''}>Home</span>
      </a>
      {availableAreas.map(item => {
        const expanded = !compact && expandedAreas.includes(item.id)
        const groupId = `portal-navigation-${source}-${item.id}`
        return <section className="portal-navigation-group" key={item.id} data-active={areaId === item.id}>
          <button className="portal-navigation-group-button" type="button" aria-expanded={expanded} aria-controls={groupId}
            title={compact ? item.label : undefined} onClick={() => toggleArea(item.id, compact)}>
            <Icon name={areaIcons[item.id]} /><span className={compact ? 'visually-hidden' : 'portal-navigation-label'}>{item.label}</span>
            {!expanded && item.id === 'assistant' && <Badge count={skillsToReview} what="skills to review" />}
            {!compact && <Icon className="portal-navigation-chevron" name="down" />}
          </button>
          <div className="portal-navigation-links" id={groupId} hidden={!expanded}>
            {workspaceLinks(item.id, route, session.isOwner).map(link => <a key={link.id} href={link.href} onClick={follow} aria-current={link.current}>
              <span>{link.label}</span>{link.id === 'assistant-skills' && <Badge count={skillsToReview} what="to review" />}
            </a>)}
          </div>
        </section>
      })}
    </nav>
  }
  const breadcrumb = <nav className="portal-breadcrumb" ref={breadcrumbNav} aria-label="Breadcrumb">
    <ol>{crumbs.map((item, index) => <li key={`${item.label}-${index}`}>
      {item.href ? <a href={item.href} onClick={follow}>{item.label}</a> : <span aria-current="page">{item.label}</span>}
    </li>)}
      {!current && <li><span aria-current="page">Page not found</span></li>}
    </ol>
  </nav>
  return <>
    {!useDrawer && <aside className="portal-sidebar" data-collapsed={collapsed}>
      <div className="portal-sidebar-top">
        <a className="brand portal-sidebar-brand" href="#/" aria-label="Lucia Home" onClick={follow}>
          <span className="portal-brand-full">Lucia</span><span className="portal-brand-short" aria-hidden="true">L</span><span className="portal-brand-mark" aria-hidden="true">.</span>
        </a>
        <button className="portal-sidebar-toggle" type="button" onClick={() => changeCollapsed(!collapsed)}
          aria-label={collapsed ? 'Expand navigation' : 'Collapse navigation'} title={collapsed ? 'Expand navigation' : 'Collapse navigation'}
          aria-expanded={!collapsed} aria-controls="portal-navigation-sidebar"><Icon name="dock-left" /></button>
      </div>
      {navigation(collapsed, 'sidebar')}
    </aside>}
    <header className="portal-header">
      {useDrawer ? <>
        <button className="portal-menu-button" type="button" onClick={() => open('navigation')} aria-label="Open navigation"
          aria-haspopup="dialog" aria-expanded={panel === 'navigation'} aria-controls="portal-dialog"><Icon name="menu" /></button>
        <a className="brand" href="#/" aria-label="Lucia Home" onClick={follow}>Lucia<span className="portal-brand-mark" aria-hidden="true">.</span></a>
      </> : breadcrumb}
      <div className="portal-header-tools">
        {assistant && <button className="portal-search-button portal-assistant-button" type="button" data-assistant-toggle onClick={assistant.toggle}
          aria-expanded={assistant.open} aria-controls="assistant-dock" aria-keyshortcuts="Control+J Meta+J"
          aria-label={assistant.waiting && !assistant.open ? `Assistant, ${assistant.waiting} waiting for you` : 'Assistant'}>
          <Icon name="chat" /><span>Assistant</span>
          {assistant.waiting > 0 && !assistant.open && <span className="portal-assistant-waiting">{assistant.waiting}</span>}<kbd>⌘ / Ctrl J</kbd>
        </button>}
        <button className="portal-search-button" onClick={() => open('search')} aria-haspopup="dialog" aria-label="Find a tool">
          <Icon name="search" /><span>Find a tool</span><kbd>⌘ / Ctrl K</kbd>
        </button>
        <button className="portal-account-button" onClick={() => open('account')} aria-haspopup="dialog" aria-label="Account and appearance">
          <Icon name="user" /><span>{session.displayName || session.username || 'Development'}</span>
        </button>
      </div>
    </header>
    {useDrawer && <div className="portal-context">{breadcrumb}</div>}
    <dialog id="portal-dialog" className={`portal-dialog ${panel === 'account' ? 'portal-account-dialog' : ''} ${panel === 'navigation' ? 'portal-navigation-dialog' : ''}`} ref={dialog}
      aria-labelledby="portal-dialog-title" onCancel={event => { event.preventDefault(); close() }}
      onClick={event => {
        if (event.target !== event.currentTarget) return
        const rect = event.currentTarget.getBoundingClientRect()
        if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) close()
      }}>
      <div className="portal-dialog-heading"><h2 id="portal-dialog-title">{title}</h2>
        <button className="portal-dialog-close" aria-label={panel === 'navigation' ? 'Close navigation' : panel === 'search' ? 'Close search' : 'Close account'}
          onClick={() => close()}><Icon name="close" /></button></div>
      {panel === 'search' && <>
        <label className="visually-hidden" htmlFor="tool-query">Search available tools</label>
        <div className="tool-search-field"><Icon name="search" /><input id="tool-query" ref={search} value={query}
          placeholder="Models, API keys, hardware…" autoComplete="off" onChange={event => setQuery(event.target.value)}
          onKeyDown={event => {
            if (event.key === 'ArrowDown') { event.preventDefault(); dialog.current?.querySelector<HTMLAnchorElement>('.tool-result')?.focus() }
          }} /></div>
        <p className="portal-dialog-note">Jump to an available tool. This searches navigation, not your private data.</p>
        <div className="tool-results">{results.map(item => <a className="tool-result" key={item.page} href={item.href} onClick={follow}>
          <div><strong>{item.label}</strong><span>{item.description}</span></div><span className="tool-result-area">{areas.find(area => area.id === item.area)?.label ?? 'Home'}</span><Icon name="arrow" />
        </a>)}</div>
        {results.length === 0 && <p className="tool-empty" role="status">No matching tools. Try “models,” “keys,” or “appearance.”</p>}
      </>}
      {panel === 'navigation' && navigation(false, 'drawer')}
      {panel === 'account' && <div className="portal-account-content">
        <p>Signed in to <strong>{location.hostname}</strong>{session.enabled ? ' through Authentik.' : ' in development mode.'}</p>
        <AccountActions session={session} />
        <a className="tool-result" href="#/settings" onClick={follow}><Icon name="settings" /><div><strong>Appearance</strong><span>Color mode, themes, and custom accent</span></div><Icon name="arrow" /></a>
      </div>}
    </dialog>
  </>
}
