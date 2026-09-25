async page => {
  const base = 'http://127.0.0.1:4175'
  const check = (value, message) => { if (!value) throw new Error(message) }
  const mutations = []
  page.on('request', request => { if (request.method() !== 'GET') mutations.push(request.url()) })
  await page.request.get(base + '/__fixture?mode=owner&onboarding=installable')
  await page.goto(base + '/#/devices')
  await page.reload()
  await page.getByText('Review installation or reject this device', { exact: true }).click()
  await page.getByLabel('GitHub username', { exact: true }).fill('synthetic-owner')
  await page.getByRole('button', { name: 'Import from GitHub', exact: true }).click()
  const selected = page.getByRole('radio')
  await selected.waitFor()
  check(await page.getByRole('textbox', { name: 'SSH public key', exact: true }).inputValue() === '', 'A GitHub key was selected without review')
  await selected.check()
  check((await page.getByRole('textbox', { name: 'SSH public key', exact: true }).inputValue()).startsWith('ssh-ed25519 AAAA'), 'Selected public key was not pinned into approval')
  check(await page.getByRole('button', { name: 'Erase selected disk and install', exact: true }).isDisabled(), 'Key import authorized installation')
  for (const width of [320, 390, 820, 1440]) {
    await page.setViewportSize({ width, height: 1000 })
    check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Recovery-key picker overflow at ' + width)
  }
  await page.screenshot({ path: '.impeccable\\review\\github-recovery-desktop.png', fullPage: true })
  await page.setViewportSize({ width: 390, height: 844 })
  await page.screenshot({ path: '.impeccable\\review\\github-recovery-mobile.png', fullPage: true })
  check(mutations.length === 0, 'Importing a public key performed an installation or configuration mutation')
  return { noOAuth: true, explicitSelection: true, pinnedKey: true, noInstallSideEffects: true, responsive: true }
}
