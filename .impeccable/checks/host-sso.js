async page => {
  const failures = []
  page.on('pageerror', error => failures.push(String(error)))
  const check = (condition, message) => { if (!condition) throw new Error(message) }
  const fixture = async () => page.evaluate(() => {
    let notice = document.getElementById('fixture-notice')
    if (!notice) {
      notice = document.createElement('div')
      notice.id = 'fixture-notice'
      notice.style.cssText = 'position:fixed;bottom:0;left:0;right:0;z-index:9999;padding:5px;background:#e9edf4;color:#202839;text-align:center;font:12px sans-serif'
      document.body.append(notice)
    }
    notice.textContent = 'Synthetic sign-in fixture · No real account or inference request'
  })
  const noOverflow = async () => check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Horizontal overflow')
  await page.setViewportSize({ width: 1280, height: 900 })
  await page.request.get('http://127.0.0.1:4175/__fixture?mode=anonymous')
  await page.goto('http://127.0.0.1:4175/#/ai')
  await page.getByRole('heading', { name: 'Welcome to Lucia.' }).waitFor()
  check((await page.getByRole('link', { name: 'Sign in with Authentik' }).getAttribute('href')) === '/auth/login?returnUrl=%2F%23%2Fai', 'Local return destination lost')
  check((await (await page.request.get('http://127.0.0.1:4175/__requests')).json()).length === 0, 'Inference was requested before authentication')
  await fixture()
  await page.screenshot({ path: '.impeccable/screenshots/host-signin-desktop.png', fullPage: true })
  await page.setViewportSize({ width: 390, height: 844 })
  await noOverflow()
  await page.screenshot({ path: '.impeccable/screenshots/host-signin-mobile.png', fullPage: true })

  await page.request.get('http://127.0.0.1:4175/__fixture?mode=owner')
  await page.setViewportSize({ width: 1280, height: 900 })
  await page.reload()
  await page.getByLabel('Your message', { exact: true }).waitFor()
  await page.getByLabel('Your message', { exact: true }).fill('Synthetic connection check')
  await page.getByRole('button', { name: 'Send message' }).click()
  await page.getByText('Synthetic authenticated response.', { exact: true }).waitFor()
  const requests = await (await page.request.get('http://127.0.0.1:4175/__requests')).json()
  const chat = requests.find(request => request.method === 'POST')
  check(chat?.url.endsWith('/v1/chat/completions'), 'Managed chat did not use same-origin API')
  check(chat.headers['x-csrf-token'] === 'fixture-csrf-request', 'CSRF header missing')
  check(!chat.headers.authorization && !chat.headers['x-lucia-playground'], 'A development/API-key bridge leaked into managed mode')
  check(!requests.some(request => request.url.includes('/api/playground')), 'Managed mode used the development proxy')
  check(await page.locator('form[action="/auth/logout"] input[name="__RequestVerificationToken"]').inputValue() === 'fixture-csrf-request', 'Sign-out form lacks CSRF')
  await fixture()
  await noOverflow()
  await page.screenshot({ path: '.impeccable/screenshots/host-session-desktop.png', fullPage: true })
  await page.setViewportSize({ width: 768, height: 900 })
  await noOverflow()
  await page.setViewportSize({ width: 390, height: 844 })
  await noOverflow()
  await page.screenshot({ path: '.impeccable/screenshots/host-session-mobile.png', fullPage: true })
  check(await page.evaluate(() => !Object.keys(localStorage).some(key => /token|session|auth/i.test(key))), 'Authentication persisted in browser localStorage')

  await page.request.get('http://127.0.0.1:4175/__fixture?mode=denied')
  await page.reload()
  await page.getByRole('heading', { name: 'Your account needs access.' }).waitFor()
  check(await page.getByRole('button', { name: 'Sign out' }).count() === 1, 'Denied account cannot sign out')
  await page.request.get('http://127.0.0.1:4175/__fixture?mode=error')
  await page.reload()
  await page.getByRole('heading', { name: 'We cannot check your sign-in.' }).waitFor()
  check(await page.getByRole('button', { name: 'Try again' }).count() === 1, 'Failed session has no retry')
  check(failures.length === 0, failures.join('\n'))
  return { anonymousGate: true, csrf: true, sameOriginInference: true, deniedAndErrorStates: true, responsive: true }
}
