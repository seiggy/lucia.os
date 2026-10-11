import { chromium } from 'playwright'
const browser = await chromium.launch({ executablePath: 'C:/Users/zackw/AppData/Local/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-win64/chrome-headless-shell.exe', args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] })
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } })
page.on('pageerror', e => console.log('pageerror:', String(e).slice(0, 400)))
page.on('console', m => { if (m.type() === 'error') console.log('err:', m.text().slice(0, 300)) })
await page.goto('http://127.0.0.1:5199/universe.html')
await page.waitForFunction(() => !!window.scene?.cosmos, null, { timeout: 150000 })
await page.waitForTimeout(4000)
const shots = {
  galaxy: () => { window.scene.overview(false) },
  mid: () => { const s = window.scene, h = s.systems.get('~home'); s.flyGalactic(h.center.clone(), new s.camera.position.constructor(0, 0.8, 0.6).normalize(), 3e6, null, false) },
  home: () => { window.scene.home(false) },
  edge: () => { const s = window.scene, h = s.systems.get('~home'); s.flyGalactic(h.center.clone(), new s.camera.position.constructor(1, 0.04, 0.2).normalize(), s.frame3(h.size), null, false) },
  system: () => { const s = window.scene, sys = [...s.systems.values()].find(x => x.id !== '~home'); s.flyGalactic(sys.center.clone(), new s.camera.position.constructor(0, 0.8, 0.6).normalize(), s.frame3(sys.size), null, false) },
}
for (const [name, fn] of Object.entries(shots)) {
  await page.evaluate(`(() => { const s = window.scene; s.fly = null; s.follow = null; s.userMoved = true; (${fn.toString()})(); })()`)
  await page.waitForTimeout(2200)
  await page.screenshot({ path: process.argv[2] + '-' + name + '.png' })
}
await browser.close()
