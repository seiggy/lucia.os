import * as THREE from 'three'
import { OrbitControls } from 'three/addons/controls/OrbitControls.js'
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js'
import { RenderPass } from 'three/addons/postprocessing/RenderPass.js'
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js'
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js'
import type { Health, LabMapLive, Layout, MapNode, Pt, Service } from './labMapModel'
import { carServices, effectiveHealth, lodLevel, mixTotals, portsOf, roadPath, routeOf, serviceColors, services, wirelessVia, streaks } from './labMapModel'

export const palette = { void: 0x04050a, cyan: 0x00f0ff, acid: 0xa6ff00, amber: 0xffb000, red: 0xff4d6d, magenta: 0xff2bd6, dim: 0x5f6b7a, wire: 0x3f6577, asphalt: 0x080a14, kerb: 0xb8c2ff, air: 0xd38cff }
export const healthColor: Record<Health, number> = { ok: palette.acid, warn: palette.amber, failed: palette.red, stale: palette.dim, unknown: palette.dim }
// Quarters and Internet districts stay cyan; anything unwell shows its health; otherwise a building wears its category colour.
export const toneOf = (node: MapNode, health: Health) =>
  node.kind === 'network' || node.kind === 'region' ? palette.cyan : health === 'warn' || health === 'failed' || health === 'stale' ? healthColor[health] : node.tint ?? healthColor[health]
export interface Layers { clients: boolean; apps: boolean; storage: boolean; traffic: boolean; flows: boolean }
export interface SceneEvents { select: (id: string | null) => void }

interface Morph { group: THREE.Group; s0: THREE.Vector3; t0: THREE.Vector3; s1: THREE.Vector3; t1: THREE.Vector3; start: number; ghost: boolean }
const morphTime = 1200, flat = 0.001
const smooth = (t: number) => t < 0.5 ? 4 * t ** 3 : 1 - (2 - 2 * t) ** 3 / 2
// A district's floor: its bounding box, so a re-layout can scale it from its old footprint to its new one.
function footprint(layout: Layout, node: MapNode) {
  const polygon = node.kind === 'network' ? layout.plinths.find(item => item.id === node.id)?.polygon : layout.blocks.find(item => item.id === node.id)?.polygon
  if (!polygon || polygon.length < 3) return null
  const xs = polygon.map(point => point[0]), zs = polygon.map(point => point[1])
  const [l, r, t, b] = [Math.min(...xs), Math.max(...xs), Math.min(...zs), Math.max(...zs)]
  return { cx: (l + r) / 2, cz: (t + b) / 2, w: Math.max(r - l, 0.1), d: Math.max(b - t, 0.1) }
}
interface Construct { node: MapNode; group: THREE.Group; edges: THREE.LineBasicMaterial[]; faces: THREE.MeshBasicMaterial[]; label: HTMLElement; anchor: THREE.Vector3; labelAt: THREE.Vector3 }
interface Lane { id: string; curve: THREE.Curve<THREE.Vector3>; length: number; up: number; down: number; speed: number; flow: boolean; route: string[]; air?: boolean; tints?: (THREE.Color | undefined)[] }

const ease = (t: number) => t >= 1 ? 1 : 1 - 2 ** (-10 * t)
// Roads are dark asphalt with pale kerbs, so they read apart from the cyan land and the cars glow against them. [kind, width, kerb, asphalt]
const roadTiers = [['lane', 0.4, 0.2, 0.7], ['street', 0.75, 0.3, 0.8], ['avenue', 1.25, 0.4, 0.88], ['highway', 1.9, 0.62, 0.92]] as const
const serviceTint = Object.fromEntries(services.map(item => [item, new THREE.Color(serviceColors[item])])) as Record<Service, THREE.Color>
const tintsOf = (list: Service[]) => list.length ? list.map(item => serviceTint[item]) : undefined
const buildingKinds = new Set(['spark', 'node', 'app', 'client', 'site'])
const maxDoors = 1500, doorColor = new THREE.Color()
const maxCars = 1600, maxDrones = 800, carAir = new THREE.Color(palette.air), base = 0.12, carIn = new THREE.Color(palette.cyan), carOut = new THREE.Color(palette.amber), carDim = 0.12, carColor = new THREE.Color(), white = new THREE.Color(0xffffff)
function pathCurve(points: Pt[] | undefined, y: number) {
  const at = (points ?? []).map(([x, z]) => new THREE.Vector3(x, y, z)).filter((point, index, all) => !index || !point.equals(all[index - 1]))
  if (at.length < 2) return null
  const curve = new THREE.CurvePath<THREE.Vector3>()
  for (let index = 1; index < at.length; index++) curve.add(new THREE.LineCurve3(at[index - 1], at[index]))
  return curve
}

export class LabScene {
  private renderer: THREE.WebGLRenderer
  private scene = new THREE.Scene()
  private camera: THREE.PerspectiveCamera
  private controls: OrbitControls
  private composer: EffectComposer | null = null
  private world = new THREE.Group()
  private constructs = new Map<string, Construct>()
  private picks: THREE.Object3D[] = []
  private lanes: Lane[] = []
  private laneLines = new THREE.Group()
  private flowLines = new THREE.Group()
  private tetherLines = new THREE.Group()
  private routeLines = new THREE.Group()
  private routeStart = 0
  private ghosts = new THREE.Group()
  private morphs: Morph[] = []
  private cars: THREE.InstancedMesh
  private drones: THREE.InstancedMesh
  private doors: THREE.InstancedMesh
  private layout: Layout | null = null
  private signature = ''
  private live: LabMapLive | null = null
  private layers: Layers = { clients: true, apps: true, storage: true, traffic: true, flows: true }
  private selected: string | null = null
  private route: string[] = []
  private hovered: string | null = null
  private fly: { from: THREE.Vector3; to: THREE.Vector3; fromTarget: THREE.Vector3; toTarget: THREE.Vector3; start: number } | null = null
  private frame = 0
  private clock = new THREE.Timer()
  private userMoved = false
  private labelSizes = new Map<string, { w: number; h: number }>()
  private pointer = new THREE.Vector2()
  private down: { x: number; y: number } | null = null
  private raycaster = new THREE.Raycaster()
  private resizeObserver: ResizeObserver
  private dummy = new THREE.Object3D()
  private projected = new THREE.Vector3()
  private disposed = false

