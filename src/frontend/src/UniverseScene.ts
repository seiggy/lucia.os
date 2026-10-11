import * as THREE from 'three'
import { OrbitControls } from 'three/addons/controls/OrbitControls.js'
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js'
import { RenderPass } from 'three/addons/postprocessing/RenderPass.js'
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js'
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js'
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js'
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js'
import type { Health, LabMapLive, Layout, MapNode } from './labMapModel'
import { carServices, effectiveHealth, mixTotals, routeOf, serviceColors, services, streaks } from './labMapModel'
import type { Layers, SceneEvents } from './LabMapScene'
import { palette, toneOf } from './LabMapScene'
import type { Body, BodyKind, Cosmos } from './universeModel'
import { armAngle, armTurn, galaxyArms, hash, layoutCosmos } from './universeModel'
import { buildStation } from './universeStation'
import { accretionFragment, cloudFragment, cloudVertex, coronaFragment, dustFragment, dustVertex, funnelFragment, galaxyFragment, gateFragment, glowVertex, linkFragment, linkVertex, nebulaFragment, planetFragment, planetVertex, portalFragment, ringFragment, ringVertex, speckFragment, starFragment, starVertex, sunFragment, sunVertex } from './universeShaders'

export interface MapScene {
  setLayout(layout: Layout): void
  setLive(live: LabMapLive | null): void
  setLayers(layers: Layers): void
  setWallboard(on: boolean): void
  select(id: string | null, route: string[]): void
  overview(animate?: boolean): void
  flyTo(id: string): void
  dispose(): void
}

const TAU = Math.PI * 2
const grey = new THREE.Color(0x6b7482)
const serviceTint = Object.fromEntries(services.map(item => [item, new THREE.Color(serviceColors[item])])) as Record<(typeof services)[number], THREE.Color>
const ownsTint = new Set(['spark', 'node', 'app', 'client', 'site', 'storage'])
const white = new THREE.Color(0xffffff)
const maxShips = 450, models = ['spaceship', 'spaceship-jqfed124pq', 'spaceship-u105myhlhu', 'spaceship-vsxuafhzba'] as const
const parts = ['base-large', 'geodesic-dome', 'building-l', 'house-cylinder'] as const
const reachFor: Record<BodyKind, number> = { portal: 70, exit: 30, relay: 20, planet: 30, moon: 11, satellite: 4, city: 3.5, station: 8, pod: 4, depot: 8, sun: 40, world: 5 }
// How far from a body its label shows without it being picked; everything bigger than a moon is always labelled.
const labelReach: Record<BodyKind, number> = { portal: 1e9, exit: 1e9, relay: 1e9, planet: 1e9, sun: 1e9, moon: 85, station: 60, depot: 60, pod: 18, satellite: 16, city: 16, world: 24 }
const labelRank: Record<BodyKind, number> = { portal: 0, exit: 0, relay: 0, sun: 1, planet: 1, moon: 2, station: 3, depot: 3, world: 4, pod: 4, satellite: 5, city: 5 }
// System units per galaxy unit. A home system about 760 units across is 0.076 galaxy units, against neighbours thousands of units away.
const K = 10000
const homeKey = '~home'
const ease = (p: number) => p < 0.5 ? 4 * p * p * p : 1 - (-2 * p + 2) ** 3 / 2

interface Lane { id: string; stops: string[]; up: number; down: number; speed: number; flow: boolean; route: string[]; air?: boolean; tints?: (THREE.Color | undefined)[]; model: number }
interface Plate { body: Body; label: HTMLElement }

// Flattens a glTF into one geometry coloured per vertex, normalised to unit length, so one instanced mesh can draw it tinted.
function bake(root: THREE.Object3D, keep: number): THREE.BufferGeometry {
  root.updateWorldMatrix(true, true)
  const merged: THREE.BufferGeometry[] = []
  root.traverse(object => {
    const mesh = object as THREE.Mesh
    if (!mesh.isMesh) return
    let geometry = mesh.geometry.clone()
    geometry.applyMatrix4(mesh.matrixWorld)
    for (const name of Object.keys(geometry.attributes)) if (name !== 'position' && name !== 'normal') geometry.deleteAttribute(name)
    if (geometry.index) geometry = geometry.toNonIndexed()
    const count = geometry.getAttribute('position').count, colors = new Float32Array(count * 3)
    const paint = (material: THREE.Material | undefined, from: number, to: number) => {
      const base = (material as THREE.MeshStandardMaterial | undefined)?.color ?? white, lum = 0.299 * base.r + 0.587 * base.g + 0.114 * base.b
      const c = new THREE.Color(THREE.MathUtils.lerp(lum, base.r, keep), THREE.MathUtils.lerp(lum, base.g, keep), THREE.MathUtils.lerp(lum, base.b, keep))
      for (let index = from; index < to; index++) { colors[index * 3] = c.r; colors[index * 3 + 1] = c.g; colors[index * 3 + 2] = c.b }
    }
    if (Array.isArray(mesh.material)) for (const group of geometry.groups) paint(mesh.material[group.materialIndex ?? 0], group.start, Math.min(count, group.start + group.count))
    else paint(mesh.material, 0, count)
    geometry.clearGroups()
    geometry.setAttribute('color', new THREE.BufferAttribute(colors, 3))
    merged.push(geometry)
  })
  const geometry = mergeGeometries(merged) ?? new THREE.BufferGeometry()
  geometry.computeBoundingBox()
  const box = geometry.boundingBox!, size = box.getSize(new THREE.Vector3()), center = box.getCenter(new THREE.Vector3())
  geometry.translate(-center.x, -center.y, -center.z)
  geometry.scale(1 / Math.max(size.x, size.y, size.z, 1e-6), 1 / Math.max(size.x, size.y, size.z, 1e-6), 1 / Math.max(size.x, size.y, size.z, 1e-6))
  return geometry
}

// Hulls keep their detail from vertex colour, take the instance's colour as a tint, and glow with it. Structures also get plating, panel seams
// and lit windows, generated from their own coordinates so they need no textures.
function hull(glow: number, plated = false) {
  const material = new THREE.MeshStandardMaterial({ vertexColors: true, roughness: plated ? 0.92 : 0.55, metalness: plated ? 0.1 : 0.25 })
  // Pale plating under a bright light would clip into bloom; a dimmer, matte hull keeps its seams and windows readable.
  if (plated) material.color.setScalar(0.55)
  material.onBeforeCompile = shader => {
    if (plated) {
      shader.vertexShader = `varying vec3 vLocal;\n${shader.vertexShader.replace('#include <begin_vertex>', '#include <begin_vertex>\n vLocal = position;')}`
      shader.fragmentShader = `varying vec3 vLocal;\n${shader.fragmentShader.replace('#include <color_fragment>', `#include <color_fragment>
        vec3 cell = floor(vLocal * 14.0); vec3 edge = abs(fract(vLocal * 14.0) - 0.5);
        float seam = step(0.455, max(max(edge.x, edge.y), edge.z));
        float grain = fract(sin(dot(cell, vec3(12.9898, 78.233, 37.719))) * 43758.5453);
        diffuseColor.rgb *= (0.78 + 0.34 * grain) * (1.0 - 0.5 * seam);
        vec3 wc = floor(vLocal * vec3(22.0, 13.0, 22.0)) + vec3(3.0);
        float lit = step(0.84, fract(sin(dot(wc, vec3(41.3, 17.7, 29.1))) * 9341.7)) * (1.0 - seam) * step(-0.38, vLocal.y);`)}`
    }
    shader.fragmentShader = shader.fragmentShader.replace('#include <emissivemap_fragment>', `#include <emissivemap_fragment>\n totalEmissiveRadiance += vColor.rgb * ${glow.toFixed(2)};${plated ? '\n totalEmissiveRadiance += vColor.rgb * lit * 0.55;' : ''}`)
  }
  return material
}

// A satellite: a foil-wrapped bus, two long solar wings on booms, a dish on a stalk and an antenna. One unit wide, wing tip to wing tip.
function satellite(): THREE.BufferGeometry {
  const pieces: THREE.BufferGeometry[] = []
  const add = (geometry: THREE.BufferGeometry, color: number, at: [number, number, number], turn: [number, number, number] = [0, 0, 0]) => {
    geometry.rotateX(turn[0]); geometry.rotateY(turn[1]); geometry.rotateZ(turn[2]); geometry.translate(...at)
    const flat = geometry.index ? geometry.toNonIndexed() : geometry, count = flat.getAttribute('position').count, tint = new THREE.Color(color), colors = new Float32Array(count * 3)
    for (let index = 0; index < count; index++) colors.set([tint.r, tint.g, tint.b], index * 3)
    flat.deleteAttribute('uv')
    flat.setAttribute('color', new THREE.BufferAttribute(colors, 3))
    pieces.push(flat)
  }
  add(new THREE.BoxGeometry(0.17, 0.17, 0.24), 0xd9b04a, [0, 0, 0])
  add(new THREE.BoxGeometry(0.19, 0.03, 0.26), 0x8a8f99, [0, 0.1, 0])
  for (const side of [-1, 1]) {
    add(new THREE.CylinderGeometry(0.007, 0.007, 0.12, 6), 0xb8bec9, [side * 0.14, 0, 0], [0, 0, Math.PI / 2])
    add(new THREE.BoxGeometry(0.36, 0.008, 0.2), 0x1a2f78, [side * 0.32, 0, 0])
    add(new THREE.BoxGeometry(0.36, 0.004, 0.012), 0xc9d2e6, [side * 0.32, 0.006, 0])
  }
  add(new THREE.CylinderGeometry(0.01, 0.01, 0.1, 6), 0xb8bec9, [0, 0.16, 0])
  add(new THREE.SphereGeometry(0.1, 20, 8, 0, Math.PI * 2, 0, 0.95), 0xe6e9ef, [0, 0.27, 0.03], [-0.5, 0, 0])
  add(new THREE.CylinderGeometry(0.004, 0.004, 0.2, 5), 0xb8bec9, [0, 0.02, -0.2], [Math.PI / 2, 0, 0])
  const merged = mergeGeometries(pieces) ?? new THREE.BufferGeometry()
  merged.computeBoundingBox()
  return merged
}

// An instanced mesh refilled every frame with whatever is currently drawn.
class Fleet {
  mesh: THREE.InstancedMesh
  count = 0
  private capacity: number
  constructor(geometry: THREE.BufferGeometry, material: THREE.Material, capacity: number, order = 10) {
    this.capacity = capacity
    this.mesh = new THREE.InstancedMesh(geometry, material, capacity)
    this.mesh.count = 0
    this.mesh.frustumCulled = false
    this.mesh.renderOrder = order
    this.mesh.setColorAt(0, white)
  }
  push(matrix: THREE.Matrix4, color: THREE.Color) {
    if (this.count >= this.capacity) return
    this.mesh.setMatrixAt(this.count, matrix)
    this.mesh.setColorAt(this.count++, color)
  }
  commit() {
    this.mesh.count = this.count
    this.count = 0
    this.mesh.instanceMatrix.needsUpdate = true
    if (this.mesh.instanceColor) this.mesh.instanceColor.needsUpdate = true
  }
}

let kitsPromise: Promise<Record<string, THREE.BufferGeometry>> | null = null
function loadKits() {
  kitsPromise ??= (async () => {
    const loader = new GLTFLoader(), result: Record<string, THREE.BufferGeometry> = {}
    await Promise.all([...models, ...parts].map(async name => {
      try {
        const gltf = await loader.loadAsync(`${import.meta.env.BASE_URL}universe/meshes/${name}.glb`)
        result[name] = bake(gltf.scene, models.some(item => item === name) ? 0.5 : 0.25)
      } catch { /* the ship falls back to a plain hull */ }
    }))
    return result
  })()
  return kitsPromise
}

