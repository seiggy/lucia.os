import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { KeyboardEvent as ReactKeyboardEvent, MouseEvent as ReactMouseEvent } from 'react'
import '@fontsource/chakra-petch/latin-500.css'
import { Icon } from './Icon'
import { askAssistant } from './assistant'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { ago, assistantPrompt, buildingCategories, buildingCategoriesOf, buildingCategoryColor, buildingCategoryLabel, buildingCategoryValue, categoryLabel, clientGroups, destCategories, districtValue, districtsOf, dominant, effectiveHealth, factsFor, formatBps, groupLabels, healthLabels, kindLabels, labCounts, layoutLab, mixList, mixTotals, parseLabMap, parseLabMapLive,
  portsOf, routeOf, searchLab, serviceColors, serviceLabels, services } from './labMapModel'
import type { CitySite, ClientGroup, LabMap as LabMapData, LabMapLive, Layout, MapNode, Service } from './labMapModel'
import { LabScene, palette, toneOf, webglAvailable } from './LabMapScene'
import type { Layers } from './LabMapScene'
import './LabMap.css'

type Session = { session: AuthenticationSession; refreshSession: () => Promise<void> }
const hex = (value: number) => `#${value.toString(16).padStart(6, '0')}`
const message = (failure: unknown, fallback: string) => failure instanceof Error ? failure.message : fallback
const layerNames: { key: keyof Layers; label: string }[] = [
  { key: 'traffic', label: 'Link traffic' }, { key: 'flows', label: 'Conversations' }, { key: 'apps', label: 'Apps' }, { key: 'clients', label: 'Clients' }, { key: 'storage', label: 'Storage' },
]
const quietKinds = new Set<MapNode['kind']>(['network', 'region', 'site'])
// Destinations stay standing for 15 minutes after their traffic stops; the city is rebuilt at most once a minute.
const siteLinger = 15 * 60_000, cityEvery = 60_000
// Traffic averaged over the last minute (live) or a longer window, as Prometheus' rate range.
const spans = [{ key: '1m', label: 'Live', strip: 'Live' }, { key: '15m', label: '15m', strip: '15-minute average' },
  { key: '1h', label: '1h', strip: '1-hour average' }, { key: '24h', label: '24h', strip: '24-hour average' }] as const
type Span = typeof spans[number]['key']
const savedSpan = (): Span => spans.find(item => item.key === localStorage.getItem('labmap-span'))?.key ?? '1m'
// How many internet destinations the city holds; 0 is every one the server can read.
const caps = [{ key: '40', label: '40' }, { key: '100', label: '100' }, { key: '1000', label: '1k' }, { key: '0', label: 'All' }] as const
type Cap = typeof caps[number]['key']
const savedCap = (): Cap => caps.find(item => item.key === localStorage.getItem('labmap-sites'))?.key ?? '40'

function Segmented<T extends string>({ name, label, options, value, onChange }: { name: string; label: string; options: readonly { key: T; label: string }[]; value: T; onChange: (value: T) => void }) {
  return <div className="labmap-span" role="radiogroup" aria-label={label}><span aria-hidden="true">{label}</span>
    {options.map(item => <label key={item.key}><input type="radio" name={name} value={item.key} checked={value === item.key} onChange={() => onChange(item.key)} />{item.label}</label>)}
  </div>
}
const openLabels: Partial<Record<MapNode['kind'], string>> = {
  gateway: 'Open UniFi settings', switch: 'Open UniFi settings', ap: 'Open UniFi settings', spark: 'Open Spark', node: 'Open in Devices', app: 'Open app', storage: 'Open storage settings',
}

export default function LabMap(props: Session) {
  if (!props.session.isOwner) return <div className="page-intro"><h1>Owner access is needed.</h1><p>Ask an owner of this Lucia host to open the lab map.</p></div>
  return <LabMapView {...props} />
}

function useVisible() {
  const [visible, setVisible] = useState(() => document.visibilityState === 'visible')
  useEffect(() => {
    const change = () => setVisible(document.visibilityState === 'visible')
    document.addEventListener('visibilitychange', change)
    return () => document.removeEventListener('visibilitychange', change)
  }, [])
  return visible
}

function useMedia(query: string) {
  const [matches, setMatches] = useState(() => window.matchMedia(query).matches)
  useEffect(() => {
    const list = window.matchMedia(query), change = () => setMatches(list.matches)
    list.addEventListener('change', change)
    return () => list.removeEventListener('change', change)
  }, [query])
  return matches
}

