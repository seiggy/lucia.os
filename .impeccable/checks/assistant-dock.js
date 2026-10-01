async page => {
  const base = 'http://127.0.0.1:4175'
  const seed = '5eed0000000000000000000000000001'
  const starter = 'How do custom app stacks work in Lucia?'
  const closing = 'Start with one service, confirm it answers, then add the rest.'
  const failure = 'The model stopped responding.'
  const check = (value, message) => { if (!value) throw new Error(message) }
  const errors = []
  const sent = []
  const stops = []
  const onPageError = error => errors.push(error.message)
  const onConsole = message => {
    if (message.type() === 'error' && !message.text().startsWith('Failed to load resource')) errors.push(message.text())
  }
  const onRequest = request => {
    if (request.method() !== 'POST') return
    if (request.url() === base + '/api/assistant/chat') sent.push({ body: request.postDataJSON(), csrf: request.headers()['x-csrf-token'] })
    if (/\/api\/assistant\/sessions\/[a-f0-9]{32}\/stop$/.test(request.url())) stops.push(request.url())
  }
  page.on('pageerror', onPageError)
  page.on('console', onConsole)
  page.on('request', onRequest)

  const toggle = page.locator('[data-assistant-toggle]')
  const dock = page.locator('#assistant-dock')
  const shell = page.locator('.app-shell')
  const input = page.getByRole('textbox', { name: 'Message the assistant', exact: true })
  const title = page.locator('.assistant-heading p')
  const model = page.getByRole('combobox', { name: 'Model', exact: true })
  const search = page.getByRole('button', { name: 'Find a tool', exact: true }).first()
  const button = name => page.getByRole('button', { name, exact: true })
  const wait = (locator, timeout = 10000) => locator.first().waitFor({ timeout })
  const style = (locator, property) => locator.evaluate((element, name) => getComputedStyle(element)[name], property)
  const saved = key => page.evaluate(name => localStorage.getItem(name), key)
  // Playwright scrolls a target into view before clicking, and the portal's scroll padding counts the sticky header as covered, so click where a pointer would.
  const pointer = async locator => {
    const box = await locator.boundingBox()
    await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2)
  }
  // The fixture server's safety banner is not product chrome; captures show the page as it ships.
  const shot = async name => {
    await page.evaluate(() => document.querySelector('body > [role="note"]')?.remove())
    await page.screenshot({ path: '.impeccable\\review\\' + name + '.png' })
  }
  const layout = value => page.waitForFunction(expected => document.querySelector('#assistant-dock')?.dataset.layout === expected, value, { timeout: 5000 })
  const noOverflow = async label => check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth
    && document.querySelector('.app-shell').scrollWidth <= document.querySelector('.app-shell').clientWidth), 'Horizontal overflow: ' + label)
  // Pushed, the page scrolls in its own column: the dock meets the screen edge and the page's scrollbar ends at the divider.
  const column = () => page.evaluate(() => {
    const shell = document.querySelector('.app-shell')
    return { dock: document.querySelector('#assistant-dock').getBoundingClientRect().right, page: shell.getBoundingClientRect().right,
      scroller: getComputedStyle(shell).overflowY, windowBar: innerWidth - document.documentElement.clientWidth, top: shell.scrollTop }
  })
  async function focused(locator, message) {
    for (let attempt = 0; attempt < 30; attempt++) {
      if (await locator.evaluate(element => element === document.activeElement).catch(() => false)) return
      await page.waitForTimeout(100)
    }
    throw new Error(`${message} (focus is on ${await page.evaluate(() => document.activeElement?.outerHTML.slice(0, 120))})`)
  }
  async function start(assistant, open = true) {
    await page.request.get(`${base}/__fixture?mode=owner&assistant=${assistant}`)
    await page.evaluate(value => {
      localStorage.setItem('lucia.assistant.dock.v1', JSON.stringify({ open: value, side: 'right' }))
      localStorage.removeItem('lucia.assistant.chat.v1')
      localStorage.removeItem('lucia.assistant.model.v1')
    }, open)
    await page.reload()
    await wait(open ? input : toggle)
  }
  const replies = () => page.evaluate(text => {
    const items = [...document.querySelectorAll('.assistant-reply')]
    const count = phrase => items.reduce((total, item) => total + item.textContent.split(phrase).length - 1, 0)
    return { users: document.querySelectorAll('.assistant-bubble').length, replies: items.length, intros: count('Custom app stacks are'), closings: count(text) }
  }, closing)

  try {
    // Wide screens: Ctrl+J toggles a dock that pushes the page, unless a dialog is open.
    await page.setViewportSize({ width: 1440, height: 900 })
    await page.emulateMedia({ colorScheme: 'light' })
    await page.request.get(base + '/__fixture?mode=owner')
    await page.goto(base + '/#/')
    await start('ready', false)
    check(await toggle.getAttribute('aria-expanded') === 'false' && await dock.isHidden(), 'The dock should start closed')
    await search.click()
    await wait(page.locator('dialog[open]'))
    await page.keyboard.press('Control+j')
    check(await dock.isHidden(), 'Ctrl+J opened the dock over an open dialog')
    await page.keyboard.press('Escape')
    await page.locator('dialog[open]').waitFor({ state: 'detached' })
    const reading = await page.evaluate(() => { scrollTo({ top: 240, behavior: 'instant' }); return scrollY })
    check(reading === 240, 'Home should scroll at 1440 so the dock can carry the reading position')
    await page.keyboard.press('Control+j')
    await wait(input)
    check(await dock.getAttribute('data-layout') === 'push' && await dock.getAttribute('data-side') === 'right', 'Wide screens should push the page with a right dock')
    check(await toggle.getAttribute('aria-expanded') === 'true', 'The header toggle did not report the open dock')
    await focused(input, 'Opening the dock did not focus the composer')
    check(await style(shell, 'marginRight') === '400px', 'The pushed dock did not reserve its width')
    const pushed = await column()
    check(pushed.dock === 1440 && pushed.page === 1040 && pushed.scroller === 'auto' && pushed.windowBar === 0 && pushed.top === reading,
      'The pushed page does not scroll in its own column: ' + JSON.stringify(pushed))
    check((await page.locator('.assistant-page').innerText()).includes('Home'), 'The empty state did not name the current page')
    check((await model.innerText()).includes('GPT-5 mini'), 'The host default model was not selected')
    check(await button('Execute').getAttribute('aria-pressed') === 'true', 'Execute should be the default answer mode')
    await page.keyboard.press('Control+j')
    await dock.waitFor({ state: 'hidden' })
    await focused(search, 'Closing with Ctrl+J did not return focus to where it was before opening')
    check(await page.evaluate(() => scrollY) === reading && await style(shell, 'overflowY') === 'visible', 'Closing the dock lost the reading position')
    await pointer(toggle)
    await wait(input)
    check((await column()).top === reading, 'Opening from the header toggle lost the reading position')
    await pointer(toggle)
    await dock.waitFor({ state: 'hidden' })
    check(await page.evaluate(() => scrollY) === reading, 'Closing from the header toggle lost the reading position')
    await page.evaluate(() => scrollTo({ top: 0, behavior: 'instant' }))
    await search.focus()
    await page.keyboard.press('Control+j')
    await focused(input, 'Reopening with Ctrl+J did not focus the composer')

    // A starter streams reasoning and a formatted answer; the request carries the chat contract and CSRF token.
    await button(starter).click()
    await wait(page.getByText(closing), 15000)
    await wait(button('Send message'))
    const [first] = sent
    check(sent.length === 1 && first.csrf === 'fixture-csrf-request', 'The chat request did not carry the CSRF token')
    check(Object.keys(first.body).sort().join() === 'messageId,mode,model,route,sessionId,text', 'Unexpected chat fields: ' + Object.keys(first.body))
    check(/^[a-f0-9]{32}$/.test(first.body.sessionId) && first.body.text === starter && first.body.mode === 'execute'
      && first.body.model === 'gpt-5-mini' && first.body.route === '/', 'Unexpected chat body: ' + JSON.stringify(first.body))
    check(await title.innerText() === starter && await saved('lucia.assistant.chat.v1') === first.body.sessionId, 'The new chat was not titled and remembered')
    const markdown = await page.locator('.assistant-reply').last().evaluate(reply => ({
      list: reply.querySelectorAll('ol li').length, rows: reply.querySelectorAll('table tr').length, quote: !!reply.querySelector('blockquote'),
      code: !!reply.querySelector('pre') && reply.textContent.includes('traefik/whoami:v1.10'),
      bold: [...reply.querySelectorAll('[data-streamdown="strong"]')].find(item => Number(getComputedStyle(item).fontWeight) >= 600)?.textContent,
    }))
    check(markdown.list === 3 && markdown.rows === 3 && markdown.quote && markdown.code && markdown.bold === 'Compose projects', 'The reply markdown did not render: ' + JSON.stringify(markdown))
    check(!(await page.locator('.assistant-reasoning-trigger').innerText()).includes('Thinking'), 'Reasoning still claims to be thinking')
    await noOverflow('1440 push with a reply')
    await page.waitForTimeout(300)
    await shot('assistant-dock-desktop')
    const rules = await page.evaluate(() => [document.querySelector('.portal-context').getBoundingClientRect().top + 1, document.querySelector('.assistant-header').getBoundingClientRect().bottom])
    check(rules[0] === rules[1], 'The dock header rule does not meet the portal context rule: ' + rules.join(' vs '))
    await page.emulateMedia({ colorScheme: 'dark' })
    await page.waitForTimeout(300)
    await shot('assistant-dock-dark')
    await page.emulateMedia({ colorScheme: 'light' })

    // Plan mode and another model travel with the next message.
    await button('Plan').click()
    check(await button('Plan').getAttribute('aria-pressed') === 'true' && await button('Execute').getAttribute('aria-pressed') === 'false', 'Plan mode did not turn on')
    const resting = await dock.boundingBox()
    await model.click()
    const claude = page.getByRole('option', { name: 'Claude Sonnet 4.5', exact: true })
    await wait(claude)
    check((await dock.boundingBox()).x === resting.x, 'Opening the model menu shifted the dock')
    await shot('assistant-model-menu')
    await claude.click()
    await input.fill('Plan a whoami stack for me')
    await input.press('Enter')
    await page.waitForFunction(text => [...document.querySelectorAll('.assistant-reply')].filter(reply => reply.textContent.includes(text)).length === 2, closing, { timeout: 15000 })
    await wait(button('Send message'))
    check(sent.length === 2 && sent[1].body.mode === 'plan' && sent[1].body.model === 'claude-sonnet-4.5' && sent[1].body.sessionId === first.body.sessionId,
      'The follow-up request was wrong: ' + JSON.stringify(sent[1]?.body))
    check(await input.inputValue() === '' && await saved('lucia.assistant.model.v1') === 'claude-sonnet-4.5', 'The draft or model choice was not settled after sending')

    // History lists saved chats, opens one, and deletes another only after confirmation.
    const history = button('Chat history')
    const heading = page.getByRole('heading', { name: 'Your chats', exact: true })
    const entry = text => page.locator('.assistant-history-open', { hasText: text })
    await history.click()
    await wait(page.locator('.assistant-history-open'))
    check(await history.getAttribute('aria-pressed') === 'true' && await input.isHidden(), 'History did not replace the conversation')
    await focused(heading, 'History did not focus its heading')
    const titles = await page.locator('.assistant-history-open strong').allInnerTexts()
    check(titles.join('|') === starter + '|Why does my media stack keep restarting?', 'Unexpected history: ' + titles.join('|'))
    check(await page.locator('.assistant-history-open[aria-current="true"] strong').innerText() === starter, 'History did not mark the open chat')
    await entry('Why does my media stack keep restarting?').click()
    await wait(page.getByRole('heading', { name: 'Check the failing service first', exact: true }))
    check(await title.innerText() === 'Why does my media stack keep restarting?' && await saved('lucia.assistant.chat.v1') === seed, 'The opened chat was not shown and remembered')
    await focused(input, 'Opening a chat did not focus the composer')
    await history.click()
    await wait(page.locator('.assistant-history-open'))
    await button(`Delete “${starter}”`).click()
    await wait(button('Delete chat'))
    await shot('assistant-history')
    await button('Delete chat').click()
    await entry(starter).waitFor({ state: 'detached' })
    await focused(heading, 'Deleting a chat did not return focus to the history heading')
    await history.click()
    await focused(input, 'Leaving history did not focus the composer')

    // A pushed dock ignores Escape, keeps the header clear, and can move to the left edge.
    await page.keyboard.press('Escape')
    check(await dock.isVisible(), 'Escape closed the pushed dock')
    for (const width of [1200, 1280]) {
      await page.setViewportSize({ width, height: 900 })
      await layout('push')
      const box = await toggle.boundingBox()
      check(box && box.x + box.width <= width - 400, 'The header toggle is covered by the pushed dock at ' + width)
      await noOverflow('push at ' + width)
    }
    await button('Move to the left').click()
    check(await dock.getAttribute('data-side') === 'left' && await style(shell, 'marginLeft') === '400px', 'The dock did not move to the left edge')
    const left = await column()
    check(left.page === 1280 && left.scroller === 'auto' && left.windowBar === 0, 'The left dock did not keep the page in its own column: ' + JSON.stringify(left))
    check(JSON.parse(await saved('lucia.assistant.dock.v1')).side === 'left', 'The dock side was not saved')
    await noOverflow('left push at 1280')
    await page.waitForTimeout(300)
    await shot('assistant-dock-left')
    await button('Move to the right').click()

    // Mid-width screens: the dock floats over the page, and Escape returns focus to the header.
    await page.setViewportSize({ width: 1024, height: 800 })
    await layout('overlay')
    check(await style(shell, 'marginRight') !== '400px' && await style(shell, 'overflowY') === 'visible', 'The overlay dock still pushed the page')
    await button('Close assistant').click()
    await dock.waitFor({ state: 'hidden' })
    await focused(search, 'Closing the dock did not return focus to where it was before opening')
    await toggle.click()
    await focused(input, 'Reopening the dock did not focus the composer')
    await page.waitForTimeout(300)
    await shot('assistant-dock-overlay')
    await page.keyboard.press('Escape')
    await dock.waitFor({ state: 'hidden' })
    await focused(toggle, 'Escape did not return focus to the header toggle')
    for (const width of [700, 820]) {
      await page.setViewportSize({ width, height: 800 })
      const own = await toggle.boundingBox()
      const find = await search.boundingBox()
      check(own && find && Math.abs(own.y - find.y) < 2 && own.x + own.width <= width, 'The header toggle does not fit beside search at ' + width)
      await noOverflow('header at ' + width)
    }

    // Phones: a bottom bar opens a full-screen sheet that keeps the page out of reach.
    await page.setViewportSize({ width: 390, height: 844 })
    await layout('sheet')
    const bar = button('Ask the assistant')
    await wait(bar)
    check(await toggle.isHidden(), 'The header toggle should give way to the bottom bar on phones')
    await noOverflow('390 with the bottom bar')
    await shot('assistant-bar-mobile')
    await bar.click()
    await focused(input, 'Opening the sheet did not focus the composer')
    await dock.evaluate(element => Promise.all(element.getAnimations().map(animation => animation.finished)))
    const sheet = await dock.boundingBox()
    check(sheet && sheet.x === 0 && sheet.y === 0 && sheet.width === 390 && Math.abs(sheet.height - 844) < 1, 'The sheet did not cover the screen: ' + JSON.stringify(sheet))
    const inert = () => page.evaluate(() => [...document.querySelector('.app-shell').children]
      .filter(child => child.id !== 'assistant-dock' && child.tagName !== 'DIALOG').map(child => child.inert))
    check((await inert()).every(Boolean), 'The page behind the sheet stayed interactive')
    check(await page.getByRole('button', { name: /^Move to the / }).count() === 0, 'The sheet offered to move the dock')
    await noOverflow('390 sheet')
    await page.waitForTimeout(300)
    await shot('assistant-dock-mobile')
    await page.keyboard.press('Escape')
    await dock.waitFor({ state: 'hidden' })
    await focused(bar, 'Closing the sheet did not return focus to the bottom bar')
    check((await inert()).every(value => !value), 'The page stayed inert after the sheet closed')
    await page.setViewportSize({ width: 320, height: 800 })
    await noOverflow('320 with the bottom bar')

    // Stopping keeps the partial answer, marks it stopped, and survives a reload.
    await page.setViewportSize({ width: 1440, height: 900 })
    await start('slow')
    await button(starter).click()
    await wait(button('Stop answering'))
    await wait(page.getByText('Compose projects'), 5000)
    await button('Stop answering').click()
    await wait(page.getByText('Stopped', { exact: true }), 5000)
    await wait(button('Send message'))
    check(stops.length === 1 && (await replies()).closings === 0, 'Stop did not reach the host or the answer kept streaming')
    await page.reload()
    await wait(page.getByText('Stopped', { exact: true }))
    check((await replies()).replies === 1, 'The stopped answer was not saved')
    check(await page.locator('.assistant-reply li').evaluateAll(items => items.every(item => item.textContent.trim())), 'The stopped answer ended on an empty list item')
    await shot('assistant-stopped')

    // Reloading mid-answer replays the running answer exactly once.
    await button('New chat').click()
    await focused(input, 'New chat did not focus the composer')
    const question = 'What should I check when an app stops responding?'
    await button(question).click()
    await wait(page.getByText('Compose projects'), 5000)
    await page.reload()
    await wait(page.getByText(closing), 20000)
    await wait(button('Send message'))
    const resumed = await replies()
    check(resumed.users === 1 && resumed.replies === 1 && resumed.intros === 1 && resumed.closings === 1, 'The resumed answer was duplicated or lost: ' + JSON.stringify(resumed))
    check(await title.innerText() === question, 'The resumed chat lost its title')

    // A chat already answering elsewhere is a state, not an error: a note offers a reload and keeps the refused message for resending.
    await start('busy')
    await input.fill('Is my whoami app healthy?')
    await input.press('Enter')
    const refused = page.locator('.assistant-note').filter({ hasText: 'already answering somewhere else' })
    await wait(refused)
    check(await refused.getByRole('button', { name: 'Reload chat', exact: true }).count() === 1 && await page.getByRole('alert').count() === 0,
      'run_active was not explained as a note with a reload')
    await shot('assistant-busy')
    await refused.getByRole('button', { name: 'Reload chat', exact: true }).click()
    await page.waitForFunction(() => document.querySelector('textarea[aria-label="Message the assistant"]')?.value === 'Is my whoami app healthy?', null, { timeout: 5000 })
    check(await page.locator('.assistant-bubble').count() === 0, 'The refused message stayed in the transcript')

    // Without GitHub the composer explains why nothing can be sent and offers a device-code sign-in.
    await start('disconnected')
    const signIn = button('Sign in with GitHub')
    await wait(page.locator('.assistant-connect').filter({ hasText: 'Sign in with GitHub to start asking' }))
    const before = sent.length
    await input.fill('Hello')
    await input.press('Enter')
    check(await button('Send message').isDisabled() && await button(starter).isDisabled(), 'Sending stayed available without GitHub')
    check(await model.count() === 0 && sent.length === before, 'A message was sent or a model offered without GitHub')
    await shot('assistant-disconnected')

    // Signing in shows the code beside a link to GitHub, and cancelling returns to the offer.
    const card = page.getByRole('group', { name: 'Enter this code at github.com/login/device', exact: true })
    const toGitHub = card.getByRole('link', { name: 'Copy code and open GitHub', exact: true })
    const spoken = page.locator('.assistant-gate [role="status"]')
    await signIn.click()
    await wait(card)
    await focused(toGitHub, 'Starting a sign-in did not focus the GitHub link')
    check(await card.locator('code').innerText() === 'WDJB-MJHT' && await toGitHub.getAttribute('href') === 'https://github.com/login/device'
      && await toGitHub.getAttribute('target') === '_blank' && /\bnoreferrer\b/.test(await toGitHub.getAttribute('rel')), 'The card did not offer the code and a safe GitHub link')
    check(await spoken.innerText() === 'Enter the code WDJB-MJHT at github.com/login/device.', 'The code was not announced: ' + await spoken.innerText())
    await card.getByRole('button', { name: 'Cancel', exact: true }).click()
    await wait(signIn)
    await focused(signIn, 'Cancelling did not return focus to the sign-in button')
    check(await card.count() === 0, 'Cancelling left the code card')
    await signIn.click()
    await wait(card)
    await card.getByRole('button', { name: 'Copy code', exact: true }).click()
    await page.waitForFunction(() => document.querySelector('.assistant-gate [role="status"]')?.textContent === 'Code copied.'
      || !!document.querySelector('.assistant-copy-blocked'), null, { timeout: 5000 })
    const copied = await spoken.innerText() === 'Code copied.'
    await page.waitForTimeout(300)
    await shot('assistant-signin')
    await page.setViewportSize({ width: 390, height: 844 })
    await layout('sheet')
    await wait(card)
    await noOverflow('390 sign-in card')
    await page.waitForTimeout(300)
    await shot('assistant-signin-mobile')
    await page.setViewportSize({ width: 1440, height: 900 })
    await layout('push')

    // Approval on GitHub swaps the card for the composer, names the account, and unlocks models and starters.
    await page.request.get(base + '/api/assistant/github?approve')
    await card.waitFor({ state: 'detached', timeout: 5000 })
    await wait(model)
    check(!await button(starter).isDisabled(), 'Starters stayed disabled after signing in')
    check(await spoken.innerText() === 'Signed in to GitHub as @octocat.', 'Finishing the sign-in was not announced')
    await focused(input, 'Finishing the sign-in did not focus the composer')

    // History names the account and signs out only after saying what GitHub keeps.
    await button('Chat history').click()
    const account = page.locator('.assistant-account')
    await wait(account.filter({ hasText: 'Signed in to GitHub as @octocat' }))
    const disconnect = account.getByRole('button', { name: 'Disconnect', exact: true })
    await disconnect.click()
    check(await disconnect.getAttribute('aria-expanded') === 'true', 'Disconnect did not report its confirmation')
    check(await account.getByRole('link', { name: 'Authorized GitHub Apps', exact: true }).getAttribute('href') === 'https://github.com/settings/apps/authorizations',
      'The confirmation did not link to GitHub’s app authorizations')
    await shot('assistant-account')
    await account.getByRole('button', { name: 'Disconnect GitHub', exact: true }).click()
    await wait(account.filter({ hasText: 'Signed out of GitHub on this host.' }))
    await focused(account.locator('p[tabindex="-1"]'), 'Signing out did not focus its result')
    await button('Chat history').click()
    await wait(signIn)
    check(await model.count() === 0, 'Models stayed offered after signing out')

    // When Copilot refuses the account, the composer says so and offers to sign in again.
    await start('refused')
    const refusal = page.locator('.assistant-connect').filter({ hasText: 'Copilot did not accept @octocat.' })
    await wait(refusal)
    check(await refusal.getByRole('button', { name: 'Sign in again', exact: true }).count() === 1, 'A refusal did not offer to sign in again')
    check(await button(starter).isDisabled() && await model.count() === 0, 'Asking stayed available after Copilot refused')
    await shot('assistant-refused')

    // A failed answer keeps its partial text, explains the failure once with a retry, and survives a reload.
    await start('failing')
    await button(starter).click()
    await wait(page.getByText(failure, { exact: true }))
    await wait(button('Send message'))
    const again = button('Try again')
    check(await page.getByRole('alert').count() === 0 && (await replies()).intros === 1 && await again.count() === 1,
      'The failure was not shown once, with a retry, beside the partial answer')
    await page.reload()
    await wait(again)
    await shot('assistant-failed')
    // Retrying a saved failure replaces that answer instead of repeating the question. The SDK cancels a stream that
    // reports an error, so wait for the composer to settle rather than for the request to finish.
    await Promise.all([page.waitForRequest(base + '/api/assistant/chat'), again.click()])
    await wait(button('Send message'))
    await wait(again)
    const redone = await replies()
    check(sent.at(-1).body.messageId === sent.at(-2).body.messageId && redone.users === 1 && redone.replies === 1 && redone.intros === 1,
      'Retrying the failed answer duplicated the chat: ' + JSON.stringify(redone))
    await page.reload()
    await wait(again)
    check(JSON.stringify(await replies()) === JSON.stringify(redone), 'The retried chat was not saved once')

    check(sent.every(item => item.csrf === 'fixture-csrf-request'), 'A chat request went out without the CSRF token')
    check(errors.length === 0, 'Page errors: ' + errors.join(' | '))
    return { contract: true, focus: true, history: true, layouts: ['push', 'overlay', 'sheet'], column: pushed, stop: true, resume: resumed, busy: true, disconnected: true, signin: { copied }, account: true, refused: true, failing: true, retry: redone }
  } finally {
    page.off('pageerror', onPageError)
    page.off('console', onConsole)
    page.off('request', onRequest)
    await page.emulateMedia({ colorScheme: null }).catch(() => {})
    await page.request.get(base + '/__fixture?mode=owner').catch(() => {})
  }
}
