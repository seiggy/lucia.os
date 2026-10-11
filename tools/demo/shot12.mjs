import { chromium } from 'playwright'
const browser = await chromium.launch({ executablePath: 'C:/Users/zackw/AppData/Local/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-win64/chrome-headless-shell.exe', args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] })
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } })
page.on('pageerror', e => console.log('pageerror:', String(e).slice(0, 400)))
page.on('console', m => { if (m.type() === 'error') console.log('err:', m.text().slice(0, 300)) })
await page.goto('http://127.0.0.1:5199/universe.html')
await page.waitForFunction(() => !!window.scene?.cosmos, null, { timeout: 120000 })
await page.waitForTimeout(3500)
const shots = {
  overview: () => null,
  front: () => { const s = window.scene, p = s.cosmos.byId.get(s.layout.gatewayId); const x = p.center[0]; s.camera.position.set(x - 34, 10, 16); s.controls.target.set(x + 2, 0, 0) },
  side: () => { const s = window.scene, p = s.cosmos.byId.get(s.layout.gatewayId); const x = p.center[0]; s.camera.position.set(x + 6, 16, 34); s.controls.target.set(x + 4, 0, 0) },
  exit: () => { const s = window.scene, e = s.cosmos.bodies.find(b => b.kind === 'exit'); const c = e.center, f = e.face; s.camera.position.set(c[0] + f[0] * 16 + 8, 7, c[2] + f[2] * 16 + 9); s.controls.target.set(c[0] - f[0] * 3, 0, c[2] - f[2] * 3) },
  relay: () => { const s = window.scene, r = s.cosmos.byId.get('wan'); const c = r.center; s.camera.position.set(c[0] - 14, 9, c[2] + 14); s.controls.target.set(c[0], 0, c[2] - 6) },
}
for (const [name, fn] of Object.entries(shots)) {
  if (name !== 'overview') await page.evaluate(`(() => { const s = window.scene; s.fly = null; s.follow = null; s.userMoved = true; (${fn.toString()})(); })()`)
  await page.waitForTimeout(1800)
  await page.screenshot({ path: process.argv[2] + '-' + name + '.png' })
}
console.log(JSON.stringify(await page.evaluate(() => ({ galaxy: window.scene.cosmos.galaxy, lan: window.scene.cosmos.lan, exits: window.scene.cosmos.bodies.filter(b => b.kind === 'exit').length }))))
await browser.close()

