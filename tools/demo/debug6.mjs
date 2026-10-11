import { chromium } from 'playwright'
const browser = await chromium.launch({ executablePath: 'C:/Users/zackw/AppData/Local/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-win64/chrome-headless-shell.exe', args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] })
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } })
await page.goto('http://127.0.0.1:5199/universe.html')
await page.waitForFunction(() => !!window.scene?.cosmos && Object.keys(window.scene.kits).length > 3, null, { timeout: 90000 })
await page.waitForTimeout(3000)
console.log(JSON.stringify(await page.evaluate(() => { const s = window.scene, n = []; s.world.traverse(o => { if (o.isLineSegments) n.push(o.geometry.getAttribute('position')?.count ?? 0) }); return { lineSegmentMeshes: n, kitsLoaded: Object.keys(s.kits).length, timed: s.timed.length } })))
await browser.close()
