import type { Layout, MapNode } from './labMapModel'
import { wirelessVia } from './labMapModel'

// The map as a universe. The LAN is one solar system, with the gateway as a wormhole at its edge: switches and access points are
// planets, client groups their moons, wireless clients satellites around a moon and wired clients cities on it. Servers are stations
// at their planet and apps pods at a station. The Internet is a galaxy through the wormhole: a solar system per destination
// category and a planet per destination.
export type BodyKind = 'portal' | 'exit' | 'relay' | 'planet' | 'moon' | 'satellite' | 'city' | 'station' | 'pod' | 'depot' | 'sun' | 'world'
export type Vec = [number, number, number]

export interface Body {
  id: string
  node: MapNode
  kind: BodyKind
  parent: string | null
  radius: number
  // An orbit around the parent body, or around `center` without one. A city sits on its moon at lat and lon instead.
  center: Vec
  orbit: number
  omega: number
  phase: number
  // The orbit's inclination, and the angle its plane is turned about the vertical.
  tilt: number
  plane: number
  spin: number
  lat: number
  lon: number
  // What lights the body: the star of its system.
  star: Vec
  look: number
  // An app's docking port on its station, in station units.
  port: Vec | null
  // The way a wormhole's mouth faces.
  face: Vec
}
// Where each app docks on its station, in station units (one unit is the station's radius): eight ports a tier around the ring, tiers stacked above and below it.
export interface Port { x: number; y: number; z: number; angle: number }
export const perTier = 8
export const stationReach = 1.7
export function portsOf(count: number): Port[] {
  return Array.from({ length: count }, (_, index) => {
    const tier = Math.floor(index / perTier), inTier = Math.min(perTier, count - tier * perTier), angle = (index % perTier) / inTier * TAU + tier * 0.39
    return { x: Math.cos(angle), y: tier === 0 ? 0 : (tier % 2 ? 1 : -1) * Math.ceil(tier / 2) * 0.42, z: Math.sin(angle), angle }
  })
}
// thick scales how far a belt's rocks and dust spread above and below the plane; the outer cloud is a deep halo.
// A belt or halo of rocks and dust around a star: an asteroid belt, a rocky dust ring, or an Oort-style halo.
// The galaxy's spiral: three arms winding outward from the black hole.
export const galaxyArms = 3
// Arms wind evenly outward, one full turn every armTurn units of radius.
export const armTurn = 24
export const armAngle = (arm: number, radius: number, unit = 190) => arm * Math.PI * 2 / galaxyArms + radius / (unit * armTurn) * Math.PI * 2
export type BeltKind = 'asteroid' | 'dust' | 'oort'
export interface Belt { inner: number; outer: number; thick?: number; kind: BeltKind; center: Vec; home: boolean }
// A coloured gas cloud drifting near a star.
export interface Haze { center: Vec; radius: number; color: number }
export interface System { id: string; x: number; z: number; radius: number; color: number }
export interface Cosmos { bodies: Body[]; byId: Map<string, Body>; lan: { radius: number; star: Vec }; galaxy: { radius: number; unit: number }; systems: System[]; rings: number[]; belts: Belt[]; hazes: Haze[]; hole: { radius: number }; extent: number }

const TAU = Math.PI * 2
export function hash(text: string): number {
  let h = 2166136261
  for (let index = 0; index < text.length; index++) h = Math.imul(h ^ text.charCodeAt(index), 16777619)
  return ((h >>> 0) % 100000) / 100000
}
const starColors = [0xffd9a0, 0xa8d8ff, 0xffb3a0, 0xd9c2ff, 0xb8ffd9, 0xfff3a0, 0xffa8e0, 0xa0f0ff]
const isDevice = (node: MapNode | undefined) => node?.kind === 'switch' || node?.kind === 'ap'

