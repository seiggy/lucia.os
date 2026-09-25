async page => {
  const base = 'http://127.0.0.1:4175'
  const check = (value, message) => { if (!value) throw new Error(message) }
  const changes = []
  const record = request => {
    if (request.url().includes('/api/host/') && request.method() !== 'GET')
      changes.push({ path: new URL(request.url()).pathname, method: request.method(),
        csrf: request.headers()['x-csrf-token'], body: request.postData() })
  }
  const seed = async data => {
    const response = await page.request.post(base + '/api/host/domains/__fixture/failure',
      { headers: { 'X-CSRF-TOKEN': 'fixture-csrf-request' }, data })
    check(response.ok(), 'Could not prepare synthetic DNS failure')
    return response.json()
  }
  page.on('request', record)
  try {
    await page.request.get(base + '/__fixture?mode=owner')
    const gateway = await seed({ scenario: 'gateway', issuedCertificate: true, renewalScheduled: true })
    await page.goto(base + '/#/settings/domains')
    await page.reload()
    await page.getByRole('heading', { name: 'Certificate issued, but new addresses are not active.' }).waitFor()
    const section = page.locator('.network-section').filter({ has: page.locator('.network-step-heading') })
    const details = section.locator('details').filter({ has: page.getByText('Technical details', { exact: true }) })
    const explanation = section.locator('.network-failure-text')
    const compactHeights = {}
    check(await explanation.count() === 1 && await explanation.innerText() === gateway.diagnosis.summary, 'Expected one confirmed primary explanation')
    check(await section.getByText(gateway.message, { exact: true }).count() === 0, 'Raw job message repeats the explanation')
    check(await section.getByText(gateway.diagnosis.summary, { exact: true }).count() === 1, 'Confirmed summary is duplicated')
    check(!await details.getByText(gateway.support.explanation, { exact: true }).isVisible(), 'AI advice expanded the ordinary failure view')
    check(await section.getByRole('heading').count() === 1, 'Failure has repeated outcome headings')
    check(await section.getByRole('button').count() === 1, 'Failure shows more than one primary action')
    check(await details.getAttribute('open') === null, 'Technical details are open by default')
    check(!await details.getByText('Setup progress', { exact: true }).isVisible(), 'Raw phase history is expanded')
    check(!await details.getByText(/synthetic-local-chat/).isVisible(), 'Model metadata is exposed before expanding details')
    check(await section.getByText(/Next renewal check|Renewal is checked/).count() === 0, 'Failed activation falsely claims renewal is scheduled')
    for (const width of [320, 390, 1440]) {
      await page.setViewportSize({ width, height: 900 })
      check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'DNS support overflow at ' + width)
      compactHeights[width] = Math.round((await section.boundingBox()).height)
      check(compactHeights[width] < (width === 1440 ? 500 : 800), 'Failure section remains too tall at ' + width)
    }
    await details.getByText('Technical details', { exact: true }).click()
    check(await details.getByText('Setup progress', { exact: true }).isVisible(), 'Technical history cannot be opened')
    check(await details.getByText(/synthetic-local-chat/).isVisible(), 'Local model attribution was lost')
    check(await details.getByText(gateway.support.explanation, { exact: true }).isVisible(), 'Optional AI explanation is missing from Technical details')
    await seed({ recoveryRequired: true })
    await page.reload()
    await page.getByRole('heading', { name: 'DNS setup did not finish.' }).waitFor()
    await details.getByText('Technical details', { exact: true }).click()
    const localAdvice = details.locator('.network-support-text')
    check((await localAdvice.innerText()).includes('<strong>permission denied</strong>'), 'Generated text was interpreted as HTML')
    check(await localAdvice.locator('*').count() === 0, 'Generated text contains rendered markup')
    check(await page.getByRole('button', { name: 'Review setup again' }).count() === 0, 'canRetry bypassed recovery')
    check(await page.getByRole('button', { name: 'Retry ownership-checked cleanup' }).isEnabled(), 'Authoritative recovery is not available')
    const unknown = await seed({ scenario: 'unknown' })
    await page.reload()
    await page.getByRole('heading', { name: 'DNS setup did not finish.' }).waitFor()
    check(await explanation.innerText() === unknown.diagnosis.summary, 'Unknown cause displays speculative model advice')
    check(await section.getByText(unknown.support.explanation, { exact: true }).count() === 0, 'Unknown cause repeats false AI policy advice')
    check(await page.getByRole('button', { name: 'Review setup again' }).isEnabled(), 'canRetry=false incorrectly blocks setup review')
    await seed({ scenario: 'unknown', recoveryRequired: true, configured: true })
    await page.reload()
    await page.getByRole('button', { name: 'Retry ownership-checked cleanup' }).waitFor()
    check(await page.getByRole('button', { name: 'Retry ownership-checked cleanup' }).isDisabled(), 'Support bypassed configured-state recovery gate')
    const unavailable = await seed({ supportState: 'Unavailable' })
    await page.reload()
    await page.getByRole('heading', { name: 'DNS setup did not finish.' }).waitFor()
    check(await explanation.innerText() === unavailable.diagnosis.summary, 'Unavailable AI lost the confirmed explanation')
    check(!await details.getByRole('button', { name: 'Retry local explanation' }).isVisible(), 'Support retry escaped Technical details')
    await details.getByText('Technical details', { exact: true }).click()
    await details.getByText(/No local chat model is loaded/).waitFor()
    const diagnosePath = '/api/host/domains/jobs/22222222-2222-4222-8222-222222222222/diagnose'
    const denied = await page.request.post(base + diagnosePath)
    check(denied.status() === 403, 'Fixture diagnose endpoint accepted a missing CSRF token')
    await page.route('**' + diagnosePath, route => route.fulfill({ status: 503, contentType: 'application/json',
      body: JSON.stringify({ error: { message: 'Synthetic local support is unavailable. Try again.' } }) }))
    await details.getByRole('button', { name: 'Retry local explanation' }).click()
    await details.getByRole('alert').filter({ hasText: 'Synthetic local support is unavailable.' }).waitFor()
    check(await details.getByRole('button', { name: 'Retry local explanation' }).isEnabled(), 'Request failure left retry stuck')
    await page.unroute('**' + diagnosePath)
    const accepted = page.waitForResponse(response => response.url().endsWith(diagnosePath) && response.status() === 202)
    await details.getByRole('button', { name: 'Retry local explanation' }).evaluate(button => { button.click(); button.click() })
    await accepted
    check(await details.getByRole('button', { name: 'Preparing explanation…' }).isDisabled(), 'Duplicate support request was not prevented')
    await details.getByRole('status').filter({ hasText: 'This can take up to 90 seconds.' }).waitFor()
    await details.getByText('Technical details', { exact: true }).click()
    check(await page.getByRole('button', { name: 'Review setup again' }).isEnabled(), 'Pending support blocked setup review')
    await page.getByRole('button', { name: 'Review setup again' }).click()
    await page.getByRole('button', { name: 'Replace account token' }).click()
    await page.getByLabel('Account API token').fill('synthetic-unsaved-token')
    await page.waitForResponse(async response => response.url().endsWith('/api/host/domains/status')
      && (await response.json()).job?.support?.state === 'Complete', { timeout: 25000 })
    check(await page.getByRole('heading', { name: 'Choose your domain provider' }).isVisible(), 'Support update forced navigation away from the form')
    check(await page.getByLabel('Account API token').inputValue() === 'synthetic-unsaved-token', 'Support update discarded unsaved form values')
    check(changes.length === 2 && changes.every(request => request.path === diagnosePath && request.method === 'POST'
      && request.csrf === 'fixture-csrf-request' && request.body === null), 'Support triggered an unexpected mutation, duplicate, request body, or missing CSRF')
    await page.reload()
    await page.getByRole('heading', { name: 'DNS setup did not finish.' }).waitFor()
    check(await explanation.count() === 1 && await details.getAttribute('open') === null, 'Completed support expands or duplicates the explanation')
    await seed({ supportState: 'Missing' })
    await page.reload()
    await page.getByRole('heading', { name: 'DNS setup did not finish.' }).waitFor()
    check((await explanation.innerText()).includes('could not capture the specific error'), 'Legacy response hides the diagnostic gap')
    for (const jobState of ['Active', 'Activating', 'Running']) {
      await seed({ jobState, issuedCertificate: true, renewalScheduled: true })
      await page.reload()
      await page.getByText(/Certificate expires/).waitFor()
      check(await page.getByText(/Next renewal check/).count() === (jobState === 'Running' ? 0 : 1), 'Renewal schedule displayed for wrong job state')
    }
    await seed({ jobState: 'Active', issuedCertificate: true })
    await page.reload()
    await page.getByText(/Certificate expires/).waitFor()
    check(await page.getByText(/Next renewal check|Renewal is checked/).count() === 0, 'Missing renewal schedule presented as running')
    return { oneExplanation: true, collapsedTechnicalDetails: true, compactHeights, correctCertificateState: true,
      unknownCauseHonest: true, canRetryNotPolicy: true, localAdvisoryAttribution: true, plainText: true, recoveryAuthoritative: true,
      unavailableAndRequestFailure: true, csrf: true, noProviderOrModelMutations: true, duplicatePrevention: true,
      pendingReviewAvailable: true, unsavedTokenPreserved: true, responsive: true, legacyCompatible: true }
  } finally {
    page.off('request', record)
  }
}
