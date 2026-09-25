async page => {
  const base = 'http://127.0.0.1:4175'
  const check = (value, message) => { if (!value) throw new Error(message) }
  const requests = []
  page.on('request', request => { if (request.url().includes('/api/host/domains/')) requests.push({ method: request.method(), url: request.url() }) })
  await page.request.get(base + '/__fixture?mode=owner')
  const activate = async mode => {
    await page.request.post(base + '/api/host/domains/__fixture/active', { headers: { 'X-CSRF-TOKEN': 'fixture-csrf-request' }, data: { mode } })
    await page.goto(base + '/#/settings/domains')
    await page.reload()
    await page.getByRole('heading', { name: 'Traefik routes' }).waitFor()
    await page.getByRole('cell', { name: /http:\/\/lucia-host:8080/ }).waitFor()
  }
  await activate('ready')
  check(await page.getByRole('list', { name: 'DNS setup progress' }).count() === 0, 'Activated domain still opens the setup wizard')
  check(await page.getByRole('heading', { name: 'Let’s Encrypt' }).isVisible(), 'Certificate jobs are missing')
  check(await page.getByText('Matches configuration', { exact: true }).count() === 3, 'Observed AdGuard records are missing')
  check(await page.getByText('Scheduled', { exact: true }).isVisible(), 'Renewal schedule is missing')
  const history = page.locator('details').filter({ has: page.getByText('Setup history', { exact: true }) })
  check(await history.getAttribute('open') === null, 'Setup history is open by default')
  for (const width of [320, 390, 820, 1440]) {
    await page.setViewportSize({ width, height: 900 })
    check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Overview overflows at ' + width)
  }
  await page.screenshot({ path: '.impeccable\\review\\domain-overview-desktop.png', fullPage: true })
  await page.setViewportSize({ width: 390, height: 844 })
  await page.screenshot({ path: '.impeccable\\review\\domain-overview-mobile.png', fullPage: true })
  await page.getByRole('button', { name: 'Replace Cloudflare token' }).click()
  await page.getByLabel('Account API token').fill('synthetic-unsaved-token')
  await page.getByRole('button', { name: 'Back to domain overview' }).click()
  await page.getByRole('heading', { name: 'Traefik routes' }).waitFor()
  await page.getByRole('button', { name: 'Refresh status' }).click()
  await page.getByRole('button', { name: 'Refresh status' }).waitFor()
  await activate('drift')
  check(await page.getByText('Conflict', { exact: true }).isVisible() && await page.getByText('Missing', { exact: true }).isVisible(), 'DNS drift is hidden')
  await activate('dns-unavailable')
  check(await page.getByText('Could not check', { exact: true }).count() === 3, 'Unavailable DNS is shown as empty or healthy')
  await activate('renewal-failed')
  check(await page.getByText('Needs attention', { exact: true }).isVisible(), 'Renewal failure is hidden')
  await activate('renewing')
  check(await page.getByText('Checking now', { exact: true }).isVisible(), 'Running certificate check is hidden')
  check(requests.filter(request => !request.url.includes('/__fixture/')).every(request => request.method === 'GET'), 'Overview performed a mutation')
  await page.request.get(base + '/__fixture?mode=owner')
  await page.reload()
  await page.getByRole('heading', { name: 'Connect AdGuard first' }).waitFor()
  return { activeOverview: true, routesAndRecords: true, certificateJobs: true, failureStates: true, readOnly: true, mobile: true, initialOnboardingPreserved: true }
}
