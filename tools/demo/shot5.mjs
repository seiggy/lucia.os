import { chromium } from 'playwright'
const browser = await chromium.launch({ executablePath: 'C:/Users/zackw/AppData/Local/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-win64/chrome-headless-shell.exe', args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] })
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } })
page.on('pageerror', e => console.log('pageerror:', String(e).slice(0, 300)))
page.on('console', m => { if (m.type() === 'error') console.log('err:', m.text().slice(0, 300)) })
await page.goto('http://127.0.0.1:5199/universe.html')
await page.waitForFunction(() => !!window.scene?.cosmos, null, { timeout: 90000 })
await page.waitForTimeout(2500)
console.log(await page.evaluate(() => { const s = window.scene, c = s.cosmos
  const moon = c.bodies.filter(b => b.kind === 'station').sort((a, b) => b.radius - a.radius)[0]
  const at = s.worldAt.get(moon.id); s.fly = null; s.follow = { id: moon.id, last: at.clone() }; s.userMoved = true
  s.camera.position.set(at.x + moon.radius * 0.9, at.y + moon.radius * 0.5, at.z + moon.radius * 2.6); s.controls.target.copy(at); return moon.node.label }))
await page.waitForTimeout(1500)
await page.screenshot({ path: process.argv[2] })
await browser.close()