export function layoutCosmos(layout: Layout): Cosmos {
  const nodes = layout.nodes
  const bodies: Body[] = []
  const byId = new Map<string, Body>()
  const origin: Vec = [0, 0, 0]
  const put = (node: MapNode, kind: BodyKind, values: Partial<Body>) => {
    const body: Body = { id: node.id, node, kind, parent: null, radius: 1, center: origin, orbit: 0, omega: 0, phase: 0, tilt: 0, plane: 0, spin: 0.1, lat: 0, lon: 0, star: origin, look: hash(node.id), port: null, face: [0, 0, 1], ...values }
    bodies.push(body)
    byId.set(node.id, body)
    return body
  }

  // Planets: a ring per hop from the gateway, the nearest hop outermost, each planet near its parent so links stay short.
  const devices = [...nodes.values()].filter(isDevice)
  const hops = (id: string) => {
    let depth = 1, at = nodes.get(id)
    const seen = new Set<string>()
    while (at && !seen.has(at.id)) {
      seen.add(at.id)
      const up = at.parentId ? nodes.get(at.parentId) : undefined
      if (!up) break
      if (isDevice(up)) depth++
      at = up
    }
    return depth
  }
  const depth = new Map(devices.map(device => [device.id, hops(device.id)]))
  const planetOf = (id: string): string | null => {
    let at = nodes.get(id)
    for (let step = 0; at && step < 32; step++) {
      if (isDevice(at)) return at.id
      at = at.parentId ? nodes.get(at.parentId) : undefined
    }
    return devices[0]?.id ?? null
  }
  const planetRadius = (device: MapNode) => device.kind === 'switch' ? 5 : 3.5
  const lanSun: Vec = origin
  // Everything circles the same way at its Kepler speed, so inner bodies lap outer ones and never cross paths.
  const kepler = (reference: number, speed: number, orbit: number) => speed * (reference / Math.max(orbit, 0.1)) ** 1.5
  const clients = [...nodes.values()].filter(node => node.kind === 'client')
  const moonOf = (client: MapNode) => (client.block && nodes.get(client.block)?.kind === 'clients' ? client.block : null)
  const wirelessOn = (moon: string) => clients.filter(client => moonOf(client) === moon && wirelessVia(layout, client.id))
  const wiredOn = (moon: string) => clients.filter(client => moonOf(client) === moon && !wirelessVia(layout, client.id))
  const moonSize = (node: MapNode) => 1 + Math.min(1, Math.log10((node.count ?? 1) + 1) * 0.5)
  // Satellites share a few shells around their moon, each on its own tilted plane, so they spread out like a real constellation.
  // Planes in a shell sit about 0.6 apart, a satellite's width, so two planes can never meet where they cross.
  const planeGap = 0.62, layerGap = 2.2
  const constellation = (count: number) => {
    const layers = count > 10 ? 2 : 1, planes = Math.min(3, Math.max(1, Math.ceil(Math.ceil(count / layers) / 5)))
    return { layers, planes }
  }
  const moonShell = (node: MapNode) => {
    const count = wirelessOn(node.id).length, inner = moonSize(node) * 1.5 + 0.8, { layers, planes } = constellation(count)
    return { inner, extent: count ? inner + (layers - 1) * layerGap + (planes - 1) * planeGap + 0.7 : inner - 0.7 }
  }
  const hosts = [...nodes.values()].filter(node => node.kind === 'spark' || node.kind === 'node' || node.kind === 'storage')
  const podsOf = (host: string) => [...nodes.values()].filter(node => node.kind === 'app' && node.parentId === host)
  // A station grows with the apps docked at it.
  const stationSize = (node: MapNode) => node.kind === 'storage' ? 1.5 : (node.kind === 'spark' ? 1.5 : 1.2) + 0.32 * Math.sqrt(podsOf(node.id).length)

  // What circles each planet, nearest first: its moons, then its stations, each given the room its own satellites or pods need.
  type Orbiter = { node: MapNode; kind: 'moon' | 'station'; extent: number }
  const orbiters = new Map<string, Orbiter[]>()
  const attach = (planet: string | null, item: Orbiter) => { const key = planet ?? ''; orbiters.set(key, [...(orbiters.get(key) ?? []), item]) }
  for (const node of nodes.values()) if (node.kind === 'clients') attach(planetOf(node.parentId ?? ''), { node, kind: 'moon', extent: moonShell(node).extent })
  for (const node of hosts) attach(planetOf(node.parentId ?? ''), { node, kind: 'station', extent: stationSize(node) * stationReach })
  // Switches are ringed gas giants (rings reach 2.5 radii), so what circles them starts beyond the rings.
  const clear = (planet: MapNode) => planetRadius(planet) * (planet.kind === 'switch' ? 2.5 : 1) + (planet.kind === 'switch' ? 1.6 : 1.2)
  const reach = (planet: MapNode) => {
    let cursor = clear(planet)
    for (const item of orbiters.get(planet.id) ?? []) cursor += item.extent * 2 + 0.5
    return cursor
  }

  // Planets sit on orbits spaced by their own reach, the devices nearest the gateway outermost, at golden-angle phases so they never bunch.
  const sequence = [...devices].sort((a, b) => depth.get(b.id)! - depth.get(a.id)! || a.id.localeCompare(b.id))
  const orbitOf = new Map<string, number>()
  // Asteroid belts fill the open space: one inside the first planet, one in a gap opened across the middle, and one past the last planet.
  const belts: Belt[] = [{ inner: 12.5, outer: 18.5, kind: 'asteroid', center: origin, home: true }]
  let cursor = 22
  sequence.forEach((device, index) => {
    const extent = reach(device)
    orbitOf.set(device.id, cursor + extent)
    cursor += extent * 2 + 3
    if (sequence.length > 2 && index === Math.floor(sequence.length / 2) - 1) { belts.push({ inner: cursor + 3, outer: cursor + 15, kind: 'asteroid', center: origin, home: true }); cursor += 18 }
  })
  belts.push({ inner: cursor + 4, outer: cursor + 70, thick: 8, kind: 'oort', center: origin, home: true })
  cursor += 69
  const rings = [...orbitOf.values()]
  for (const device of devices) {
    const orbit = orbitOf.get(device.id)!, index = sequence.indexOf(device)
    put(device, 'planet', { radius: planetRadius(device), orbit, omega: kepler(40, 0.035, orbit), phase: index * 2.399963 + hash(device.id) * 0.4, tilt: 0.04, spin: 0.2, star: lanSun })
  }
  const portalAt = Math.max(30, cursor) + 14

  for (const planet of devices) {
    let at = clear(planet), index = 0
    for (const item of orbiters.get(planet.id) ?? []) {
      const orbit = at + item.extent, node = item.node
      at = orbit + item.extent + 0.5
      const phase = (index++ * 2.399963 + hash(node.id) * 0.5) % TAU
      if (item.kind === 'moon') put(node, 'moon', { parent: planet.id, radius: moonSize(node), orbit, omega: kepler(8, 0.3, orbit), phase, tilt: 0.08, spin: 0.25, star: lanSun })
      else put(node, node.kind === 'storage' ? 'depot' : 'station', { parent: planet.id, radius: stationSize(node), orbit, omega: kepler(8, 0.3, orbit), phase, tilt: 0.08, spin: 0.15, star: lanSun })
    }
  }
  // Anything without a planet to belong to circles the sun.
  let loose = 0
  for (const [key, list] of orbiters) if (!byId.has(key)) for (const item of list) {
    const orbit = 14 + loose++ * 12
    put(item.node, item.kind === 'moon' ? 'moon' : item.node.kind === 'storage' ? 'depot' : 'station', { radius: item.kind === 'moon' ? moonSize(item.node) : stationSize(item.node), center: lanSun, orbit, omega: kepler(14, 0.1, orbit), phase: hash(item.node.id) * TAU, star: lanSun })
  }
  // Apps are docking modules, fixed to their station's ports and turning with it.
  for (const host of hosts) {
    const station = byId.get(host.id), apps = podsOf(host.id), ports = portsOf(apps.length)
    apps.forEach((node, index) => put(node, 'pod', { parent: station?.id ?? null, center: lanSun, radius: stationSize(host) * 0.2, port: [ports[index].x, ports[index].y, ports[index].z], star: lanSun }))
  }
  // Satellites share evenly spaced rings around their moon; wired clients are cities spread over its surface.
  for (const moon of [...nodes.values()].filter(node => node.kind === 'clients')) {
    const shell = moonShell(moon)
    // Satellites form a constellation shell: nearly one distance from the moon, split over a few evenly rotated planes, spaced evenly in each.
    // A plane's starting phase is chosen from a few candidates so its satellites keep clear of the other planes' over a few minutes of orbit.
    type Track = { orbit: number; omega: number; phase: number; tilt: number; plane: number }
    const placed: Track[] = []
    const at = (s: Track, t: number): Vec => {
      const angle = s.phase + s.omega * t, x = Math.cos(angle) * s.orbit, flat = Math.sin(angle) * s.orbit
      const y = flat * Math.sin(s.tilt), z = flat * Math.cos(s.tilt), c = Math.cos(s.plane), n = Math.sin(s.plane)
      return [x * c + z * n, y, -x * n + z * c]
    }
    const sats = wirelessOn(moon.id), { layers, planes } = constellation(sats.length)
    for (let layer = 0; layer < layers; layer++) {
      const members = sats.filter((_, i) => i % layers === layer)
      for (let j = 0; j < planes; j++) {
        const crew = members.filter((_, i) => i % planes === j)
        if (!crew.length) continue
        const orbit = shell.inner + layer * layerGap + j * planeGap, omega = kepler(2.2, 0.8, orbit), tilt = layer ? 0.62 : 1.0, plane = j * TAU / planes + layer * 0.5
        const step = TAU / crew.length
        let offset = 0, bestGap = -1
        for (let tryIndex = 0; tryIndex < 24 && bestGap < 1.4; tryIndex++) {
          const candidate = tryIndex / 24 * step
          let gap = 1e9
          for (let t = 0; t < 240 && gap > bestGap; t += 1.5) for (let m = 0; m < crew.length; m++) {
            const here = at({ orbit, omega, phase: candidate + m * step, tilt, plane }, t)
            for (const other of placed) { const there = at(other, t); gap = Math.min(gap, Math.hypot(here[0] - there[0], here[1] - there[1], here[2] - there[2])) }
          }
          if (gap > bestGap) { bestGap = gap; offset = candidate }
        }
        crew.forEach((node, m) => {
          const track = { orbit, omega, phase: offset + m * step, tilt, plane }
          placed.push(track)
          put(node, 'satellite', { parent: moon.id, radius: 0.26, orbit, omega, phase: track.phase, tilt, plane, spin: 0.5, star: lanSun })
        })
      }
    }    const cities = wiredOn(moon.id)
    cities.forEach((node, index) => put(node, 'city', {
      parent: moon.id, radius: 0.32, lat: Math.asin(1 - 2 * (index + 0.5) / Math.max(cities.length, 1)) * 0.8, lon: index * 2.399963 + hash(node.id) * 0.3, star: lanSun,
    }))
  }
  // One galaxy: our solar system at its centre, the Internet's destination systems in a ring around it. The gateway is a wormhole at the edge
  // of ours with its mouth facing our sun, and each destination system has an exit wormhole facing its own sun, so traffic warps between them.
  const gateway = nodes.get(layout.gatewayId)
  if (gateway) put(gateway, 'portal', { radius: 9, center: [portalAt, 0, 0], face: [-1, 0, 0], star: lanSun })
  const wan = nodes.get('wan')
  if (wan) put(wan, 'relay', { radius: 2.4, center: [portalAt - 4, 0, 24], star: lanSun })
  const homeBodies = bodies.length
  const homeReach = portalAt + 26

  const sites = [...nodes.values()].filter(node => node.kind === 'site')
  const regions = [...nodes.values()].filter(node => node.kind === 'region')
  const systems: System[] = []
  const hazes: Haze[] = []

  // Each destination category is a solar system of its own at about our scale: a star, planets on orbits of their own at Kepler speeds
  // (several to an orbit once there are many), and one to three belts, halos or clouds so no two look alike.
  const plans = regions.map(region => {
    const list = sites.filter(site => site.block === region.id), kinds: BeltKind[] = ['asteroid', 'dust', 'asteroid', 'oort']
    const orbits = Math.max(1, Math.min(12, list.length)), perOrbit = Math.ceil(list.length / orbits)
    const wanted = 1 + Math.floor(hash(region.id + 'belts') * 3)
    const features: { kind: BeltKind; after: number }[] = []
    let oort = false
    for (let j = 0; j < wanted; j++) {
      const kind = kinds[Math.floor(hash(region.id + 'k' + j) * kinds.length)]
      if (kind === 'oort') { if (!oort) { oort = true; features.push({ kind, after: orbits }) } else features.push({ kind: 'asteroid', after: Math.floor((j + 1) * orbits / (wanted + 1)) }) }
      else features.push({ kind, after: Math.floor((j + 1) * orbits / (wanted + 1)) })
    }
    const star = 6 + Math.min(3, Math.log10(list.length + 1) * 2)
    let cursor = star + 16
    const orbitAt: number[] = [], belts: { inner: number; outer: number; kind: BeltKind; thick: number }[] = []
    for (let o = 0; o < orbits; o++) {
      for (const feature of features) if (feature.after === o && feature.kind !== 'oort') { belts.push({ inner: cursor + 3, outer: cursor + 15, kind: feature.kind, thick: 1.2 }); cursor += 18 }
      orbitAt.push(cursor)
      cursor += 10
    }
    for (const feature of features) if (feature.after >= orbits) {
      if (feature.kind === 'oort') { belts.push({ inner: cursor + 4, outer: cursor + 70, kind: 'oort', thick: 8 }); cursor += 74 }
      else { belts.push({ inner: cursor + 3, outer: cursor + 15, kind: feature.kind, thick: 1.2 }); cursor += 18 }
    }
    return { region, list, orbits, perOrbit, star, orbitAt, belts, size: cursor + 20 }
  }).sort((a, b) => b.size - a.size)

  // The galaxy is a spiral: a black hole at the centre and systems strung along three arms. Systems are stationary (a galaxy's turn is far
  // too slow to watch), placed in order of size so big ones claim room first, ours on the first arm about halfway out.
  const arms = galaxyArms, unit = homeReach, holeRadius = unit * 0.5, margin = unit * 4
  const placed: { x: number; z: number; size: number }[] = []
  const armAt = (arm: number, radius: number, key: string) => armAngle(arm, radius, unit) + (hash(key + radius) - 0.5) * 0.3
  const settle = (size: number, from: number, arm0: number, key: string) => {
    for (let radius = from; ; radius += unit * 0.2) for (let step = 0; step < arms; step++) {
      const theta = armAt((arm0 + step) % arms, radius, key), x = Math.cos(theta) * radius, z = Math.sin(theta) * radius
      if (placed.every(other => Math.hypot(other.x - x, other.z - z) >= other.size + size + margin)) { placed.push({ x, z, size }); return { x, z } }
    }
  }
  const homeSpot = settle(homeReach, unit * 22, 0, 'home')!
  const home: Vec = [homeSpot.x, 0, homeSpot.z]
  const spots = new Map<string, { x: number; z: number }>()
  // Spread along the arms from well outside the black hole to beyond ours, each searching outward from its own share of the radius.
  const innerEdge = unit * 6, outerEdge = unit * Math.max(34, 6 + plans.length * 3)
  plans.forEach((plan, index) => spots.set(plan.region.id, settle(plan.size, innerEdge + (index + 0.5) / plans.length * (outerEdge - innerEdge), (index + 1) % arms, plan.region.id)!))
  const galaxyRadius = Math.max(...placed.map(item => Math.hypot(item.x, item.z) + item.size)) + unit * 3

  // Move everything built around the origin to our place in the galaxy.
  const shift = (v: Vec): Vec => [v[0] + home[0], v[1] + home[1], v[2] + home[2]]
  for (let index = 0; index < homeBodies; index++) { bodies[index].center = shift(bodies[index].center); bodies[index].star = shift(bodies[index].star) }
  for (const belt of belts) belt.center = home
  const homeStar = shift(lanSun)
  const galaxyBelts: Belt[] = [...belts]

  plans.forEach(plan => {
    const spot = spots.get(plan.region.id)!, color = starColors[Math.floor(hash(plan.region.id) * starColors.length)], center: Vec = [spot.x, 0, spot.z]
    systems.push({ id: plan.region.id, x: spot.x, z: spot.z, radius: plan.size, color })
    put(plan.region, 'sun', { radius: plan.star, center, spin: 0.05, star: center })
    for (const belt of plan.belts) galaxyBelts.push({ ...belt, center, home: false })
    if (hash(plan.region.id + 'nebula') < 0.45) hazes.push({ center: [spot.x + (hash(plan.region.id + 'nx') - 0.5) * plan.size * 0.6, 0, spot.z + (hash(plan.region.id + 'nz') - 0.5) * plan.size * 0.6], radius: plan.size * (0.9 + hash(plan.region.id + 'nr') * 0.6), color })
    // The exit wormhole stands just outside the system on the side towards home, its mouth facing the star.
    const dx = home[0] - spot.x, dz = home[2] - spot.z, length = Math.hypot(dx, dz) || 1, toward: Vec = [dx / length, 0, dz / length]
    put({ ...plan.region, id: `exit:${plan.region.id}` }, 'exit', { radius: 5 + Math.min(4, plan.size / 80), center: [spot.x + toward[0] * (plan.size + 25), 0, spot.z + toward[2] * (plan.size + 25)], face: [-toward[0], 0, -toward[2]], star: center })
    plan.list.forEach((site, k) => {
      const orbit = plan.orbitAt[k % plan.orbits], slot = Math.floor(k / plan.orbits), index = k % plan.orbits
      put(site, 'world', {
        radius: 3.5, center, orbit, omega: kepler(60, 0.03, orbit),
        phase: (slot / plan.perOrbit) * TAU + index * 0.9 + hash(site.id) * 0.3, tilt: 0.05, spin: 0.3 + hash(site.id) * 0.4, star: center,
      })
    })
  })
  // Parents before children, so one pass places everything.
  const order = new Map<string, number>()
  const rank = (body: Body): number => {
    const known = order.get(body.id)
    if (known !== undefined) return known
    order.set(body.id, 0)
    const value = body.parent && byId.has(body.parent) ? rank(byId.get(body.parent)!) + 1 : 0
    order.set(body.id, value)
    return value
  }
  bodies.sort((a, b) => rank(a) - rank(b))
  return {
    bodies, byId, lan: { radius: portalAt + 14, star: homeStar }, galaxy: { radius: galaxyRadius, unit }, systems,
    rings, belts: galaxyBelts, hazes, hole: { radius: holeRadius }, extent: galaxyRadius,
  }
}
