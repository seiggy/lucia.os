// Part 4: Local AI: the Playground, an API key for another app, then the model library and Hugging Face downloads.
// It creates a "Demo video" API key and revokes it again. Nothing is downloaded or loaded, and the Spark recipe keeps running.
export const start = '#/ai';

const then = (...steps) => async h => { for (const step of steps) await step(h); };
// Scrolls smoothly to a target, then spotlights it. Targets must fit below the sticky header, about 520px.
const at = (find, label = '', ms) => async h => {
  const target = find(h.page);
  await h.scroll(target);
  await h.spotlight(target, label, ms);
};
// The Playground stays mounted, hidden, under the other Local AI pages, so only the visible intro counts.
const intro = p => p.locator('.page-intro:visible');
const go = name => async h => {
  await h.page.getByRole('navigation', { name: 'Local AI navigation' }).getByRole('link', { name, exact: true }).click();
  await intro(h.page).waitFor();
  await h.pause(900);
};
const type = (find, text, delay = 55) => h => find(h.page).pressSequentially(text, { delay });
const reply = p => p.locator('.message-assistant').last();
// Sends the composer's message and waits until the reply is complete.
const send = async h => {
  const replies = h.page.locator('.message-assistant');
  const count = await replies.count();
  await h.page.getByRole('button', { name: /^Send message/ }).click();
  await replies.nth(count).locator('.message-state', { hasText: /^Complete/ }).waitFor({ timeout: 60000 });
};
const demoKey = p => p.locator('.key-list > article').filter({ has: p.getByRole('heading', { name: 'Demo video', exact: true }) });
const shipped = p => p.locator('.model-library-list > article').filter({ hasText: 'Qwen3.6-35B-A3B' });
const repository = p => p.locator('.model-repository');
// A section's title card shows while its first line starts; spotlights wait for it to fade.
const card = (title, description) => h => h.chapter(title, description, 2000, 0);

