async (page) => {
  const base = 'http://192.168.0.222:36263';
  const output = 'E:\\github\\lucia-os\\.impeccable\\screenshots';
  const leakedHeaders = [];
  const pageErrors = [];
  page.on('request', request => {
    if (request.url().includes('/api/playground/') && request.headers().authorization) leakedHeaders.push(request.url());
  });
  page.on('pageerror', error => pageErrors.push(error.message));
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  await page.setViewportSize({ width: 1280, height: 1050 });
  await page.goto(base + '/#/ai');
  await page.evaluate(() => localStorage.setItem('lucia.preview.appearance.v1', JSON.stringify({ appearance: 'light', theme: 'ocean', customAccent: '#285bdd' })));
  await page.reload();
  await page.getByText('Ready to respond', { exact: true }).waitFor();
  check(await page.getByLabel('Base URL', { exact: true }).inputValue() === 'http://192.168.0.222:5329/v1', 'Wrong external API base URL.');
  await page.getByLabel('Your message', { exact: true }).fill('Reply with exactly PLAYGROUND_CONNECTED and nothing else.');
  await page.getByRole('button', { name: 'Send message', exact: true }).click();
  await page.locator('.message-assistant .message-state').filter({ hasText: 'Complete' }).waitFor({ timeout: 120000 });
  const response = await page.locator('.message-assistant .message-text').last().innerText();
  check(response.includes('PLAYGROUND_CONNECTED'), 'The hosted model did not answer the GUI probe.');
  await page.screenshot({ path: output + '\\local-ai-desktop-light.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  check(!await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), 'Mobile playground overflows horizontally.');
  await page.screenshot({ path: output + '\\local-ai-mobile-light.png', fullPage: true });

  await page.setViewportSize({ width: 1280, height: 1050 });
  await page.evaluate(() => localStorage.setItem('lucia.preview.appearance.v1', JSON.stringify({ appearance: 'dark', theme: 'ocean', customAccent: '#285bdd' })));
  await page.reload();
  await page.getByText('Ready to respond', { exact: true }).waitFor();
  await page.screenshot({ path: output + '\\local-ai-desktop-dark.png', fullPage: true });
  await page.getByText('Response settings', { exact: true }).click();
  await page.getByRole('spinbutton', { name: 'Maximum response tokens' }).fill('1024');
  await page.getByLabel('Your message', { exact: true }).fill('Count from 1 to 1000, one number per line, without skipping any number.');
  await page.getByRole('button', { name: 'Send message', exact: true }).click();
  await page.getByRole('button', { name: 'Stop response', exact: true }).click();
  await page.locator('.message-state').filter({ hasText: 'Stopped by you' }).waitFor();
  const refreshed = page.waitForResponse(response => response.url().includes('/api/playground/models') && response.status() === 200, { timeout: 15000 });
  await page.getByRole('button', { name: 'Refresh status', exact: true }).click();
  await refreshed;
  await page.getByText('Ready to respond', { exact: true }).waitFor({ timeout: 15000 });

  await page.route('**/api/playground/models', route => route.fulfill({ status: 200, contentType: 'application/json', body: '{"object":"list","data":[]}' }));
  await page.reload();
  await page.getByText('There is no model to talk to yet.', { exact: true }).waitFor();
  check(await page.getByRole('button', { name: 'Send message', exact: true }).isDisabled(), 'No-model state allowed generation.');
  await page.unroute('**/api/playground/models');
  await page.route('**/api/playground/models', route => route.fulfill({ status: 503, contentType: 'application/json', body: '{"error":{"message":"Host temporarily unavailable."}}' }));
  await page.reload();
  await page.getByText('We could not connect.', { exact: true }).waitFor();
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({ path: output + '\\local-ai-mobile-error.png', fullPage: true });
  await page.unroute('**/api/playground/models');
  await page.reload();
  await page.getByText('Ready to respond', { exact: true }).waitFor();
  await page.route('**/api/playground/chat', route => route.fulfill({ status: 200, contentType: 'text/event-stream', body: 'data: {"choices":[{"delta":{"content":"Partial answer"},"finish_reason":null}]}\n\n' }));
  await page.getByLabel('Your message', { exact: true }).fill('Exercise an interrupted response.');
  await page.getByRole('button', { name: 'Send message', exact: true }).click();
  await page.getByText('The connection ended before the model finished. Any partial response is shown below.', { exact: true }).waitFor();
  check(await page.getByText('Request interrupted', { exact: true }).count() === 1, 'Truncated connection was reported complete.');
  await page.unroute('**/api/playground/chat');
  check(leakedHeaders.length === 0, 'Browser sent an inference credential.');
  check(pageErrors.length === 0, JSON.stringify(pageErrors));
  for (const width of [320, 390, 768, 1280]) {
    await page.setViewportSize({ width, height: 1000 });
    check(!await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), 'Playground overflow at ' + width);
  }
  await page.evaluate(() => localStorage.setItem('lucia.preview.appearance.v1', JSON.stringify({ appearance: 'light', theme: 'ocean', customAccent: '#285bdd' })));
  await page.reload();
  return { actualModelResponse: response, stopped: true, noModelAndErrorStates: true, interruptedStreamReported: true, browserAuthorizationHeaders: leakedHeaders, pageErrors, screenshots: output };
}