function LabMapView({ session, refreshSession }: Session) {
  const visible = useVisible()
  const reducedMotion = useMedia('(prefers-reduced-motion: reduce)')
  const compact = useMedia('(max-width: 700px)')
  const [webgl] = useState(webglAvailable)
  const flat = reducedMotion || !webgl
  const [map, setMap] = useState<LabMapData | null>(null)
  const [mapError, setMapError] = useState<string | null>(null)
  const [mapTick, setMapTick] = useState(0)
  const [liveTick, setLiveTick] = useState(0)
  const [span, setSpan] = useState<Span>(savedSpan)
  const [cap, setCap] = useState<Cap>(savedCap)
  const [live, setLive] = useState<LabMapLive | null>(null)
  const [liveAt, setLiveAt] = useState<number | null>(null)
  const [now, setNow] = useState(() => Date.now())
  const [selected, setSelected] = useState<string | null>(null)
  const [layers, setLayers] = useState<Layers>({ traffic: true, flows: true, apps: true, clients: true, storage: true })
  const [listOpen, setListOpen] = useState(false)
  const [wallboard, setWallboard] = useState(false)
  const [tour, setTour] = useState<string | null>(null)
  const root = useRef<HTMLDivElement>(null)
  const stage = useRef<HTMLDivElement>(null)
  const plates = useRef<HTMLDivElement>(null)
  const scene = useRef<LabScene | null>(null)
  const sightings = useRef(new Map<string, CitySite & { at: number }>())
  const cityAt = useRef(0)
  const [city, setCity] = useState<CitySite[]>([])

  useEffect(() => {
    if (!visible) return
    const controller = new AbortController()
    void ownerRequest(session, refreshSession, '/api/host/lab-map', 'GET', undefined, controller.signal).then(response => response.json()).then(parseLabMap)
      .then(value => { if (!controller.signal.aborted) { setMap(value); setMapError(null) } })
      .catch(failure => { if (!controller.signal.aborted) setMapError(message(failure, 'Lucia could not draw your lab map.')) })
    const timer = window.setTimeout(() => setMapTick(value => value + 1), 60_000)
    return () => { controller.abort(); window.clearTimeout(timer) }
  }, [session, refreshSession, visible, mapTick])

  useEffect(() => {
    if (!visible || !map) return
    let controller = new AbortController()
    const read = () => {
      controller.abort()
      controller = new AbortController()
      void ownerRequest(session, refreshSession, `/api/host/lab-map/live?window=${span}&sites=${cap}`, 'GET', undefined, controller.signal).then(response => response.json()).then(parseLabMapLive)
        .then(value => { setLive(value); setLiveAt(Date.now()) })
        .catch(() => undefined)
    }
    read()
    const timer = window.setInterval(read, 10_000)
    return () => { controller.abort(); window.clearInterval(timer) }
  }, [session, refreshSession, visible, map, span, cap, liveTick])

  useEffect(() => {
    localStorage.setItem('labmap-span', span)
    localStorage.setItem('labmap-sites', cap)
    sightings.current.clear()
    cityAt.current = 0
  }, [span, cap])

  useEffect(() => {
    if (!visible) return
    const timer = window.setInterval(() => setNow(Date.now()), 5_000)
    return () => window.clearInterval(timer)
  }, [visible])

  useEffect(() => {
    if (!live) return
    const seen = sightings.current, at = Date.now()
    for (const dest of live.destinations) seen.set(dest.id, { id: dest.id, name: dest.name, category: dest.category, chosen: dest.chosen, service: dominant(dest.mix), org: dest.org, domains: dest.domains,
      peak: Math.max(seen.get(dest.id)?.peak ?? 0, dest.bps), at })
    for (const [id, item] of seen) if (at - item.at > siteLinger) seen.delete(id)
    const next = [...seen.values()].sort((a, b) => b.peak - a.peak).slice(0, Number(cap) || Infinity).map(({ id, name, category, chosen, service, peak, org, domains }) => ({ id, name, category, chosen, service, peak, org, domains }))
    const same = city.length === next.length && next.every(site => city.some(other => other.id === site.id && other.category === site.category && other.chosen === site.chosen))
    if (same || (city.length && at - cityAt.current < cityEvery)) return
    cityAt.current = at
    setCity(next)
    // The city redraws only when its sites change; `city` itself is read, never a reason to rerun.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [live, cap])

  const layout = useMemo(() => map ? layoutLab(map, city) : null, [map, city])
  const route = useMemo(() => layout && selected ? routeOf(layout, selected) : [], [layout, selected])
  const counts = useMemo(() => map && layout ? labCounts(map, layout, live) : null, [map, layout, live])
  const stale = !!map && (liveAt === null ? now - Date.parse(map.generatedAt) > 45_000 : now - liveAt > 35_000)

  useLayoutEffect(() => {
    const element = root.current
    if (!element) return
    const place = () => element.style.setProperty('--labmap-top', `${Math.max(0, element.getBoundingClientRect().top + window.scrollY)}px`)
    place()
    window.addEventListener('resize', place)
    return () => window.removeEventListener('resize', place)
  }, [wallboard])

  useEffect(() => {
    if (flat || !stage.current || !plates.current) return
    const instance = new LabScene(stage.current, plates.current, { select: id => setSelected(id) }, compact)
    scene.current = instance
    return () => { instance.dispose(); scene.current = null }
  }, [flat, compact])
  useEffect(() => { if (layout) scene.current?.setLayout(layout) }, [layout, flat, compact])
  useEffect(() => { scene.current?.setLive(live) }, [live, layout, flat, compact])
  useEffect(() => { scene.current?.setLayers(layers) }, [layers, layout, flat, compact])
  useEffect(() => { scene.current?.select(selected, route) }, [selected, route, flat, compact])
  useEffect(() => { scene.current?.setWallboard(wallboard) }, [wallboard, flat, compact])
  useEffect(() => { if (selected && layout && !layout.nodes.has(selected)) setSelected(null) }, [layout, selected])

  useEffect(() => {
    const change = () => { if (!document.fullscreenElement) setWallboard(false) }
    document.addEventListener('fullscreenchange', change)
    return () => document.removeEventListener('fullscreenchange', change)
  }, [])
  useEffect(() => {
    if (!wallboard || !visible) { setTour(null); return }
    let index = -1
    const step = () => {
      const attention = counts?.attention ?? []
      if (!attention.length) { setTour(null); scene.current?.overview(); return }
      index = (index + 1) % attention.length
      setTour(attention[index])
      scene.current?.flyTo(attention[index])
    }
    step()
    const timer = window.setInterval(step, 12_000)
    return () => window.clearInterval(timer)
  }, [wallboard, visible, counts])

  const toggleWallboard = useCallback(async () => {
    if (wallboard) {
      setWallboard(false)
      if (document.fullscreenElement) await document.exitFullscreen().catch(() => undefined)
      return
    }
    setSelected(null)
    setWallboard(true)
    await root.current?.requestFullscreen?.().catch(() => undefined)
  }, [wallboard])

  useEffect(() => {
    const key = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return
      if (wallboard && !document.fullscreenElement) setWallboard(false)
      else if (selected) setSelected(null)
    }
    window.addEventListener('keydown', key)
    return () => window.removeEventListener('keydown', key)
  }, [selected, wallboard])

  const reclassify = useCallback(async (mac: string, group: ClientGroup | null) => {
    await ownerRequest(session, refreshSession, `/api/host/lab-map/clients/${encodeURIComponent(mac)}`, 'PUT', { group })
    setMapTick(value => value + 1)
  }, [session, refreshSession])

  // A site's district is kept on the server for everyone; its sighting moves now, and the city redraws on the next reading.
  const moveSite = useCallback(async (site: string, district: string | null) => {
    await ownerRequest(session, refreshSession, '/api/host/lab-map/sites', 'PUT', { site, district })
    const seen = sightings.current.get(site)
    if (seen && district) sightings.current.set(site, { ...seen, category: district, chosen: true })
    cityAt.current = 0
    setLiveTick(value => value + 1)
  }, [session, refreshSession])

  // A building's category is kept on the server for everyone and colours it on the next map reading.
  const setCategory = useCallback(async (object: string, category: string | null) => {
    await ownerRequest(session, refreshSession, '/api/host/lab-map/categories', 'PUT', { object, category })
    setMapTick(value => value + 1)
  }, [session, refreshSession])

  const select = useCallback((id: string | null) => { setSelected(id); setListOpen(false) }, [])
  const tourNode = tour && layout ? layout.nodes.get(tour) : undefined

  return <div ref={root} className="labmap" data-wallboard={wallboard || undefined} data-panel={selected ? 'open' : undefined}>
    {!wallboard && <header className="labmap-toolbar">
      <h1>Map</h1>
      {map && layout && <MapSearch map={map} layout={layout} onSelect={select} />}
      <details className="labmap-layers">
        <summary><Icon name="layers" />Layers</summary>
        <fieldset><legend>Show on the map</legend>
          {layerNames.map(layer => <label key={layer.key}><input type="checkbox" checked={layers[layer.key]} onChange={event => setLayers({ ...layers, [layer.key]: event.target.checked })} />{layer.label}</label>)}
        </fieldset>
      </details>
      <Segmented name="labmap-span" label="Traffic" options={spans} value={span} onChange={setSpan} />
      <Segmented name="labmap-sites" label="Internet sites" options={caps} value={cap} onChange={setCap} />
      <button type="button" className="labmap-tool" aria-expanded={listOpen} aria-controls="labmap-list" onClick={() => setListOpen(!listOpen)}><Icon name="tasks" />List</button>
      <button type="button" className="labmap-tool" onClick={() => void toggleWallboard()}><Icon name="expand" />Wallboard</button>
    </header>}
    <div className="labmap-stage" ref={stage} role="img" aria-label={counts ? `Lab map: ${counts.servers} servers, ${counts.apps} apps, ${counts.clients} clients online, ${counts.attention.length} need attention. Use List to browse every object.` : 'Lab map is loading.'}>
      {flat && layout && <FlatMap layout={layout} live={live} selected={selected} route={route} layers={layers} onSelect={select} />}
      <div ref={plates} className="labmap-plates" aria-hidden="true" />
      {!map && <div className="labmap-state">{mapError
        ? <><Icon name="attention" /><h2>The map could not be drawn.</h2><p>{mapError}</p><button type="button" className="button primary" onClick={() => setMapTick(value => value + 1)}><Icon name="refresh" />Try again</button></>
        : <><span className="labmap-state-pulse" aria-hidden="true" /><h2>Mapping your lab…</h2><p>Reading UniFi, your servers, and their apps.</p></>}
      </div>}
      {live && layout && <MapLegend layout={layout} live={live} />}
      {map && counts && <StatusStrip map={map} live={live} span={spans.find(item => item.key === span)!.strip} liveAt={liveAt} now={now} stale={stale} counts={counts} onAttention={() => counts.attention[0] && select(counts.attention[0])} />}
      {wallboard && <button type="button" className="labmap-exit" onClick={() => void toggleWallboard()}><Icon name="shrink" />Exit wallboard</button>}
      {wallboard && tourNode && <div className="labmap-callout" role="status" data-health={effectiveHealth(tourNode, live)}>
        <strong>{tourNode.label}</strong><span>{kindLabels[tourNode.kind]} · {healthLabels[effectiveHealth(tourNode, live)]}</span>
      </div>}
    </div>
    {map && layout && <nav id="labmap-list" className="labmap-list" aria-label="Lab objects" hidden={!listOpen}>
      <div className="labmap-list-head"><h2>Everything on the map</h2><button type="button" className="icon-button" aria-label="Close list" onClick={() => setListOpen(false)}><Icon name="close" /></button></div>
      <ObjectList layout={layout} live={live} selected={selected} onSelect={select} />
    </nav>}
    {map && layout && selected && layout.nodes.has(selected) && <DetailPanel key={selected} map={map} layout={layout} live={live} id={selected} route={route} now={now}
      onSelect={select} onClose={() => setSelected(null)} onReclassify={reclassify} sites={city} onMoveSite={moveSite} onCategory={setCategory} />}
  </div>
}

function MapSearch({ map, layout, onSelect }: { map: LabMapData; layout: Layout; onSelect: (id: string) => void }) {
  const [query, setQuery] = useState('')
  const [active, setActive] = useState(0)
  const [open, setOpen] = useState(false)
  const hits = useMemo(() => searchLab(map, layout, query), [map, layout, query])
  const choose = (index: number) => {
    const hit = hits[index]
    if (!hit) return
    onSelect(hit.id)
    setOpen(false)
    setQuery(hit.label)
  }
  const key = (event: ReactKeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'ArrowDown') { event.preventDefault(); setOpen(true); setActive(Math.min(active + 1, hits.length - 1)) }
    else if (event.key === 'ArrowUp') { event.preventDefault(); setActive(Math.max(active - 1, 0)) }
    else if (event.key === 'Enter') { event.preventDefault(); choose(active) }
    else if (event.key === 'Escape' && open) { event.stopPropagation(); setOpen(false) }
  }
  const expanded = open && !!query.trim()
  return <div className="labmap-search">
    <Icon name="search" />
    <input type="search" role="combobox" aria-label="Find on the map" placeholder="Find a server, app, device, or client" value={query} aria-expanded={expanded}
      aria-controls="labmap-search-results" aria-autocomplete="list" aria-activedescendant={expanded && hits[active] ? `labmap-hit-${active}` : undefined}
      onChange={event => { setQuery(event.target.value); setActive(0); setOpen(true) }} onKeyDown={key} onFocus={() => setOpen(true)} onBlur={() => window.setTimeout(() => setOpen(false), 120)} />
    {expanded && <ul id="labmap-search-results" role="listbox" aria-label="Matches">
      {hits.length ? hits.map((hit, index) => <li key={`${hit.id}-${hit.label}-${index}`} id={`labmap-hit-${index}`} role="option" aria-selected={index === active}
        onMouseDown={event => { event.preventDefault(); choose(index) }} onMouseEnter={() => setActive(index)}>
        <strong>{hit.label}</strong><span>{hit.detail}</span></li>)
        : <li className="labmap-search-empty" role="option" aria-selected="false" aria-disabled="true">Nothing on the map matches “{query.trim()}”.</li>}
    </ul>}
  </div>
}

