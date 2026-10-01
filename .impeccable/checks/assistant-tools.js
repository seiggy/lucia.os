async page => {
  const base = 'http://127.0.0.1:4175'
  const starter = 'How do custom app stacks work in Lucia?'
  const closing = 'I kept whoami, as you asked.'
  const planned = 'Switch to Execute when you want me to run it.'
  const reason = 'Keep whoami, I use it for testing.'
  const sites = ['docs.docker.com', 'hub.docker.com', 'github.com', 'raw.githubusercontent.com']
  const check = (value, message) => { if (!value) throw new Error(message) }
  const errors = []
  const posts = []
  const onPageError = error => errors.push(error.message)
  const onConsole = message => {
    if (message.type() === 'error' && !message.text().startsWith('Failed to load resource')) errors.push(message.text())
  }
  const onRequest = request => {
    const answer = request.url().match(/\/api\/assistant\/sessions\/[a-f0-9]{32}\/((?:approvals|answers)\/[^/]+)$/)
    if (request.method() === 'POST' && answer) posts.push({ path: answer[1], body: request.postDataJSON(), csrf: request.headers()['x-csrf-token'] })
    if (request.method() === 'PUT' && request.url() === base + '/api/assistant/settings')
      posts.push({ path: 'settings', body: request.postDataJSON(), csrf: request.headers()['x-csrf-token'] })
  }
  page.on('pageerror', onPageError)
  page.on('console', onConsole)
  page.on('request', onRequest)

  const toggle = page.locator('[data-assistant-toggle]')
  const dock = page.locator('#assistant-dock')
  const input = page.getByRole('textbox', { name: 'Message the assistant', exact: true })
  const button = name => page.getByRole('button', { name, exact: true })
  const wait = (locator, timeout = 10000) => locator.first().waitFor({ timeout })
  const exact = text => new RegExp('^' + text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$')
  const tool = title => page.locator('.assistant-tool').filter({ has: page.locator('[data-slot="tool-title"]', { hasText: exact(title) }) })
  const approval = title => page.getByRole('group', { name: 'Approve: ' + title, exact: true })
  const question = page.getByRole('group', { name: 'Question from the assistant', exact: true })
  const last = () => posts.at(-1) ?? {}
  const same = (a, b) => JSON.stringify(a) === JSON.stringify(b)
  const shot = async name => {
    await page.evaluate(() => document.querySelector('body > [role="note"]')?.remove())
    await page.screenshot({ path: '.impeccable\\review\\' + name + '.png' })
  }
  const layout = value => page.waitForFunction(expected => document.querySelector('#assistant-dock')?.dataset.layout === expected, value, { timeout: 5000 })
  const noOverflow = async label => check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth
    && document.querySelector('.app-shell').scrollWidth <= document.querySelector('.app-shell').clientWidth), 'Horizontal overflow: ' + label)
  async function focused(locator, message) {
    for (let attempt = 0; attempt < 30; attempt++) {
      if (await locator.evaluate(element => element === document.activeElement).catch(() => false)) return
      await page.waitForTimeout(100)
    }
    throw new Error(`${message} (focus is on ${await page.evaluate(() => document.activeElement?.outerHTML.slice(0, 120))})`)
  }
  // Waits for the conversation's smooth scroll to stop, then returns how far it sits above the bottom.
  const settle = () => page.evaluate(() => new Promise(resolve => {
    let scroller = document.querySelector('.assistant-messages')
    while (scroller && !/auto|scroll/.test(getComputedStyle(scroller).overflowY)) scroller = scroller.parentElement
    let last = -1, still = 0
    const tick = () => {
      if (scroller.scrollTop !== last) { last = scroller.scrollTop; still = 0 } else if (++still >= 15) return resolve(Math.round(scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight))
      requestAnimationFrame(tick)
    }
    tick()
  }))
  const inView = locator => locator.evaluate(element => {
    let scroller = element.parentElement
    while (scroller && !/auto|scroll/.test(getComputedStyle(scroller).overflowY)) scroller = scroller.parentElement
    const inner = element.getBoundingClientRect(), outer = scroller.getBoundingClientRect()
    return inner.top >= outer.top - 1 && inner.bottom <= outer.bottom + 1
  })
  // True when this text inside the element sits on one line, not broken mid-name.
  const whole = (locator, text) => locator.evaluate((element, text) => {
    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT)
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const at = node.data.indexOf(text)
      if (at < 0) continue
      const range = document.createRange()
      range.setStart(node, at)
      range.setEnd(node, at + text.length)
      return new Set([...range.getClientRects()].map(rect => Math.round(rect.top))).size === 1
    }
    return false
  }, text)
  async function atBottom(label) {
    const gap = await settle()
    check(gap <= 2, `${label} arrived ${gap}px above the end of the conversation`)
  }
  async function start(assistant) {
    await page.request.get(`${base}/__fixture?mode=owner&assistant=${assistant}`)
    await page.evaluate(() => {
      localStorage.setItem('lucia.assistant.dock.v1', JSON.stringify({ open: true, side: 'right' }))
      localStorage.removeItem('lucia.assistant.chat.v1')
      localStorage.removeItem('lucia.assistant.model.v1')
    })
    await page.reload()
    await wait(input)
  }
  // A call has settled when its status word shows; its one-line note says why it didn't run, how it failed, or what the owner answered.
  async function settled(title, word, note) {
    await wait(tool(title).locator('.assistant-tool-status', { hasText: exact(word) }))
    const notes = await tool(title).locator('.assistant-tool-note').allInnerTexts()
    check(note === undefined ? notes.length === 0 : same(notes, [note]), `${title}: expected the note ${JSON.stringify(note)}, got ${JSON.stringify(notes)}`)
  }
  const rows = () => page.locator('.assistant-tool').evaluateAll(items => items.map(item => [item.querySelector('[data-slot="tool-title"]').textContent,
    item.querySelector('.assistant-tool-status').textContent, item.querySelector('.assistant-tool-note')?.textContent ?? '']))
  const finished = [
    ['List apps', 'Done', ''],
    ['Read logs of jellyfin on lucialab02', 'Failed', 'jellyfin isn\'t running on lucialab02, so it has no live log.'],
    ['Restart whoami', 'Done', ''],
    ['Read docs.linuxserver.io', 'Done', ''],
    ['Delete whoami', 'Not run', `You declined: “${reason}”`],
    ['Which server should run Jellyfin?', 'Answered', 'You answered: lucialab02'],
    ['media needs PLEX_CLAIM_TOKEN', 'Saved', 'Saved in media’s settings. The assistant never sees it.'],
  ]

  try {
    await page.setViewportSize({ width: 1440, height: 900 })
    await page.emulateMedia({ colorScheme: 'light' })
    await page.request.get(base + '/__fixture?mode=owner')
    await page.goto(base + '/#/')
    await start('tools')

    // Reads run at once and a failure says why; the owner's settings let a restart run without asking.
    await button(starter).click()
    await settled('List apps', 'Done')
    await settled('Read logs of jellyfin on lucialab02', 'Failed', finished[1][2])
    await settled('Restart whoami', 'Done')

    // A site off the allowed list waits for approval; approving it for the chat sends always.
    const web = approval('Read docs.linuxserver.io')
    await wait(web)
    check(await tool('Read docs.linuxserver.io').getAttribute('data-waiting') === '', 'The waiting call was not marked')
    await settled('Read docs.linuxserver.io', 'Needs your approval')
    check(await web.locator('[data-slot="confirmation-title"]').innerText() === 'docs.linuxserver.io isn\'t on the assistant\'s allowed sites.', 'The approval did not say why it asks')
    check(!await button('Approve').evaluate(element => element.classList.contains('assistant-danger')), 'A web read was styled as dangerous')
    const grant = web.getByRole('button', { name: 'Approve, and let it read docs.linuxserver.io for the rest of this chat', exact: true })
    check(await grant.count() === 1, 'The web approval did not offer to allow the site for the chat')
    await noOverflow('1440 approval card')
    await atBottom('The web approval')
    check(await inView(web), 'The web approval was cut off')
    await shot('assistant-tools-approval')
    await grant.click()
    await settled('Read docs.linuxserver.io', 'Done')
    check(/^approvals\/approval-[a-f0-9]{32}$/.test(last().path) && same(last().body, { approved: true, always: true }) && last().csrf === 'fixture-csrf-request',
      'Unexpected grant request: ' + JSON.stringify(last()))
    await focused(tool('Read docs.linuxserver.io').locator('[data-slot="tool-header"]'), 'Approving did not move focus to the call')

    // A destructive call always asks, says what it risks, looks dangerous, and can't be allowed for the chat; a decline can say why.
    const removal = approval('Delete whoami')
    await wait(removal)
    check(await removal.locator('[data-slot="confirmation-title"]').innerText() === 'Lucia stops whoami and removes it from its server. Its data directory stays there.',
      'The destructive approval did not say what it risks')
    check(await removal.getByRole('button', { name: 'Approve', exact: true }).evaluate(element => element.classList.contains('assistant-danger')), 'Approving a deletion did not look dangerous')
    check(await removal.getByRole('button', { name: /^Approve, and let it/ }).count() === 0, 'A destructive call offered to skip asking')
    await atBottom('The destructive approval')
    check(await inView(removal), 'The destructive approval was cut off')
    await page.emulateMedia({ colorScheme: 'dark' })
    await page.waitForTimeout(300)
    await shot('assistant-tools-destructive-dark')
    await page.emulateMedia({ colorScheme: 'light' })
    const declining = removal.getByRole('button', { name: 'Decline…', exact: true })
    await declining.click()
    const why = removal.getByRole('textbox', { name: 'Tell the assistant why (optional)', exact: true })
    await focused(why, 'Decline… did not focus the reason')
    await removal.getByRole('button', { name: 'Back', exact: true }).click()
    await focused(declining, 'Back did not return focus to Decline…')
    await declining.click()
    await why.fill(reason)
    await settle()
    check(await inView(removal.getByRole('button', { name: 'Decline', exact: true })), 'Opening the decline form left its buttons out of view')
    await shot('assistant-tools-decline')
    await removal.getByRole('button', { name: 'Decline', exact: true }).click()
    await settled('Delete whoami', 'Not run', finished[4][2])
    check(same(last().body, { approved: false, reason }), 'Unexpected decline request: ' + JSON.stringify(last()))

    // A question offers its choices and a typed answer; answering moves focus to the call.
    await wait(question)
    await settled('Which server should run Jellyfin?', 'Waiting for you')
    check(same(await question.locator('.assistant-choices button').allInnerTexts(), ['lucialab01', 'lucialab02']), 'The choices did not render')
    check(await question.getByRole('textbox', { name: 'Or type your answer', exact: true }).count() === 1, 'The question offered no typed answer')
    check(await question.getByRole('button', { name: 'Send answer', exact: true }).getAttribute('aria-disabled') === 'true', 'An empty answer could be sent')
    await atBottom('The question')
    await shot('assistant-tools-question')
    await question.getByRole('button', { name: 'lucialab02', exact: true }).click()
    await settled('Which server should run Jellyfin?', 'Answered', finished[5][2])
    check(same(last().body, { answer: 'lucialab02' }) && /^answers\/call_/.test(last().path), 'Unexpected answer request: ' + JSON.stringify(last()))
    await focused(tool('Which server should run Jellyfin?').locator('[data-slot="tool-header"]'), 'Answering did not move focus to the question')

    // A secret is typed into a password field the model never sees, and only Lucia's unquoted characters are accepted.
    const secret = page.getByRole('form', { name: 'PLEX_CLAIM_TOKEN for media', exact: true })
    await wait(secret)
    await settled('media needs PLEX_CLAIM_TOKEN', 'Waiting for you')
    await atBottom('The secret request')
    const field = secret.locator('input[type="password"]')
    check(await field.evaluate(input => input.labels[0]?.textContent) === 'PLEX_CLAIM_TOKEN for media', 'The secret field was not labelled')
    await field.fill('claim token')
    check(await field.getAttribute('aria-invalid') === 'true' && await secret.getByRole('button', { name: 'Save secret', exact: true }).getAttribute('aria-disabled') === 'true',
      'A secret with a space could be saved')
    await field.fill('claim-4n8Xq2')
    await settle()
    await shot('assistant-tools-secret')
    await secret.getByRole('button', { name: 'Save secret', exact: true }).click()
    await settled('media needs PLEX_CLAIM_TOKEN', 'Saved', finished[6][2])
    check(same(last().body, { secret: 'claim-4n8Xq2' }), 'Unexpected secret request')

    // The answer ends the turn; the settled calls survive a reload, with why each ran in its details.
    await wait(page.getByText(closing))
    await wait(button('Send message'))
    check(await page.locator('.assistant-tool[data-waiting]').count() === 0, 'A settled call still looks like it waits')
    check(same(await rows(), finished), 'Unexpected calls: ' + JSON.stringify(await rows()))
    await noOverflow('1440 settled calls')
    await shot('assistant-tools-done')
    await page.reload()
    await wait(page.getByText(closing))
    check(same(await rows(), finished), 'The calls did not survive a reload: ' + JSON.stringify(await rows()))
    await tool('Restart whoami').locator('[data-slot="tool-header"]').click()
    await wait(tool('Restart whoami').getByText('Runs automatically in your assistant settings.', { exact: true }))
    await tool('Read docs.linuxserver.io').locator('[data-slot="tool-header"]').click()
    await wait(tool('Read docs.linuxserver.io').getByText('You approved this.', { exact: true }))
    await shot('assistant-tools-details')
    await page.emulateMedia({ colorScheme: 'dark' })
    await page.waitForTimeout(300)
    await shot('assistant-tools-dark')
    await page.emulateMedia({ colorScheme: 'light' })

    // Plan mode refuses every change on its own and says how to run it.
    const before = posts.length
    await button('New chat').click()
    await button('Plan').click()
    await button(starter).click()
    await wait(page.getByText(planned))
    await settled('List apps', 'Done')
    const plan = 'Plan mode doesn\'t change anything. Switch to Execute to run it.'
    await settled('Restart whoami', 'Not run', plan)
    await settled('Delete whoami', 'Not run', plan)
    check(posts.length === before && await page.locator('.assistant-card').count() === 0, 'Plan mode asked the owner something')
    await shot('assistant-tools-plan')
    await button('Execute').click()

    // Stopping while an approval waits refuses it with the host's reason.
    await button('New chat').click()
    await button(starter).click()
    await wait(approval('Read docs.linuxserver.io'))
    await button('Stop answering').click()
    await settled('Read docs.linuxserver.io', 'Not run', 'The run stopped before you answered.')
    check(await page.locator('.assistant-card').count() === 0, 'A card outlived its stopped turn')
    await wait(button('Send message'))

    // A closed dock counts what waits and names a new request; phones show the count on the bar.
    await button('New chat').click()
    await button(starter).click()
    await page.keyboard.press('Control+j')
    await dock.waitFor({ state: 'hidden' })
    const pill = page.locator('.portal-assistant-waiting')
    await wait(pill)
    check(await pill.innerText() === '1' && await toggle.getAttribute('aria-label') === 'Assistant, 1 waiting for you', 'The header did not count the waiting request')
    await page.waitForFunction(text => [...document.querySelectorAll('[role="status"]')].some(item => item.textContent === text),
      'Approval needed: Read docs.linuxserver.io', { timeout: 5000 })
    await shot('assistant-tools-waiting')
    await page.setViewportSize({ width: 390, height: 844 })
    await layout('sheet')
    const bar = page.locator('.assistant-bar button')
    await wait(bar)
    check(await bar.getAttribute('aria-label') === 'Ask the assistant, 1 waiting for you' && await bar.locator('.assistant-waiting').innerText() === '1 waiting',
      'The phone bar did not count the waiting request')
    await noOverflow('390 bar with a waiting request')
    await shot('assistant-tools-bar-mobile')
    await bar.click()
    await wait(approval('Read docs.linuxserver.io'))
    await dock.evaluate(element => Promise.all(element.getAnimations().map(animation => animation.finished)))
    await noOverflow('390 sheet with an approval')
    await atBottom('The phone approval')
    check(await inView(approval('Read docs.linuxserver.io')), 'The phone approval was cut off')
    await shot('assistant-tools-mobile')
    await approval('Read docs.linuxserver.io').getByRole('button', { name: 'Approve', exact: true }).click()
    await settled('Read docs.linuxserver.io', 'Done')
    check(same(last().body, { approved: true }), 'Unexpected approval request: ' + JSON.stringify(last()))
    await wait(approval('Delete whoami'))
    await approval('Delete whoami').getByRole('button', { name: 'Approve', exact: true }).click()
    await settled('Delete whoami', 'Done')
    await wait(question)
    await noOverflow('390 sheet with a question')
    await atBottom('The phone question')
    await shot('assistant-tools-question-mobile')

    // Stopping while a question waits ends it as unfinished, not failed.
    await button('Stop answering').click()
    await settled('Which server should run Jellyfin?', 'Not finished', 'The run stopped before this finished.')
    await shot('assistant-tools-stopped-mobile')
    await page.setViewportSize({ width: 1440, height: 900 })
    await layout('push')
    if (await dock.isVisible()) await button('Close assistant').click()
    await dock.waitFor({ state: 'hidden' })

    // Going public and running a script as root each say what they risk, and the script shows line by line.
    await start('danger')
    await button(starter).click()
    const publish = approval('Publish whoami.lab.example as whoami.example.com')
    await wait(publish)
    check(await publish.locator('[data-slot="confirmation-title"]').innerText() === 'Anyone on the internet will be able to reach whoami.example.com.',
      'The publish approval did not say what it risks')
    const published = tool('Publish whoami.lab.example as whoami.example.com').locator('[data-slot="tool-title"]')
    check(await whole(published, 'whoami.lab.example') && await whole(published, 'whoami.example.com'), 'The waiting publish title broke a name')
    await atBottom('The publish approval')
    check(await inView(publish), 'The publish approval was cut off')
    await shot('assistant-tools-publish')
    await publish.getByRole('button', { name: 'Approve', exact: true }).click()
    await settled('Publish whoami.lab.example as whoami.example.com', 'Done')
    const script = approval('Run a command on lucialab02')
    await wait(script)
    check(await script.locator('[data-slot="confirmation-title"]').innerText() === 'This runs the command below as root on lucialab02. It can change anything there.',
      'The command approval did not say what it risks')
    const gpuCheck = 'set -e\nlspci -nn | grep -i nvidia\nnvidia-smi --query-gpu=name,driver_version --format=csv,noheader'
    const lines = script.locator('[data-slot="tool-input-lines"] > li')
    check(same(await lines.allInnerTexts(), gpuCheck.split('\n')), 'The script did not show line by line: ' + JSON.stringify(await lines.allInnerTexts()))
    check((await lines.evaluateAll(items => items.map(item => getComputedStyle(item, '::before').content))).every(content => content === 'counter(line)'),
      'The script lines were not numbered')
    await atBottom('The command approval')
    check(await inView(script), 'The command approval was cut off')
    await noOverflow('1440 command approval')
    await shot('assistant-tools-command')
    await page.setViewportSize({ width: 390, height: 844 })
    await layout('sheet')
    await dock.evaluate(element => Promise.all(element.getAnimations().map(animation => animation.finished)))
    await atBottom('The phone command approval')
    check(await inView(script), 'The phone command approval was cut off')
    check(await whole(published, 'whoami.example.com'), 'The phone publish title broke a name')
    check(await lines.nth(2).evaluate(item => item.getClientRects()[0].height > parseFloat(getComputedStyle(item).lineHeight) * 1.5),
      'The long script line did not wrap on a phone, so its numbering went untested')
    await noOverflow('390 command approval')
    await shot('assistant-tools-command-mobile')
    await script.getByRole('button', { name: 'Approve', exact: true }).click()
    await settled('Run a command on lucialab02', 'Done')
    await wait(page.getByText('sees its RTX 3090 with driver 550.120.'))
    await wait(button('Send message'))
    await page.setViewportSize({ width: 1440, height: 900 })
    await layout('push')
    if (await dock.isVisible()) await button('Close assistant').click()
    await dock.waitFor({ state: 'hidden' })

    // Settings: the change tools that run without asking, and the sites read without asking.
    await page.goto(base + '/#/settings/assistant')
    await wait(page.getByRole('heading', { name: 'Assistant', level: 1, exact: true }))
    const changes = page.getByRole('group', { name: 'Changes it may make without asking', exact: true })
    await wait(changes.getByRole('checkbox'))
    check(same(await changes.locator('label').allInnerTexts(), ['Create and change custom apps', 'Install catalog apps and change their settings',
      'Start, stop, restart and update apps', 'Move apps between servers', 'Back up apps', 'Upgrade app images', 'Check servers for updates']),
      'Unexpected change tools: ' + JSON.stringify(await changes.locator('label').allInnerTexts()))
    check(same(await changes.getByRole('checkbox').evaluateAll(boxes => boxes.map(box => box.checked)), [false, false, true, false, false, false, false]),
      'The saved automatic tools were not checked')
    const allowed = page.getByRole('textbox', { name: 'Allowed sites', exact: true })
    check(await allowed.inputValue() === sites.join('\n'), 'The allowed sites did not load')
    await noOverflow('1440 settings')
    await shot('assistant-settings')
    await changes.getByRole('checkbox', { name: 'Back up apps', exact: true }).check()
    await allowed.fill(sites.join('\n') + '\ndocs.linuxserver.io, https://Docs.Linuxserver.io/images/docker-jellyfin/')
    await button('Save settings').click()
    const savedNotice = page.getByText('Saved. The assistant follows these from your next message.', { exact: true })
    await wait(savedNotice)
    const savedBox = await savedNotice.boundingBox()
    check(savedBox.y >= 0 && savedBox.y + savedBox.height <= 900, 'The save result showed out of view at ' + JSON.stringify(savedBox))
    check(same(last().body, { autoTools: ['app_action', 'run_backup'], hosts: [...sites, 'docs.linuxserver.io', 'docs.linuxserver.io'] }) && last().csrf === 'fixture-csrf-request',
      'Unexpected settings request: ' + JSON.stringify(last()))
    check(await allowed.inputValue() === [...sites, 'docs.linuxserver.io'].join('\n'), 'The saved sites were not shown as the host keeps them')
    await shot('assistant-settings-saved')
    await allowed.fill('192.168.0.10')
    check(await savedNotice.count() === 0, 'Editing kept the stale saved notice')
    await button('Save settings').click()
    const invalid = page.getByRole('alert').filter({ hasText: '192.168.0.10 is an address, not a site name. Add sites by name, like docs.docker.com.' })
    await wait(invalid)
    await focused(allowed, 'The refused sites did not take focus')
    check(await allowed.getAttribute('aria-invalid') === 'true'
      && (await allowed.getAttribute('aria-describedby')).split(' ').includes(await invalid.getAttribute('id')), 'The refused sites were not marked, or not linked to why')
    check(await allowed.evaluate(field => {
      const probe = document.createElement('i')
      probe.style.color = 'var(--failed-ink)'
      field.parentElement.append(probe)
      const ink = getComputedStyle(probe).color
      probe.remove()
      return getComputedStyle(field).borderTopColor === ink
    }), 'The refused sites field did not take the failure colour')
    await page.setViewportSize({ width: 390, height: 844 })
    await page.waitForTimeout(300)
    await noOverflow('390 settings')
    await page.mouse.move(195, 400)
    await page.mouse.wheel(0, 6000)
    await page.waitForTimeout(400)
    const errorBox = await invalid.boundingBox(), barBox = await page.locator('.assistant-bar').boundingBox()
    check(errorBox.y >= 0 && errorBox.y + errorBox.height <= barBox.y, `The phone bar covered the save error (${JSON.stringify(errorBox)} vs ${JSON.stringify(barBox)})`)
    await shot('assistant-settings-mobile')
    await page.setViewportSize({ width: 1440, height: 900 })

    check(posts.every(item => item.csrf === 'fixture-csrf-request'), 'An answer went out without the CSRF token')
    check(errors.length === 0, 'Page errors: ' + errors.join(' | '))
    return { calls: finished.length, grant: true, decline: true, question: true, secret: true, reload: true, plan: true, stop: true, waiting: true, phone: true,
      danger: true, settings: true }
  } finally {
    page.off('pageerror', onPageError)
    page.off('console', onConsole)
    page.off('request', onRequest)
    await page.emulateMedia({ colorScheme: null }).catch(() => {})
    await page.request.get(base + '/__fixture?mode=owner').catch(() => {})
  }
}
