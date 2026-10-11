import { chromium } from 'playwright'
const browser = await chromium.launch({ executablePath: 'C:/Users/zackw/AppData/Local/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-win64/chrome-headless-shell.exe', args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] })
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } })
await page.goto('http://127.0.0.1:5199/universe.html')
await page.waitForFunction(() => !!window.scene?.cosmos, null, { timeout: 90000 })
await page.waitForTimeout(2500)
console.log(JSON.stringify(await page.evaluate(() => {
  const s = window.scene, c = s.cosmos
  const moon = c.bodies.filter(b => b.kind === 'moon').map(m => ({ m, n: c.bodies.filter(b => b.parent === m.id && (b.kind === 'city' || b.kind === 'satellite')).length })).sort((a, b) => b.n - a.n)[0].m
  const at = s.worldAt.get(moon.id); s.fly = null; s.follow = { id: moon.id, last: at.clone() }; s.userMoved = true
  s.camera.position.set(at.x + 4, at.y + 9, at.z + 13); s.controls.target.copy(at); s.controls.update(); s.camera.updateMatrixWorld()
  const W = 1500, H = 900, V = s.projected, res = []
  const proj = (v) => { V.copy(v).project(s.camera); return [Math.round((V.x + 1) / 2 * W), Math.round((1 - V.y) / 2 * H), +V.z.toFixed(4)] }
  const pos = s.links.geometry.getAttribute('position')
  for (const b of c.bodies) { const p = proj(s.worldAt.get(b.id)); if (p[0] > 380 && p[0] < 640 && p[1] > 650 && p[1] < 860) res.push({ body: b.kind + ':' + b.node.label, p }) }
  // links with a vertex in that box
  const links = []
  for (let i = 0; i < pos.count; i += 2) { const a = proj(new s.projected.constructor(pos.getX(i), pos.getY(i), pos.getZ(i))), e = proj(new s.projected.constructor(pos.getX(i + 1), pos.getY(i + 1), pos.getZ(i + 1))); if ((a[0] > 380 && a[0] < 640 && a[1] > 650 && a[1] < 860) || (e[0] > 380 && e[0] < 640 && e[1] > 650 && e[1] < 860)) links.push({ a, e }) }
  return { moon: moon.node.label, bodiesInBox: res, linksInBox: links.slice(0, 8), nLinks: pos.count / 2 }
})))
await browser.close()