function StatusStrip({ map, live, span, liveAt, now, stale, counts, onAttention }: { map: LabMapData; live: LabMapLive | null; span: string; liveAt: number | null; now: number; stale: boolean
  counts: NonNullable<ReturnType<typeof labCounts>>; onAttention: () => void }) {
  const traffic = live?.traffic ?? map.traffic
  const notes: string[] = []
  if (map.unifi.state !== 'connected') notes.push(map.unifi.state === 'error' ? `UniFi: ${map.unifi.message ?? 'not responding'}` : 'UniFi is not connected, so only Lucia servers and apps are shown')
  if (traffic === 'reduced') notes.push('Traffic is from UniFi only; detailed metrics are unavailable')
  else if (traffic === 'none') notes.push('No traffic readings yet')
  else if (!map.sources.netflow) notes.push('Conversations appear once NetFlow arrives')
  return <div className="labmap-status" data-stale={stale || undefined}>
    <p className="labmap-counts">
      <span><b>{counts.servers}</b> servers</span><span><b>{counts.running}</b>/{counts.apps} apps running</span><span><b>{counts.clients}</b> clients online</span>
      {counts.attention.length > 0 && <button type="button" onClick={onAttention}><Icon name="attention" /><b>{counts.attention.length}</b> need attention</button>}
    </p>
    <p className="labmap-fresh">{stale ? <><Icon name="clock" />Live readings paused · last {ago(liveAt ? new Date(liveAt).toISOString() : map.generatedAt, now)}</>
      : <><span className="labmap-live-dot" aria-hidden="true" />{span} · updated {ago(live?.at ?? map.generatedAt, now)}</>}</p>
    {notes.map(note => <p key={note} className="labmap-note">{map.unifi.state !== 'connected' && note.startsWith('UniFi') ? <a href="#/settings/unifi">{note}</a> : note}</p>)}
  </div>
}

