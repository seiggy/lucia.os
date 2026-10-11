import { chromium } from 'playwright'
const browser = await chromium.launch({ executablePath: 'C:/Users/zackw/AppData/Local/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-win64/chrome-headless-shell.exe', args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] })
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } })
page.on('pageerror', e => console.log('pageerror:', String(e).slice(0, 300)))
await page.goto('http://127.0.0.1:5199/universe.html')
await page.waitForFunction(() => !!window.scene?.cosmos, null, { timeout: 90000 })
console.log(JSON.stringify(await page.evaluate(() => {
  const s = window.scene, c = s.cosmos, groups = new Map()
  for (const b of c.bodies) { if (!b.parent || b.kind === 'city') continue; const k = b.parent; groups.set(k, [...(groups.get(k) ?? []), b]) }
  const worst = { gap: 1e9, a: '', b: '' }, vis = { pods: 0 }
  for (let t = 0; t < 600; t += 1.5) {
    s.positions(t)
    for (const list of groups.values()) for (let i = 0; i < list.length; i++) for (let j = i + 1; j < list.length; j++) {
      const A = list[i], B = list[j], d = s.worldAt.get(A.id).distanceTo(s.worldAt.get(B.id)), size = (x) => x.kind === 'satellite' ? 0.4 : x.kind === 'pod' ? 0.45 : x.radius
      const gap = d - size(A) - size(B)
      if (gap < worst.gap) { worst.gap = gap; worst.a = A.kind + ':' + A.node.label; worst.b = B.kind + ':' + B.node.label }
    }
  }
  const planets = c.bodies.filter(b => b.kind === 'planet'); let pw = 1e9; const movers = c.bodies.filter(b => ['moon','station','depot'].includes(b.kind)); let xw = 1e9, xa = ''; for (let t = 0; t < 2400; t += 4) { s.positions(t); for (let i = 0; i < movers.length; i++) for (let j = i + 1; j < movers.length; j++) { const A = movers[i], B = movers[j]; if (A.parent === B.parent) continue; const g = s.worldAt.get(A.id).distanceTo(s.worldAt.get(B.id)) - A.radius - B.radius; if (g < xw) { xw = g; xa = A.node.label + ' / ' + B.node.label } } }
  for (let t = 0; t < 3000; t += 10) { s.positions(t); for (const A of planets) for (const B of planets) if (A.id < B.id) pw = Math.min(pw, s.worldAt.get(A.id).distanceTo(s.worldAt.get(B.id)) - A.radius - B.radius) }
  return { worstSiblingGap: worst, worstPlanetGap: pw, crossPlanetMoonGap: xw, crossPair: xa, orbits: c.rings, counts: [...groups.values()].map(l => l.length).sort((a, b) => b - a).slice(0, 5) }
})))
await browser.close()

