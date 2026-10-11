import { chromium } from 'playwright'
const browser = await chromium.launch({ executablePath: 'C:/Users/zackw/AppData/Local/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-win64/chrome-headless-shell.exe', args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] })
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } })
await page.goto('http://127.0.0.1:5199/universe.html')
await page.waitForFunction(() => !!window.scene?.cosmos, null, { timeout: 90000 })
await page.waitForTimeout(2000)
console.log(JSON.stringify(await page.evaluate(() => {
  const s = window.scene, c = s.cosmos; s.quiet = true
  const moon = c.bodies.filter(b => b.kind === 'moon').map(m => ({ m, n: c.bodies.filter(b => b.parent === m.id && (b.kind === 'city' || b.kind === 'satellite')).length })).sort((a, b) => b.n - a.n)[0].m
  const at = s.worldAt.get(moon.id); s.fly = null; s.follow = null; s.userMoved = true
  s.camera.position.set(at.x + 4, at.y + 9, at.z + 13); s.controls.target.copy(at)
  return { moon: moon.node.label }
})))
await page.waitForTimeout(2500)
console.log(JSON.stringify(await page.evaluate(() => {
  const s = window.scene, c = s.cosmos, W = 1500, H = 900, V = s.projected
  s.camera.updateMatrixWorld()
  const proj = (v) => { V.copy(v).project(s.camera); return [Math.round((V.x + 1) / 2 * W), Math.round((1 - V.y) / 2 * H)] }
  const pos = s.links.geometry.getAttribute('position'), report = []
  for (let i = 0; i < pos.count; i += 2) {
    const A = new s.projected.constructor(pos.getX(i), pos.getY(i), pos.getZ(i)), B = new s.projected.constructor(pos.getX(i + 1), pos.getY(i + 1), pos.getZ(i + 1))
    const near = (v) => c.bodies.reduce((best, b) => { const d = s.worldAt.get(b.id).distanceTo(v); return d < best.d ? { d, k: b.kind } : best }, { d: 1e9, k: '' })
    const a = near(A), e = near(B); report.push({ A: proj(A), B: proj(B), da: +a.d.toFixed(2), ka: a.k, db: +e.d.toFixed(2), kb: e.k })
  }
  return report.filter(r => r.da > 0.05 || r.db > 0.05).slice(0, 10)
})))
await page.screenshot({ path: process.argv[2] })
await browser.close()