function ObjectList({ layout, live, selected, onSelect }: { layout: Layout; live: LabMapLive | null; selected: string | null; onSelect: (id: string) => void }) {
  const nodes = [...layout.nodes.values()]
  const item = (node: MapNode, children?: MapNode[]) => <li key={node.id}>
    <button type="button" aria-current={node.id === selected || undefined} onClick={() => onSelect(node.id)}>
      <span className="labmap-dot" style={{ background: hex(toneOf(node, effectiveHealth(node, live))) }} />
      <span>{node.kind === 'clients' ? `${node.label} · ${node.detail}` : node.label}</span><small>{kindLabels[node.kind]}</small>
    </button>
    {children && children.length > 0 && <ul>{children.map(child => item(child, nodes.filter(inner => inner.block === child.id && inner.id !== child.id)))}</ul>}
  </li>
  const infrastructure = nodes.filter(node => ['wan', 'gateway', 'switch', 'ap'].includes(node.kind))
  return <ul className="labmap-tree">
    {infrastructure.map(node => item(node))}
    {layout.plinths.map(plinth => item(layout.nodes.get(plinth.id)!, nodes.filter(node => node.quarter === plinth.id && ['spark', 'node', 'storage', 'clients'].includes(node.kind))))}
    {nodes.filter(node => node.kind === 'region').map(region => item(region, nodes.filter(node => node.kind === 'site' && node.block === region.id)))}
  </ul>
}

