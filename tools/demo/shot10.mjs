import { chromium } from 'playwright'
const browser = await chromium.launch({ executablePath: 'C:/Users/zackw/AppData/Local/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-win64/chrome-headless-shell.exe', args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] })
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } })
await page.goto('http://127.0.0.1:5199/universe.html')
await page.waitForFunction(() => !!window.scene?.cosmos, null, { timeout: 90000 })
await page.waitForTimeout(3000)
await page.evaluate(() => { const s = window.scene, c = s.cosmos; s.fly = null; s.follow = null; s.userMoved = true; s.camera.position.set(8, 34, 52); s.controls.target.set(2, 0, 4) })
await page.waitForTimeout(1500)
await page.screenshot({ path: process.argv[2] })
await browser.close()

