import { chromium } from 'playwright'
const browser = await chromium.launch({ executablePath: 'C:/Users/zackw/AppData/Local/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-win64/chrome-headless-shell.exe', args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] })
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } })
page.on('pageerror', e => console.log('pageerror:', String(e).slice(0, 400)))
page.on('console', m => { if (m.type() === 'error') console.log('err:', m.text().slice(0, 300)) })
await page.goto('http://127.0.0.1:5199/universe.html')
await page.waitForFunction(() => !!window.scene?.cosmos, null, { timeout: 150000 })
await page.waitForTimeout(4000)
const view = (pick, side) => `(() => { const s = window.scene, P = s.portals.find(${pick}); const b = s.cosmos.byId.get(P.body), at = s.worldAt.get(P.body), sys = s.systems.get(s.systemOf.get(P.body)); const f = new s.camera.position.constructor(...b.face).normalize(); const d = f.clone().add(new s.camera.position.constructor(${side}, 0.35, 0)).normalize(); s.flyGalactic(s.toGalaxy(at, sys.center), d, b.radius * 7, null, false) })`
const shots = {
  home: () => {}, exit: () => {}, side: () => {},
}
const code = { home: view("p => p.body === 'wan' || s.cosmos.byId.get(p.body).kind === 'portal'", 0.5), exit: view("p => s.cosmos.byId.get(p.body).kind === 'exit'", -0.4), side: view("p => s.cosmos.byId.get(p.body).kind === 'portal'", 2.5) }
await page.waitForFunction(() => window.scene.portals.every(p => p.group.userData.rings), null, { timeout: 60000 }).catch(() => console.log('gate not loaded'))
for (const name of Object.keys(shots)) {
  await page.evaluate(`(() => { const s = window.scene; s.fly = null; s.follow = null; s.userMoved = true; (${code[name]})(); })()`)
  await page.waitForTimeout(2500)
  await page.screenshot({ path: process.argv[2] + '-' + name + '.png' })
}
await browser.close()