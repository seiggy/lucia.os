import * as THREE from 'three'
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js'
import type { MapNode } from './labMapModel'
import { hash, perTier, portsOf } from './universeModel'

// A host as a space station, kitbashed from a seed: a spine, a habitat ring with spokes, solar arrays and a dish, and a docking module for each
// app on an arm. Built in station units, where one unit is the station's radius.
const TAU = Math.PI * 2
const grey = 0xc4cad6, steel = 0x8d95a6, panel = 0x1c3278, white = 0xeef1f7
function piece(geometry: THREE.BufferGeometry, color: number | THREE.Color, place: (matrix: THREE.Matrix4) => void = () => undefined): THREE.BufferGeometry {
  const matrix = new THREE.Matrix4()
  place(matrix)
  const flat = (geometry.index ? geometry.toNonIndexed() : geometry).clone()
  flat.applyMatrix4(matrix)
  for (const name of Object.keys(flat.attributes)) if (name !== 'position' && name !== 'normal') flat.deleteAttribute(name)
  const count = flat.getAttribute('position').count, tint = new THREE.Color(color), colors = new Float32Array(count * 3)
  for (let index = 0; index < count; index++) colors.set([tint.r, tint.g, tint.b], index * 3)
  flat.setAttribute('color', new THREE.BufferAttribute(colors, 3))
  return flat
}
const at = (x: number, y: number, z: number, turn: [number, number, number] = [0, 0, 0], scale: [number, number, number] = [1, 1, 1]) => (matrix: THREE.Matrix4) => {
  matrix.compose(new THREE.Vector3(x, y, z), new THREE.Quaternion().setFromEuler(new THREE.Euler(...turn, 'YXZ')), new THREE.Vector3(...scale))
}
const merge = (list: THREE.BufferGeometry[]) => mergeGeometries(list) ?? new THREE.BufferGeometry()

export interface StationParts { frame: THREE.BufferGeometry; modules: THREE.BufferGeometry }

