async page => {
  const base = 'http://127.0.0.1:4175'
  await page.request.get(base + '/__fixture?mode=owner')
  await page.goto(base + '/#/settings')
  await page.reload()
  await page.getByRole('button', { name: 'Light', exact: true }).click()
  await page.getByRole('button', { name: 'Ocean', exact: true }).click()
  await page.goto(base + '/#/')
  await page.getByRole('heading', { name: 'Your lab, within reach.' }).waitFor()
  await page.getByText('Spark is reporting normally.', { exact: true }).waitFor()
  await page.evaluate(() => {
    const note = document.createElement('div')
    note.textContent = 'Synthetic review data — no live hardware or credentials'
    note.style.cssText = 'padding:6px 16px;background:#e9edf4;color:#202839;font:12px system-ui;text-align:center'
    document.querySelector('.portal-context').after(note)
  })
  for (const [name, width, height] of [['desktop', 1440, 1000], ['tablet', 820, 1180], ['mobile', 390, 844]]) {
    await page.setViewportSize({ width, height })
    await page.evaluate(() => scrollTo(0, 0))
    await page.screenshot({ path: '.impeccable\\review\\portal-' + name + '.png', fullPage: true })
    await page.getByRole('button', { name: 'Switch workspace, current: Overview' }).click()
    await page.getByRole('dialog').waitFor()
    await page.screenshot({ path: '.impeccable\\review\\portal-menu-' + name + '.png' })
    await page.keyboard.press('Escape')
  }
  await page.goto(base + '/#/ai/models')
  await page.getByRole('heading', { name: 'Models', exact: true }).waitFor()
  await page.setViewportSize({ width: 1440, height: 1000 })
  await page.screenshot({ path: '.impeccable\\review\\portal-models.png', fullPage: true })
  await page.goto(base + '/#/ai/keys')
  await page.getByRole('heading', { name: 'API keys', exact: true }).waitFor()
  await page.setViewportSize({ width: 390, height: 844 })
  await page.screenshot({ path: '.impeccable\\review\\portal-keys-mobile.png', fullPage: true })
  return { captured: true, synthetic: true }
}
