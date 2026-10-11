// Builds the universe jumpgate from the purchased Commercial Jumpgate AX13 pack (CGTrader). The pack's licence forbids redistributing it,
// and this repo is public, so the output is gitignored: run this locally before building the frontend.
//   node build-jumpgate.mjs "D:\3d-models\CommercialJumpgateAX13FBX+OBJ"
// Writes src/frontend/public/universe/models/jumpgate/: gate.glb (outer and inner ring, sized so the opening has radius 1, with shared
// normal, occlusion/roughness/metalness and glow maps) plus one albedo per colour scheme, which the scene picks between per gate.
import { readFileSync, mkdirSync, rmSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { Document, NodeIO } from '@gltf-transform/core'
import { weld } from '@gltf-transform/functions'
import sharp from 'sharp'

const pack = process.argv[2]
if (!pack) { console.error('usage: node build-jumpgate.mjs <pack folder>'); process.exit(1) }
const name = 'CommercialJumpgateAX13', size = 1024
const meshes = join(pack, 'MeshesOBJ', name), maps = join(pack, 'Textures', name)
const out = join(dirname(fileURLToPath(import.meta.url)), '../../src/frontend/public/universe/models/jumpgate')

// The pack's Static mesh is just both rings merged, for engines that won't animate them, so only the two rings are used.
// A plain OBJ reader: the pack's files are triangles and quads with positions, uvs and normals.
const readObj = file => {
  const v = [], t = [], n = [], pos = [], uv = [], nor = []
  for (const line of readFileSync(file, 'utf8').split('\n')) {
    const p = line.trim().split(/\s+/)
    if (p[0] === 'v') v.push(p.slice(1, 4).map(Number))
    else if (p[0] === 'vt') t.push(p.slice(1, 3).map(Number))
    else if (p[0] === 'vn') n.push(p.slice(1, 4).map(Number))
    else if (p[0] === 'f') {
      const corners = p.slice(1).map(c => c.split('/').map(i => Number(i) - 1))
      for (let k = 1; k < corners.length - 1; k++) for (const [a, b, c] of [corners[0], corners[k], corners[k + 1]]) {
        pos.push(...v[a]); const [s, w] = t[b] ?? [0, 0]; uv.push(s, 1 - w); nor.push(...(n[c] ?? [0, 0, 1]))
      }
    }
  }
  return { pos: new Float32Array(pos), uv: new Float32Array(uv), nor: new Float32Array(nor) }
}
const parts = Object.fromEntries(['OuterRing', 'InnerRing'].map(part => [part, readObj(join(meshes, `${name}${part}.obj`))]))

// Centre on the gate and scale so the inner ring's opening has radius 1; the gate faces +z.
const all = Object.values(parts).flatMap(part => [...part.pos])
const centre = [0, 1, 2].map(axis => { let lo = Infinity, hi = -Infinity; for (let i = axis; i < all.length; i += 3) { lo = Math.min(lo, all[i]); hi = Math.max(hi, all[i]) } return (lo + hi) / 2 })
const inner = parts.InnerRing.pos
let opening = Infinity
for (let i = 0; i < inner.length; i += 3) opening = Math.min(opening, Math.hypot(inner[i] - centre[0], inner[i + 1] - centre[1]))
for (const part of Object.values(parts)) for (let i = 0; i < part.pos.length; i++) part.pos[i] = (part.pos[i] - centre[i % 3]) / opening

const webp = (input, options = {}) => sharp(input).resize(size, size).webp({ quality: 82, ...options }).toBuffer()
const raw = async file => (await sharp(join(maps, file)).resize(size, size).greyscale().raw().toBuffer({ resolveWithObject: true })).data
const [ao, rough, metal] = await Promise.all([`${name}AO.png`, `${name}Roughness.png`, `${name}PBRMetalness.png`].map(raw))
const orm = Buffer.alloc(size * size * 3)
for (let i = 0; i < size * size; i++) { orm[i * 3] = ao[i]; orm[i * 3 + 1] = rough[i]; orm[i * 3 + 2] = metal[i] }

const doc = new Document(), buffer = doc.createBuffer()
const texture = (label, image) => doc.createTexture(label).setImage(image).setMimeType('image/webp')
const material = doc.createMaterial('gate')
  .setBaseColorTexture(texture('albedo', await webp(join(maps, 'Albedo', `${name}GreyAlbedo.png`))))
  .setNormalTexture(texture('normal', await webp(join(maps, `${name}Normal.png`), { lossless: true })))
  .setMetallicRoughnessTexture(texture('orm', await sharp(orm, { raw: { width: size, height: size, channels: 3 } }).webp({ quality: 90 }).toBuffer()))
  .setOcclusionTexture(doc.getRoot().listTextures().at(-1))
  .setRoughnessFactor(1).setMetallicFactor(1)
  .setEmissiveTexture(texture('glow', await webp(join(maps, `${name}Illumination.png`)))).setEmissiveFactor([1, 1, 1])
const scene = doc.createScene('gate')
for (const [part, data] of Object.entries(parts)) {
  const accessor = (array, type) => doc.createAccessor().setArray(array).setType(type).setBuffer(buffer)
  const primitive = doc.createPrimitive().setMaterial(material)
    .setAttribute('POSITION', accessor(data.pos, 'VEC3')).setAttribute('NORMAL', accessor(data.nor, 'VEC3')).setAttribute('TEXCOORD_0', accessor(data.uv, 'VEC2'))
  scene.addChild(doc.createNode(part).setMesh(doc.createMesh(part).addPrimitive(primitive)))
}

rmSync(out, { recursive: true, force: true })
mkdirSync(out, { recursive: true })
await doc.transform(weld())
await new NodeIO().write(join(out, 'gate.glb'), doc)
// The orange accent panels are baked into every albedo; the pack's ID map marks them in blue, so a mask lets the scene repaint them per gate.
const id = await sharp(join(maps, `${name}ID.png`)).removeAlpha().raw().toBuffer({ resolveWithObject: true })
const mask = Buffer.alloc(id.info.width * id.info.height)
for (let i = 0; i < mask.length; i++) { const [r, g, b] = id.data.subarray(i * 3, i * 3 + 3); mask[i] = b > 180 && r < 120 && g < 120 ? 255 : 0 }
await sharp(mask, { raw: { width: id.info.width, height: id.info.height, channels: 1 } }).resize(size, size).webp({ lossless: true }).toFile(join(out, 'panels.webp'))
for (const scheme of ['Blue', 'Green', 'Grey', 'Red', 'White'])
  await sharp(join(maps, 'Albedo', `${name}${scheme}Albedo.png`)).resize(size, size).webp({ quality: 82 }).toFile(join(out, `${scheme.toLowerCase()}.webp`))
console.log(`opening ${opening.toFixed(1)} units, outer radius ${(Math.max(...[...parts.OuterRing.pos].filter((_, i) => i % 3 < 2).map(Math.abs))).toFixed(2)}, ${(all.length / 9) | 0} triangles`)
