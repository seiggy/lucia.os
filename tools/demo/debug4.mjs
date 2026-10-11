import { chromium } from 'playwright'
const browser = await chromium.launch({ executablePath: 'C:/Users/zackw/AppData/Local/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-win64/chrome-headless-shell.exe', args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] })
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } })
await page.goto('http://127.0.0.1:5199/universe.html')
await page.waitForFunction(() => !!window.scene?.cosmos, null, { timeout: 90000 })
await page.waitForTimeout(2000)
await page.evaluate(() => { const s = window.scene, c = s.cosmos; s.quiet = true
  const moons = c.bodies.filter(b => b.kind === 'moon' && b.node.health !== 'ok')
  window.__off = moons.map(m => m.node.label + ' ' + m.node.health)
  const m = c.bodies.find(b => b.kind === 'moon' && b.node.label.startsWith('Unsorted')) ?? moons[0] ?? c.bodies.find(b => b.kind === 'moon')
  const at = s.worldAt.get(m.id); s.fly = null; s.follow = null; s.userMoved = true
  // look at the moon from the side away from the sun
  const away = at.clone().normalize().multiplyScalar(1); s.camera.position.copy(at).add(away.multiplyScalar(-8)).add({ x: 0, y: 3, z: 0 }); s.controls.target.copy(at); window.__m = m.node.label })
await page.waitForTimeout(2500)
console.log(await page.evaluate(() => window.__m + ' | ' + JSON.stringify(window.__off)))
await page.screenshot({ path: process.argv[2] })
await browser.close()
