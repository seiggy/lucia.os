async page => {
  const base = 'http://127.0.0.1:4175'
  const check = (value, message) => { if (!value) throw new Error(message) }
  await page.request.get(base + '/__fixture?mode=owner&metrics=healthy')
  await page.goto(base + '/#/')
  await page.reload()
  await page.getByText('Spark is reporting normally.', { exact: true }).waitFor()
  await page.getByText('Unified memory', { exact: true }).waitFor()
  await page.locator('.spark-history summary').click()
  check(await page.locator('.spark-trend svg').count() === 3, 'Expected CPU, memory and GPU history')
  for (const width of [320, 390, 768, 1280]) {
    await page.setViewportSize({ width, height: 900 })
    check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Horizontal overflow at ' + width)
  }
  await page.evaluate(() => {
    const note = document.createElement('div')
    note.textContent = 'Synthetic metrics fixture - not live hardware readings'
    note.style.cssText = 'padding:8px 16px;background:#e9edf4;color:#202839;font:14px system-ui;text-align:center'
    document.body.prepend(note)
  })
  await page.setViewportSize({ width: 1280, height: 1000 })
  await page.screenshot({ path: '.impeccable\\review\\spark-desktop.png', fullPage: true })
  await page.setViewportSize({ width: 390, height: 844 })
  await page.screenshot({ path: '.impeccable\\review\\spark-mobile.png', fullPage: true })
  await page.request.get(base + '/__fixture?mode=owner&metrics=partial')
  await page.reload()
  await page.getByText('Some host readings are unavailable.', { exact: true }).waitFor()
  check(await page.locator('.spark-readings').getByText('Unavailable', { exact: true }).count() === 1, 'Unsupported GPU became a zero')
  await page.request.get(base + '/__fixture?mode=owner&metrics=error')
  await page.getByText('Connection needs attention', { exact: true }).waitFor({ timeout: 15000 })
  await page.getByText('Values below are from the last successful check.', { exact: false }).waitFor()
  check(await page.getByText('Spark is reporting normally.', { exact: true }).count() === 0, 'Connection failure retained a healthy label')
  await page.request.get(base + '/__fixture?mode=owner&metrics=starting')
  await page.getByRole('button', { name: 'Try again', exact: true }).click()
  await page.getByText('Waiting for the first host readings.', { exact: true }).waitFor()
  check(await page.locator('.spark-trend svg').count() === 0, 'Empty cache invented history')
  return { responsive: true, oneHourHistory: true, unsupportedHonest: true, errorsNotHealthy: true, emptyNotInvented: true }
}