function DetailPanel({ map, layout, live, id, route, now, onSelect, onClose, onReclassify, sites, onMoveSite, onCategory }: { map: LabMapData; layout: Layout; live: LabMapLive | null; id: string; route: string[]; now: number
  onSelect: (id: string) => void; onClose: () => void; onReclassify: (mac: string, group: ClientGroup | null) => Promise<void>; sites: CitySite[]; onMoveSite: MoveSite; onCategory: SetCategory }) {
  const node = layout.nodes.get(id)!
  const site = node.kind === 'site' ? sites.find(item => item.id === id) : undefined
  const facts = factsFor(map, layout, live, id, now)
  const health = effectiveHealth(node, live)
  const app = node.kind === 'app' ? map.apps.find(item => item.id === id) : undefined
  const clients = node.kind === 'clients' ? layout.groups.get(id) : undefined
  const member = node.kind === 'client' ? map.clientGroups.flatMap(group => group.members).find(item => `client:${item.mac}` === id) : undefined
  const mix = mixList(mixTotals(layout, live).get(id)), mixTotal = mix.reduce((sum, item) => sum + item.bps, 0), ports = portsOf(layout, live, id)
  const heading = useRef<HTMLHeadingElement>(null)
  useEffect(() => { heading.current?.focus({ preventScroll: true }) }, [])
  return <aside className="labmap-panel" aria-labelledby="labmap-panel-title">
    <header>
      <div><h2 id="labmap-panel-title" ref={heading} tabIndex={-1}>{node.label}</h2>
        <p className="labmap-health" data-health={quietKinds.has(node.kind) ? undefined : health}>{!quietKinds.has(node.kind) && <span className="labmap-dot" />}{kindLabels[node.kind]}{!quietKinds.has(node.kind) && ` · ${healthLabels[health]}`}</p></div>
      <button type="button" className="icon-button" aria-label="Close details" onClick={onClose}><Icon name="close" /></button>
    </header>
    {route.length > 1 && <section aria-label="Route to the internet"><h3>Route out</h3>
      <ol className="labmap-route">{route.map(step => <li key={step}><button type="button" aria-current={step === id || undefined} onClick={() => onSelect(step)}>{layout.nodes.get(step)?.label ?? step}</button></li>)}</ol>
    </section>}
    <dl className="labmap-facts">{facts.map(fact => <div key={fact.label}><dt>{fact.label}</dt><dd>{fact.value}</dd></div>)}</dl>
    {mixTotal > 0 && <section><h3>Traffic by type</h3>
      <div className="labmap-mix" aria-hidden="true">{mix.map(item => <span key={item.service} style={{ flexGrow: item.bps, background: hex(serviceColors[item.service]) }} />)}</div>
      <ul className="labmap-mix-key">{mix.map(item => <li key={item.service}><span className="labmap-dot" style={{ background: hex(serviceColors[item.service]) }} />
        {serviceLabels[item.service]}<b>{Math.max(1, Math.round(item.bps / mixTotal * 100))}%</b></li>)}</ul>
    </section>}
    {ports.length > 0 && <section><h3>Ports</h3>
      <ul className="labmap-ports">{ports.map(port => <li key={`${port.port}/${port.protocol}`}><span className="labmap-dot" style={{ background: hex(serviceColors[port.service]) }} />
        <code>{port.port}/{port.protocol}</code><span>{serviceLabels[port.service]}</span>{port.bps ? <small>{formatBps(port.bps)}</small> : null}</li>)}</ul>
    </section>}
    {app && app.containers.length > 0 && <section><h3>Containers</h3>
      <ul className="labmap-containers">{app.containers.map(container => <li key={container.id}>
        <p><strong>{container.name}</strong><span data-health={container.health}>{container.state || healthLabels[container.health]}</span></p>
        <code>{container.image}</code>
        {container.networks.length > 0 && <p className="labmap-sub">Networks: {container.networks.map(network => network.address ? `${network.name} (${network.address})` : network.name).join(', ')}</p>}
        {container.mounts.length > 0 && <p className="labmap-sub">Storage: {container.mounts.map(mount => `${mount.source} → ${mount.destination}`).join(', ')}</p>}
      </li>)}</ul>
    </section>}
    {clients && <ClientMembers group={clients} onReclassify={onReclassify} />}
    {member && <ClientGroupPicker member={member} onReclassify={onReclassify} />}
    {node.category && <CategoryPicker key={`${node.category}|${node.categoryChosen}`} node={node} known={[...layout.nodes.values()].flatMap(item => item.category ? [item.category] : [])} onChange={onCategory} />}
    {site && <SiteDistrictPicker key={`${site.category}|${site.chosen}`} site={site} districts={sites.map(item => item.category)} onMove={onMoveSite} />}
    <div className="labmap-actions">
      {node.href && openLabels[node.kind] && <a className="button primary" href={node.href}>{openLabels[node.kind]}<Icon name="arrow" /></a>}
      <button type="button" className="button" onClick={() => askAssistant(assistantPrompt(layout, facts, id))}><Icon name="chat" />Ask the assistant about this</button>
    </div>
  </aside>
}