// The frame is the same for every station of a kind apart from its seed; the modules follow the apps docked at it.
export function buildStation(seed: string, apps: MapNode[], depot: boolean): StationParts {
  const frame: THREE.BufferGeometry[] = [], modules: THREE.BufferGeometry[] = []
  const ports = portsOf(apps.length), tiers = Math.ceil(apps.length / perTier)
  const roll = hash(seed + 'frame')
  // Spine with docking noses, hub, and the habitat ring on four spokes.
  const span = 0.8 + Math.max(0, tiers - 1) * 0.24
  frame.push(piece(new THREE.CylinderGeometry(0.13, 0.13, span * 2, 14), grey))
  for (const end of [-1, 1]) {
    frame.push(piece(new THREE.CylinderGeometry(end > 0 ? 0.06 : 0.17, end > 0 ? 0.17 : 0.06, 0.18, 14), steel, at(0, end * (span + 0.09), 0)))
    frame.push(piece(new THREE.TorusGeometry(0.16, 0.022, 8, 20), white, at(0, end * (span - 0.05), 0, [Math.PI / 2, 0, 0])))
  }
  frame.push(piece(new THREE.SphereGeometry(0.3, 20, 12), white))
  frame.push(piece(new THREE.TorusGeometry(0.62, 0.075, 10, 56), grey, at(0, 0, 0, [Math.PI / 2, 0, 0])))
  frame.push(piece(new THREE.TorusGeometry(0.62, 0.02, 6, 56), steel, at(0, 0.09, 0, [Math.PI / 2, 0, 0])))
  for (let spoke = 0; spoke < 4; spoke++) {
    const angle = spoke * Math.PI / 2 + roll
    frame.push(piece(new THREE.CylinderGeometry(0.018, 0.018, 0.34, 6), steel, at(Math.cos(angle) * 0.46, 0, Math.sin(angle) * 0.46, [0, -angle, Math.PI / 2])))
  }
  for (let tier = 1; tier < tiers; tier++) {
    const y = (tier % 2 ? 1 : -1) * Math.ceil(tier / 2) * 0.42
    frame.push(piece(new THREE.TorusGeometry(0.34, 0.045, 8, 36), grey, at(0, y, 0, [Math.PI / 2, 0, 0])))
  }
  if (depot) {
    // A depot is a cargo station: tanks clustered around the ring instead of arrays and modules.
    for (let tank = 0; tank < 5; tank++) {
      const angle = tank / 5 * TAU + roll
      frame.push(piece(new THREE.SphereGeometry(0.2, 16, 10), tank % 2 ? 0xd9dde6 : 0xb7c0d0, at(Math.cos(angle) * 0.98, (tank % 2 ? 0.12 : -0.12), Math.sin(angle) * 0.98)))
      frame.push(piece(new THREE.CylinderGeometry(0.02, 0.02, 0.36, 6), steel, at(Math.cos(angle) * 0.8, 0, Math.sin(angle) * 0.8, [0, -angle, Math.PI / 2])))
    }
  } else {
    // Solar arrays on a crossbar, and a dish and mast on top.
    const top = span * 0.82
    frame.push(piece(new THREE.CylinderGeometry(0.015, 0.015, 3.2, 6), steel, at(0, top, 0, [0, 0, Math.PI / 2])))
    for (const side of [-1, 1]) for (const cell of [0, 1]) frame.push(piece(new THREE.BoxGeometry(0.66, 0.012, 0.5), panel, at(side * (0.55 + cell * 0.7), top, 0)))
    frame.push(piece(new THREE.CylinderGeometry(0.012, 0.012, 0.3, 6), steel, at(0, span + 0.3, 0)))
    frame.push(piece(new THREE.SphereGeometry(0.14, 16, 6, 0, TAU, 0, 0.95), white, at(0, span + 0.42, 0.02, [-0.6, 0, 0])))
  }

  // A docking module for each app, on an arm from the spine. They are station hardware, not habitats: a lab (ribbed cylinder with
  // adapters and radiator fins), a node (a hub with four docking stubs), a cargo pallet (canisters on a truss) or a propellant tank (a banded
  // capsule with a solar fin). Each is built along the arm's axis, x, and ends in a lit docking port.
  apps.forEach((app, index) => {
    const port = ports[index], tint = new THREE.Color(app.tint ?? 0x7fb2ff), shell = new THREE.Color(0xc9cfdb).lerp(tint, 0.38), band = tint.clone().lerp(new THREE.Color(white), 0.2)
    const kind = Math.floor(hash(app.id + 'm') * 4)
    const spot = new THREE.Matrix4().makeTranslation(port.x, port.y, port.z).multiply(new THREE.Matrix4().makeRotationY(-port.angle))
    const put = (geometry: THREE.BufferGeometry, color: number | THREE.Color, x: number, y = 0, z = 0, turn: [number, number, number] = [0, 0, 0]) =>
      modules.push(piece(geometry, color, matrix => matrix.copy(spot).multiply(new THREE.Matrix4().compose(new THREE.Vector3(x, y, z), new THREE.Quaternion().setFromEuler(new THREE.Euler(...turn, 'YXZ')), new THREE.Vector3(1, 1, 1)))))
    const along: [number, number, number] = [0, 0, Math.PI / 2]
    modules.push(piece(new THREE.CylinderGeometry(0.02, 0.02, 0.78, 6), steel, at(port.x * 0.52, port.y, port.z * 0.52, [0, -port.angle, Math.PI / 2])))
    let tip = 0.3
    if (kind === 0) {
      put(new THREE.CylinderGeometry(0.085, 0.085, 0.46, 16), shell, 0, 0, 0, along)
      for (const rib of [-0.15, 0, 0.15]) put(new THREE.TorusGeometry(0.087, 0.011, 6, 20), steel, rib, 0, 0, [0, Math.PI / 2, 0])
      put(new THREE.CylinderGeometry(0.05, 0.085, 0.07, 14), steel, -0.265, 0, 0, along)
      put(new THREE.CylinderGeometry(0.085, 0.05, 0.07, 14), steel, 0.265, 0, 0, along)
      for (const side of [-1, 1]) put(new THREE.BoxGeometry(0.2, 0.01, 0.16), 0xe8ecf3, 0.02, side * 0.16, 0, [0, 0, side * 0.35])
      put(new THREE.BoxGeometry(0.3, 0.03, 0.03), band, 0, 0, 0.09)
      tip = 0.32
    } else if (kind === 1) {
      put(new THREE.CylinderGeometry(0.1, 0.1, 0.2, 16), shell, 0, 0, 0, along)
      for (const side of [-1, 1]) {
        put(new THREE.CylinderGeometry(0.055, 0.055, 0.16, 12), steel, 0, 0, side * 0.14, [Math.PI / 2, 0, 0])
        put(new THREE.CylinderGeometry(0.055, 0.055, 0.16, 12), steel, 0, side * 0.14, 0)
        put(new THREE.TorusGeometry(0.058, 0.012, 6, 14), white, 0, side * 0.225, 0, [Math.PI / 2, 0, 0])
        put(new THREE.TorusGeometry(0.058, 0.012, 6, 14), white, 0, 0, side * 0.225)
      }
      put(new THREE.TorusGeometry(0.1, 0.012, 6, 18), band, 0, 0, 0, [0, Math.PI / 2, 0])
      tip = 0.16
    } else if (kind === 2) {
      put(new THREE.BoxGeometry(0.5, 0.025, 0.2), steel, 0, 0, 0)
      for (const edge of [-1, 1]) put(new THREE.BoxGeometry(0.5, 0.02, 0.02), grey, 0, 0.012, edge * 0.1)
      for (const slot of [-0.15, 0.15]) {
        put(new THREE.CylinderGeometry(0.06, 0.06, 0.2, 14), shell, slot, 0.085, 0, [Math.PI / 2, 0, 0])
        put(new THREE.TorusGeometry(0.061, 0.01, 6, 16), band, slot, 0.085, 0.07)
        put(new THREE.TorusGeometry(0.061, 0.01, 6, 16), band, slot, 0.085, -0.07)
      }
      put(new THREE.BoxGeometry(0.12, 0.1, 0.1), 0x59627a, 0, 0.06, 0)
      tip = 0.29
    } else {
      put(new THREE.CylinderGeometry(0.09, 0.09, 0.3, 16), shell, 0, 0, 0, along)
      for (const end of [-1, 1]) put(new THREE.SphereGeometry(0.09, 14, 8, 0, TAU, 0, Math.PI / 2), shell, end * 0.15, 0, 0, [0, 0, -end * Math.PI / 2])
      for (const rib of [-0.1, 0, 0.1]) put(new THREE.TorusGeometry(0.092, 0.012, 6, 20), band, rib, 0, 0, [0, Math.PI / 2, 0])
      put(new THREE.BoxGeometry(0.01, 0.2, 0.22), panel, 0, 0.14, 0)
      put(new THREE.CylinderGeometry(0.008, 0.008, 0.1, 6), steel, 0, 0.08, 0)
      tip = 0.255
    }
    put(new THREE.CylinderGeometry(0.05, 0.05, 0.025, 12), tint.clone().lerp(new THREE.Color(white), 0.4), tip + 0.02, 0, 0, along)
    put(new THREE.TorusGeometry(0.05, 0.01, 6, 16), white, tip + 0.035, 0, 0, [0, Math.PI / 2, 0])
  })
  return { frame: merge(frame), modules: modules.length ? merge(modules) : new THREE.BufferGeometry() }
}