// The jumpgate is a licensed model that isn't in the repo (see tools/assets/build-jumpgate.mjs); without it gates fall back to bare wormholes.
// Its opening has radius 1 and it faces +z. Each colour scheme is the same material with a different albedo.
// The inner ring's lip is painted as a bright white glow strip; around the field it's toned down to a thin, dim blue line.
// The orange accent panels are masked out (panels.webp) so each gate can repaint them in its own colour, keeping the paint's shading.
type Gate = { model: THREE.Object3D; schemes: { outer: THREE.MeshStandardMaterial; inner: THREE.MeshStandardMaterial }[]; panels: THREE.Texture | null }
function paintPanels(material: THREE.MeshStandardMaterial, panels: THREE.Texture, color: THREE.Color) {
  const painted = material.clone()
  painted.customProgramCacheKey = () => 'gate-panels'
  painted.onBeforeCompile = shader => {
    shader.uniforms.uPanels = { value: panels }; shader.uniforms.uPanel = { value: color }
    shader.fragmentShader = 'uniform sampler2D uPanels;\nuniform vec3 uPanel;\n' + shader.fragmentShader.replace('#include <map_fragment>', `#include <map_fragment>
    diffuseColor.rgb = mix(diffuseColor.rgb, uPanel * dot(diffuseColor.rgb, vec3(0.2126, 0.7152, 0.0722)) * 2.2, texture2D(uPanels, vMapUv).r);`)
  }
  return painted
}
let gatePromise: Promise<Gate | null> | null = null
function loadGate() {
  gatePromise ??= (async () => {
    try {
      const base = `${import.meta.env.BASE_URL}universe/meshes/jumpgate/`, model = (await new GLTFLoader().loadAsync(`${base}gate.glb`)).scene
      let material: THREE.MeshStandardMaterial | null = null
      model.traverse(object => { if ((object as THREE.Mesh).isMesh) material = (object as THREE.Mesh).material as THREE.MeshStandardMaterial })
      const loader = new THREE.TextureLoader()
      const schemes = await Promise.all(['blue', 'green', 'grey', 'red', 'white'].map(async scheme => {
        const map = await loader.loadAsync(`${base}${scheme}.webp`)
        map.colorSpace = THREE.SRGBColorSpace; map.flipY = false; map.anisotropy = 4
        const outer = material!.clone(); outer.map = map; outer.emissiveIntensity = 2.2
        const inner = outer.clone(); inner.emissive = new THREE.Color(0x4f8fe0); inner.emissiveIntensity = 0.45
        return { outer, inner }
      }))
      const panels = await loader.loadAsync(`${base}panels.webp`).catch(() => null)
      if (panels) panels.flipY = false
      return { model, schemes, panels }
    } catch { return null }
  })()
  return gatePromise
}

export class UniverseScene implements MapScene {
  private renderer: THREE.WebGLRenderer
  private scene = new THREE.Scene()
  private camera: THREE.PerspectiveCamera
  private controls: OrbitControls
  private composer: EffectComposer | null = null
  private world = new THREE.Group()
  private cosmos: Cosmos | null = null
  private layout: Layout | null = null
  private signature = ''
  private live: LabMapLive | null = null
  private layers: Layers = { clients: true, apps: true, storage: true, traffic: true, flows: true }
  private selected: string | null = null
  private route: string[] = []
  private hovered: string | null = null
  private follow: { id: string; last: THREE.Vector3 } | null = null
  // A flight runs in galaxy units: the target slides across, the view distance (in system units) eases on a log scale, rising to `peak` on the way.
  private fly: { fromTarget: THREE.Vector3; toTarget: THREE.Vector3; fromDir: THREE.Vector3; toDir: THREE.Vector3; from: number; to: number; peak: number; start: number; duration: number; body: string | null } | null = null
  private worlds: THREE.InstancedMesh | null = null
  private slots = new Map<string, number>()
  private atmos: THREE.InstancedBufferAttribute | null = null
  private kits: Record<string, THREE.BufferGeometry> = {}
  private gate: Gate | null = null
  private fleets = new Map<string, Fleet>()
  private shipFleets: Fleet[] = []
  private trails: Fleet | null = null
  private links: THREE.LineSegments | null = null
  private routeLine: THREE.Line | null = null
  private marker: THREE.Mesh
  private rings: { body: string; mesh: THREE.Mesh }[] = []
  private stations: { body: string; frame: THREE.Mesh; modules: THREE.Mesh }[] = []
  private rocks: { mesh: THREE.InstancedMesh; orbit: Float32Array; tumble: Float32Array; size: Float32Array; belt: number; center: THREE.Vector3; owner: THREE.Group }[] = []
  private sunLight: THREE.PointLight
  private portals: { body: string; group: THREE.Group; material: THREE.ShaderMaterial }[] = []
  private suns: { body: string; mesh: THREE.Mesh; corona: THREE.Mesh }[] = []
  private timed: THREE.ShaderMaterial[] = []
  // Two scales at once. The galaxy lives in its own scene in galaxy units, where a solar system is a speck K times smaller than it is drawn
  // here; the system the camera is nearest (the anchor) is drawn full size in this.world around its own centre, and the camera moves
  // between the two by mapping through that centre. Only the anchor's contents are drawn; every other system is a single star-point.
  private sky = new THREE.Scene()
  private skyCamera = new THREE.PerspectiveCamera(45, 1, 1, 3e5)
  private skybox: THREE.Points | null = null
  private glows: THREE.ShaderMaterial[] = []
  private screen = { value: 1 }
  private dots: THREE.Points | null = null
  private systems = new Map<string, { id: string; center: THREE.Vector3; size: number; color: THREE.Color; head: string; sun: number; label: string; group: THREE.Group; index: number }>()
  private systemOf = new Map<string, string>()
  private anchor = homeKey
  private expanded = true
  private navState = ''
  private nav: HTMLElement
  private navSelect: HTMLSelectElement
  private galaxyButton: HTMLButtonElement
  private worldAt = new Map<string, THREE.Vector3>()
  private plates = new Map<string, Plate>()
  private sizes = new Map<string, { w: number; h: number }>()
  private lanes: Lane[] = []
  private frame = 0
  private clock = new THREE.Timer()
  private userMoved = false
  private down: { x: number; y: number } | null = null
  private resizeObserver: ResizeObserver
  private dummy = new THREE.Object3D()
  private mat = new THREE.Matrix4()
  private tmp = new THREE.Vector3()
  private ahead = new THREE.Vector3()
  private forward = new THREE.Vector3()
  private side = new THREE.Vector3()
  private lift = new THREE.Vector3()
  private shift = new THREE.Vector3()
  private up = new THREE.Vector3(0, 1, 0)
  private color = new THREE.Color()
  private projected = new THREE.Vector3()
  private quiet = window.matchMedia('(prefers-reduced-motion: reduce)').matches
  private disposed = false
  private host: HTMLElement
  private labels: HTMLElement
  private events: SceneEvents

