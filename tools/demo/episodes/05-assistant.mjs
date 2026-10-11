// Part 5: the assistant: a question, an approved change, its ground rules, then assistant jobs and notifications.
// It restarts flaresolverr with approval on camera, and deletes the chat it started at the end. Settings, jobs and devices don't change.
export const start = '#/apps';

const then = (...steps) => async h => { for (const step of steps) await step(h); };
// Scrolls smoothly to a target, then spotlights it. Targets must fit below the sticky header, about 520px.
const at = (find, label = '', ms) => async h => {
  const target = find(h.page);
  await h.scroll(target);
  await h.spotlight(target, label, ms);
};
const type = (find, text, delay = 55) => h => find(h.page).pressSequentially(text, { delay });
const section = (p, name) => p.locator('section').filter({ has: p.getByRole('heading', { name, exact: true }) });
const dock = p => p.locator('#assistant-dock');
const latest = p => dock(p).locator('.is-assistant').last();
// Empty text parts leave hidden replies behind.
const reply = p => latest(p).locator('.assistant-reply:visible').last();
const closeDock = h => dock(h.page).getByRole('button', { name: 'Close assistant' }).click();
// The send button reads Stop answering until the answer ends, including while an approval card waits.
const answered = (h, card) => h.cut(async () => {
  await dock(h.page).getByRole('button', { name: 'Stop answering' }).waitFor({ timeout: 5000 }).catch(() => {});
  await dock(h.page).locator(`${card ? '.assistant-card, ' : ''}.assistant-send[aria-label="Send message"]`).first().waitFor({ timeout: 180000 });
  await h.pause(600);
});
const ask = (text, card) => async h => {
  await type(p => p.getByRole('textbox', { name: 'Message the assistant' }), text, 30)(h);
  await dock(h.page).getByRole('button', { name: 'Send message' }).click();
  await answered(h, card);
};
// Every assistant page is one search away.
const search = (link, hash) => async h => {
  await h.page.keyboard.press('Control+k');
  await h.pause(400);
  await h.page.keyboard.type('assistant', { delay: 90 });
  await h.pause(500);
  await h.page.getByRole('link', { name: link }).click();
  await h.page.waitForFunction(hash => location.hash === hash, hash);
  await h.pause(900);
};
// A section's title card shows over the page it opens; spotlights wait for it to fade.
const chapter = (title, description) => h => h.chapter(title, description, 2000, 0);
// Eases the open chat's scroller to a position, or to its end, without input actions.
const glide = (ms, to = -1) => h => h.page.locator('#assistant-dock .assistant-conversation').evaluate((root, [ms, to]) => new Promise(done => {
  const el = [...root.querySelectorAll('*')].find(e => /auto|scroll/.test(getComputedStyle(e).overflowY));
  const from = el.scrollTop, end = to < 0 ? el.scrollHeight - el.clientHeight : to, start = performance.now();
  const frame = now => {
    const k = ms ? Math.min(1, (now - start) / ms) : 1;
    el.scrollTop = from + (end - from) * (k < .5 ? 2 * k * k : 1 - 2 * (1 - k) ** 2);
    if (k < 1) requestAnimationFrame(frame); else done();
  };
  requestAnimationFrame(frame);
}), [ms, to]);
const job = p => p.locator('.job-table tbody tr').filter({ hasText: 'Fix stopped containers' });
const chats = h => h.page.evaluate(async () => (await (await fetch('/api/assistant/sessions', { cache: 'no-store' })).json()).sessions);
let before;

