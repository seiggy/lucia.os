async page => {
  const base = 'http://127.0.0.1:4175'
  const check = (value, message) => { if (!value) throw new Error(message) }
  await page.request.get(base + '/__fixture?mode=owner')
  await page.setViewportSize({ width: 1280, height: 900 })
  await page.goto(base + '/#/')
  await page.reload()
  await page.getByRole('heading', { name: 'Your lab, within reach.' }).waitFor()
  await page.getByText('Ready to respond', { exact: true }).waitFor()
  const body = await page.locator('body').innerText()
  for (const removed of ['Backups are paused', 'Home server', 'Media server', 'Backup drive', 'Checking for updates', 'Approve preview fix', 'Reset preview', 'Sample snapshot'])
    check(!body.includes(removed), 'Retired simulation is still visible: ' + removed)
  check(await page.getByRole('link', { name: 'Open playground', exact: true }).count() >= 1, 'Live AI entry point missing')
  check(await page.locator('.nav-count').count() === 0, 'Invented task count remains')
  const label = async () => page.evaluate(() => {
    const note = document.createElement('div')
    note.textContent = 'Synthetic API fixture for UI checks; production reads live status'
    note.style.cssText = 'position:fixed;bottom:0;left:0;right:0;padding:4px;background:#e9edf4;color:#202839;font:12px sans-serif;text-align:center;z-index:100'
    document.body.append(note)
  })
  await label()
  await page.screenshot({ path: '.impeccable/screenshots/dashboard-home-clean-desktop.png', fullPage: true })
  for (const width of [320, 390, 768, 1280]) {
    await page.setViewportSize({ width, height: 900 })
    check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Horizontal overflow at ' + width)
  }
  await page.setViewportSize({ width: 390, height: 844 })
  await page.screenshot({ path: '.impeccable/screenshots/dashboard-home-clean-mobile.png', fullPage: true })
  await page.goto(base + '/#/devices')
  await page.getByRole('heading', { name: 'No devices have been reported.' }).waitFor()
  check(await page.getByRole('button', { name: 'Add hardware', exact: true }).isDisabled(), 'Unconfigured boot service can be opened')
  check(await page.locator('.device-tile').count() === 0, 'Fake device inventory remains')
  await page.goto(base + '/#/tasks')
  await page.getByRole('heading', { name: 'No installation tasks have been reported.' }).waitFor()
  check(await page.locator('button').filter({ hasText: 'Approve' }).count() === 0, 'Fake approval control remains')
  for (const route of ['#/devices/spark', '#/devices/home-server', '#/tasks/backup-repair', '#/tasks/update-check']) {
    await page.goto(base + '/' + route)
    await page.getByRole('heading', { name: 'This page is not available.' }).waitFor()
  }
  await page.goto(base + '/#/settings')
  await page.getByRole('heading', { name: 'Appearance', exact: true }).waitFor()
  check(await page.locator('#scenario').count() === 0, 'Preview scenario selector remains')
  await page.getByRole('button', { name: 'Dark', exact: true }).click()
  check(await page.locator('html').getAttribute('data-scheme') === 'dark', 'Dark theme stopped working')
  await page.getByRole('button', { name: 'Meadow', exact: true }).click()
  await page.reload()
  check(await page.getByRole('button', { name: 'Meadow', exact: true }).getAttribute('aria-pressed') === 'true', 'Appearance choices were not preserved')
  await page.goto(base + '/#/')
  await page.getByText('Ready to respond', { exact: true }).waitFor()
  await label()
  await page.screenshot({ path: '.impeccable/screenshots/dashboard-home-clean-dark.png', fullPage: true })
  await page.request.get(base + '/__fixture?mode=owner&models=unavailable')
  await page.reload()
  await page.getByText('Status unavailable', { exact: true }).waitFor()
  check(await page.getByText('Ready to respond', { exact: true }).count() === 0, 'Failed status was reported healthy')
  await page.request.get(base + '/__fixture?mode=owner&models=empty')
  await page.getByRole('button', { name: 'Check again' }).click()
  await page.getByText('No chat model loaded', { exact: true }).waitFor()
  await page.goto(base + '/#/ai')
  await page.getByRole('link', { name: 'Playground', exact: true }).first().waitFor()
  return { noSyntheticActivity: true, retiredRoutes: true, realStatusAndErrors: true, appearancePreserved: true, responsive: true }
}