  constructor(host: HTMLElement, labels: HTMLElement, events: SceneEvents) {
    this.host = host
    this.labels = labels
    this.events = events
    this.renderer = new THREE.WebGLRenderer({ antialias: true, powerPreference: 'high-performance' })
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2))
    this.renderer.setClearColor(0x02030a)
    this.renderer.domElement.setAttribute('aria-hidden', 'true')
    host.prepend(this.renderer.domElement)
    this.camera = new THREE.PerspectiveCamera(45, 1, 0.05, 600000)
    this.controls = new OrbitControls(this.camera, this.renderer.domElement)
    this.controls.enableDamping = true
    this.controls.dampingFactor = 0.08
    this.controls.minDistance = 1.2
    this.controls.maxDistance = 1e9
    this.controls.zoomSpeed = 2.5
    this.controls.zoomToCursor = true
    this.controls.autoRotateSpeed = 0.3
    this.controls.addEventListener('start', () => { this.userMoved = true })
    this.scene.add(this.world, new THREE.AmbientLight(0x93a2c8, 1.1))
    const sun = this.sunLight = new THREE.PointLight(0xfff0d8, 6, 0, 0)
    this.scene.add(sun)
    this.marker = new THREE.Mesh(new THREE.RingGeometry(0.92, 1, 56), new THREE.MeshBasicMaterial({ color: palette.magenta, transparent: true, opacity: 0.95, side: THREE.DoubleSide, depthWrite: false, depthTest: false }))
    this.marker.visible = false
    this.marker.renderOrder = 40
    this.scene.add(this.marker)
    if (host.clientWidth >= 700) {
      this.composer = new EffectComposer(this.renderer)
      this.composer.addPass(new RenderPass(this.sky, this.skyCamera))
      const near = new RenderPass(this.scene, this.camera)
      near.clear = false
      near.clearDepth = true
      this.composer.addPass(near)
      this.composer.addPass(new UnrealBloomPass(new THREE.Vector2(256, 256), 0.45, 0.4, 0.85))
      this.composer.addPass(new OutputPass())
    }
    const canvas = this.renderer.domElement
    // Jumps between the galaxy, home and each destination system, so nobody has to scroll across light years.
    this.nav = document.createElement('nav')
    this.nav.className = 'labmap-warp'
    this.nav.setAttribute('aria-label', 'Universe navigation')
    const button = (text: string, go: () => void) => { const item = document.createElement('button'); item.type = 'button'; item.textContent = text; item.addEventListener('click', go); return item }
    this.galaxyButton = button('Galaxy', () => { this.overview() })
    const label = document.createElement('label'), caption = document.createElement('span')
    caption.textContent = 'System'
    this.navSelect = document.createElement('select')
    this.navSelect.addEventListener('change', () => { const id = this.navSelect.value; if (id === homeKey) this.home(); else if (id) this.flyTo(id) })
    label.append(caption, this.navSelect)
    this.nav.append(this.galaxyButton, button('Home', () => { this.home() }), label)
    host.append(this.nav)
    canvas.addEventListener('pointerdown', this.onDown)
    canvas.addEventListener('pointerup', this.onUp)
    canvas.addEventListener('pointermove', this.onMove)
    canvas.addEventListener('pointerleave', this.onLeave)
    document.addEventListener('visibilitychange', this.onVisibility)
    this.resizeObserver = new ResizeObserver(() => this.resize())
    this.resizeObserver.observe(host)
    this.resize()
    void loadGate().then(gate => { if (this.disposed || !gate) return; this.gate = gate; this.dressGates() })
    void loadKits().then(kits => { if (this.disposed) return; this.kits = kits; if (this.layout) { this.buildFleets(); this.buildLanes() } })
    this.loop()
  }

  setLayout(layout: Layout) {
    const signature = [...layout.nodes.values()].map(node => `${node.id}:${node.kind}:${node.parentId}:${node.block ?? ''}:${node.tint ?? ''}`).join('|')
    const first = !this.layout
    this.layout = layout
    if (signature !== this.signature) {
      this.signature = signature
      this.build()
    }
    if (first) this.home(false)
    this.paint()
  }

  setLive(live: LabMapLive | null) {
    this.live = live
    this.buildLanes()
    this.paint()
  }

  setLayers(layers: Layers) { this.layers = layers }
  setWallboard(on: boolean) { this.controls.autoRotate = on; this.nav.hidden = on }

  select(id: string | null, route: string[]) {
    this.selected = id
    this.route = id ? route : []
    this.follow = null
    this.paint()
    if (id) this.flyTo(id)
  }

  // The whole galaxy, seen from above at an angle.
  overview(animate = true) {
    const cosmos = this.cosmos
    if (!cosmos) return
    const tilt = THREE.MathUtils.degToRad(48)
    this.follow = null
    if (animate) this.userMoved = false
    this.flyGalactic(new THREE.Vector3(0, 0, 0), new THREE.Vector3(0, Math.sin(tilt), Math.cos(tilt)), this.frame3(cosmos.galaxy.radius) * K, null, animate)
  }

  // Our own solar system, filling the view.
  home(animate = true) {
    const home = this.systems.get(homeKey)
    if (!home) return
    this.follow = null
    this.flyGalactic(home.center.clone(), new THREE.Vector3(-0.1, 0.8, 0.59).normalize(), this.frame3(home.size), null, animate)
  }

  flyTo(id: string) {
    const body = this.cosmos?.byId.get(id), at = this.worldAt.get(id)
    if (!body || !at) return
    if (body.kind === 'exit') return
    const system = this.systems.get(this.systemOf.get(id) ?? homeKey)!
    const direction = this.camera.position.clone().sub(this.controls.target).normalize()
    if (direction.y < 0.3) direction.setY(0.5).normalize()
    // A star stands for its whole system, so going to one frames everything that orbits it.
    const distance = body.kind === 'sun' ? this.frame3(system.size) : Math.max(reachFor[body.kind], body.radius * 5)
    this.follow = { id, last: at.clone() }
    this.flyGalactic(this.toGalaxy(at, system.center), direction, distance, id, true)
  }

  // How far back the camera has to stand for a disc of this radius to fill the view.
  private frame3(radius: number) {
    const vertical = THREE.MathUtils.degToRad(this.camera.fov / 2), horizontal = Math.atan(Math.tan(vertical) * this.camera.aspect)
    return radius / Math.tan(Math.min(vertical, horizontal)) * 1.08
  }

  private toGalaxy(local: THREE.Vector3, center = this.systems.get(this.anchor)?.center, out = new THREE.Vector3()) {
    return center ? out.copy(local).sub(center).divideScalar(K).add(center) : out.copy(local)
  }

  private toLocal(galactic: THREE.Vector3, center = this.systems.get(this.anchor)?.center, out = new THREE.Vector3()) {
    return center ? out.copy(galactic).sub(center).multiplyScalar(K).add(center) : out.copy(galactic)
  }

  private flyGalactic(target: THREE.Vector3, direction: THREE.Vector3, distance: number, body: string | null, animate: boolean) {
    if (!animate) {
      const camera = target.clone().addScaledVector(direction, distance / K)
      this.setAnchor(this.nearest(camera), false)
      this.camera.position.copy(this.toLocal(camera))
      this.controls.target.copy(this.toLocal(target))
      this.controls.update()
      return
    }
    const from = this.camera.position.distanceTo(this.controls.target), fromTarget = this.toGalaxy(this.controls.target)
    // Crossing between systems climbs high enough to see both ends of the trip before coming down.
    const across = fromTarget.distanceTo(target) * K, peak = across > Math.max(from, distance) * 2 ? across * 1.3 : 0
    const span = Math.log10(Math.max(from, distance, peak) / Math.max(1e-3, Math.min(from, distance)))
    this.fly = { fromTarget, toTarget: target, fromDir: this.camera.position.clone().sub(this.controls.target).normalize(), toDir: direction.clone().normalize(), from, to: distance, peak,
      start: performance.now(), duration: this.quiet ? 1 : 800 + 1900 * Math.min(1, span / 5), body }
  }

  private nearest(galactic: THREE.Vector3) {
    let best = this.anchor, distance = Infinity
    for (const system of this.systems.values()) { const d = system.center.distanceToSquared(galactic); if (d < distance) { distance = d; best = system.id } }
    return best
  }

  // Moves the anchor to another system. Rebasing keeps the camera where it is in the galaxy while the coordinates under it change.
  private setAnchor(id: string, rebase: boolean) {
    if (id === this.anchor) return
    const from = this.systems.get(this.anchor), to = this.systems.get(id)
    if (rebase && from && to) {
      const shift = from.center.clone().sub(to.center).multiplyScalar(K - 1)
      this.camera.position.add(shift)
      this.controls.target.add(shift)
    }
    this.anchor = id
    for (const system of this.systems.values()) system.group.visible = system.id === id
    if (this.follow && this.systemOf.get(this.follow.id) !== id) this.follow = null
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
    this.clear(this.sky)
    this.nav.remove()
    this.composer?.dispose()
    this.renderer.dispose()
    canvas.remove()
    this.labels.replaceChildren()
  }

  // Once the model is in, every wormhole becomes a jumpgate: the frame and its two rings around the vortex, which now lies flat in the opening.
  private dressGates() {
    if (!this.gate) return
    for (const portal of this.portals) {
      if (portal.group.userData.rings) continue
      for (const part of [...portal.group.userData.wormhole, ...portal.group.userData.billboards] as THREE.Object3D[]) part.visible = false
      const gate = this.gate.model.clone(), scheme = this.gate.schemes[Math.floor(hash(portal.body + 'gate') * this.gate.schemes.length)]
      // Exits take their system's star colour; the home gate takes the transmission beam's.
      const accent = portal.body.startsWith('exit:') ? this.systems.get(portal.body.slice(5))?.color : new THREE.Color(palette.air)
      const paint = (material: THREE.MeshStandardMaterial) => this.gate!.panels && accent ? paintPanels(material, this.gate!.panels, accent) : material
      const outer = paint(scheme.outer), inner = paint(scheme.inner)
      gate.traverse(object => { if ((object as THREE.Mesh).isMesh) (object as THREE.Mesh).material = object.name === 'InnerRing' ? inner : outer })
      portal.group.add(gate)
      portal.group.userData.rings = [gate.getObjectByName('OuterRing'), gate.getObjectByName('InnerRing')]
      // The psychedelic vortex suits a bare wormhole; inside a gate the field is a calm, translucent membrane.
      portal.material.blending = THREE.AdditiveBlending
      portal.material.fragmentShader = gateFragment
      portal.material.needsUpdate = true
    }
  }

  private clear(root: THREE.Object3D) {
    root.traverse(object => {
      const item = object as THREE.Mesh
      if (item.geometry && !Object.values(this.kits).includes(item.geometry)) item.geometry.dispose()
      const material = item.material as THREE.Material | THREE.Material[] | undefined
      for (const entry of Array.isArray(material) ? material : material ? [material] : []) entry.dispose()
    })
  }

  private hidden(node: MapNode) {
    return ((node.kind === 'clients' || node.kind === 'client') && !this.layers.clients) || (node.kind === 'app' && !this.layers.apps) || (node.kind === 'storage' && !this.layers.storage)
  }

  private build() {
    const layout = this.layout!
    this.clear(this.world)
    this.world.clear()
    this.clear(this.sky)
    this.sky.clear()
    this.fleets.clear()
    this.rings = []; this.rocks = []; this.stations = []; this.portals = []; this.suns = []; this.timed = []; this.shipFleets = []; this.trails = null; this.links = null; this.routeLine = null; this.glows = []
    this.labels.replaceChildren()
    this.plates.clear(); this.sizes.clear(); this.worldAt.clear(); this.slots.clear(); this.systems.clear(); this.systemOf.clear()
    const cosmos = this.cosmos = layoutCosmos(layout)
    this.controls.maxDistance = cosmos.galaxy.radius * K * 4

    // Every solar system has a group of its own in the near scene, shown only while it is the anchor.
    const addSystem = (id: string, center: THREE.Vector3, size: number, color: number, head: string, sun: number, label: string) => {
      const group = new THREE.Group()
      group.visible = id === this.anchor
      this.world.add(group)
      this.systems.set(id, { id, center, size, color: new THREE.Color(color), head, sun, label, group, index: this.systems.size })
    }
    addSystem(homeKey, new THREE.Vector3(...cosmos.lan.star), cosmos.lan.radius + 12, 0xffc27a, homeKey, 7.5, 'Home lab')
    for (const system of cosmos.systems) {
      const body = cosmos.byId.get(system.id)
      addSystem(system.id, new THREE.Vector3(system.x, 0, system.z), system.radius + 45, system.color, system.id, body?.radius ?? 7, body?.node.label ?? system.id)
    }
    if (!this.systems.has(this.anchor)) this.anchor = homeKey
    this.systems.get(this.anchor)!.group.visible = true
    for (const body of cosmos.bodies) this.systemOf.set(body.id, body.kind === 'world' ? body.node.block ?? homeKey : body.kind === 'sun' ? body.id : body.kind === 'exit' ? body.id.slice(5) : homeKey)
    const home = this.systems.get(homeKey)!.group, groupAt = (center: readonly number[]) => [...this.systems.values()].find(item => item.id !== homeKey && Math.abs(item.center.x - center[0]) < 1e-3 && Math.abs(item.center.z - center[2]) < 1e-3)?.group ?? home

    // The galaxy, in galaxy units: the night sky all round, a soft glow under the disc, a spiral of stars along three arms, gas clouds,
    // and a black hole with its accretion disc at the centre.
    const sky = new Float32Array(2600 * 3), tint = new Float32Array(2600 * 3)
    for (let index = 0; index < 2600; index++) {
      const u = Math.random() * 2 - 1, a = Math.random() * TAU, r = Math.sqrt(1 - u * u), distance = 1e5
      sky.set([Math.cos(a) * r * distance, u * distance, Math.sin(a) * r * distance], index * 3)
      const c = new THREE.Color().setHSL(0.55 + Math.random() * 0.15, 0.4, 0.55 + Math.random() * 0.4)
      tint.set([c.r, c.g, c.b], index * 3)
    }
    const stars = new THREE.BufferGeometry()
    stars.setAttribute('position', new THREE.BufferAttribute(sky, 3))
    stars.setAttribute('color', new THREE.BufferAttribute(tint, 3))
    this.skybox = new THREE.Points(stars, new THREE.PointsMaterial({ size: 1.6, sizeAttenuation: false, vertexColors: true, transparent: true, opacity: 0.8, depthWrite: false, fog: false }))
    this.skybox.frustumCulled = false
    this.sky.add(this.skybox)
    const glowMaterial = new THREE.ShaderMaterial({ vertexShader: glowVertex, fragmentShader: galaxyFragment, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide,
      uniforms: { uColor: { value: new THREE.Color(0xffc98a) }, uColor2: { value: new THREE.Color(0x8fb4ff) }, uArms: { value: galaxyArms }, uHole: { value: 0.04 }, uFade: { value: 1 },
        uWind: { value: galaxyArms * TAU * cosmos.galaxy.radius * 1.5 / (cosmos.galaxy.unit * armTurn) }, uPhase: { value: 0 } } })
    this.glows.push(glowMaterial)
    const glowPlane = new THREE.Mesh(new THREE.PlaneGeometry(cosmos.galaxy.radius * 3, cosmos.galaxy.radius * 3), glowMaterial)
    glowPlane.rotation.x = -Math.PI / 2
    glowPlane.position.y = -3
    glowPlane.renderOrder = -5
    this.sky.add(glowPlane)
    // Stars scattered about evenly wound arms: most sit loosely around an arm, scattered further along it near the centre where the arms
    // merge. Stars tight on an arm are young and blue, stragglers older and orange. Under them, a warm bulge, pink star-forming knots and a
    // faint halo; over them, big soft clouds of dust and gas along the same arms.
    let seed = 1013904223
    const rand = () => { seed = (seed + 0x6d2b79f5) | 0; let t = Math.imul(seed ^ (seed >>> 15), 1 | seed); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296 }
    const gauss = () => Math.sqrt(-2 * Math.log(rand() + 1e-9)) * Math.cos(TAU * rand())
    const reach = cosmos.galaxy.radius, core = cosmos.hole.radius, unit = cosmos.galaxy.unit, count = 250000
    const armWidth = unit * armTurn / galaxyArms * 0.14, scatter = reach * 0.08, thick = reach * 0.02
    const onArm = (power: number) => {
      let base = 0
      do base = core * 2 + Math.pow(rand(), power) * (reach * 1.25 - core * 2); while (base > reach * 0.7 && rand() > Math.pow(1 - (base - reach * 0.7) / (reach * 0.55), 2))
      const across = gauss(), along = (rand() * 2 - 1) * scatter / (base + reach * 0.02)
      const radius = Math.max(core * 1.5, base + across * armWidth), theta = armAngle(Math.floor(rand() * galaxyArms), base, unit) + along
      return { x: Math.cos(theta) * radius, z: Math.sin(theta) * radius, y: (rand() * 2 - 1) * thick * (1 - 0.7 * Math.min(1, base / reach)), sparse: Math.min(1, Math.abs(across) * 0.45 + Math.abs(along) * 0.4) }
    }
    const spiral = new Float32Array(count * 3), shade = new Float32Array(count * 3), scale = new Float32Array(count), pick = new THREE.Color()
    const dense = new THREE.Color(0.4, 0.6, 1), loose = new THREE.Color(1, 0.6, 0.3)
    const knots = Array.from({ length: 260 }, () => { const at = onArm(0.6); return { ...at, size: unit * (0.3 + rand() * rand() * 2), hot: rand() < 0.45 } })
    for (let index = 0; index < count; index++) {
      const roll = rand()
      let x: number, y: number, z: number, bright = 0.25 + 0.75 * Math.pow(rand(), 4)
      if (roll < 0.07) {
        const radius = core * 1.4 + Math.abs(gauss()) * reach * 0.05, theta = rand() * TAU
        x = Math.cos(theta) * radius; z = Math.sin(theta) * radius * 0.8; y = gauss() * radius * 0.4
        pick.setHSL(0.08 + rand() * 0.04, 0.6, 0.6 + rand() * 0.25); bright = 0.4 + 0.6 * rand()
      } else if (roll < 0.16) {
        const knot = knots[Math.floor(rand() * knots.length)]
        x = knot.x + gauss() * knot.size; z = knot.z + gauss() * knot.size; y = knot.y + gauss() * unit * 0.2
        if (knot.hot && rand() < 0.5) pick.setHSL(0.94 + rand() * 0.04, 0.75, 0.66)
        else pick.setHSL(0.58 + rand() * 0.05, 0.75, 0.75 + rand() * 0.15)
        bright = 0.4 + 0.6 * rand()
      } else if (roll < 0.97) {
        const at = onArm(0.5)
        x = at.x; y = at.y; z = at.z
        pick.copy(dense).lerp(loose, at.sparse).offsetHSL((rand() - 0.5) * 0.04, 0, (rand() - 0.5) * 0.1)
      } else {
        const radius = reach * (0.15 + 1.3 * Math.pow(rand(), 0.7)), u = rand() * 2 - 1, theta = rand() * TAU, ring = Math.sqrt(1 - u * u)
        x = Math.cos(theta) * ring * radius; z = Math.sin(theta) * ring * radius; y = u * radius * 0.5
        pick.setHSL(0.08 + rand() * 0.05, 0.35, 0.6); bright *= 0.4
      }
      spiral.set([x!, y!, z!], index * 3)
      shade.set([pick.r * bright, pick.g * bright, pick.b * bright], index * 3)
      scale[index] = bright > 0.8 ? 1.8 : 1
    }
    const spiralStars = new THREE.BufferGeometry()
    spiralStars.setAttribute('position', new THREE.BufferAttribute(spiral, 3))
    spiralStars.setAttribute('color', new THREE.BufferAttribute(shade, 3))
    spiralStars.setAttribute('scale', new THREE.BufferAttribute(scale, 1))
    const starCloud = new THREE.Points(spiralStars, new THREE.ShaderMaterial({ vertexShader: starVertex, fragmentShader: starFragment, vertexColors: true, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, uniforms: { uRef: { value: unit * 6 }, uSize: { value: 1.6 * this.renderer.getPixelRatio() } } }))
    starCloud.frustumCulled = false
    this.sky.add(starCloud)
    const puffs = 3000, puffAt = new Float32Array(puffs * 3), puffTint = new Float32Array(puffs * 3), puffInfo = new Float32Array(puffs * 3)
    // Gas glows mostly purple, orange and green, now and then blue.
    const gases = [[0.62, 0.3, 0.85], [0.95, 0.5, 0.2], [0.35, 0.8, 0.45], [0.3, 0.5, 1]].map(([r, g, b]) => new THREE.Color(r, g, b)), gasOdds = [0.32, 0.62, 0.9, 1]
    for (let index = 0; index < puffs; index++) {
      const at = onArm(0.7)
      puffAt.set([at.x, at.y * 0.5, at.z], index * 3)
      const roll = rand(), gas = gasOdds.findIndex(odds => roll < odds)
      pick.copy(gases[gas]).lerp(gases[(gas + 1 + Math.floor(rand() * 3)) % 4], rand() * 0.35).multiplyScalar(0.07 * (1 - 0.6 * at.sparse))
      puffTint.set([pick.r, pick.g, pick.b], index * 3)
      puffInfo.set([reach * 0.05 * (1 + rand() * 3), rand() * TAU, rand() * 100], index * 3)
    }
    const puffShape = new THREE.BufferGeometry()
    puffShape.setAttribute('position', new THREE.BufferAttribute(puffAt, 3))
    puffShape.setAttribute('color', new THREE.BufferAttribute(puffTint, 3))
    puffShape.setAttribute('aCloud', new THREE.BufferAttribute(puffInfo, 3))
    this.screen.value = this.renderer.domElement.height / 2
    const puffMaterial = new THREE.ShaderMaterial({ vertexShader: cloudVertex, fragmentShader: cloudFragment, vertexColors: true, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, uniforms: { uScreen: this.screen, uFade: { value: 1 } } })
    this.glows.push(puffMaterial)
    const gasCloud = new THREE.Points(puffShape, puffMaterial)
    gasCloud.frustumCulled = false
    this.sky.add(gasCloud)
    for (const gas of cosmos.hazes) {
      const material = new THREE.ShaderMaterial({ vertexShader: glowVertex, fragmentShader: nebulaFragment, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide,
        uniforms: { uColor: { value: new THREE.Color(gas.color) }, uFade: { value: 1 } } })
      this.glows.push(material)
      const cloudMesh = new THREE.Mesh(new THREE.PlaneGeometry(gas.radius * 4, gas.radius * 4), material)
      cloudMesh.rotation.x = -Math.PI / 2
      cloudMesh.position.set(gas.center[0], -10, gas.center[2])
      cloudMesh.renderOrder = -4
      this.sky.add(cloudMesh)
    }
    const hole = cosmos.hole.radius, accretion = new THREE.ShaderMaterial({ vertexShader: ringVertex, fragmentShader: accretionFragment, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide,
      uniforms: { uTime: { value: 0 }, uInner: { value: hole * 1.25 }, uOuter: { value: hole * 2.7 } } })
    this.timed.push(accretion)
    const disc = new THREE.Mesh(new THREE.RingGeometry(hole * 1.25, hole * 2.7, 128), accretion)
    disc.rotation.x = -Math.PI / 2 + 0.12
    disc.renderOrder = 3
    const horizon = new THREE.Mesh(new THREE.SphereGeometry(hole, 48, 24), new THREE.MeshBasicMaterial({ color: 0x000000 }))
    const photon = new THREE.Mesh(new THREE.TorusGeometry(hole * 1.06, hole * 0.018, 8, 128), new THREE.MeshBasicMaterial({ color: 0xffe2b0 }))
    photon.rotation.x = -Math.PI / 2 + 0.12
    const glare = new THREE.Mesh(new THREE.PlaneGeometry(2, 2), new THREE.ShaderMaterial({ vertexShader: glowVertex, fragmentShader: coronaFragment, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false,
      uniforms: { uColor: { value: new THREE.Color(0xff8a3a) }, uPower: { value: 2.6 } } }))
    glare.scale.setScalar(hole * 5)
    glare.renderOrder = 2
    this.suns.push({ body: '', mesh: horizon, corona: glare })
    this.sky.add(horizon, disc, photon, glare)
    // From the galaxy every solar system is one point of light, coloured by its star; the anchor's fades out as its real star comes into view.
    const dotAt = new Float32Array(this.systems.size * 3), dotTint = new Float32Array(this.systems.size * 3)
    for (const system of this.systems.values()) { dotAt.set([system.center.x, system.center.y, system.center.z], system.index * 3); dotTint.set([system.color.r, system.color.g, system.color.b], system.index * 3) }
    const dotShape = new THREE.BufferGeometry()
    dotShape.setAttribute('position', new THREE.BufferAttribute(dotAt, 3))
    dotShape.setAttribute('color', new THREE.BufferAttribute(dotTint, 3))
    this.dots = new THREE.Points(dotShape, new THREE.PointsMaterial({ size: 3.2, sizeAttenuation: false, vertexColors: true, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, depthTest: false }))
    this.dots.frustumCulled = false
    this.dots.renderOrder = 10
    this.sky.add(this.dots)
    const open = document.createElement('option')
    open.value = ''; open.textContent = 'Deep space'; open.disabled = true
    this.navSelect.replaceChildren(open, ...[...this.systems.values()].map(system => { const option = document.createElement('option'); option.value = system.id; option.textContent = system.label; return option }))
    this.navState = ''

    // The LAN is a solar system, so it gets a smooth dusty glow around its sun; spiral arms are for the galaxy.
    const dust = new THREE.Mesh(new THREE.PlaneGeometry(cosmos.lan.radius * 2.3, cosmos.lan.radius * 2.3), new THREE.ShaderMaterial({ vertexShader: glowVertex, fragmentShader: dustFragment, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide, uniforms: { uColor: { value: new THREE.Color(0xffb067) } } }))
    dust.rotation.x = -Math.PI / 2
    dust.position.set(cosmos.lan.star[0], -3, cosmos.lan.star[2])
    dust.renderOrder = -5
    home.add(dust)
    this.sunLight.position.set(...cosmos.lan.star)
    for (const radius of cosmos.rings) {
      const points = Array.from({ length: 129 }, (_, k) => new THREE.Vector3(Math.cos(k / 128 * TAU) * radius, 0, Math.sin(k / 128 * TAU) * radius))
      const orbitLine = new THREE.Line(new THREE.BufferGeometry().setFromPoints(points), new THREE.LineBasicMaterial({ color: 0x6aa8ff, transparent: true, opacity: 0.1, depthWrite: false }))
      orbitLine.position.set(...cosmos.lan.star)
      home.add(orbitLine)
    }
    // Asteroid belts: tumbling rocks on near-circular orbits, each at its Kepler speed so a belt shears slowly as it turns.
    const rockShape = new THREE.IcosahedronGeometry(1, 1)
    const corners = rockShape.getAttribute('position')
    for (let index = 0; index < corners.count; index++) {
      const lump = 0.72 + 0.5 * hash(`,${corners.getY(index).toFixed(3)},${corners.getZ(index).toFixed(3)}`)
      corners.setXYZ(index, corners.getX(index) * lump, corners.getY(index) * lump * 0.85, corners.getZ(index) * lump)
    }
    rockShape.computeVertexNormals()
    const rockMaterial = new THREE.MeshStandardMaterial({ color: 0xffffff, emissive: 0x2c2620, roughness: 1, metalness: 0, flatShading: true })
    cosmos.belts.forEach((belt, beltIndex) => {
      const mid = (belt.inner + belt.outer) / 2, half = (belt.outer - belt.inner) / 2, deep = belt.thick ?? 1, grow = Math.min(2.2, 0.45 + (belt.inner + belt.outer) / 520)
      const area = Math.PI * (belt.outer ** 2 - belt.inner ** 2), phase = [1, 2, 3, 4].map(k => hash(`belt${beltIndex}p${k}`) * TAU)
      const rand = (key: string) => hash(`belt${beltIndex}:${key}`)
      // The belt is not a clean ring: it wanders in and out, and its density clumps and thins as it goes round.
      const density = (angle: number) => 0.5 + 0.5 * (0.5 * Math.sin(angle * 3 + phase[0]) + 0.3 * Math.sin(angle * 7 + phase[1]) + 0.2 * Math.sin(angle * 13 + phase[2]))
      const lane = (angle: number) => mid + half * (0.28 * Math.sin(angle * 2 + phase[3]) + 0.12 * Math.sin(angle * 5 + phase[0]))
      // Boulders first, each given a radial lane wider than itself and its neighbours, so no two can ever meet on their circular orbits.
      const small = !belt.home, bare = belt.kind === 'dust'
      const wanted = bare ? 2 : Math.min(small ? 14 : 60, Math.max(3, Math.round(area * (small ? 0.006 : 0.02)))), boulders: { radius: number; size: number; angle: number }[] = []
      for (let tries = 0; boulders.length < wanted && tries < 900; tries++) {
        const angle = rand(`ba${tries}`) * TAU, size = (0.85 + rand(`bs${tries}`) * 0.9) * grow
        const radius = Math.min(belt.outer - size, Math.max(belt.inner + size, lane(angle) + (rand(`br${tries}`) - 0.5) * half * 1.5))
        if (rand(`bd${tries}`) > density(angle) + 0.25) continue
        if (boulders.every(other => Math.abs(other.radius - radius) >= 1.3 * (other.size + size) + 0.3)) boulders.push({ radius, size, angle })
      }
      // Small rocks fill in around them, keeping out of any boulder's lane.
      const rocks: { radius: number; angle: number; size: number; lift: number }[] = boulders.map(item => ({ radius: item.radius, angle: item.angle, size: item.size, lift: (rand(`bl${item.angle}`) - 0.5) * 1.2 * deep }))
      const target = Math.round((bare ? 0.12 : 1) * Math.min(small ? 320 : 2800, area * (small ? 0.12 : 0.75)))
      for (let tries = 0; rocks.length - boulders.length < target && tries < target * 8; tries++) {
        const angle = rand(`sa${tries}`) * TAU, spread = rand(`sr${tries}`) + rand(`sq${tries}`) - 1
        if (rand(`sd${tries}`) > density(angle) ** 1.4) continue
        const radius = lane(angle) + spread * half * 0.95, size = (0.12 + Math.pow(rand(`ss${tries}`), 4) * 0.5) * grow
        if (radius < belt.inner || radius > belt.outer || boulders.some(other => Math.abs(other.radius - radius) < 1.15 * (other.size + size) + 0.1)) continue
        rocks.push({ radius, angle, size, lift: (rand(`sy${tries}`) - 0.5) * 1.9 * deep * (1 - Math.abs(spread) * 0.4) })
      }
      const count = rocks.length, mesh = new THREE.InstancedMesh(rockShape, rockMaterial, count), orbit = new Float32Array(count * 4), tumble = new Float32Array(count * 4), size = new Float32Array(count)
      const tone = new THREE.Color()
      rocks.forEach((rock, index) => {
        orbit.set([rock.radius, rock.angle, rock.lift, 0.035 * (40 / rock.radius) ** 1.5], index * 4)
        const axis = new THREE.Vector3(rand(`x${index}`) - 0.5, rand(`u${index}`) - 0.5, rand(`v${index}`) - 0.5).normalize()
        tumble.set([axis.x, axis.y, axis.z, (rand(`t${index}`) - 0.5) * (index < boulders.length ? 0.3 : 1.2)], index * 4)
        size[index] = rock.size
        mesh.setColorAt(index, tone.setHSL(0.06 + rand(`h${index}`) * 0.05, 0.06 + rand(`c${index}`) * 0.16, 0.3 + rand(`l${index}`) * 0.28))
      })
      mesh.frustumCulled = false
      const owner = belt.home ? home : groupAt(belt.center)
      owner.add(mesh)
      this.rocks.push({ mesh, orbit, tumble, size, belt: beltIndex, center: new THREE.Vector3(...belt.center), owner })

      // Dust: a haze of tiny specks on the same orbits, wider and thicker than the rocks, drifting with them.
      const dustCount = Math.round((bare ? 1.6 : 1) * Math.min(small ? 4200 : 16000, area * (small ? 1.4 : 2.4))), dust = new Float32Array(dustCount * 4), dustSize = new Float32Array(dustCount)
      let kept = 0
      for (let tries = 0; kept < dustCount && tries < dustCount * 4; tries++) {
        const angle = rand(`da${tries}`) * TAU, spread = rand(`dr${tries}`) + rand(`dq${tries}`) - 1
        if (rand(`dd${tries}`) > density(angle) ** 0.7) continue
        const radius = lane(angle) + spread * half * 1.35
        dust.set([radius, angle, (rand(`dy${tries}`) - 0.5) * 3.2 * deep * (1 - Math.abs(spread) * 0.3), 0.035 * (40 / Math.max(radius, 8)) ** 1.5], kept * 4)
        dustSize[kept++] = 0.6 + rand(`dz${tries}`) * 1.4
      }
      const specks = new THREE.BufferGeometry()
      specks.setAttribute('position', new THREE.BufferAttribute(new Float32Array(kept * 3), 3))
      specks.setAttribute('aOrbit', new THREE.BufferAttribute(dust.slice(0, kept * 4), 4))
      specks.setAttribute('aSize', new THREE.BufferAttribute(dustSize.slice(0, kept), 1))
      const haze = new THREE.ShaderMaterial({ vertexShader: dustVertex, fragmentShader: speckFragment, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, uniforms: { uTime: { value: 0 }, uColor: { value: new THREE.Color(0xcdb99a) }, uCenter: { value: new THREE.Vector3(...belt.center) } } })
      this.timed.push(haze)
      const points = new THREE.Points(specks, haze)
      points.frustumCulled = false
      points.renderOrder = 4
      owner.add(points)
    })
    // Suns, with a corona that always faces the camera.
    const sunMaterial = (color: number) => {
      const material = new THREE.ShaderMaterial({ vertexShader: sunVertex, fragmentShader: sunFragment, uniforms: { uTime: { value: 0 }, uColor: { value: new THREE.Color(color) } } })
      this.timed.push(material)
      return material
    }
    const makeSun = (id: string, radius: number, color: number, at: THREE.Vector3, owner: THREE.Group) => {
      const mesh = new THREE.Mesh(new THREE.SphereGeometry(1, 48, 24), sunMaterial(color))
      mesh.scale.setScalar(radius); mesh.position.copy(at)
      const corona = new THREE.Mesh(new THREE.PlaneGeometry(2, 2), new THREE.ShaderMaterial({ vertexShader: glowVertex, fragmentShader: coronaFragment, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false,
        uniforms: { uColor: { value: new THREE.Color(color) }, uPower: { value: 3 } } }))
      corona.scale.setScalar(radius * 3.2); corona.position.copy(at); corona.renderOrder = 3
      owner.add(mesh, corona)
      this.suns.push({ body: id, mesh, corona })
    }
    makeSun('', 7.5, 0xffb347, new THREE.Vector3(...cosmos.lan.star), home)
    for (const body of cosmos.bodies) if (body.kind === 'sun') makeSun(body.id, body.radius, cosmos.systems.find(system => system.id === body.id)?.color ?? 0xffffff, new THREE.Vector3(...body.center), this.systems.get(body.id)?.group ?? home)

    // Wormholes, in the manner of Deep Space Nine: a vortex mouth facing the sun, opening into a funnel of light that narrows to a white-hot throat.
    // Ours leads out of the system; each destination system has an exit of the same kind. The mouth faces +z locally, the funnel runs back along -z.
    const funnelProfile = Array.from({ length: 48 }, (_, index) => { const t = index / 47; return new THREE.Vector2(0.07 + 0.93 * (1 - t) ** 2.3, -t * 2.6) })
    for (const body of cosmos.bodies) if (body.kind === 'portal' || body.kind === 'exit') {
      const home = body.kind === 'portal', a = new THREE.Color(home ? 0x3b86ff : 0x3fd6ff), b = new THREE.Color(home ? 0xffa25a : 0xb582ff)
      const uniforms = () => ({ uTime: { value: 0 }, uA: { value: a }, uB: { value: b } })
      const group = new THREE.Group()
      const mouthMaterial = new THREE.ShaderMaterial({ vertexShader: glowVertex, fragmentShader: portalFragment, transparent: true, depthWrite: false, side: THREE.DoubleSide, uniforms: uniforms() })
      const funnelMaterial = new THREE.ShaderMaterial({ vertexShader: glowVertex, fragmentShader: funnelFragment, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide, uniforms: uniforms() })
      this.timed.push(mouthMaterial, funnelMaterial)
      const mouth = new THREE.Mesh(new THREE.CircleGeometry(1, 72), mouthMaterial)
      mouth.position.z = 0.02
      const funnelShape = new THREE.LatheGeometry(funnelProfile, 72).rotateX(Math.PI / 2)
      const rim = new THREE.Mesh(new THREE.TorusGeometry(1, 0.04, 12, 96), new THREE.MeshBasicMaterial({ color: new THREE.Color(0xdbe9ff) }))
      const flare = (color: THREE.Color, scale: number, at: number, power: number) => {
        const mesh = new THREE.Mesh(new THREE.PlaneGeometry(2, 2), new THREE.ShaderMaterial({ vertexShader: glowVertex, fragmentShader: coronaFragment, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false,
          uniforms: { uColor: { value: color }, uPower: { value: power } } }))
        mesh.scale.setScalar(scale); mesh.position.z = at
        return mesh
      }
      const halo = flare(a, 2.3, 0.05, 2.6), core = flare(new THREE.Color(0xfff4e0), 0.6, -2.55, 2.4)
      const funnel = new THREE.Mesh(funnelShape, funnelMaterial)
      group.add(funnel, mouth, rim, halo, core)
      group.userData.billboards = [halo, core]
      group.userData.wormhole = [funnel, rim, core]
      group.quaternion.setFromUnitVectors(new THREE.Vector3(0, 0, 1), new THREE.Vector3(...body.face).normalize())
      group.scale.setScalar(body.radius)
      group.position.set(...body.center)
      this.systems.get(this.systemOf.get(body.id) ?? homeKey)!.group.add(group)
      this.portals.push({ body: body.id, group, material: mouthMaterial })
    }
    this.dressGates()
    this.buildWorlds()
    this.buildFleets()
    for (const body of cosmos.bodies) {
      if (body.kind === 'exit') continue
      const node = body.node, label = document.createElement('div')
      label.className = 'labmap-plate'
      label.dataset.kind = node.kind
      const name = document.createElement('b')
      name.textContent = node.kind === 'clients' ? `${node.label} ${node.count ?? 0}` : node.label
      label.append(name)
      if (node.detail && node.kind !== 'app' && node.kind !== 'client' && node.kind !== 'site') { const small = document.createElement('span'); small.textContent = node.detail; label.append(small) }
      label.hidden = true
      this.labels.append(label)
      this.plates.set(body.id, { body, label })
    }
    // Our system's own name plate, shown when it is seen from afar as one star among many.
    const gateway = cosmos.byId.get(layout.gatewayId)
    if (gateway) {
      const label = document.createElement('div'), name = document.createElement('b')
      label.className = 'labmap-plate'
      label.dataset.kind = 'region'
      name.textContent = 'Home lab'
      label.append(name)
      label.hidden = true
      this.labels.append(label)
      this.plates.set(homeKey, { body: { ...gateway, id: homeKey, kind: 'sun', radius: 7.5, node: { ...gateway.node, id: homeKey, kind: 'region', label: 'Home lab' } }, label })
    }
    this.buildLanes()
    this.positions(0)
  }

  // Every planet, moon and destination is one instance of a noise-shaded sphere.
  private buildWorlds() {
    const cosmos = this.cosmos!
    const bodies = cosmos.bodies.filter(body => body.kind === 'planet' || body.kind === 'moon' || body.kind === 'world')
    const count = Math.max(1, bodies.length)
    const geometry = new THREE.SphereGeometry(1, 40, 24)
    const star = new Float32Array(count * 3), atmos = new Float32Array(count * 3), seedKind = new Float32Array(count * 2)
    bodies.forEach((body, index) => {
      this.slots.set(body.id, index)
      star.set(body.star, index * 3)
      const kind = body.kind === 'planet' ? (body.node.kind === 'switch' ? 2 : [1, 0, 3][Math.floor(hash(body.id + 'k') * 3)])
        : body.kind === 'moon' ? (hash(body.id + 'k') < 0.7 ? 0 : 3) : [0, 1, 2, 3, 0, 1][Math.floor(hash(body.id + 'k') * 6)]
      seedKind.set([hash(body.id + 's'), kind], index * 2)
    })
    geometry.setAttribute('aStar', new THREE.InstancedBufferAttribute(star, 3))
    this.atmos = new THREE.InstancedBufferAttribute(atmos, 3)
    geometry.setAttribute('aAtmos', this.atmos)
    geometry.setAttribute('aSeedKind', new THREE.InstancedBufferAttribute(seedKind, 2))
    const material = new THREE.ShaderMaterial({ vertexShader: planetVertex, fragmentShader: planetFragment })
    const mesh = this.worlds = new THREE.InstancedMesh(geometry, material, count)
    mesh.count = bodies.length
    mesh.frustumCulled = false
    for (let index = 0; index < count; index++) mesh.setColorAt(index, white)
    this.world.add(mesh)
    // Gas giants (switches) wear rings.
    for (const body of bodies) if (body.kind === 'planet' && body.node.kind === 'switch') {
      const ring = new THREE.Mesh(new THREE.RingGeometry(1.45, 2.5, 96), new THREE.ShaderMaterial({ vertexShader: ringVertex, fragmentShader: ringFragment, transparent: true, depthWrite: false, side: THREE.DoubleSide,
        uniforms: { uColor: { value: new THREE.Color(0xd8c7a3) }, uInner: { value: 1.45 }, uOuter: { value: 2.5 } } }))
      ring.rotation.x = Math.PI / 2 + 0.35 + hash(body.id + 'r') * 0.3
      ring.renderOrder = 2
      this.systems.get(homeKey)!.group.add(ring)
      this.rings.push({ body: body.id, mesh: ring })
    }
  }

  private buildFleets() {
    const cosmos = this.cosmos
    if (!cosmos) return
    for (const fleet of this.fleets.values()) { this.world.remove(fleet.mesh); fleet.mesh.dispose() }
    for (const fleet of this.shipFleets) { this.world.remove(fleet.mesh); fleet.mesh.dispose() }
    for (const station of this.stations) { station.frame.removeFromParent(); station.modules.removeFromParent(); station.frame.geometry.dispose(); station.modules.geometry.dispose() }
    this.stations = []
    if (this.trails) { this.world.remove(this.trails.mesh); this.trails.mesh.dispose() }
    // Fleets are rebuilt when the models finish loading, so the previous link mesh has to go too or it stays behind frozen where it was.
    if (this.links) {
      this.world.remove(this.links)
      const old = this.links.material as THREE.ShaderMaterial
      this.timed = this.timed.filter(material => material !== old)
      this.links.geometry.dispose(); old.dispose()
      this.links = null
    }
    this.fleets.clear(); this.shipFleets = []
    const structure = hull(0.15, true), count = (kind: BodyKind) => cosmos.bodies.filter(body => body.kind === kind).length
    for (const name of parts) {
      const geometry = this.kits[name]
      if (!geometry) continue
      const fleet = new Fleet(geometry, structure, Math.max(8, 80 + count('city') + count('pod') + count('station') + count('depot')))
      this.fleets.set(name, fleet)
      this.world.add(fleet.mesh)
    }
    // Each host is a station kitbashed from its id, with a docking module for every app on it.
    const plated = hull(0.08, true)
    for (const body of cosmos.bodies) if (body.kind === 'station' || body.kind === 'depot' || body.kind === 'relay') {
      const apps = [...this.layout!.nodes.values()].filter(node => node.kind === 'app' && node.parentId === body.id)
      const parts = buildStation(body.id, apps, body.kind === 'depot')
      const frame = new THREE.Mesh(parts.frame, plated), modules = new THREE.Mesh(parts.modules, plated)
      frame.frustumCulled = false; modules.frustumCulled = false
      this.systems.get(homeKey)!.group.add(frame, modules)
      this.stations.push({ body: body.id, frame, modules })
    }
    const sats = Math.max(8, count('satellite'))
    const bus = new Fleet(satellite(), hull(0.12, false), sats), beacon = new Fleet(new THREE.SphereGeometry(0.045, 10, 8), new THREE.MeshBasicMaterial({ transparent: true, opacity: 0.95, blending: THREE.AdditiveBlending, depthWrite: false }), sats, 14)
    this.fleets.set('satellite', bus); this.fleets.set('beacon', beacon)
    this.world.add(bus.mesh, beacon.mesh)
    const shipHull = hull(0.4)
    for (const name of models) {
      const fleet = new Fleet(this.kits[name] ?? new THREE.ConeGeometry(0.25, 1, 6).rotateX(Math.PI / 2), shipHull, maxShips, 12)
      this.shipFleets.push(fleet)
      this.world.add(fleet.mesh)
    }
    const trail = new THREE.ConeGeometry(0.1, 1, 8).rotateX(-Math.PI / 2).translate(0, 0, -0.5)
    this.trails = new Fleet(trail, new THREE.MeshBasicMaterial({ transparent: true, opacity: 0.45, blending: THREE.AdditiveBlending, depthWrite: false }), maxShips * models.length, 14)
    this.world.add(this.trails.mesh)
    const link = new THREE.ShaderMaterial({ vertexShader: linkVertex, fragmentShader: linkFragment, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, uniforms: { uTime: { value: 0 }, uColor: { value: new THREE.Color(palette.air) } } })
    this.timed.push(link)
    const links = new THREE.LineSegments(new THREE.BufferGeometry(), link)
    links.renderOrder = 14
    links.frustumCulled = false
    this.links = links
    this.world.add(links)
  }

  private chain(id: string): string[] {
    const cosmos = this.cosmos!, layout = this.layout!, body = cosmos.byId.get(id)
    // A destination is reached through its system's exit wormhole, and everything at home climbs to the gateway; ships warp between the two.
    if (body?.kind === 'world') { const exit = `exit:${body.node.block}`; return cosmos.byId.has(exit) ? [id, exit, layout.gatewayId] : [id, layout.gatewayId] }
    if (id === 'wan') return ['wan', layout.gatewayId]
    const ids = routeOf(layout, id).filter(item => cosmos.byId.has(item) && item !== 'wan')
    // A client's ships launch from its moon.
    const moon = body && (body.kind === 'satellite' || body.kind === 'city') ? body.parent : null
    return moon && cosmos.byId.has(moon) ? [ids[0], moon, ...ids.slice(1)] : ids
  }
  private buildLanes() {
    this.lanes = []
    const layout = this.layout, live = this.live, cosmos = this.cosmos
    if (!layout || !live || !cosmos) return
    const mixes = mixTotals(layout, live)
    const tinted = (list: string[] | undefined) => list?.length ? list.map(item => serviceTint[item as (typeof services)[number]]) : undefined
    const speed = (shape: { speed: number }) => 7 + shape.speed * 28
    for (const [id, rate] of Object.entries(live.rates)) {
      const body = cosmos.byId.get(id), node = body?.node
      if (!body || !node || node.kind === 'network' || node.kind === 'wan' || node.kind === 'clients') continue
      const total = rate.rxBps + rate.txBps, shape = streaks(total)
      if (!shape.count) continue
      const ups = this.chain(id)
      const cars = Math.min(14, Math.round(shape.count * 1.4)), up = Math.round(cars * (rate.txBps / Math.max(total, 1)))
      // The gate talks to the uplink satellite by transmission, not by ships.
      if (body.kind === 'portal') { if (cosmos.byId.has('wan')) this.lanes.push({ id, stops: [id, 'wan'], up, down: cars - up, speed: 3 + shape.speed * 6, flow: false, route: routeOf(layout, id), air: true, model: 0 }); continue }
      const stops = body.kind === 'satellite' ? ups.slice(1) : ups
      if (stops.length < 2) continue
      const mix = tinted(carServices(mixes.get(id), cars)), own = ownsTint.has(node.kind) ? node.tint : undefined, home = own === undefined ? undefined : new THREE.Color(own)
      this.lanes.push({ id, stops, up, down: cars - up, speed: speed(shape), flow: false, route: routeOf(layout, id), model: Math.floor(hash(id) * models.length),
        tints: Array.from({ length: cars }, (_, k) => k < up ? mix?.[k] : home ?? mix?.[k]) })
      if (body.kind === 'satellite' && body.parent) this.lanes.push({ id, stops: [id, body.parent], up: Math.ceil(shape.count / 2), down: Math.floor(shape.count / 2), speed: 3 + shape.speed * 6, flow: false, route: routeOf(layout, id), air: true, model: 0 })
    }
    const known = (id: string) => cosmos.byId.has(id) ? id : id === 'wan' || id.startsWith('dest:') ? 'wan' : null
    for (const flow of live.flows) {
      const source = known(flow.from), target = known(flow.to)
      if (!source || !target || source === target) continue
      const shape = streaks(flow.bps)
      if (!shape.count) continue
      const up = this.chain(source), down = this.chain(target), meet = up.findIndex(item => down.includes(item))
      if (meet < 0) continue
      const stops = [...up.slice(0, meet + 1), ...down.slice(0, down.indexOf(up[meet])).reverse()]
      if (stops.length < 2) continue
      const goal = cosmos.byId.get(target)?.node.tint, tint = goal === undefined ? serviceTint[flow.service] : new THREE.Color(goal)
      this.lanes.push({ id: `${flow.from}>${flow.to}`, stops, up: shape.count, down: 0, speed: speed(shape), flow: true, route: [source, target], model: Math.floor(hash(flow.to) * models.length), tints: Array(shape.count).fill(tint) })
    }
  }

  private paint() {
    const worlds = this.worlds, cosmos = this.cosmos
    if (!worlds || !cosmos) return
    for (const body of cosmos.bodies) {
      const slot = this.slots.get(body.id)
      if (slot === undefined) continue
      const node = body.node, health = effectiveHealth(node, this.live), offline = health === 'stale' || (node.kind === 'clients' && health === 'unknown')
      const base = offline ? grey : this.color.setHex(this.baseOf(body)), rim = toneOf(node, health)
      worlds.setColorAt(slot, base)
      this.atmos?.setXYZ(slot, ((offline ? 0x8a94a3 : rim) >> 16 & 255) / 255, ((offline ? 0x8a94a3 : rim) >> 8 & 255) / 255, ((offline ? 0x8a94a3 : rim) & 255) / 255)
      const plate = this.plates.get(body.id)
      if (plate) {
        plate.label.dataset.health = health
        plate.label.classList.toggle('is-selected', body.id === this.selected)
        plate.label.classList.toggle('is-route', this.route.includes(body.id) && body.id !== this.selected)
      }
    }
    if (this.atmos) this.atmos.needsUpdate = true
    if (worlds.instanceColor) worlds.instanceColor.needsUpdate = true
    for (const [id, plate] of this.plates) if (!this.slots.has(id)) {
      plate.label.dataset.health = effectiveHealth(plate.body.node, this.live)
      plate.label.classList.toggle('is-selected', id === this.selected)
      plate.label.classList.toggle('is-route', this.route.includes(id) && id !== this.selected)
    }
  }

  private baseOf(body: Body): number {
    const palettes = [0xc9a06a, 0x6f9bd1, 0xb97a8f, 0x7fbf9a, 0xd4b86a, 0x9a86d9]
    if (body.kind === 'world') return body.node.tint ?? palettes[Math.floor(hash(body.id) * palettes.length)]
    if (body.kind === 'moon') return body.node.tint ?? 0x8fa3b8
    return palettes[Math.floor(hash(body.id + 'c') * palettes.length)]
  }

  // Where every body is at `time`: planets around their sun, moons and stations around planets, satellites around moons, cities on moons.
  private positions(time: number) {
    const cosmos = this.cosmos
    if (!cosmos) return
    for (const body of cosmos.bodies) {
      const out = this.worldAt.get(body.id) ?? new THREE.Vector3()
      this.worldAt.set(body.id, out)
      const parent = body.parent ? this.worldAt.get(body.parent) : undefined
      if (body.port && body.parent && parent) {
        // An app's module is bolted to its station and turns with it: Y-rotation by the station's spin.
        const host = cosmos.byId.get(body.parent)!, turn = host.spin * time + host.look * TAU, x = body.port[0] * host.radius, z = body.port[2] * host.radius
        out.set(parent.x + x * Math.cos(turn) + z * Math.sin(turn), parent.y + body.port[1] * host.radius, parent.z - x * Math.sin(turn) + z * Math.cos(turn))
        continue
      }
      if (body.kind === 'city') {
        const moon = parent && cosmos.byId.get(body.parent!)
        // The moon's mesh turns by spin * time + look * TAU about Y, which lowers a surface point's longitude by that angle.
        const lon = body.lon - (moon ? moon.spin * time + moon.look * TAU : 0), r = moon ? moon.radius : 1
        out.set(Math.cos(body.lat) * Math.cos(lon), Math.sin(body.lat), Math.cos(body.lat) * Math.sin(lon)).multiplyScalar(r).add(parent ?? this.tmp.set(0, 0, 0))
        continue
      }
      // A circle in its own plane: inclined about x, then the plane turned about the vertical.
      const angle = body.phase + body.omega * time, x = Math.cos(angle) * body.orbit, flat = Math.sin(angle) * body.orbit
      const y = flat * Math.sin(body.tilt), z = flat * Math.cos(body.tilt), c = Math.cos(body.plane), n = Math.sin(body.plane)
      out.set(x * c + z * n, y, -x * n + z * c)
      out.add(parent ?? this.tmp.set(...body.center))
    }
  }

  private write(time: number) {
    const cosmos = this.cosmos, worlds = this.worlds
    if (!cosmos || !worlds) return
    for (const body of cosmos.bodies) {
      const at = this.worldAt.get(body.id)!, hide = this.hidden(body.node) || this.systemOf.get(body.id) !== this.anchor, spin = body.spin * time + body.look * TAU
      const slot = this.slots.get(body.id)
      if (slot !== undefined) {
        this.dummy.position.copy(at)
        this.dummy.rotation.set(0, spin, 0)
        this.dummy.scale.setScalar(hide ? 1e-4 : body.radius)
        this.dummy.updateMatrix()
        worlds.setMatrixAt(slot, this.dummy.matrix)
        continue
      }
      if (hide || body.kind === 'station' || body.kind === 'depot' || body.kind === 'pod' || body.kind === 'relay' || body.kind === 'portal' || body.kind === 'exit') continue
      const color = this.tintOf(body)
      this.dummy.position.copy(at)
      this.dummy.rotation.set(0, spin, 0)
      this.dummy.quaternion.identity()
      if (body.kind === 'city') {
        const moon = body.parent ? this.worldAt.get(body.parent) : undefined
        if (moon) this.dummy.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), this.tmp.copy(at).sub(moon).normalize())
        this.dummy.position.addScaledVector(this.tmp, body.radius * 0.12)
      } else this.dummy.rotation.set(0.3 * body.look, spin, 0)
      this.dummy.scale.setScalar(body.radius * (body.kind === 'city' ? 1.05 : 2))
      if (body.kind === 'city') {
        this.dummy.updateMatrix()
        this.fleets.get((['house-cylinder', 'base-large', 'geodesic-dome', 'building-l'] as const)[Math.floor(body.look * 4)])?.push(this.dummy.matrix, color)
      } else if (body.kind === 'satellite') {
        this.dummy.rotation.set(0, spin, 0)
        this.dummy.scale.setScalar(body.radius * 2.2)
        this.dummy.updateMatrix()
        this.fleets.get('satellite')?.push(this.dummy.matrix, this.color.copy(color).lerp(white, 0.85))
        this.mat.makeTranslation(0, 0.37, 0.07)
        this.fleets.get('beacon')?.push(this.dummy.matrix.clone().multiply(this.mat), color)
      }
    }
    worlds.instanceMatrix.needsUpdate = true
    for (const station of this.stations) {
      const body = cosmos.byId.get(station.body)!, at = this.worldAt.get(station.body)!, shown = !this.hidden(body.node)
      for (const mesh of [station.frame, station.modules]) { mesh.position.copy(at); mesh.rotation.y = body.spin * time + body.look * TAU; mesh.scale.setScalar(body.radius); mesh.visible = shown }
      station.modules.visible = shown && this.layers.apps
    }
    for (const fleet of this.fleets.values()) fleet.commit()
    for (const ring of this.rings) {
      const at = this.worldAt.get(ring.body), body = cosmos.byId.get(ring.body)
      if (at && body) { ring.mesh.position.copy(at); ring.mesh.scale.setScalar(body.radius); ring.mesh.visible = true }
    }
    if (this.links) this.links.visible = this.anchor === homeKey
    for (const sun of this.suns) {
      const at = sun.body ? this.worldAt.get(sun.body) : undefined
      if (at) { sun.mesh.position.copy(at); sun.corona.position.copy(at) }
      sun.mesh.rotation.y = time * 0.05
      sun.corona.quaternion.copy(this.camera.quaternion)
    }
    for (const portal of this.portals) {
      const at = this.worldAt.get(portal.body)
      if (at) portal.group.position.copy(at)
      const facing = portal.group.quaternion.clone().invert()
      for (const flare of portal.group.userData.billboards as THREE.Mesh[]) flare.quaternion.copy(this.camera.quaternion).premultiply(facing)
    }
  }

  private tintOf(body: Body): THREE.Color {
    const health: Health = effectiveHealth(body.node, this.live)
    if (health === 'stale') return this.color.copy(grey)
    const tint = body.node.tint ?? (body.kind === 'satellite' || body.kind === 'city' ? 0x7fd6ff : 0xb8c7ff)
    return this.color.setHex(tint)
  }

  private point(lane: Lane, points: THREE.Vector3[], lengths: number[], total: number, distance: number, out: THREE.Vector3) {
    let left = Math.min(Math.max(distance, 0), total)
    for (let leg = 0; leg < lengths.length; leg++) {
      if (left <= lengths[leg] || leg === lengths.length - 1) {
        const u = lengths[leg] > 0 ? Math.min(1, left / lengths[leg]) : 0, from = this.cosmos!.byId.get(lane.stops[leg])!, to = this.cosmos!.byId.get(lane.stops[leg + 1])!
        const gateLeg = (from.kind === 'portal' || from.kind === 'exit') && (to.kind === 'portal' || to.kind === 'exit')
        out.lerpVectors(points[leg], points[leg + 1], u)
        out.y += gateLeg ? 0 : Math.min(lengths[leg] * 0.2, 5) * 4 * u * (1 - u)
        return out
      }
      left -= lengths[leg]
    }
    return out.copy(points[points.length - 1])
  }

  // Each ship holds its own place in a sunflower pattern around the lane's centre line, with a slow wobble, so ships sharing a lane or a
  // hyperlane pass beside each other instead of through each other. The pattern closes in at both ends so traffic still funnels through a mouth.
  private lean(lane: Lane, index: number, time: number, edge: number, at: THREE.Vector3, ahead: THREE.Vector3, points: THREE.Vector3[], lengths: number[], total: number, s: number) {
    const gate = [lane.stops[0], lane.stops[lane.stops.length - 1]].map(id => this.cosmos!.byId.get(id)).find(body => body?.kind === 'portal' || body?.kind === 'exit')
    const k = index + Math.floor(hash(lane.id) * 9), spread = Math.min(0.95 * Math.sqrt(k + 0.5), gate ? gate.radius * 0.6 : Infinity), turn = k * 2.399963 + hash(lane.id + 'o') * TAU
    const phase = hash(lane.id + index) * TAU
    const ox = (Math.cos(turn) * spread + Math.sin(time * 0.8 + phase) * 0.3) * edge, oy = (Math.sin(turn) * spread + Math.cos(time * 0.6 + phase * 1.3) * 0.3) * edge
    // Right at the end of a route there's nothing ahead, so take the heading from just behind instead.
    if (s + 0.6 <= total) this.point(lane, points, lengths, total, s + 0.6, this.forward).sub(at)
    else this.forward.copy(at).sub(this.point(lane, points, lengths, total, s - 0.6, this.side))
    if (this.forward.lengthSq() < 1e-8) return
    this.forward.normalize()
    this.side.crossVectors(this.forward, this.up)
    if (this.side.lengthSq() < 1e-6) this.side.set(1, 0, 0)
    this.side.normalize()
    this.lift.crossVectors(this.forward, this.side).normalize()
    this.shift.set(0, 0, 0).addScaledVector(this.side, ox).addScaledVector(this.lift, oy)
    at.add(this.shift)
    ahead.add(this.shift)
  }

  private updateRocks(time: number) {
    const quat = new THREE.Quaternion(), axis = new THREE.Vector3(), scale = new THREE.Vector3()
    for (const belt of this.rocks) {
      if (!belt.owner.visible) continue
      for (let index = 0; index < belt.size.length; index++) {
        const angle = belt.orbit[index * 4 + 1] + belt.orbit[index * 4 + 3] * time, radius = belt.orbit[index * 4]
        axis.set(belt.tumble[index * 4], belt.tumble[index * 4 + 1], belt.tumble[index * 4 + 2])
        quat.setFromAxisAngle(axis, belt.tumble[index * 4 + 3] * time + index)
        this.tmp.set(Math.cos(angle) * radius, belt.orbit[index * 4 + 2], Math.sin(angle) * radius).add(belt.center)
        this.mat.compose(this.tmp, quat, scale.setScalar(belt.size[index]))
        belt.mesh.setMatrixAt(index, this.mat)
      }
      belt.mesh.instanceMatrix.needsUpdate = true
    }
  }

  // Where a link meets a body: a gate's transmissions land on its outer ring, on the side facing the other end.
  private dish(id: string, at: THREE.Vector3, other: THREE.Vector3) {
    const body = this.cosmos!.byId.get(id)
    if (body?.kind !== 'portal') return at
    const face = new THREE.Vector3(...body.face).normalize(), toward = other.clone().sub(at)
    toward.addScaledVector(face, -toward.dot(face))
    return toward.lengthSq() < 1e-8 ? at : at.clone().addScaledVector(toward.normalize(), body.radius * 1.38)
  }

  // Traffic between the gate and the uplink satellite is carried by the transmission between them, so ships on that leg aren't drawn.
  private beamed(from: string, to: string | undefined) {
    const kind = (id: string | undefined) => id === 'wan' ? 'wan' : id ? this.cosmos!.byId.get(id)?.kind : undefined
    const ends = [kind(from), kind(to)]
    return ends.includes('wan') && ends.includes('portal')
  }

  private updateShips(time: number) {
    const trails = this.trails
    if (!trails) return
    const links: number[] = [], along: number[] = [], info: number[] = []
    const used = new Array(models.length).fill(0)
    if (this.layers.traffic || this.layers.flows) for (const lane of this.lanes) {
      if (lane.flow ? !this.layers.flows : !this.layers.traffic) continue
      const body = this.cosmos!.byId.get(lane.id)
      if (body && this.hidden(body.node)) continue
      const points = lane.stops.map(stop => this.worldAt.get(stop)!), lengths: number[] = []
      if (points.some(point => !point)) continue
      // A wormhole crossing takes no travel: the leg between a gateway and an exit counts for a few units, and a ship is hidden while it's inside it.
      const warp: boolean[] = []
      let total = 0
      for (let leg = 0; leg + 1 < points.length; leg++) {
        const from = this.cosmos!.byId.get(lane.stops[leg])!, to = this.cosmos!.byId.get(lane.stops[leg + 1])!
        const through = (from.kind === 'portal' && to.kind === 'exit') || (from.kind === 'exit' && to.kind === 'portal')
        const length = through ? 5 : points[leg].distanceTo(points[leg + 1])
        warp.push(through); lengths.push(length); total += length
      }
      if (total < 0.05) continue
      // Where ships go through a gate along this lane: either side of a crossing, and the ends of a route that starts or stops at one.
      const gates: number[] = [], isGate = (id: string) => { const kind = this.cosmos!.byId.get(id)?.kind; return kind === 'portal' || kind === 'exit' }
      for (let leg = 0, at = 0; leg < lengths.length; at += lengths[leg], leg++) if (warp[leg]) gates.push(at, at + lengths[leg])
      if (isGate(lane.stops[0])) gates.push(0)
      if (isGate(lane.stops[lane.stops.length - 1])) gates.push(total)
      const lit = !this.selected || lane.route.includes(this.selected) || this.route.includes(lane.id), dim = lit ? 1 : 0.12, count = lane.up + lane.down
      if (lane.air) {
        if (this.systemOf.get(lane.stops[0]) !== this.anchor) continue
        // One line per direction that has traffic, each carrying pulses that run that way; busier directions pulse more often and faster.
        for (const [trains, sign] of [[lane.up, 1], [lane.down, -1]] as const) {
          if (!trains) continue
          // The dots on a link follow its traffic: a busier direction carries more of them, and they run faster the more it moves.
          const seed = hash(lane.id + sign), dots = Math.max(1, Math.min(14, Math.round(trains * 1.4))), rate = (1.1 + lane.speed / total * 0.4) * (lit ? 1 : 0.35)
          const train = [sign * rate * dots, dots, seed] as const
          const [a, b] = [0, 1].map(end => this.dish(lane.stops[end], points[end], points[1 - end]))
          links.push(a.x, a.y, a.z, b.x, b.y, b.z)
          along.push(0, 1)
          info.push(...train, ...train)
        }
        continue
      }
      for (let index = 0; index < count; index++) {
        const out = index < lane.up, phase = (time * lane.speed / total + (out ? index / Math.max(1, lane.up) : (index - lane.up) / Math.max(1, lane.down) + 0.37)) % 1
        const t = out ? phase : 1 - phase, model = (lane.model + index) % models.length
        // Traffic funnels into a planet or station, but at a gate each ship keeps its lane offset so it warps through its own spot in the ring.
        const s = t * total, edge = Math.min(1, Math.min(isGate(lane.stops[0]) ? Infinity : s, isGate(lane.stops[lane.stops.length - 1]) ? Infinity : total - s) / 6)
        let into = 0, left = s
        while (into < lengths.length - 1 && left > lengths[into]) { left -= lengths[into]; into++ }
        if (warp[into] || this.beamed(lane.stops[into], lane.stops[into + 1]) || this.systemOf.get(lane.stops[into]) !== this.anchor) continue
        this.point(lane, points, lengths, total, s, this.tmp)
        this.point(lane, points, lengths, total, s + (out ? 0.4 : -0.4), this.ahead)
        this.lean(lane, index, time, edge, this.tmp, this.ahead, points, lengths, total, s)
        this.dummy.position.copy(this.tmp)
        this.dummy.quaternion.identity()
        this.dummy.lookAt(this.ahead)
        // Near a gate a ship narrows and stretches into a streak of light, so it warps in and out instead of popping.
        const warpIn = Math.min(1, gates.reduce((best, at) => Math.min(best, Math.abs(s - at)), Infinity) / 3), squeeze = Math.max(0.05, warpIn ** 0.7), stretch = 1 + 4 * (1 - warpIn) ** 2
        this.dummy.scale.set(1.25 * squeeze, 1.25 * squeeze, 1.25 * stretch)
        this.dummy.updateMatrix()
        const tint = lane.tints?.[index]
        this.color.copy(tint ?? (out ? this.color.setHex(palette.amber) : this.color.setHex(palette.cyan)))
        if (tint && out) this.color.lerp(white, 0.35)
        this.color.lerp(white, (1 - warpIn) * 0.8)
        this.color.multiplyScalar(dim)
        if (used[model] < maxShips) { this.shipFleets[model].push(this.dummy.matrix, this.color); used[model]++ }
        this.dummy.scale.set(1.2 * squeeze, 1.2 * squeeze, 2.6 * stretch)
        this.dummy.updateMatrix()
        trails.push(this.dummy.matrix, this.color)
      }
    }
    for (const fleet of this.shipFleets) fleet.commit()
    trails.commit()
    if (this.links) {
      this.links.geometry.setAttribute('position', new THREE.Float32BufferAttribute(links, 3))
      this.links.geometry.setAttribute('aAlong', new THREE.Float32BufferAttribute(along, 1))
      this.links.geometry.setAttribute('aInfo', new THREE.Float32BufferAttribute(info, 3))
      this.links.geometry.computeBoundingSphere()
    }
  }

  private updateMarker(time: number) {
    const id = this.selected, here = !!id && this.world.visible && this.systemOf.get(id) === this.anchor
    const at = here ? this.worldAt.get(id) : undefined, body = here ? this.cosmos?.byId.get(id) : undefined
    this.marker.visible = !!at && !!body
    if (this.routeLine) { this.world.remove(this.routeLine); this.routeLine.geometry.dispose(); this.routeLine = null }
    if (!at || !body) return
    this.marker.position.copy(at)
    this.marker.quaternion.copy(this.camera.quaternion)
    this.marker.scale.setScalar(Math.max(body.radius * 1.55, 0.5) * (1 + Math.sin(time * 4) * 0.04))
    const stops = this.route.filter(item => this.worldAt.has(item) && this.systemOf.get(item) === this.anchor)
    if (stops.length > 1) {
      this.routeLine = new THREE.Line(new THREE.BufferGeometry().setFromPoints(stops.map(item => this.worldAt.get(item)!)), new THREE.LineBasicMaterial({ color: palette.magenta, transparent: true, opacity: 0.8, depthTest: false }))
      this.routeLine.renderOrder = 30
      this.world.add(this.routeLine)
    }
  }

  private resize() {
    const width = this.host.clientWidth, height = this.host.clientHeight
    if (!width || !height) return
    this.renderer.setSize(width, height, false)
    this.screen.value = this.renderer.domElement.height / 2
    this.composer?.setSize(width, height)
    this.camera.aspect = width / height
    this.camera.updateProjectionMatrix()
    if (this.cosmos && !this.userMoved && !this.selected && !this.fly) this.home(false)
  }

  private loop = () => {
    if (this.disposed) return
    this.frame = requestAnimationFrame(this.loop)
    this.clock.update()
    const delta = Math.min(this.clock.getDelta(), 0.1), time = this.clock.getElapsed(), now = performance.now()
    const orbit = this.quiet ? 0 : time
    for (const material of this.timed) material.uniforms.uTime.value = time
    for (const portal of this.portals) { const rings = portal.group.userData.rings as THREE.Object3D[] | undefined; if (rings) { rings[0].rotation.z = orbit * 0.06; rings[1].rotation.z = -orbit * 0.11 } }
    this.positions(orbit)
    if (this.fly) {
      const fly = this.fly, progress = Math.min(1, (now - fly.start) / fly.duration), t = ease(progress)
      const goal = fly.body ? this.worldAt.get(fly.body) : undefined
      if (goal && fly.body) this.toGalaxy(goal, this.systems.get(this.systemOf.get(fly.body) ?? homeKey)?.center, fly.toTarget)
      // Climb out first, cross while high, then descend, so the trip never skims through the disc.
      const slide = fly.peak ? THREE.MathUtils.smoothstep(t, 0.3, 0.7) : t
      const target = fly.fromTarget.clone().lerp(fly.toTarget, slide), direction = fly.fromDir.clone().lerp(fly.toDir, t).normalize()
      const low = Math.log(fly.from) + (Math.log(fly.to) - Math.log(fly.from)) * t
      const lift = fly.peak ? Math.sin(Math.PI * t) * Math.max(0, Math.log(fly.peak) - Math.max(Math.log(fly.from), Math.log(fly.to))) : 0
      const camera = target.clone().addScaledVector(direction, Math.exp(low + lift) / K)
      this.setAnchor(this.nearest(camera), false)
      this.camera.position.copy(this.toLocal(camera))
      this.controls.target.copy(this.toLocal(target))
      if (progress >= 1) { this.fly = null; if (this.follow && goal) this.follow.last.copy(goal) }
    } else {
      this.setAnchor(this.nearest(this.toGalaxy(this.camera.position)), true)
      if (this.follow) {
        const at = this.worldAt.get(this.follow.id)
        if (at) { const shift = at.clone().sub(this.follow.last); this.camera.position.add(shift); this.controls.target.add(shift); this.follow.last.copy(at) }
      }
    }
    this.controls.update(delta)
    // The scene spans a galaxy down to a docking port, so the depth range tracks how far the camera is from what it's looking at.
    const range = this.camera.position.distanceTo(this.controls.target), near = Math.max(0.05, range * 0.002)
    if (Math.abs(near - this.camera.near) > near * 0.2) { this.camera.near = near; this.camera.far = Math.max(600000, range * 60); this.camera.updateProjectionMatrix() }
    // The galaxy camera sees the same way from the same place, in galaxy units.
    const sky = this.skyCamera, anchor = this.systems.get(this.anchor), skyRange = range / K
    sky.quaternion.copy(this.camera.quaternion)
    this.toGalaxy(this.camera.position, undefined, sky.position)
    sky.fov = this.camera.fov; sky.aspect = this.camera.aspect
    sky.near = Math.max(0.5, skyRange * 0.25); sky.far = Math.max(3e5, skyRange * 10)
    sky.updateProjectionMatrix()
    sky.updateMatrixWorld()
    this.skybox?.position.copy(sky.position)
    // The glow under the disc is how the galaxy looks from outside it; from inside, it is only stars.
    const glow = THREE.MathUtils.smoothstep(skyRange, 400, 5000)
    for (const material of this.glows) material.uniforms.uFade.value = glow
    // A system is drawn only once it is more than a point; until its star shows as a disc of light, its point stays lit too.
    const pxPerUnit = this.host.clientHeight / 2 / Math.tan(THREE.MathUtils.degToRad(this.camera.fov / 2))
    const away = anchor ? Math.max(this.camera.position.distanceTo(anchor.center), 1e-3) : Infinity
    const span = anchor ? anchor.size * pxPerUnit / away : 0
    this.world.visible = span >= 1.5
    this.expanded = span >= 80
    if (this.dots && anchor) {
      const colors = this.dots.geometry.getAttribute('color') as THREE.BufferAttribute, fade = 1 - THREE.MathUtils.smoothstep(anchor.sun * 3.2 * pxPerUnit / away, 2, 8)
      for (const system of this.systems.values()) { const k = system.id === this.anchor ? fade : 1; colors.setXYZ(system.index, system.color.r * k, system.color.g * k, system.color.b * k) }
      colors.needsUpdate = true
    }
    if (this.world.visible) {
      this.write(orbit)
      this.updateRocks(orbit)
      this.updateShips(time)
    }
    for (const sun of this.suns) if (!sun.body) sun.corona.quaternion.copy(this.camera.quaternion)
    this.updateMarker(time)
    this.updateLabels()
    this.updateNav()
    if (this.composer) this.composer.render(delta)
    else {
      this.renderer.autoClear = false
      this.renderer.clear()
      this.renderer.render(this.sky, sky)
      this.renderer.clearDepth()
      this.renderer.render(this.scene, this.camera)
    }
  }

  private updateNav() {
    const state = `${this.anchor}|${this.expanded}`
    if (state === this.navState) return
    this.navState = state
    this.galaxyButton.setAttribute('aria-pressed', String(!this.expanded))
    this.navSelect.value = this.expanded ? this.anchor : ''
  }

  // A system is labelled by its star when it is seen from afar, placed by the galaxy camera; up close, its bodies are labelled one by one.
  private systemPlate(id: string, out: THREE.Vector3) {
    for (const system of this.systems.values()) if (system.head === id) {
      if (system.id === this.anchor && this.expanded) return null
      return out.copy(system.center).project(this.skyCamera)
    }
    return null
  }

  private updateLabels() {
    const width = this.host.clientWidth, height = this.host.clientHeight, camera = this.camera.position
    const candidates: { plate: Plate; x: number; y: number; rank: number; lift: number }[] = []
    const pxPerUnit = height / 2 / Math.tan(THREE.MathUtils.degToRad(this.camera.fov / 2))
    for (const plate of this.plates.values()) {
      const body = plate.body, at = this.worldAt.get(body.id)
      const pinned = body.id === this.selected || body.id === this.hovered || this.route.includes(body.id)
      const far = this.systemPlate(body.id, this.projected)
      if (far) {
        if (far.z < 1 && Math.abs(far.x) < 1.1 && Math.abs(far.y) < 1.1) {
          candidates.push({ plate, x: (far.x + 1) / 2 * width, y: (1 - far.y) / 2 * height, rank: pinned ? -1 : labelRank[body.kind] * 10 + far.z, lift: 4 })
          continue
        }
      } else if (at && this.expanded && this.systemOf.get(body.id) === this.anchor && !this.hidden(body.node) && (pinned || camera.distanceTo(at) < labelReach[body.kind])) {
        this.projected.copy(at).project(this.camera)
        if (this.projected.z < 1 && Math.abs(this.projected.x) < 1.1 && Math.abs(this.projected.y) < 1.1) {
          const lift = Math.min(60, body.radius * pxPerUnit / Math.max(camera.distanceTo(at), 1))
          candidates.push({ plate, x: (this.projected.x + 1) / 2 * width, y: (1 - this.projected.y) / 2 * height, rank: pinned ? -1 : labelRank[body.kind] * 10 + this.projected.z, lift })
          continue
        }
      }
      if (!plate.label.hidden) plate.label.hidden = true
    }
    candidates.sort((a, b) => a.rank - b.rank)
    const placed: { l: number; t: number; r: number; b: number }[] = []
    for (const { plate, x, y, rank, lift } of candidates) {
      if (plate.label.hidden) plate.label.hidden = false
      let size = this.sizes.get(plate.body.id)
      if (!size?.w) { size = { w: plate.label.offsetWidth, h: plate.label.offsetHeight }; this.sizes.set(plate.body.id, size) }
      const kind = plate.body.node.kind
      const side = kind === 'app' || kind === 'client' || kind === 'site'
      const box = side ? { l: x + 10 + lift, t: y - size.h / 2, r: x + 10 + lift + size.w, b: y + size.h / 2 } : { l: x - size.w / 2, t: y - size.h - 14 - lift, r: x + size.w / 2, b: y - 14 - lift }
      if (rank >= 0 && placed.some(other => box.l < other.r + 4 && box.r > other.l - 4 && box.t < other.b + 2 && box.b > other.t - 2)) { plate.label.hidden = true; continue }
      placed.push(box)
      plate.label.style.transform = `translate3d(${x.toFixed(1)}px, ${(y - (side ? 0 : lift)).toFixed(1)}px, 0)`
    }
  }

  private pickAt(event: PointerEvent): string | null {
    const cosmos = this.cosmos
    if (!cosmos) return null
    const rect = this.renderer.domElement.getBoundingClientRect(), x = event.clientX - rect.left, y = event.clientY - rect.top
    const pxPerUnit = rect.height / 2 / Math.tan(THREE.MathUtils.degToRad(this.camera.fov / 2))
    let best: { id: string; z: number } | null = null
    // Far systems are picked by their point of light.
    for (const system of this.systems.values()) {
      if (system.id === this.anchor && this.expanded) continue
      this.projected.copy(system.center).project(this.skyCamera)
      if (this.projected.z >= 1 || this.projected.z < -1) continue
      const distance = this.skyCamera.position.distanceTo(system.center) * K
      if (Math.hypot((this.projected.x + 1) / 2 * rect.width - x, (1 - this.projected.y) / 2 * rect.height - y) <= 12 && (!best || distance < best.z)) best = { id: system.head, z: distance }
    }
    if (this.expanded) for (const body of cosmos.bodies) {
      const at = this.worldAt.get(body.id)
      if (!at || body.kind === 'exit' || this.hidden(body.node) || this.systemOf.get(body.id) !== this.anchor) continue
      this.projected.copy(at).project(this.camera)
      if (this.projected.z >= 1 || this.projected.z < -1) continue
      const distance = this.camera.position.distanceTo(at), reach = Math.max(body.radius * (body.kind === 'sun' || body.kind === 'portal' || body.kind === 'relay' ? 1 : 1.3) * pxPerUnit / Math.max(distance, 0.1), 9)
      const px = (this.projected.x + 1) / 2 * rect.width, py = (1 - this.projected.y) / 2 * rect.height
      if (Math.hypot(px - x, py - y) <= reach && (!best || distance < best.z)) best = { id: body.id, z: distance }
    }
    return best?.id ?? null
  }

  private onDown = (event: PointerEvent) => { this.down = { x: event.clientX, y: event.clientY }; this.fly = null }
  private onUp = (event: PointerEvent) => {
    if (!this.down || Math.hypot(event.clientX - this.down.x, event.clientY - this.down.y) > 6) { this.down = null; return }
    this.down = null
    const id = this.pickAt(event)
    if (id === homeKey) this.home()
    else this.events.select(id)
  }
  private onMove = (event: PointerEvent) => {
    if (event.buttons) return
    const id = this.pickAt(event)
    if (id === this.hovered) return
    this.plates.get(this.hovered ?? '')?.label.classList.remove('is-hovered')
    this.hovered = id
    if (id) this.plates.get(id)?.label.classList.add('is-hovered')
    this.renderer.domElement.style.cursor = id ? 'pointer' : ''
  }
  private onLeave = () => { this.plates.get(this.hovered ?? '')?.label.classList.remove('is-hovered'); this.hovered = null }
  private onVisibility = () => {
    cancelAnimationFrame(this.frame)
    if (document.visibilityState === 'visible') { this.clock.update(); this.loop() }
  }
}