  private host: HTMLElement
  private labels: HTMLElement
  private events: SceneEvents
  private topDown: boolean

  constructor(host: HTMLElement, labels: HTMLElement, events: SceneEvents, topDown: boolean) {
    this.host = host
    this.labels = labels
    this.events = events
    this.topDown = topDown
    this.renderer = new THREE.WebGLRenderer({ antialias: true, powerPreference: 'high-performance' })
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2))
    this.renderer.setClearColor(palette.void)
    this.renderer.domElement.setAttribute('aria-hidden', 'true')
    host.prepend(this.renderer.domElement)
    this.camera = new THREE.PerspectiveCamera(45, 1, 0.5, 4000)
    this.controls = new OrbitControls(this.camera, this.renderer.domElement)
    this.controls.enableDamping = true
    this.controls.dampingFactor = 0.08
    this.controls.screenSpacePanning = false
    this.controls.maxPolarAngle = topDown ? 0.5 : Math.PI * 0.46
    this.controls.minDistance = 6
    if (topDown) this.controls.touches = { ONE: THREE.TOUCH.PAN, TWO: THREE.TOUCH.DOLLY_ROTATE }
    this.controls.autoRotateSpeed = 0.35
    this.controls.addEventListener('start', () => { this.userMoved = true })
    this.scene.add(this.world, this.ghosts, this.laneLines, this.flowLines, this.tetherLines, this.routeLines)
    this.cars = new THREE.InstancedMesh(new THREE.BoxGeometry(0.3, 0.16, 0.62),
      new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.95, blending: THREE.AdditiveBlending, depthWrite: false }), maxCars)
    for (let index = 0; index < maxCars; index++) this.cars.setColorAt(index, carIn)
    this.cars.count = 0
    this.cars.frustumCulled = false
    this.cars.renderOrder = 20
    this.scene.add(this.cars)
    this.drones = new THREE.InstancedMesh(new THREE.SphereGeometry(0.2, 12, 8),
      new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.95, blending: THREE.AdditiveBlending, depthWrite: false }), maxDrones)
    for (let index = 0; index < maxDrones; index++) this.drones.setColorAt(index, carAir)
    this.drones.count = 0
    this.drones.frustumCulled = false
    this.drones.renderOrder = 20
    this.scene.add(this.drones)
    this.doors = new THREE.InstancedMesh(new THREE.BoxGeometry(0.34, 0.42, 0.06),
      new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.9, blending: THREE.AdditiveBlending, depthWrite: false }), maxDoors)
    for (let index = 0; index < maxDoors; index++) this.doors.setColorAt(index, carIn)
    this.doors.count = 0
    this.doors.frustumCulled = false
    this.doors.renderOrder = 20
    this.scene.add(this.doors)
    if (host.clientWidth >= 700) {
      this.composer = new EffectComposer(this.renderer)
      this.composer.addPass(new RenderPass(this.scene, this.camera))
      this.composer.addPass(new UnrealBloomPass(new THREE.Vector2(256, 256), 0.7, 0.3, 0.32))
      this.composer.addPass(new OutputPass())
    }
    const canvas = this.renderer.domElement
    canvas.addEventListener('pointerdown', this.onDown)
    canvas.addEventListener('pointerup', this.onUp)
    canvas.addEventListener('pointermove', this.onMove)
    canvas.addEventListener('pointerleave', this.onLeave)
    document.addEventListener('visibilitychange', this.onVisibility)
    this.resizeObserver = new ResizeObserver(() => this.resize())
    this.resizeObserver.observe(host)
    this.resize()
    this.loop()
  }

  setLayout(layout: Layout) {
    const signature = [...layout.nodes.values()].map(node => `${node.id}@${node.x.toFixed(1)},${node.y.toFixed(1)},${node.z.toFixed(1)}:${node.label}`).join('|')
      + layout.tethers.map(line => `${line.from}>${line.to}`).join('|')
    const first = !this.layout, previous = this.layout
    this.layout = layout
    if (signature !== this.signature) {
      this.signature = signature
      this.build(previous)
    }
    if (first) this.overview(false)
    this.paint()
  }

  setLive(live: LabMapLive | null) {
    this.live = live
    this.buildLanes()
    this.paint()
  }

  setLayers(layers: Layers) {
    this.layers = layers
    this.applyLayers()
  }

  setWallboard(on: boolean) { this.controls.autoRotate = on }

  select(id: string | null, route: string[]) {
    this.selected = id
    this.route = id ? route : []
    this.paint()
    this.buildRoute()
    if (id) this.flyTo(id)
  }

  overview(animate = true) {
    if (!this.layout) return
    const bounds: THREE.Vector3[] = [new THREE.Vector3(0, 10, 0)]
    for (const point of this.layout.outline) bounds.push(new THREE.Vector3(point[0], 0, point[1]))
    for (const node of this.layout.nodes.values()) bounds.push(new THREE.Vector3(node.x, node.y, node.z))
    const box = new THREE.Box3().setFromPoints(bounds)
    const target = box.getCenter(new THREE.Vector3()).setY(0)
    const reach = Math.max(box.max.x - box.min.x, box.max.z - box.min.z) / 2
    const tilt = this.topDown ? 0.01 : THREE.MathUtils.degToRad(54)
    const direction = new THREE.Vector3(0, Math.sin(tilt), Math.cos(tilt))
    const probe = this.camera.clone()
    const point = new THREE.Vector3()
    const half = Math.tan(THREE.MathUtils.degToRad(probe.fov / 2))
    let distance = reach
    for (let pass = 0; pass < 3; pass++) {
      for (distance = reach * 0.4; distance < reach * 8; distance += reach * 0.03) {
        probe.position.copy(target).addScaledVector(direction, distance)
        probe.lookAt(target)
        probe.updateMatrixWorld()
        if (bounds.every(bound => { point.copy(bound).project(probe); return Math.abs(point.x) < 0.9 && Math.abs(point.y) < 0.82 })) break
      }
      if (pass === 2) break
      let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity
      for (const bound of bounds) {
        point.copy(bound).project(probe)
        minX = Math.min(minX, point.x); maxX = Math.max(maxX, point.x); minY = Math.min(minY, point.y); maxY = Math.max(maxY, point.y)
      }
      target.x += (minX + maxX) / 2 * half * probe.aspect * distance
      target.z -= (minY + maxY) / 2 * half * distance / Math.max(Math.sin(tilt), 0.2)
    }
    if (animate) this.userMoved = false
    this.moveCamera(target.clone().addScaledVector(direction, distance), target, animate)
  }

  flyTo(id: string) {
    const item = this.constructs.get(id)
    if (!item) return
    if (item.node.kind === 'wan') { this.overview(); return }
    const distance = item.node.kind === 'network' ? Math.max(28, item.node.size * 1.6) : item.node.kind === 'gateway' ? 34 : item.node.kind === 'app' || item.node.kind === 'client' ? 13 : 20
    const target = item.anchor.clone()
    const direction = this.camera.position.clone().sub(this.controls.target).normalize()
    if (this.topDown) direction.set(0, 1, 0.01).normalize()
    else if (direction.y < 0.35) direction.setY(0.55).normalize()
    this.moveCamera(target.clone().add(direction.multiplyScalar(distance)), target, true)
  }

  dispose() {
    this.disposed = true
    cancelAnimationFrame(this.frame)
    this.resizeObserver.disconnect()
    document.removeEventListener('visibilitychange', this.onVisibility)
    const canvas = this.renderer.domElement
    canvas.removeEventListener('pointerdown', this.onDown)
    canvas.removeEventListener('pointerup', this.onUp)
    canvas.removeEventListener('pointermove', this.onMove)
    canvas.removeEventListener('pointerleave', this.onLeave)
    this.controls.dispose()
    this.clear(this.scene)
    this.composer?.dispose()
    this.renderer.dispose()
    canvas.remove()
    this.labels.replaceChildren()
  }

  private moveCamera(position: THREE.Vector3, target: THREE.Vector3, animate: boolean) {
    if (!animate) {
      this.camera.position.copy(position)
      this.controls.target.copy(target)
      this.controls.update()
      return
    }
    this.fly = { from: this.camera.position.clone(), to: position, fromTarget: this.controls.target.clone(), toTarget: target, start: performance.now() }
  }

  private clear(root: THREE.Object3D) {
    root.traverse(object => {
      const item = object as THREE.Mesh
      item.geometry?.dispose()
      const material = item.material
      if (Array.isArray(material)) material.forEach(entry => entry.dispose())
      else material?.dispose()
    })
  }

  private wire(geometry: THREE.BufferGeometry, color: number, face = 0.07, edge = 1) {
    const edges = new THREE.LineBasicMaterial({ color, transparent: true, opacity: edge })
    const faces = new THREE.MeshBasicMaterial({ color, transparent: true, opacity: face, depthWrite: false, side: THREE.DoubleSide })
    const group = new THREE.Group()
    group.add(new THREE.Mesh(geometry, faces), new THREE.LineSegments(new THREE.EdgesGeometry(geometry, 20), edges))
    return { group, edges, faces }
  }

  private ring(radius: number, color: number, opacity: number, vertical = false, sides = 64) {
    const points = new THREE.EllipseCurve(0, 0, radius, radius).getPoints(sides).map(point => vertical ? new THREE.Vector3(point.x, point.y, 0) : new THREE.Vector3(point.x, 0, point.y))
    const material = new THREE.LineBasicMaterial({ color, transparent: true, opacity })
    return { line: new THREE.LineLoop(new THREE.BufferGeometry().setFromPoints(points), material), material }
  }

  private anchorOf(node: MapNode) { return new THREE.Vector3(node.x, base + 0.05, node.z) }

  private build(previous: Layout | null = null) {
    const layout = this.layout!
    // On a re-layout, new buildings rise, gone ones sink, moved ones glide and districts grow or shrink to their new footprint.
    const before = new Map(this.constructs), at = performance.now()
    const animate = !!previous && before.size > 0 && !window.matchMedia('(prefers-reduced-motion: reduce)').matches
    this.morphs = this.morphs.filter(morph => morph.ghost)
    if (animate) for (const [id, item] of before) if (!layout.nodes.has(id) && item.group.visible) {
      const area = footprint(previous!, item.node)
      this.world.remove(item.group)
      this.ghosts.add(item.group)
      this.morphs.push({ group: item.group, s0: item.group.scale.clone(), t0: item.group.position.clone(), ghost: true, start: at,
        s1: area ? new THREE.Vector3(flat, 1, flat) : new THREE.Vector3(1, flat, 1), t1: area ? new THREE.Vector3(area.cx, 0, area.cz) : new THREE.Vector3() })
    }
    for (const group of [this.world, this.laneLines, this.flowLines, this.tetherLines, this.routeLines]) { this.clear(group); group.clear() }
    for (const object of [...this.scene.children]) if (object.userData.environment) { this.clear(object); this.scene.remove(object) }
    this.constructs.clear()
    this.picks = []
    this.labelSizes.clear()
    this.labels.replaceChildren()
    const radius = layout.radius

    this.scene.fog = new THREE.FogExp2(palette.void, 1.1 / (radius * 5))
    const size = Math.ceil(radius * 14 / 8) * 8
    const grid = new THREE.GridHelper(size, size / 4, palette.cyan, palette.cyan)
    const gridMaterial = grid.material as THREE.LineBasicMaterial
    gridMaterial.transparent = true
    gridMaterial.opacity = 0.11
    gridMaterial.depthWrite = false
    grid.userData.environment = true
    this.scene.add(grid)

    for (const node of layout.nodes.values()) {
      const plinth = node.kind === 'network' ? layout.plinths.find(item => item.id === node.id) : undefined
      const color = this.tone(node, node.health)
      const group = new THREE.Group()
      const edges: THREE.LineBasicMaterial[] = [], faces: THREE.MeshBasicMaterial[] = []
      const take = (built: { group: THREE.Group; edges: THREE.LineBasicMaterial; faces: THREE.MeshBasicMaterial }) => { edges.push(built.edges); faces.push(built.faces); group.add(built.group); return built.group }
      let pick: THREE.Object3D
      let labelAt = new THREE.Vector3(node.x, 0, node.z)
      if (node.kind === 'network' && plinth) {
        const shape = new THREE.Shape(plinth.polygon.map(([x, z]) => new THREE.Vector2(x, -z)))
        const floor = new THREE.ExtrudeGeometry(shape, { depth: base, bevelEnabled: false })
        floor.rotateX(-Math.PI / 2)
        const slab = take(this.wire(floor, color, 0.035, 0.42))
        pick = slab.children[0]
        labelAt = new THREE.Vector3(plinth.labelX, base, plinth.labelZ)
      } else if (node.kind === 'gateway') {
        // The gateway is a suspension bridge carrying the highway out to the internet.
        const span = new THREE.Group()
        span.position.set(node.x, 0, node.z)
        span.rotation.y = node.angle ?? 0
        const deck = take(this.wire(new THREE.BoxGeometry(3.4, 0.16, 12), color, 0.08))
        deck.position.y = 0.5
        span.add(deck)
        for (const x of [-1.9, 1.9]) {
          for (const z of [-4.5, 4.5]) { const post = take(this.wire(new THREE.BoxGeometry(0.45, 6, 0.45), color, 0.1)); post.position.set(x, 3.5, z); span.add(post) }
          const cable = new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.8 })
          edges.push(cable)
          span.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(Array.from({ length: 19 }, (_, k) => { const z = -4.5 + k * 0.5; return new THREE.Vector3(x, 0.6 + 5.4 * (z / 4.5) ** 2, z) })), cable))
        }
        for (const z of [-4.5, 4.5]) { const beam = take(this.wire(new THREE.BoxGeometry(4.25, 0.35, 0.45), color, 0.1)); beam.position.set(0, 6.3, z); span.add(beam) }
        group.add(span)
        pick = deck.children[0]
        labelAt = new THREE.Vector3(node.x, 7.4, node.z)
      } else if (node.kind === 'wan') {
        const marker = take(this.wire(new THREE.OctahedronGeometry(5), healthColor[node.health], 0.1))
        marker.position.set(node.x, 9, node.z)
        pick = marker.children[0]
        for (const r of [16, 10]) {
          const ground = this.ring(r, healthColor[node.health], r === 16 ? 0.7 : 0.35, false, 6)
          ground.line.position.set(node.x, 0.05, node.z)
          edges.push(ground.material)
          group.add(ground.line)
        }
        group.traverse(object => { const material = (object as THREE.Mesh).material as THREE.Material | undefined; if (material && 'fog' in material) material.fog = false })
        labelAt = new THREE.Vector3(node.x, 16, node.z)
      } else if (node.kind === 'switch' && node.arch) {
        // The core switch is the archway every car passes through between the home and the highway.
        const arch = new THREE.Group()
        arch.position.set(node.x, 0, node.z)
        arch.rotation.y = node.angle ?? 0
        for (const x of [-3, 3]) { const pillar = take(this.wire(new THREE.BoxGeometry(0.9, 6, 0.9), color, 0.1)); pillar.position.set(x, 3, 0); arch.add(pillar) }
        const lintel = take(this.wire(new THREE.BoxGeometry(7.6, 0.9, 1.1), color, 0.12))
        lintel.position.y = 6.4
        arch.add(lintel)
        group.add(arch)
        pick = lintel.children[0]
        labelAt = new THREE.Vector3(node.x, 7.2, node.z)
      } else if (node.kind === 'switch') {
        const body = take(this.wire(new THREE.BoxGeometry(4, 0.8, 2.2), color))
        body.position.set(node.x, 0.4, node.z)
        const next = node.path?.[1]
        body.rotation.y = next ? Math.atan2(next[0] - node.x, next[1] - node.z) : 0
        pick = body.children[0]
        labelAt = new THREE.Vector3(node.x, 1, node.z)
      } else if (node.kind === 'ap') {
        const body = take(this.wire(new THREE.OctahedronGeometry(1.2), color, 0.1))
        body.position.set(node.x, 3, node.z)
        pick = body.children[0]
        const stem = new THREE.Line(new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(node.x, 0, node.z), new THREE.Vector3(node.x, 1.8, node.z)]),
          new THREE.LineBasicMaterial({ color: palette.wire, transparent: true, opacity: 0.6 }))
        group.add(stem)
        labelAt = new THREE.Vector3(node.x, 4.4, node.z)
      } else if (buildingKinds.has(node.kind)) {
        const height = node.height ?? 2, body = take(this.wire(new THREE.BoxGeometry(node.size, height, node.size), color, node.kind === 'client' ? 0.06 : 0.08))
        body.position.set(node.x, base + height / 2, node.z)
        body.rotation.y = node.angle ?? 0
        if (node.kind === 'spark' || node.kind === 'node') for (let level = 1; level < height; level++) {
          const slat = new THREE.Line(new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(-node.size / 2, level - height / 2, node.size / 2 + 0.01), new THREE.Vector3(node.size / 2, level - height / 2, node.size / 2 + 0.01)]),
            edges[edges.length - 1])
          body.add(slat)
        }
        pick = body.children[0]
        labelAt = new THREE.Vector3(node.x, base + height, node.z)
      } else if (node.kind === 'storage') {
        const r = node.size * 0.7, height = node.height ?? 3, body = take(this.wire(new THREE.CylinderGeometry(r, r, height, 8), color, 0.08))
        body.position.set(node.x, base + height / 2, node.z)
        for (const y of [0.3, 0.7]) { const band = this.ring(r + 0.02, color, 0.5); band.line.position.set(node.x, base + height * y, node.z); edges.push(band.material); group.add(band.line) }
        pick = body.children[0]
        labelAt = new THREE.Vector3(node.x, base + height, node.z)
      } else {
        const block = layout.blocks.find(item => item.id === node.id), polygon = block && block.polygon.length >= 3 ? block.polygon : [[node.x - 3, node.z - 3], [node.x + 3, node.z - 3], [node.x + 3, node.z + 3], [node.x - 3, node.z + 3]] as Pt[]
        const floor = new THREE.Mesh(new THREE.ShapeGeometry(new THREE.Shape(polygon.map(([x, z]) => new THREE.Vector2(x, -z)))),
          new THREE.MeshBasicMaterial({ color, transparent: true, opacity: 0.05, depthWrite: false, side: THREE.DoubleSide }))
        floor.rotation.x = -Math.PI / 2
        floor.position.y = base + 0.02
        faces.push(floor.material as THREE.MeshBasicMaterial)
        const outline = new THREE.LineLoop(new THREE.BufferGeometry().setFromPoints(polygon.map(([x, z]) => new THREE.Vector3(x, base + 0.03, z))), new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.45 }))
        edges.push(outline.material as THREE.LineBasicMaterial)
        group.add(floor, outline)
        pick = floor
        labelAt = new THREE.Vector3(block?.labelX ?? node.x, base, block?.labelZ ?? node.z)
      }
      pick.userData.id = node.id
      this.picks.push(pick)
      group.userData.kind = node.kind
      this.world.add(group)
      const label = document.createElement('div')
      label.className = 'labmap-plate'
      label.dataset.kind = node.kind
      const name = document.createElement('b')
      name.textContent = node.kind === 'clients' ? `${node.label} ${node.count ?? 0}` : node.label
      label.append(name)
      const detail = node.kind === 'network' ? layout.plinths.find(item => item.id === node.id)?.detail : node.kind === 'app' || node.kind === 'client' || node.kind === 'site' ? null : node.detail
      if (detail) { const small = document.createElement('span'); small.textContent = detail; label.append(small) }
      label.hidden = true
      this.labels.append(label)
      this.constructs.set(node.id, { node, group, edges, faces, label, anchor: this.anchorOf(node), labelAt })
      if (animate) this.morphIn(group, node, layout, before.get(node.id)?.node, previous!, at)
    }

    for (const [kind, width, edge, fill] of roadTiers) {
      const tier = roadTiers.findIndex(item => item[0] === kind), edges: THREE.Vector3[] = [], surface: THREE.Vector3[] = [], y = base + 0.02 + tier * 0.01
      for (const road of layout.roads) if (road.kind === kind) for (let index = 1; index < road.points.length; index++) {
        const [ax, az] = road.points[index - 1], [bx, bz] = road.points[index], length = Math.hypot(bx - ax, bz - az)
        if (!length) continue
        const ox = -(bz - az) / length * width / 2, oz = (bx - ax) / length * width / 2
        const corner = (x: number, z: number, side: number) => new THREE.Vector3(x + ox * side, y, z + oz * side)
        const [a1, a2, b1, b2] = [corner(ax, az, 1), corner(ax, az, -1), corner(bx, bz, 1), corner(bx, bz, -1)]
        surface.push(a1, a2, b1, b1, a2, b2)
        if (kind === 'lane') edges.push(new THREE.Vector3(ax, y, az), new THREE.Vector3(bx, y, bz))
        else edges.push(a1, b1, a2, b2)
      }
      const ribbon = new THREE.Mesh(new THREE.BufferGeometry().setFromPoints(surface), new THREE.MeshBasicMaterial({ color: palette.asphalt, transparent: true, opacity: fill, depthWrite: false, side: THREE.DoubleSide }))
      const lines = new THREE.LineSegments(new THREE.BufferGeometry().setFromPoints(edges), new THREE.LineBasicMaterial({ color: palette.kerb, transparent: true, opacity: edge }))
      ribbon.renderOrder = lines.renderOrder = 1 + tier
      ribbon.userData.kind = lines.userData.kind = 'road'
      this.laneLines.add(ribbon, lines)
    }
    for (const block of layout.blocks) {
      const owner = layout.nodes.get(block.id)
      if (owner?.kind === 'clients' || block.polygon.length < 3) continue
      const kind = owner?.kind ?? block.layer ?? 'network'
      const outline = new THREE.LineLoop(new THREE.BufferGeometry().setFromPoints(block.polygon.map(([x, z]) => new THREE.Vector3(x, base + 0.03, z))),
        new THREE.LineBasicMaterial({ color: block.tint ?? palette.cyan, transparent: true, opacity: block.tint === undefined ? 0.22 : 0.5 }))
      outline.userData.kind = kind
      this.laneLines.add(outline)
      if (block.fill) {
        const ground = new THREE.Mesh(new THREE.ShapeGeometry(new THREE.Shape(block.polygon.map(([x, z]) => new THREE.Vector2(x, -z)))),
          new THREE.MeshBasicMaterial({ color: block.tint ?? palette.cyan, transparent: true, opacity: 0.045, depthWrite: false, side: THREE.DoubleSide }))
        ground.rotation.x = -Math.PI / 2
        ground.position.y = base + 0.015
        ground.userData.kind = kind
        this.laneLines.add(ground)
      }
    }
    for (const tether of layout.tethers) {
      const from = this.constructs.get(tether.from), to = this.constructs.get(tether.to)
      if (!from || !to) continue
      const end = tether.kind === 'network' ? to.labelAt.clone().setY(0.5) : to.anchor
      const road = tether.kind === 'storage' ? pathCurve(roadPath(layout, tether.from, tether.to), base + 0.08) : null
      const line = new THREE.Line(new THREE.BufferGeometry().setFromPoints(road ? road.getSpacedPoints(Math.max(8, Math.round(road.getLength()))) : [from.anchor, end]), new THREE.LineDashedMaterial({ color: palette.wire, dashSize: 0.6, gapSize: 0.5, transparent: true, opacity: 0.85 }))
      line.computeLineDistances()
      line.userData.kind = tether.kind === 'storage' ? 'storage' : 'app'
      this.tetherLines.add(line)
    }
    this.applyLayers()
    this.buildLanes()
    this.buildRoute()
  }

  private morphIn(group: THREE.Group, node: MapNode, layout: Layout, old: MapNode | undefined, previous: Layout, start: number) {
    const area = footprint(layout, node), was = old && footprint(previous, old)
    let s0: THREE.Vector3, t0: THREE.Vector3
    if (!old) [s0, t0] = area ? [new THREE.Vector3(flat, 1, flat), new THREE.Vector3(area.cx, 0, area.cz)] : [new THREE.Vector3(1, flat, 1), new THREE.Vector3()]
    else if (area && was) {
      const sx = was.w / area.w, sz = was.d / area.d
      ;[s0, t0] = [new THREE.Vector3(sx, 1, sz), new THREE.Vector3(was.cx - sx * area.cx, 0, was.cz - sz * area.cz)]
    } else [s0, t0] = [new THREE.Vector3(1, old.height && node.height ? old.height / node.height : 1, 1), new THREE.Vector3(old.x - node.x, 0, old.z - node.z)]
    if (Math.abs(s0.x - 1) + Math.abs(s0.y - 1) + Math.abs(s0.z - 1) < 0.01 && t0.lengthSq() < 0.01) return
    group.scale.copy(s0)
    group.position.copy(t0)
    this.morphs.push({ group, s0, t0, s1: new THREE.Vector3(1, 1, 1), t1: new THREE.Vector3(), start, ghost: false })
  }

  private updateMorphs(now: number) {
    if (!this.morphs.length) return
    this.morphs = this.morphs.filter(morph => {
      const t = smooth(Math.min(1, (now - morph.start) / morphTime))
      morph.group.scale.lerpVectors(morph.s0, morph.s1, t)
      morph.group.position.lerpVectors(morph.t0, morph.t1, t)
      if (t < 1) return true
      if (morph.ghost) { this.ghosts.remove(morph.group); this.clear(morph.group) }
      return false
    })
    this.doors.visible = !this.morphs.some(morph => !morph.ghost)
  }

  private buildLanes() {
    const layout = this.layout
    for (const line of [...this.flowLines.children]) { this.clear(line); this.flowLines.remove(line) }
    this.lanes = []
    if (!layout || !this.live) { this.buildDoors(); return }
    const mixes = mixTotals(layout, this.live)
    const speedOf = (shape: { speed: number }, air: boolean) => (4 + shape.speed * 30) * (air ? 0.3 : 1)
    // Wi-Fi hops fly roof to AP in a straight line; everything past the AP drives the wired roads.
    const airCurve = (id: string, ap: string) => {
      const from = this.constructs.get(id), to = this.constructs.get(ap)
      return from && to ? new THREE.LineCurve3(this.roof(from), this.roof(to)) : null
    }
    for (const [id, rate] of Object.entries(this.live.rates)) {
      const node = layout.nodes.get(id), item = this.constructs.get(id)
      if (!node || !item || node.kind === 'network' || node.kind === 'wan' || node.kind === 'clients') continue
      const ap = wirelessVia(layout, id), air = ap ? airCurve(id, ap) : null
      if (air) {
        const total = rate.rxBps + rate.txBps, shape = streaks(total), length = Math.max(1, air.getLength())
        if (!shape.count) continue
        const up = Math.round(shape.count * (rate.txBps / Math.max(total, 1)))
        this.lanes.push({ id, curve: air, length, up, down: shape.count - up, speed: speedOf(shape, true), flow: false, route: routeOf(layout, id), air: true })
        continue
      }
      const parent = node.kind === 'gateway' ? this.constructs.get('wan') : node.parentId ? this.constructs.get(node.parentId) : undefined
      if (!parent) continue
      const curve = pathCurve(node.path, base + 0.14) ?? new THREE.LineCurve3(item.anchor.clone().setY(base + 0.14), parent.anchor.clone().setY(base + 0.14))
      const total = rate.rxBps + rate.txBps, shape = streaks(total), length = Math.max(1, curve.getLength())
      if (!shape.count) continue
      // Busy roads carry more cars; long roads keep the same spacing. Cars take the colours of the traffic types on that road.
      const cars = Math.round(shape.count * Math.min(4, Math.max(1, length / 22))), up = Math.round(cars * (rate.txBps / Math.max(total, 1)))
      // A car wears its destination's colour: outbound cars the traffic type they head for, inbound cars the building they are delivering to.
      const mix = tintsOf(carServices(mixes.get(id), cars)), own = buildingKinds.has(node.kind) || node.kind === 'storage' ? node.tint : undefined
      const home = own === undefined ? undefined : new THREE.Color(own), tints = Array.from({ length: cars }, (_, k) => k < up ? mix?.[k] : home ?? mix?.[k])
      this.lanes.push({ id, curve, length, up, down: cars - up, speed: 4 + shape.speed * 30, flow: false, route: routeOf(layout, id), tints })
    }
    // A destination not yet standing in the Internet city drives to the WAN instead.
    const known = (id: string) => this.constructs.has(id) ? id : id === 'wan' || id.startsWith('dest:') ? 'wan' : null
    for (const flow of this.live.flows) {
      const source = known(flow.from), target = known(flow.to)
      if (!source || !target || source === target) continue
      const shape = streaks(flow.bps), id = `${flow.from}>${flow.to}`, route = [source, target], goal = this.constructs.get(target)?.node.tint, tint = goal === undefined ? serviceTint[flow.service] : new THREE.Color(goal)
      const fromAp = wirelessVia(layout, source), toAp = wirelessVia(layout, target)
      const start = fromAp ?? source, end = toAp ?? target
      for (const [client, ap, out] of [[source, fromAp, true], [target, toAp, false]] as const) {
        const air = ap ? airCurve(client, ap) : null
        if (!air) continue
        const dash = new THREE.Line(new THREE.BufferGeometry().setFromPoints([air.v1, air.v2]), new THREE.LineDashedMaterial({ color: palette.air, dashSize: 0.4, gapSize: 0.35, transparent: true, opacity: 0.45 }))
        dash.computeLineDistances()
        this.flowLines.add(dash)
        if (shape.count) this.lanes.push({ id, curve: air, length: air.getLength(), up: out ? shape.count : 0, down: out ? 0 : shape.count, speed: speedOf(shape, true), flow: true, route, air: true })
      }
      if (start === end) continue
      const curve = pathCurve(roadPath(layout, start, end), base + 0.2)
      if (!curve) continue
      const line = new THREE.Line(new THREE.BufferGeometry().setFromPoints(curve.getSpacedPoints(Math.max(8, Math.round(curve.getLength())))), new THREE.LineBasicMaterial({ color: serviceColors[flow.service], transparent: true, opacity: 0.3 }))
      this.flowLines.add(line)
      if (shape.count) this.lanes.push({ id, curve, length: curve.getLength(), up: shape.count, down: 0, speed: speedOf(shape, false), flow: true, route, tints: Array(shape.count).fill(tint) })
    }
    this.flowLines.visible = this.layers.flows
    this.buildDoors()
  }

  // Garage doors around a building's base, one per port, coloured by the traffic type that port serves.
  private buildDoors() {
    let count = 0
    const layout = this.layout
    if (layout) for (const item of this.constructs.values()) {
      const node = item.node
      if (!item.group.visible || !(buildingKinds.has(node.kind) || node.kind === 'storage')) continue
      const ports = portsOf(layout, this.live, node.id), round = node.kind === 'storage'
      const half = round ? node.size * 0.7 * 0.92 : node.size / 2, pitch = Math.min(0.45, node.size * 0.8 / Math.max(1, Math.ceil(ports.length / 4)))
      ports.forEach((port, k) => {
        if (count >= maxDoors) return
        const face = k % 4, slot = Math.floor(k / 4), onFace = Math.floor((ports.length - 1 - face) / 4) + 1
        this.dummy.position.set(node.x, base + 0.22, node.z)
        this.dummy.rotation.set(0, (node.angle ?? 0) + face * Math.PI / 2 + (round ? Math.PI / 8 : 0), 0)
        this.dummy.translateZ(half + 0.03)
        this.dummy.translateX((slot - (onFace - 1) / 2) * pitch)
        this.dummy.updateMatrix()
        this.doors.setMatrixAt(count, this.dummy.matrix)
        this.doors.setColorAt(count++, doorColor.copy(serviceTint[port.service]).multiplyScalar(0.5))
      })
    }
    this.doors.count = count
    this.doors.instanceMatrix.needsUpdate = true
    if (this.doors.instanceColor) this.doors.instanceColor.needsUpdate = true
  }

  private tone(node: MapNode, health: Health) { return toneOf(node, health) }

  private roof(item: Construct) {
    const node = item.node
    if (node.kind === 'ap') return new THREE.Vector3(node.x, 3, node.z)
    return new THREE.Vector3(node.x, node.kind === 'client' || node.kind === 'app' || node.kind === 'spark' || node.kind === 'node' || node.kind === 'storage' ? item.labelAt.y + 0.45 : item.anchor.y + 1, node.z)
  }

  private buildRoute() {
    for (const line of [...this.routeLines.children]) { this.clear(line); this.routeLines.remove(line) }
    this.routeStart = performance.now()
    for (let index = 0; index < this.route.length - 1; index++) {
      const from = this.constructs.get(this.route[index]), to = this.constructs.get(this.route[index + 1])
      if (!from || !to) continue
      const along = from.node.parentId === to.node.id ? from.node.path : undefined
      const path = pathCurve(along, 0.2) ?? new THREE.LineCurve3(from.anchor.clone().setY(from.anchor.y + 0.05), to.anchor.clone().setY(to.anchor.y + 0.05))
      const material = new THREE.MeshBasicMaterial({ color: palette.magenta, transparent: true, opacity: 0, depthWrite: false, fog: to.node.kind !== 'wan' })
      const tube = new THREE.Mesh(new THREE.TubeGeometry(path, Math.max(1, (along?.length ?? 2) * 8), 0.09, 6), material)
      tube.userData.hop = index
      this.routeLines.add(tube)
    }
  }

  private paint() {
    for (const item of this.constructs.values()) {
      const health = effectiveHealth(item.node, this.live)
      const inRoute = this.route.includes(item.node.id)
      const color = item.node.id === this.selected ? palette.magenta : this.tone(item.node, health)
      for (const material of item.edges) material.color.setHex(color)
      for (const material of item.faces) { material.color.setHex(color); material.opacity = item.node.id === this.selected ? 0.16 : item.node.kind === 'network' ? 0.02 : item.node.kind === 'clients' || item.node.kind === 'region' ? 0.025 : 0.08 }
      item.label.dataset.health = health
      item.label.classList.toggle('is-selected', item.node.id === this.selected)
      item.label.classList.toggle('is-route', inRoute && item.node.id !== this.selected)
    }
  }

  private applyLayers() {
    const hiddenKind = (kind: string) => ((kind === 'clients' || kind === 'client') && !this.layers.clients) || (kind === 'app' && !this.layers.apps) || (kind === 'storage' && !this.layers.storage)
    for (const item of this.constructs.values()) item.group.visible = !hiddenKind(item.node.kind)
    for (const line of this.laneLines.children) line.visible = !hiddenKind(line.userData.kind)
    for (const line of this.tetherLines.children) line.visible = !hiddenKind(line.userData.kind)
    this.flowLines.visible = this.layers.flows
    this.picks = [...this.constructs.values()].filter(item => item.group.visible).map(item => this.pickOf(item)).filter((item): item is THREE.Object3D => !!item)
    this.buildDoors()
  }

  private pickOf(item: Construct) {
    let found: THREE.Object3D | undefined
    item.group.traverse(object => { if (object.userData.id === item.node.id) found = object })
    return found
  }

  private resize() {
    const width = this.host.clientWidth, height = this.host.clientHeight
    if (!width || !height) return
    this.renderer.setSize(width, height, false)
    this.composer?.setSize(width, height)
    this.camera.aspect = width / height
    this.camera.updateProjectionMatrix()
    if (this.layout && !this.userMoved && !this.selected && !this.fly) this.overview(false)
  }

  private loop = () => {
    if (this.disposed) return
    this.frame = requestAnimationFrame(this.loop)
    this.clock.update()
    const delta = Math.min(this.clock.getDelta(), 0.1), time = this.clock.getElapsed(), now = performance.now()
    if (this.fly) {
      const progress = Math.min(1, (now - this.fly.start) / 600), t = ease(progress)
      this.camera.position.lerpVectors(this.fly.from, this.fly.to, t)
      this.controls.target.lerpVectors(this.fly.fromTarget, this.fly.toTarget, t)
      if (progress >= 1) this.fly = null
    }
    this.controls.update(delta)
    for (const tube of this.routeLines.children) {
      const material = (tube as THREE.Mesh).material as THREE.MeshBasicMaterial
      material.opacity = Math.min(0.95, Math.max(0, (now - this.routeStart - tube.userData.hop * 110) / 160) * 0.95)
    }
    this.updateMorphs(now)
    this.updateCars(time)
    this.updateLabels()
    if (this.composer) this.composer.render(delta)
    else this.renderer.render(this.scene, this.camera)
  }

  // Cars drive each road coloured by traffic type, paler heading out and full colour coming home (amber out, cyan home when the type is unknown).
  // A selection fades cars that aren't on its route.
  private updateCars(time: number) {
    let count = 0, flying = 0
    if (this.layers.traffic || this.layers.flows) for (const lane of this.lanes) {
      if (lane.flow ? !this.layers.flows : !this.layers.traffic) continue
      const node = this.constructs.get(lane.id)
      if (node && !node.group.visible) continue
      const total = lane.up + lane.down, lit = !this.selected || lane.route.includes(this.selected) || this.route.includes(lane.id)
      for (let index = 0; index < total && (lane.air ? flying < maxDrones : count < maxCars); index++) {
        const out = index < lane.up, phase = (time * lane.speed / lane.length + (out ? index / Math.max(1, lane.up) : (index - lane.up) / Math.max(1, lane.down) + 0.37)) % 1
        const t = out ? phase : 1 - phase
        if (lane.air) {
          this.dummy.position.copy(lane.curve.getPointAt(t))
          this.dummy.position.y += Math.sin(time * 3 + index * 1.7) * 0.08
          this.dummy.rotation.set(0, 0, 0)
          this.dummy.updateMatrix()
          this.drones.setMatrixAt(flying, this.dummy.matrix)
          this.drones.setColorAt(flying++, carColor.copy(carAir).multiplyScalar(lit ? 1 : carDim))
          continue
        }
        const point = lane.curve.getPointAt(t), ahead = lane.curve.getPointAt(Math.min(1, Math.max(0, t + (out ? 0.004 : -0.004))))
        const dx = ahead.x - point.x, dz = ahead.z - point.z, len = Math.hypot(dx, dz)
        this.dummy.position.copy(point)
        if (len > 1e-6) { this.dummy.position.x += -dz / len * 0.2; this.dummy.position.z += dx / len * 0.2; this.dummy.lookAt(ahead.x - dz / len * 0.2, point.y, ahead.z + dx / len * 0.2) }
        this.dummy.updateMatrix()
        this.cars.setMatrixAt(count, this.dummy.matrix)
        const tint = lane.tints?.[index]
        carColor.copy(tint ?? (out ? carOut : carIn))
        if (tint && out) carColor.lerp(white, 0.45)
        this.cars.setColorAt(count++, carColor.multiplyScalar(lit ? 1 : carDim))
      }
    }
    this.cars.count = count
    this.drones.count = flying
    this.drones.instanceMatrix.needsUpdate = true
    if (this.drones.instanceColor) this.drones.instanceColor.needsUpdate = true
    this.cars.instanceMatrix.needsUpdate = true
    if (this.cars.instanceColor) this.cars.instanceColor.needsUpdate = true
  }

  private updateLabels() {
    const width = this.host.clientWidth, height = this.host.clientHeight
    const distance = this.camera.position.distanceTo(this.controls.target), radius = this.layout?.radius ?? 50
    const level = distance > radius * 1.7 ? 0 : distance > radius * 0.75 ? 1 : 2
    const candidates: { item: Construct; x: number; y: number; rank: number }[] = []
    for (const item of this.constructs.values()) {
      const kind = item.node.kind
      const pinned = item.node.id === this.selected || item.node.id === this.hovered || this.route.includes(item.node.id)
      if (item.group.visible && (pinned || (lodLevel[kind] <= level && ((kind !== 'app' && kind !== 'client' && kind !== 'site') || this.camera.position.distanceTo(item.labelAt) < (kind === 'client' ? 24 : 34))))) {
        this.projected.copy(item.labelAt).multiply(item.group.scale).add(item.group.position).project(this.camera)
        if (this.projected.z < 1 && Math.abs(this.projected.x) < 1.1 && Math.abs(this.projected.y) < 1.1) {
          candidates.push({ item, x: (this.projected.x + 1) / 2 * width, y: (1 - this.projected.y) / 2 * height,
            rank: pinned ? -1 : lodLevel[kind] * 10 + this.projected.z })
          continue
        }
      }
      if (!item.label.hidden) item.label.hidden = true
    }
    candidates.sort((a, b) => a.rank - b.rank)
    const placed: { l: number; t: number; r: number; b: number }[] = []
    for (const { item, x, y, rank } of candidates) {
      if (item.label.hidden) item.label.hidden = false
      let size = this.labelSizes.get(item.node.id)
      if (!size?.w) { size = { w: item.label.offsetWidth, h: item.label.offsetHeight }; this.labelSizes.set(item.node.id, size) }
      const kind = item.node.kind
      const box = kind === 'network' ? { l: x - size.w / 2, t: y, r: x + size.w / 2, b: y + size.h }
        : kind === 'app' || kind === 'client' || kind === 'site' ? { l: x + 10, t: y - size.h / 2, r: x + 10 + size.w, b: y + size.h / 2 }
        : { l: x - size.w / 2, t: y - size.h - 14, r: x + size.w / 2, b: y - 14 }
      if (rank >= 0 && placed.some(other => box.l < other.r + 4 && box.r > other.l - 4 && box.t < other.b + 2 && box.b > other.t - 2)) { item.label.hidden = true; continue }
      placed.push(box)
      item.label.style.transform = `translate3d(${x.toFixed(1)}px, ${y.toFixed(1)}px, 0)`
    }
  }

  private pickAt(event: PointerEvent): string | null {
    const rect = this.renderer.domElement.getBoundingClientRect()
    this.pointer.set((event.clientX - rect.left) / rect.width * 2 - 1, -((event.clientY - rect.top) / rect.height) * 2 + 1)
    this.raycaster.setFromCamera(this.pointer, this.camera)
    const hit = this.raycaster.intersectObjects(this.picks, false).find(entry => entry.object.userData.id)
    if (!hit) return null
    const id = hit.object.userData.id as string
    if (this.constructs.get(id)?.node.kind === 'network') {
      const closer = this.raycaster.intersectObjects(this.picks, false).find(entry => this.constructs.get(entry.object.userData.id)?.node.kind !== 'network')
      if (closer) return closer.object.userData.id
    }
    return id
  }

  private onDown = (event: PointerEvent) => { this.down = { x: event.clientX, y: event.clientY }; this.fly = null }
  private onUp = (event: PointerEvent) => {
    if (!this.down || Math.hypot(event.clientX - this.down.x, event.clientY - this.down.y) > 6) { this.down = null; return }
    this.down = null
    this.events.select(this.pickAt(event))
  }
  private onMove = (event: PointerEvent) => {
    if (event.buttons) return
    const id = this.pickAt(event)
    if (id === this.hovered) return
    const previous = this.hovered ? this.constructs.get(this.hovered) : undefined
    previous?.label.classList.remove('is-hovered')
    this.hovered = id
    if (id) this.constructs.get(id)?.label.classList.add('is-hovered')
    this.renderer.domElement.style.cursor = id ? 'pointer' : ''
  }
  private onLeave = () => { if (this.hovered) this.constructs.get(this.hovered)?.label.classList.remove('is-hovered'); this.hovered = null }
  private onVisibility = () => {
    cancelAnimationFrame(this.frame)
    if (document.visibilityState === 'visible') { this.clock.update(); this.loop() }
  }
}

export function webglAvailable() {
  try {
    const canvas = document.createElement('canvas')
    return !!(canvas.getContext('webgl2') || canvas.getContext('webgl'))
  } catch { return false }
}