type Member = LabMapData['clientGroups'][number]['members'][number]
type Reclassify = (mac: string, group: ClientGroup | null) => Promise<void>

function useRegroup(onReclassify: Reclassify) {
  const [saving, setSaving] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const change = async (mac: string, value: string) => {
    setSaving(mac)
    setError(null)
    try { await onReclassify(mac, value === 'auto' ? null : value as ClientGroup) }
    catch (failure) { setError(message(failure, 'Lucia could not move this client.')) }
    finally { setSaving(null) }
  }
  return { saving, error, change }
}

function GroupSelect({ member, saving, onChange, id }: { member: Member; saving: boolean; onChange: (mac: string, value: string) => void; id?: string }) {
  return <select id={id} aria-label={id ? undefined : `Group for ${member.name}`} value={member.overridden ? member.group : 'auto'} disabled={saving} onChange={event => onChange(member.mac, event.target.value)}>
    <option value="auto">Automatic ({groupLabels[member.group]})</option>
    {clientGroups.map(option => <option key={option} value={option}>{groupLabels[option]}</option>)}
  </select>
}

function ClientMembers({ group, onReclassify }: { group: NonNullable<LabMapData['clientGroups'][number]>; onReclassify: Reclassify }) {
  const { saving, error, change } = useRegroup(onReclassify)
  const members = [...group.members].sort((a, b) => Number(b.online) - Number(a.online) || a.name.localeCompare(b.name))
  return <section><h3>Clients</h3>
    <p className="labmap-sub">Move a client to another group if Lucia guessed wrong. Lucia remembers your choice.</p>
    {error && <p className="labmap-error" role="alert">{error}</p>}
    <ul className="labmap-members">{members.map(member => <li key={member.mac}>
      <div><strong>{member.name}</strong><span>{[member.address, member.vendor, member.online ? 'Online' : 'Offline'].filter(Boolean).join(' · ')}</span></div>
      <GroupSelect member={member} saving={saving === member.mac} onChange={(mac, value) => void change(mac, value)} />
    </li>)}</ul>
  </section>
}

function ClientGroupPicker({ member, onReclassify }: { member: Member; onReclassify: Reclassify }) {
  const { saving, error, change } = useRegroup(onReclassify)
  return <section><h3><label htmlFor="labmap-regroup">Group</label></h3>
    <p className="labmap-sub">{member.overridden ? 'You chose this group. Pick Automatic to let Lucia decide again.' : 'Lucia guessed this group. Move the device if it guessed wrong; Lucia remembers your choice.'}</p>
    {error && <p className="labmap-error" role="alert">{error}</p>}
    <div className="labmap-regroup"><GroupSelect id="labmap-regroup" member={member} saving={saving === member.mac} onChange={(mac, value) => void change(mac, value)} /></div>
  </section>
}

type MoveSite = (site: string, district: string | null) => Promise<void>

// The owner's district for a site, kept on the server for everyone: one of Lucia's, one already named, or a new one.
function SiteDistrictPicker({ site, districts, onMove }: { site: CitySite; districts: string[]; onMove: MoveSite }) {
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [naming, setNaming] = useState(false)
  const [name, setName] = useState('')
  const move = async (district: string | null) => {
    setSaving(true)
    setError(null)
    try { await onMove(site.id, district); setNaming(false); setName('') }
    catch (failure) { setError(message(failure, 'Lucia could not move this site.')) }
    finally { setSaving(false) }
  }
  return <section><h3><label htmlFor="labmap-district">District</label></h3>
    <p className="labmap-sub">{site.chosen ? 'Moved by an owner, for everyone using Lucia. Pick Automatic to let Lucia decide again.' : 'Lucia guessed this from the site’s names. Move it if it guessed wrong; the map keeps the change for everyone.'}</p>
    {error && <p className="labmap-error" role="alert">{error}</p>}
    <div className="labmap-regroup">
      <select id="labmap-district" value={naming ? 'new' : site.chosen ? site.category : 'auto'} disabled={saving} onChange={event => {
        const value = event.target.value
        setNaming(value === 'new')
        if (value !== 'new') void move(value === 'auto' ? null : value)
      }}>
        <option value="auto">Automatic{site.chosen ? '' : ` (${categoryLabel(site.category)})`}</option>
        {districtsOf([...destCategories, ...districts]).map(item => <option key={item} value={item}>{categoryLabel(item)}</option>)}
        <option value="new">New district…</option>
      </select>
    </div>
    {naming && <form className="labmap-district-new" onSubmit={event => { event.preventDefault(); if (name.trim()) void move(districtValue(name)) }}>
      <label htmlFor="labmap-district-name">New district name</label>
      <div><input id="labmap-district-name" value={name} maxLength={32} placeholder="Dev tools" onChange={event => setName(event.target.value)} />
        <button type="submit" className="button" disabled={saving || !name.trim()}>Move here</button></div>
    </form>}
  </section>
}

