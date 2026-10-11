import { chromium } from 'playwright'
const browser = await chromium.launch({ executablePath: 'C:/Users/zackw/AppData/Local/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-win64/chrome-headless-shell.exe', args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] })
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } })
await page.goto('http://127.0.0.1:5199/universe.html')
await page.waitForFunction(() => !!window.scene?.cosmos, null, { timeout: 90000 })
await page.waitForTimeout(2500)
console.log(JSON.stringify(await page.evaluate(() => {
  const s = window.scene, c = s.cosmos, out = { air: 0, problems: [], moonsNoSlot: [], satsTotal: c.bodies.filter(b => b.kind === 'satellite').length }
  const m = new s.dummy.matrix.constructor()
  for (const lane of s.lanes) {
    if (!lane.air) continue
    out.air++
    const sat = c.byId.get(lane.stops[0]), moon = c.byId.get(lane.stops[1])
    if (!sat || !moon) { out.problems.push({ id: lane.id, why: 'missing', sat: !!sat, moon: !!moon }); continue }
    if (sat.kind !== 'satellite' || moon.kind !== 'moon') out.problems.push({ id: lane.id, why: 'kinds', a: sat.kind, b: moon.kind })
    const slot = s.slots.get(moon.id)
    if (slot === undefined) { out.moonsNoSlot.push(moon.node.label); continue }
    s.worlds.getMatrixAt(slot, m)
    const dx = s.worldAt.get(moon.id).distanceTo({ x: m.elements[12], y: m.elements[13], z: m.elements[14], distanceTo: undefined } && new s.dummy.position.constructor(m.elements[12], m.elements[13], m.elements[14]))
    if (dx > 0.01) out.problems.push({ id: lane.id, why: 'moon instance off', dx })
  }
  const dupes = {}; for (const l of s.lanes) if (l.air) dupes[l.id] = (dupes[l.id] ?? 0) + 1
  out.multi = Object.entries(dupes).filter(([, n]) => n > 1).length
  out.linkVerts = s.links.geometry.getAttribute('position').count
  out.moonWithSats = c.bodies.filter(b => b.kind === 'moon').length
  // lanes whose satellites are not parented to this moon
  out.mismatch = s.lanes.filter(l => l.air).filter(l => c.byId.get(l.stops[0])?.parent !== l.stops[1]).length
  return out
})))
await browser.close()