export default [
  h => h.chapter('Lucia', 'Part 4 · Local AI', 3400, 1000),
  [`Welcome back. This is Local AI, where you talk to the models your Spark runs, and let your other apps use them.`, h => h.pause(1500)],
  [`It has four pages: the Playground, Models, API keys, and Assistant jobs, which we'll see with the assistant in the next video.`,
    at(p => p.getByRole('navigation', { name: 'Local AI navigation' }), '', 8000)],
  [`The Playground is a conversation with your hosted model. It stays in this tab while you explore Lucia,
    and refreshing or signing out clears it.`, at(intro, '', 8300)],
  [`Let's start small. The suggested prompt fills in a message, and we send it.`, async h => {
    await h.scroll(h.page.locator('.conversation-empty'));
    await h.page.locator('.suggested-prompt').click();
    await h.pause(700);
    await send(h);
  }],
  [`It answers in about a second. Each reply shows how long it took, and how many tokens it used.`, at(reply, '', 5800)],
  [`Response settings apply to the next message: the temperature, a response limit, and an optional system instruction.`, async h => {
    await h.page.locator('details.generation-settings summary').click();
    await h.pause(300);
    await at(p => p.locator('details.generation-settings'), '', 6400)(h);
  }],
  [`Let's give it some personality, and ask again.`, then(
    type(p => p.locator('#ai-system'), 'Talk like a pirate.', 30),
    type(p => p.locator('#ai-prompt'), 'Say hello in one short sentence.', 25),
    send)],
  [`Now it answers like a pirate. The system instruction goes first in every request, ahead of the conversation so far.`,
    at(reply, '', 7200)],
  [`Curious what was sent? View the request shows the exact JSON: the model, the messages, and your settings.`, async h => {
    await h.page.locator('details.request-details summary').click();
    await h.pause(300);
    const pre = h.page.locator('details.request-details pre');
    await Promise.all([at(p => p.locator('details.request-details'), '', 6200)(h),
      h.pause(3800).then(() => pre.evaluate(el => el.scrollTo({ top: el.scrollHeight, behavior: 'smooth' })))]);
  }],

  [`On the right, Hosted model shows what your Spark is serving, and whether it's ready. Right now, that's Qwen3.8-Flash-Next.`,
    at(p => p.locator('.ai-details > .reading-surface').first(), '', 7600)],
  [`It serves up to two hundred thousand tokens of context, with replies of up to about thirty-two thousand.
    TensorFold is the engine behind it.`, at(p => p.locator('.ai-details .fact-list'), '', 9000)],
  [`To connect another app, give it this base URL. Lucia speaks the OpenAI-compatible API,
    so most AI tools need only the address, and a key.`, at(p => p.locator('#endpoint-url'), '', 10000)],
  [`It covers chat, responses, models, and embeddings.`, at(p => p.locator('.endpoint-list'), '', 3200)],
  [`Your browser uses your sign-in, so there's no shared key in the dashboard. Every other app gets an API key of its own.`, async h => {
    await at(p => p.locator('.connection-note'), '', 6800)(h);
    await h.page.getByRole('link', { name: /^Create or manage API keys/ }).click();
    await h.page.waitForFunction(() => location.hash === '#/ai/keys');
  }],

  [`API keys let your apps use your models, without any control of your host.
    Make one for each app, so you can revoke one without touching the rest.`,
    then(card('API keys', 'Access for your other apps'), h => h.page.locator('.key-list').waitFor(), at(p => p.locator('.key-page-heading'), '', 8600))],
  [`Here are keys for two apps I already use. Each shows when it was made, and when it expires.`, at(p => p.locator('.key-list'), '', 5300)],
  [`Let's make one for this video. Give it a name, and if you like, an expiry date.`, async h => {
    await h.page.getByRole('button', { name: /^New API key/ }).click();
    await type(p => p.locator('.key-create').getByRole('textbox', { name: /^Name/ }), 'Demo video', 80)(h);
    await at(p => p.locator('.key-create'), '', 3600)(h);
  }],
  [`Create it, and the key appears just once. Lucia keeps only a hash of it, so copy it into your app now.`, async h => {
    await h.page.getByRole('button', { name: /^Create inference key/ }).click();
    await at(p => p.locator('.key-secret'), '', 5800)(h);
  }],
  [`Once it's saved, the new key joins the list.`, async h => {
    await h.page.getByRole('button', { name: 'I have saved it' }).click();
    await at(demoKey, '', 2400)(h);
  }],
  [`When an app no longer needs access, revoke its key, and its next request is turned away.`, async h => {
    await h.page.getByRole('button', { name: 'Revoke Demo video', exact: true }).click();
    await at(p => p.locator('.key-revoke'), '', 2800)(h);
    await h.page.locator('.key-revoke').getByRole('button', { name: 'Revoke key' }).click();
    await h.page.locator('.key-notice', { hasText: /revoked/ }).waitFor();
  }],

  [`Models shows what runs in your lab. Lucia's own engine runs one LLM and one embedding model,
    and keeps room for the rest of your system.`, then(go('Models'), card('Models', 'What runs on your Spark'), at(intro, '', 7600))],
  [`Right now, the Spark runs a recipe instead: Qwen3.8-Flash-Next on TensorFold,
    with four conversations at once, and two hundred thousand tokens of context each.`,
    at(p => p.locator('section[aria-labelledby="qwen-heading"]'), '', 11000)],
  [`The recipe needs nearly all of the Spark's memory, so Lucia's own models are paused. Stop the recipe, and Lucia loads them again.`,
    at(p => p.locator('section[aria-labelledby="loaded-models-heading"]'), '', 8000)],
  [`Below is the library: the models downloaded to your Spark.`, at(p => p.locator('.model-library-list'), '', 3400)],
  [`Let's check the model Lucia ships with, Qwen3.6. Context and load works out what fits, before you load anything.`, async h => {
    const button = shipped(h.page).getByRole('button', { name: /^Context & load/ });
    await h.scroll(button);
    await button.click();
    await shipped(h.page).locator('.model-estimate').waitFor();
  }],
  [`It adds up the model, reserves for the system and future voice features, and any other loaded model.
    The rest holds context, and here, the model's full two hundred sixty-two thousand tokens fit.`,
    at(p => shipped(p).locator('.model-estimate'), '', 12000)],
  [`Choose how much context to serve, from eight thousand tokens up to what fits.
    Lucia checks again when it loads, and won't load a model that doesn't fit.`, async h => {
    const input = shipped(h.page).locator('.model-context-input');
    await h.scroll(input);
    // Each Playwright input waits out the action annotation, so the thumb glides in the page instead, firing input events React reads.
    await Promise.all([h.spotlight(input, '', 8800), h.pause(1800).then(() => input.getByRole('slider').evaluate(el => new Promise(done => {
      const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
      const from = +el.value, to = +el.max, start = performance.now();
      const frame = now => {
        const k = Math.min(1, (now - start) / 4000);
        set.call(el, from + (to - from) * (k < .5 ? 2 * k * k : 1 - 2 * (1 - k) ** 2));
        el.dispatchEvent(new Event('input', { bubbles: true }));
        if (k < 1) requestAnimationFrame(frame); else done();
      };
      requestAnimationFrame(frame);
    })))]);
  }],

  [`Find and download explores Hugging Face for GGUF models, the format Lucia's engine runs.`, async h => {
    await h.page.getByRole('navigation', { name: 'Model library navigation' }).getByRole('link', { name: /Find & download/ }).click();
    await h.page.waitForFunction(() => location.hash === '#/ai/models/find');
    await card('Find & download', 'Models from Hugging Face')(h);
    await at(intro, '', 5600)(h);
  }],
  [`Search works without an account. Add a Hugging Face token for gated models and higher download limits,
    and Lucia stores it encrypted on your Spark.`, at(p => p.locator('details.provider-connection'), '', 8500)],
  [`Let's look for Unsloth's build of Qwen3 8B.`, async h => {
    await type(p => p.getByRole('textbox', { name: 'Search Hugging Face' }), 'unsloth/Qwen3-8B', 70)(h);
    await h.page.getByRole('button', { name: 'Search' }).click();
    await h.page.locator('.model-search-result').first().waitFor({ timeout: 30000 });
  }],
  [`Open a result, and Lucia pins the repository's current revision, so you get exactly the files you looked at.`, async h => {
    const result = h.page.locator('.model-search-result').filter({ hasText: /^unsloth\/Qwen3-8B-GGUF/ });
    await at(() => result, '', 2000)(h);
    await result.click();
    await repository(h.page).locator('#model-quantization').waitFor({ timeout: 30000 });
    await at(repository, '', 3200)(h);
  }],
  [`Each file is a different quantization. Smaller ones leave more room for context, sometimes at a cost in quality.
    We'll pick Q4_K_M, under five gigabytes.`, async h => {
    const select = h.page.locator('#model-quantization');
    await h.spotlight(select, '', 8000);
    await select.selectOption({ label: 'Q4_K_M · 4.7 GiB · Qwen3-8B-Q4_K_M.gguf' });
  }],
  [`Before you download anything, Lucia reads a few megabytes of the file's header, and estimates the context that fits.
    Here, that's the model's full limit, about forty-one thousand tokens.`, async h => {
    const estimate = repository(h.page).locator('.model-estimate');
    await estimate.waitFor({ timeout: 30000 });
    await at(() => estimate, '', 11000)(h);
  }],
  [`Models from outside Lucia's catalog need one more step: confirm you understand this file isn't qualified for your Spark yet.
    Lucia inspects the whole file before it can load.`,
    then(at(p => repository(p).locator('.model-checkbox'), '', 8200), at(p => p.getByRole('button', { name: /^Download selected model/ }), '', 2400))],
  [`Have another server with its own GPU? The Local AI app from the catalog runs models there, with Lucia's engine, vLLM, or llama.cpp,
    and you manage them the same way.`, at(intro, '', 11000)],
  [`That's Local AI: chat with your models, connect your apps, and choose what runs. In the last video, we meet the assistant,
    which helps you run your lab, with your approval.`,
    then(h => h.pause(3200), h => h.chapter('Up next', 'Part 5 · The assistant', 6800))],
];