type SetCategory = (object: string, category: string | null) => Promise<void>

// The owner's category for a building, kept on the server for everyone: one of Lucia's, one already named, or a new one.
function CategoryPicker({ node, known, onChange }: { node: MapNode; known: string[]; onChange: SetCategory }) {
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [naming, setNaming] = useState(false)
  const [name, setName] = useState('')
  const save = async (category: string | null) => {
    setSaving(true)
    setError(null)
    try { await onChange(node.id, category); setNaming(false); setName('') }
    catch (failure) { setError(message(failure, 'Lucia could not change this category.')) }
    finally { setSaving(false) }
  }
  const current = node.category!
  return <section><h3><label htmlFor="labmap-category">Category</label></h3>
    <p className="labmap-sub">{node.categoryChosen ? 'Set by an owner, for everyone using Lucia. Pick Automatic to let Lucia decide again.' : 'Lucia guessed this. If it’s wrong, pick the right one; the colour changes for everyone.'}</p>
    {error && <p className="labmap-error" role="alert">{error}</p>}
    <div className="labmap-regroup">
      <span className="labmap-dot" style={{ background: hex(buildingCategoryColor(current)) }} aria-hidden="true" />
      <select id="labmap-category" value={naming ? 'new' : node.categoryChosen ? current : 'auto'} disabled={saving} onChange={event => {
        const value = event.target.value
        setNaming(value === 'new')
        if (value !== 'new') void save(value === 'auto' ? null : value)
      }}>
        <option value="auto">Automatic{node.categoryChosen ? '' : ` (${buildingCategoryLabel(current)})`}</option>
        {buildingCategoriesOf([...buildingCategories, ...known]).map(item => <option key={item} value={item}>{buildingCategoryLabel(item)}</option>)}
        <option value="new">New category…</option>
      </select>
    </div>
    {naming && <form className="labmap-district-new" onSubmit={event => { event.preventDefault(); if (name.trim()) void save(buildingCategoryValue(name)) }}>
      <label htmlFor="labmap-category-name">New category name</label>
      <div><input id="labmap-category-name" value={name} maxLength={32} placeholder="Smart plugs" onChange={event => setName(event.target.value)} />
        <button type="submit" className="button" disabled={saving || !name.trim()}>Use this</button></div>
    </form>}
  </section>
}

// Only the traffic types moving right now, so the key stays short.
function MapLegend({ layout, live }: { layout: Layout; live: LabMapLive }) {
  const totals = mixTotals(layout, live).get('wan') ?? {}, present = new Set<Service>([...mixList(totals).map(item => item.service), ...live.flows.map(flow => flow.service)])
  const wifi = [...layout.nodes.values()].some(node => live.rates[node.id] && node.kind !== 'clients' && layout.nodes.get(node.parentId ?? '')?.kind === 'ap')
  // Cars heading home wear the colour of the building they deliver to.
  const homes = buildingCategoriesOf([...layout.nodes.values()].flatMap(node => node.category && live.rates[node.id] ? [node.category] : []))
  if (!present.size && !wifi && !homes.length) return null
  return <ul className="labmap-legend" aria-label="Traffic colours">
    {services.filter(item => present.has(item)).map(item => <li key={item}><span className="labmap-car" style={{ background: hex(serviceColors[item]) }} />{serviceLabels[item]}</li>)}
    {homes.map(item => <li key={`home-${item}`}><span className="labmap-car" style={{ background: hex(buildingCategoryColor(item)) }} />{buildingCategoryLabel(item)}</li>)}
    {wifi && <li><span className="labmap-car" data-air style={{ background: hex(palette.air) }} />Wi-Fi</li>}
  </ul>
}