export default [
  async h => { before = new Set((await chats(h)).map(chat => chat.id)); },
  h => h.chapter('Lucia', 'Part 5 · The assistant', 3400, 1000),
  [`Welcome back. In this last video, we meet the assistant. It checks your servers, apps and logs,
    and asks before it changes anything.`, h => h.pause(1000)],
  [`Open it with the Assistant button, or press Control J on any page.`, async h => {
    const button = h.page.locator('.portal-assistant-button');
    await h.spotlight(button, '', 2400);
    await button.click();
    await dock(h.page).locator('.assistant-empty').waitFor();
  }],
  [`It opens beside the page, and knows which page you're on, so you can ask about what you see. The starters are an easy way in.`,
    at(p => dock(p).locator('.assistant-empty'), '', 7400)],
  [`Pick the model that answers here: GitHub Copilot's models, models from your LiteLLM app, or Local AI models in your lab.
    Only Copilot needs a GitHub sign-in.`, at(p => dock(p).locator('.assistant-model'), '', 11000)],
  [`Execute lets it carry out changes, asking you first. In Plan, it can look things up and plan, but it changes nothing at all.`,
    at(p => dock(p).locator('.assistant-mode'), '', 7800)],
  [`Let's ask how my servers are doing.`, ask('How are my servers doing? Just the highlights, please.')],
  [`It used a lookup tool to check them. Looking around your lab never needs your approval. Then it summed up the highlights.`,
    then(at(p => latest(p).locator('.assistant-tool').first(), '', 3800), at(reply, '', 2400))],
  [`Now, a change. Let's ask it to restart FlareSolverr, one of my apps.`, ask('Restart flaresolverr for me.', true)],
  [`Changes stop and wait for you. The card shows what it wants to do, and to which app. Nothing happens until you answer.`,
    at(p => dock(p).locator('.assistant-tool[data-waiting]'), '', 7000)],
  [`Approve it once, decline it with a reason, or let it manage apps for the rest of this chat.`,
    at(p => dock(p).locator('.assistant-card-actions'), '', 5000)],
  [`Approvals are enforced by Lucia's server, not left to the model, so they can't be skipped. Let's approve this one.`, async h => {
    await h.pause(5600);
    await dock(h.page).getByRole('button', { name: 'Approve', exact: true }).click();
    await answered(h);
  }],
  [`The server applies the restart within half a minute, and the assistant reports back with what it could confirm.`, at(reply, '', 6000)],
  [`When an app needs a password or a token, the assistant asks for it in a card like that one.
    Lucia saves it in the app's settings, and the model never sees it.`, h => h.pause(1000)],
  [`Your chats are kept on your host. Start a new one, reopen one from the history, or move the panel to the other side.`,
    then(at(p => dock(p).locator('.assistant-header'), '', 6200), closeDock)],

  [`Its settings decide what it may do without asking. Control K finds them.`,
    then(search(/^Assistant What the assistant/, '#/settings/assistant'), chapter('Ground rules', 'What it may do without asking'))],
  [`Each kind of change can be allowed here. I've left them all unchecked, so every change waits for my approval.`,
    then(h => h.page.locator('.assistant-settings-tools .network-checkbox').first().waitFor(),
      at(p => section(p, 'Changes it may make without asking'), '', 6400))],
  [`Risky changes always ask in a chat, like deleting an app, restoring a backup, or running a command on a server.
    A job may do them only if that job allows it.`, at(p => p.getByText(/^In chats, it always asks/), '', 10200)],
  [`It may also read the public sites listed here, like Docker's docs and GitHub, and it asks before any other.
    It never reads your own network.`, at(p => section(p, 'Sites it may read without asking'), '', 8200)],

  [`Assistant jobs run saved prompts on a schedule, on their own.`,
    then(search(/^Assistant jobs/, '#/ai/jobs'), chapter('Assistant jobs', 'Prompts on a schedule'))],
  [`Each run is a new chat, and it notifies you when it needs you. This one checks every morning for containers that stopped
    when they shouldn't have. It reads their logs, fixes what it safely can, and tells me about the rest.`,
    then(at(p => p.locator('.job-table'), '', 3600), at(job, '', 8800))],
  [`Let's open its last run.`, async h => {
    await job(h.page).locator('.job-inline-link').click();
    await dock(h.page).locator('.assistant-bubble').waitFor({ timeout: 30000 });
    await h.pause(300);
    // The chat is still easing to its end: the first jump stops that, the second lands at the top.
    await glide(0, 0)(h);
    await h.pause(150);
    await glide(0, 0)(h);
    await h.pause(450);
  }],
  [`It listed the apps, servers and containers, and read the logs of the ones that looked wrong.
    No container needed fixing, but it found two failed backups, worked out why, and sent me a notification.`, glide(12500)],
  [`Making a job is quick. Choose New job, name it, and write its prompt in plain words. Follow-up prompts run in order, in the same chat.`,
    async h => {
      await closeDock(h);
      await h.page.locator('#job-new').click();
      await h.page.locator('.assistant-settings-tools .network-checkbox').first().waitFor();
      await at(p => p.locator('.job-prompt'), '', 6400)(h);
    }],
  [`Then pick when it runs: hourly, daily, on weekdays, weekly, or your own cron schedule. Lucia shows the next few runs, so you can check it.`,
    then(at(p => p.locator('.job-schedule'), '', 4800), h => h.page.locator('#job-next', { hasText: /^Next runs/ }).waitFor(),
      at(p => p.locator('#job-next'), '', 3000))],
  [`A job may also do some things without asking, on top of your settings. Risky changes are listed apart,
    because nobody checks them before they happen.`,
    then(at(p => p.locator('.assistant-settings-tools').first(), '', 3800), at(p => p.locator('.assistant-settings-tools').nth(1), '', 4800))],
  [`Anything else waits up to thirty minutes for your approval, and you get a notification.`, async h => {
    await at(p => p.locator('#job-inherited'), '', 4200)(h);
    await h.page.getByRole('button', { name: 'Cancel', exact: true }).click();
  }],

  [`Notifications are one more search away.`,
    then(search(/^Notifications Devices/, '#/settings/notifications'), chapter('Notifications', 'When the assistant needs you'))],
  [`They tell you when a job needs your approval, fails, or has something to say. Tap one, and its chat opens.`,
    at(p => p.locator('.page-intro'), '', 6400)],
  [`Turn them on in each browser you use. They arrive through the browser's own push service, signed with a key Lucia makes itself,
    so there's no account to set up.`, at(p => section(p, 'This device'), '', 9600)],
  [`On an iPhone or iPad, add Lucia to your Home Screen, and open it from there first.`, h => h.pause(1000)],
  [`These are my devices. Send each one a test, rename it, or remove it.`,
    then(h => h.page.locator('.job-devices li').first().waitFor(), at(p => section(p, 'Your devices'), '', 4400))],
  [`That's Lucia, end to end: a private dashboard for your lab, one account across it, your own AI,
    and an assistant that helps you run it all, with your approval. Thanks for watching.`,
    then(h => h.pause(4000), h => h.chapter('Lucia', 'Thanks for watching', 7400))],
  // Chats this video started go away; job runs are never touched.
  async h => {
    const fresh = (await chats(h)).filter(chat => !before.has(chat.id) && !chat.job).map(chat => chat.id);
    await h.page.evaluate(async ids => {
      const { csrfToken } = await (await fetch('/api/auth/session', { cache: 'no-store' })).json();
      for (const id of ids) await fetch(`/api/assistant/sessions/${id}`, { method: 'DELETE', headers: { 'X-CSRF-TOKEN': csrfToken } });
    }, fresh);
  },
];
