async page => {
  const base = 'http://127.0.0.1:4175'
  const check = (value, message) => { if (!value) throw new Error(message) }
  await page.request.get(base + '/__fixture?mode=owner')
  await page.goto(base + '/#/ai/models')
  await page.reload()
  await page.getByRole('button', { name: 'Context & load', exact: true }).click()
  const slider = page.getByRole('slider', { name: 'Context to serve', exact: true })
  await slider.waitFor()
  check(await slider.getAttribute('min') === '8192', 'Slider does not start at 8K')
  check(await slider.getAttribute('max') === '32768', 'Slider does not use the calculated limit')
  check(await slider.getAttribute('step') === '256', 'Slider does not follow host context alignment')
  check(await slider.inputValue() === '8192', 'Initial context was not preserved')
  await slider.focus()
  await page.keyboard.press('Home')
  await page.keyboard.press('ArrowRight')
  check(await slider.inputValue() === '8448', 'Keyboard slider increment failed')
  await page.keyboard.press('End')
  check(await slider.inputValue() === '32768', 'Keyboard cannot reach the calculated upper limit')
  check(await slider.getAttribute('aria-valuetext') === '32,768 tokens', 'Accessible selected value is missing')
  for (const width of [320, 390, 820, 1440]) {
    await page.setViewportSize({ width, height: 900 })
    check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Slider layout overflows at ' + width)
    check(await slider.evaluate(element => element.getBoundingClientRect().height >= 44), 'Slider touch target is too small')
  }
  await page.getByRole('button', { name: 'Load selected model', exact: true }).click()
  const submitted = page.waitForRequest(request => request.method() === 'POST' && request.url().endsWith('/load'))
  await page.getByRole('button', { name: 'Load LLM', exact: true }).click()
  check((await submitted).postDataJSON().contextTokens === 32768, 'Load request ignored the chosen slider value')
  await page.getByText('Load LLM completed.', { exact: true }).waitFor()
  const plan = await (await page.request.get(base + '/api/host/models/11111111-1111-4111-8111-111111111111/context')).json()
  let capacity = 4096
  await page.route('**/api/host/models/*/context', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ ...plan, memoryLimitedContextTokens: capacity, effectiveContextTokens: capacity }),
  }))
  await page.getByRole('button', { name: 'Context & load', exact: true }).click()
  await page.getByText('below the 8,192-token minimum', { exact: false }).waitFor()
  check(await slider.count() === 0, 'An invalid range was offered below the 8K minimum')
  check(await page.getByRole('button', { name: 'Load selected model', exact: true }).isDisabled(), 'Below-minimum model can still be loaded')
  capacity = 8192
  await page.getByRole('button', { name: 'Context & load', exact: true }).click()
  await slider.waitFor()
  check(await slider.getAttribute('max') === '8192' && await slider.isDisabled(), 'Single-value 8K range is not handled')
  check(await page.getByRole('button', { name: 'Load selected model', exact: true }).isEnabled(), 'Valid single-value 8K model was blocked')
  await page.unroute('**/api/host/models/*/context')
  return { range8192ToCalculatedLimit: true, keyboard: true, responsive: true, loadUsesSelection: true, belowMinimumBlocked: true, exactMinimumAllowed: true }
}