function FlatMap({ layout, live, selected, route, layers, onSelect }: { layout: Layout; live: LabMapLive | null; selected: string | null; route: string[]; layers: Layers; onSelect: (id: string | null) => void }) {
  const nodes = [...layout.nodes.values()].filter(node => node.kind !== 'wan' && !((node.kind === 'clients' || node.kind === 'client') && !layers.clients) && !(node.kind === 'storage' && !layers.storage) && !(node.kind === 'app' && !layers.apps))
  const xs = [...layout.outline.map(p => p[0]), ...nodes.map(node => node.x)], zs = [...layout.outline.map(p => p[1]), ...nodes.map(node => node.z)]
  const box = { x: Math.min(...xs) - 6, z: Math.min(...zs) - 10, w: Math.max(...xs) - Math.min(...xs) + 12, h: Math.max(...zs) - Math.min(...zs) + 16 }
  const line = (points: [number, number][]) => points.map(([x, z]) => `${x},${z}`).join(' ')
  const at = (id: string) => layout.nodes.get(id)
  const color = (node: MapNode) => node.id === selected ? hex(palette.magenta) : hex(toneOf(node, effectiveHealth(node, live)))
  const hosts = nodes.filter(node => node.kind !== 'network')
  const routePoints = route.slice(0, -1).flatMap((step, index) => { const node = at(step); return node?.parentId === route[index + 1] && node.path ? node.path : node ? [[node.x, node.z] as [number, number]] : [] })
  const pick = (id: string) => (event: ReactMouseEvent) => { event.stopPropagation(); onSelect(id) }
  return <svg className="labmap-flat" viewBox={`${box.x} ${box.z} ${box.w} ${box.h}`} preserveAspectRatio="xMidYMid meet" onClick={() => onSelect(null)} aria-hidden="true">
    <defs><pattern id="labmap-grid" width="4" height="4" patternUnits="userSpaceOnUse"><path d="M4 0H0V4" fill="none" stroke={hex(palette.cyan)} strokeOpacity="0.12" strokeWidth="0.08" /></pattern></defs>
    <rect x={box.x} y={box.z} width={box.w} height={box.h} fill="url(#labmap-grid)" />
    {layout.plinths.map(plinth => <polygon key={plinth.id} points={line(plinth.polygon)} onClick={pick(plinth.id)} className="labmap-flat-hit" fill={hex(palette.cyan)} fillOpacity="0.04"
      stroke={plinth.id === selected ? hex(palette.magenta) : hex(palette.cyan)} strokeOpacity="0.5" strokeWidth="0.15" />)}
    {layout.roads.map((road, index) => <polyline key={`road-${index}`} points={line(road.points)} fill="none" stroke={hex(palette.asphalt)} strokeOpacity="0.85" strokeLinecap="round" strokeLinejoin="round" strokeWidth={{ lane: 0.4, street: 0.75, avenue: 1.25, highway: 1.9 }[road.kind]} />)}
    {layout.roads.map((road, index) => <polyline key={`kerb-${index}`} points={line(road.points)} fill="none" stroke={hex(palette.kerb)} strokeOpacity={{ lane: 0.2, street: 0.28, avenue: 0.38, highway: 0.6 }[road.kind]} strokeWidth="0.06" />)}
    {layout.blocks.filter(block => at(block.id)?.kind !== 'clients').map(block => <polygon key={`block-${block.id}`} points={line(block.polygon)} fill={block.fill ? hex(block.tint ?? palette.cyan) : "none"} fillOpacity="0.05" stroke={hex(block.tint ?? palette.cyan)} strokeOpacity={block.tint === undefined ? 0.22 : 0.5} strokeWidth="0.1" />)}
    {layers.traffic && hosts.map(node => node.path && node.path.length > 1 && node.kind !== 'gateway' && live?.rates[node.id] ? <polyline key={`lane-${node.id}`} points={line(node.path)} fill="none" stroke={hex(palette.cyan)} strokeOpacity="0.5" strokeWidth="0.12" /> : null)}
    {routePoints.length > 1 && <polyline points={line(routePoints)} fill="none" stroke={hex(palette.magenta)} strokeWidth="0.35" strokeLinejoin="round" />}
    {hosts.map(node => <g key={node.id} onClick={pick(node.id)} className="labmap-flat-hit" stroke={color(node)} fill={color(node)} fillOpacity="0.1" strokeWidth="0.15">
      {node.kind === 'gateway' ? <><circle cx={node.x} cy={node.z} r="4.6" /><circle cx={node.x} cy={node.z} r="5.3" fill="none" strokeOpacity="0.5" /></>
        : node.kind === 'clients' || node.kind === 'region' ? <polygon points={line(layout.blocks.find(block => block.id === node.id)?.polygon ?? [])} fillOpacity="0.04" strokeOpacity="0.5" strokeDasharray="0.5 0.4" />
          : node.kind === 'ap' ? <path d={`M${node.x} ${node.z - 1.3}L${node.x + 1.3} ${node.z}L${node.x} ${node.z + 1.3}L${node.x - 1.3} ${node.z}Z`} />
            : node.kind === 'storage' ? <polygon points={Array.from({ length: 8 }, (_, index) => `${node.x + Math.cos(index * Math.PI / 4) * 1.7},${node.z + Math.sin(index * Math.PI / 4) * 1.7}`).join(' ')} />
              : <rect x={node.x - node.size / 2} y={node.z - node.size / 2} width={node.size} height={node.size} transform={`rotate(${-(node.angle ?? 0) * 180 / Math.PI} ${node.x} ${node.z})`} />}
    </g>)}
    {nodes.filter(node => node.kind !== 'app' && node.kind !== 'client' && node.kind !== 'site').map(node => {
      const plinth = node.kind === 'network' ? layout.plinths.find(item => item.id === node.id) : node.kind === 'clients' || node.kind === 'region' ? layout.blocks.find(item => item.id === node.id) : undefined
      const x = plinth ? plinth.labelX : node.x, y = plinth ? plinth.labelZ + 2.2 : node.z + (node.kind === 'gateway' ? -6.4 : 3.6)
      const label = node.kind === 'clients' ? `${node.label} ${node.count ?? 0}` : node.kind === 'spark' || node.kind === 'node' ? `${node.label} · ${layout.nodes.size && [...layout.nodes.values()].filter(app => app.parentId === node.id && app.kind === 'app').length} apps` : node.label
      return <text key={`t-${node.id}`} x={x} y={y} className="labmap-flat-label" data-kind={node.kind} fill={node.id === selected ? hex(palette.magenta) : undefined}>{label}</text>
    })}
  </svg>
}
