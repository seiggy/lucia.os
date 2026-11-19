// With Vite on 127.0.0.1:5199, run: playwright-cli run-code --filename=src\frontend\tests\navigation.browser.mjs
async page => {
  const assert = (condition, message) => { if (!condition) throw new Error(message) }
  const failures = []
  const check = async (name, run) => {
    try { await run() } catch (error) { failures.push(`${name}: ${error.message}`) }
  }
  const base = 'http://127.0.0.1:5199/'
  const mapResponse = await page.request.get(base + '_fixtures/map.json')
  const liveResponse = await page.request.get(base + '_fixtures/live.json')
  assert(mapResponse.ok() && liveResponse.ok(), 'Synthetic map fixtures are unavailable')
  const mapFixture = await mapResponse.json()
  const liveFixture = await liveResponse.json()
  let isOwner = true
  await page.route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/auth/session') return route.fulfill({ json: {
      enabled: true, authenticated: true, canAccess: true, isOwner, username: 'fixture-owner',
      displayName: 'Fixture owner', csrfToken: 'fixture-only',
    } })
    if (path === '/api/host/telemetry') return route.fulfill({ json: {
      enabled: true, state: 'Starting', message: 'Collecting readings.', sampleIntervalSeconds: 10,
      retentionSeconds: 3600, latest: null, history: [],
    } })
    if (path === '/api/assistant/skills') return route.fulfill({ json: { skills: [], needsReview: 3 } })
    if (path === '/api/host/lab-map') return route.fulfill({ json: mapFixture })
    if (path === '/api/host/lab-map/live') return route.fulfill({ json: liveFixture })
    return route.fulfill({ status: 404, json: { error: { message: 'No fixture for this request.' } } })
  })
  await page.route('**/v1/**', route => route.fulfill({ json: { data: [] } }))
  await page.emulateMedia({ reducedMotion: 'reduce', colorScheme: 'light' })
  await page.setViewportSize({ width: 1600, height: 900 })
  await page.goto(base)
  await page.evaluate(() => {
    localStorage.setItem('lucia.portal.navigation.collapsed.v1', 'false')
    localStorage.setItem('lucia.assistant.dock.v1', JSON.stringify({ open: false, side: 'right' }))
    localStorage.setItem('lucia.preview.appearance.v1', JSON.stringify({ appearance: 'light', theme: 'ocean', customAccent: '#285bdd' }))
  })
  await page.reload()
  await page.getByRole('navigation', { name: 'Main navigation' }).waitFor()
  const nav = () => page.getByRole('navigation', { name: 'Main navigation' })
  const menu = () => page.getByRole('button', { name: 'Open navigation' })
  const sidebar = () => page.locator('.portal-sidebar')

  await check('desktop groups, routes and badge', async () => {
    for (const name of ['Lab', 'Apps', 'Network', 'Local AI', 'Settings'])
      assert(await nav().getByRole('button', { name, exact: true }).isVisible(), `${name} is missing`)
    await nav().getByRole('button', { name: /Assistant.*3 skills to review/ }).waitFor()
    await nav().getByRole('button', { name: 'Apps', exact: true }).click()
    for (const name of ['Your apps', 'Catalog', 'Containers & ports', 'Backups', 'Container registries'])
      assert(await nav().getByRole('link', { name, exact: true }).isVisible(), `${name} is missing`)
    await nav().getByRole('link', { name: 'Backups', exact: true }).click()
    await page.waitForURL('**/#/apps/backups')
    assert(await nav().getByRole('link', { name: 'Backups', exact: true }).getAttribute('aria-current') === 'page', 'Backups is not current')
    assert(await page.getByRole('navigation', { name: 'Breadcrumb' }).getByText('Backups').isVisible(), 'Breadcrumb is missing')
    await nav().getByRole('button', { name: 'Apps', exact: true }).click()
    await page.evaluate(() => { location.hash = '#/apps/containers' })
    await nav().getByRole('link', { name: 'Containers & ports', exact: true }).waitFor()
    assert(await nav().getByRole('button', { name: 'Apps', exact: true }).getAttribute('aria-expanded') === 'true', 'A route in the same workspace did not reopen its group')
  })
  await check('collapsed preference and search focus', async () => {
    await page.getByRole('button', { name: 'Collapse navigation' }).click()
    await page.reload()
    assert(await sidebar().getAttribute('data-collapsed') === 'true', 'Collapsed preference was not restored')
    await nav().getByRole('button', { name: /Assistant.*3 skills to review/ }).waitFor()
    await nav().getByRole('button', { name: 'Network', exact: true }).click()
    assert(await sidebar().getAttribute('data-collapsed') === 'false', 'A workspace did not expand the rail')
    const network = nav().getByRole('button', { name: 'Network', exact: true })
    assert(await network.getAttribute('aria-expanded') === 'true', 'The selected group did not open')
    await network.focus()
    await page.keyboard.press('Control+k')
    await page.getByRole('textbox', { name: 'Search available tools' }).fill('scanner')
    await page.getByRole('dialog').getByRole('link', { name: /Gateway protection/ }).waitFor()
    await page.keyboard.press('Escape')
    assert(await network.evaluate(element => element === document.activeElement), 'Search did not restore focus')
  })
  await check('mobile drawer and focus', async () => {
    await page.setViewportSize({ width: 390, height: 900 })
    await menu().click()
    const dialog = page.getByRole('dialog', { name: 'Navigation' })
    assert(await dialog.isVisible(), 'Navigation drawer did not open')
    await page.keyboard.press('Tab')
    assert(await dialog.evaluate(element => element.contains(document.activeElement)), 'Focus escaped the drawer')
    await page.keyboard.press('Escape')
    assert(await menu().evaluate(element => element === document.activeElement), 'Escape did not restore focus')
    await menu().click()
    await page.setViewportSize({ width: 1100, height: 900 })
    await sidebar().waitFor()
    assert(await page.getByRole('button', { name: 'Collapse navigation' }).evaluate(element => element === document.activeElement), 'Resizing did not restore focus to the sidebar')
    await page.setViewportSize({ width: 390, height: 900 })
    await menu().click()
    const apps = nav().getByRole('button', { name: 'Apps', exact: true })
    if (await apps.getAttribute('aria-expanded') !== 'true') await apps.click()
    await nav().getByRole('link', { name: 'Catalog', exact: true }).click()
    await page.waitForURL('**/#/apps/catalog')
    assert(!(await dialog.isVisible()), 'Navigation drawer stayed open after following a link')
    assert(await page.getByRole('navigation', { name: 'Breadcrumb' }).getByText('App catalog').isVisible(), 'Mobile breadcrumb is missing')
    for (const width of [320, 390, 820]) {
      await page.setViewportSize({ width, height: 900 })
      assert(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), `Horizontal overflow at ${width}px`)
    }
    await page.setViewportSize({ width: 320, height: 900 })
    await page.goto(base + '#/ai/models/on/server-with-a-long-fixture-name/find')
    await page.getByRole('navigation', { name: 'Breadcrumb' }).getByText('Find & download').waitFor()
    await page.waitForFunction(() => {
      const nav = document.querySelector('.portal-breadcrumb').getBoundingClientRect()
      const current = document.querySelector('.portal-breadcrumb [aria-current="page"]').getBoundingClientRect()
      return current.left >= nav.left && current.right <= nav.right
    })
  })
  await check('member visibility', async () => {
    isOwner = false
    await page.setViewportSize({ width: 390, height: 900 })
    await page.goto(base + '#/')
    await page.reload()
    await menu().click()
    for (const name of ['Local AI', 'Settings'])
      assert(await nav().getByRole('button', { name, exact: true }).isVisible(), `${name} is missing for a member`)
    for (const name of ['Lab', 'Apps', 'Network', 'Assistant'])
      assert(await nav().getByRole('button', { name, exact: true }).count() === 0, `${name} is visible to a member`)
    await nav().getByRole('button', { name: 'Local AI', exact: true }).click()
    assert(await nav().getByRole('link', { name: 'Playground', exact: true }).isVisible(), 'Playground is missing for a member')
    assert(await nav().getByRole('link', { name: 'Local models', exact: true }).count() === 0, 'Owner-only models are visible')
    await page.keyboard.press('Escape')
  })
  await check('assistant push on either side', async () => {
    isOwner = true
    await page.setViewportSize({ width: 1280, height: 900 })
    for (const side of ['left', 'right']) {
      await page.evaluate(side => localStorage.setItem('lucia.assistant.dock.v1', JSON.stringify({ open: true, side })), side)
      await page.reload()
      await page.locator(`.assistant-dock[data-layout="push"][data-side="${side}"]:not([hidden])`).waitFor()
      assert(await sidebar().count() === 0, `Sidebar takes space beside the ${side} assistant`)
      assert(await page.getByRole('navigation', { name: 'Breadcrumb' }).isVisible(), `Breadcrumb disappeared beside the ${side} assistant`)
      await menu().click()
      const drawer = page.getByRole('dialog', { name: 'Navigation' })
      const left = await drawer.evaluate(element => Math.round(element.getBoundingClientRect().left))
      assert(left === (side === 'left' ? 400 : 0), `Drawer is misplaced beside the ${side} assistant: ${left}px`)
      await page.keyboard.press('Escape')
    }
    await page.setViewportSize({ width: 1600, height: 900 })
    await page.evaluate(() => localStorage.setItem('lucia.assistant.dock.v1', JSON.stringify({ open: true, side: 'left' })))
    await page.reload()
    await sidebar().waitFor()
    const positions = await page.evaluate(() => ({
      sidebar: document.querySelector('.portal-sidebar').getBoundingClientRect(),
      header: document.querySelector('.portal-header').getBoundingClientRect(),
    }))
    assert(Math.round(positions.sidebar.left) === 400, 'Sidebar overlaps the left assistant')
    assert(Math.round(positions.sidebar.right) === Math.round(positions.header.left), 'Sidebar does not align with the page')
    await page.goto(base + '#/map')
    await page.locator('.labmap-flat').waitFor()
    const map = await page.locator('.labmap').evaluate(element => {
      const rect = element.getBoundingClientRect()
      return { top: rect.top, bottom: rect.bottom, left: rect.left, height: rect.height }
    })
    assert(map.left >= positions.sidebar.right && map.height >= 460 && map.bottom <= 902, 'Map did not fit beside the sidebar and assistant')
  })
  if (failures.length) throw new Error(failures.join('\n'))
  return 'Navigation browser checks passed: groups, nested routes, role filtering, focus, responsive drawer, assistant sides and map layout.'
}
