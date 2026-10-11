import { chromium } from 'playwright'
const browser = await chromium.launch({ executablePath: 'C:/Users/zackw/AppData/Local/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-win64/chrome-headless-shell.exe', args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] })
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } })
page.on('pageerror', e => console.log('pageerror:', String(e).slice(0, 500)))
page.on('console', m => { if (m.type() === 'error') console.log('err:', m.text().slice(0, 400)) })
const t0 = Date.now()
await page.goto('http://127.0.0.1:5199/universe.html')
await page.waitForFunction(() => !!window.scene?.cosmos, null, { timeout: 120000 }).then(() => console.log('ready in', Date.now() - t0)).catch(() => console.log('timeout'))
await browser.close()
