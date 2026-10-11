import { chromium } from 'playwright'
const browser = await chromium.launch({ executablePath: 'C:/Users/zackw/AppData/Local/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-win64/chrome-headless-shell.exe', args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] })
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } })
await page.goto('http://127.0.0.1:5199/universe.html')
await page.waitForFunction(() => !!window.scene?.cosmos, null, { timeout: 90000 })
await page.waitForTimeout(2000)
const planets = await page.evaluate(() => { const s = window.scene; s.quiet = true; return s.cosmos.bodies.filter(b => b.kind === 'planet').map(b => b.id) })
const bad = []
for (const id of planets) {
  await page.evaluate((id) => { const s = window.scene, at = s.worldAt.get(id); s.fly = null; s.follow = null; s.userMoved = true
    s.camera.position.set(at.x + 6, at.y + 22, at.z + 26); s.controls.target.copy(at); s.controls.update(); s.links.visible = false; document.querySelectorAll('.labmap-plate').forEach(e => e.style.display = 'none') }, id)
  await page.waitForTimeout(1200)
  const shot = (await page.screenshot()).toString('base64')
  const res = await page.evaluate(async (b64) => {
    const s = window.scene, c = s.cosmos, W = 1500, H = 900, V = s.projected
    const img = new Image(); img.src = 'data:image/png;base64,' + b64; await img.decode()
    const cv = document.createElement('canvas'); cv.width = W; cv.height = H; const g = cv.getContext('2d'); g.drawImage(img, 0, 0)
    const lum = (x, y) => { const d = g.getImageData(Math.max(0, x - 1), Math.max(0, y - 1), 3, 3).data; let m = 0; for (let i = 0; i < d.length; i += 4) m = Math.max(m, d[i] * .3 + d[i + 1] * .59 + d[i + 2] * .11); return m }
    // local background = median of a ring sampled around the point
    const contrast = (x, y) => { let best = 0; const bg = lum(Math.min(W - 2, x + 40), y) ; for (let dx = -8; dx <= 8; dx += 4) for (let dy = -8; dy <= 8; dy += 4) best = Math.max(best, Math.abs(lum(x + dx, y + dy) - bg)); return best }
    s.camera.updateMatrixWorld()
    const pos = s.links.geometry.getAttribute('position'), out = []
    for (let i = 0; i < pos.count; i++) {
      const v = new V.constructor(pos.getX(i), pos.getY(i), pos.getZ(i)); const w = v.clone(); w.project(s.camera)
      const x = Math.round((w.x + 1) / 2 * W), y = Math.round((1 - w.y) / 2 * H)
      if (x < 20 || x > W - 60 || y < 20 || y > H - 20 || w.z > 1) continue
      const body = c.bodies.reduce((best, b) => { const d = s.worldAt.get(b.id).distanceTo(v); return d < best.d ? { d, b } : best }, { d: 1e9, b: null })
      out.push({ x, y, k: body.b.kind, name: body.b.node.label, contrast: Math.round(contrast(x, y)), health: body.b.node.health })
    }
    return out
  }, shot)
  bad.push(...res.filter(r => r.contrast < 10))
}
console.log(JSON.stringify(bad.slice(0, 20)), bad.length)
await browser.close()
