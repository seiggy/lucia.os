async page => {
  const base = 'http://127.0.0.1:4175'
  const check = (value, message) => { if (!value) throw new Error(message) }
  const mutations = []
  page.on('request', request => {
    if (request.url().includes('/api/host/devices/') && request.method() !== 'GET')
      mutations.push({ url: request.url(), method: request.method(), csrf: request.headers()['x-csrf-token'] })
  })
  await page.request.get(base + '/__fixture?mode=owner&onboarding=rejected')
  await page.goto(base + '/#/devices')
  await page.getByRole('button', { name: 'Dismiss discovery', exact: true }).waitFor()
  for (const width of [320, 390, 820, 1440]) {
    await page.setViewportSize({ width, height: 900 })
    check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Rejected discovery overflows at ' + width)
  }
  await page.getByRole('button', { name: 'Dismiss discovery', exact: true }).click()
  await page.getByRole('heading', { name: 'No devices to show.' }).waitFor()
  check(await page.getByRole('heading', { name: /Synthetic UEFI server/ }).count() === 0, 'Dismissed record remains in the default list')
  await page.reload()
  await page.getByRole('heading', { name: 'No devices to show.' }).waitFor()
  await page.getByRole('button', { name: 'View dismissed (1)', exact: true }).click()
  await page.getByRole('button', { name: 'Restore to list', exact: true }).waitFor()
  check(await page.getByText('Discovery rejected', { exact: true }).isVisible(), 'Dismissal undid rejection')
  await page.screenshot({ path: '.impeccable\\review\\dismissed-discoveries-desktop.png', fullPage: true })
  await page.setViewportSize({ width: 390, height: 844 })
  check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Dismissed view overflows on mobile')
  await page.screenshot({ path: '.impeccable\\review\\dismissed-discoveries-mobile.png', fullPage: true })
  await page.getByRole('button', { name: 'Restore to list', exact: true }).click()
  await page.getByRole('heading', { name: 'No dismissed discoveries.' }).waitFor()
  await page.getByRole('button', { name: 'Back to devices', exact: true }).click()
  await page.getByRole('button', { name: 'Dismiss discovery', exact: true }).waitFor()
  check(await page.getByText('Discovery rejected', { exact: true }).isVisible(), 'Restoring list visibility approved the device')
  check(mutations.length === 2 && mutations[0].url.endsWith('/dismiss') && mutations[1].url.endsWith('/restore')
    && mutations.every(request => request.method === 'POST' && request.csrf === 'fixture-csrf-request'), 'Visibility actions have wrong scope or lack CSRF')
  await page.request.get(base + '/__fixture?mode=owner&onboarding=discovered')
  await page.reload()
  await page.getByText('Needs your approval', { exact: true }).waitFor()
  check(await page.getByRole('button', { name: 'Dismiss discovery', exact: true }).count() === 0, 'Pending discovery can be hidden without rejection')
  return { persistentAcrossReload: true, reversible: true, rejectionPreserved: true, ownerCsrf: true, mobile: true }
}
