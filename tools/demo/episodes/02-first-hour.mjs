// Part 2: a tour of Home, then the connections Lucia builds on, in the order the "Your first hour" guide recommends.
export const start = '#/';

const then = (...steps) => async h => { for (const step of steps) await step(h); };
const region = (page, name) => page.getByRole('region', { name, exact: true });
const section = (page, name) => page.locator('section').filter({ has: page.getByRole('heading', { name, exact: true }) }).last();
// Scrolls smoothly to a target, then spotlights it. Targets must fit below the sticky header, about 520px.
const at = (find, label = '', ms) => async h => {
  const target = find(h.page);
  await h.scroll(target);
  await h.spotlight(target, label, ms);
};
const tab = name => async h => {
  await h.page.getByRole('navigation', { name: 'Settings navigation' }).getByRole('link', { name, exact: true }).click();
  await h.page.locator('.page-intro').waitFor();
  await h.pause(900);
};
const area = (i, ms) => h => h.spotlight(h.page.locator('.workspace-directory-heading').nth(i), '', ms);
// A section's title card shows while its first line starts; spotlights wait for it to fade.
const card = (title, description) => h => h.chapter(title, description, 2000, 0);

export default [
  h => h.chapter('Lucia', 'Part 2 · Your first hour', 3400, 1000),
  [`Welcome back. You're signed in, and this is Home, your lab at a glance.
    In this video, we tour the dashboard, then connect the services Lucia builds on, in an order that avoids backtracking.`,
    h => h.pause(2000)],

  [`Your Spark reports its CPU, memory, GPU and storage, live.`, at(p => region(p, 'Your Spark'), 'Live from your Spark', 3600)],
  [`On the Spark, the CPU and GPU share one pool of memory, so there's no separate GPU memory figure.`,
    at(p => p.getByText('The CPU and GPU share this memory'), '', 4600)],
  [`Open Last hour for a rolling history, sampled every ten seconds.`, async h => {
    await h.page.getByText('Last hour', { exact: true }).click();
    await h.pause(500);
    await at(p => p.locator('.spark-history-grid'), 'CPU, memory and GPU', 3400)(h);
  }],
  [`Local AI shows the model your Spark serves, and whether it's ready to respond.`, at(p => region(p, 'Local AI'), '', 4000)],
  [`App updates lists apps with newer images, with the release notes a click away.`,
    at(p => region(p, 'App updates').getByRole('row').nth(1), 'Running, and available', 4000)],
  [`Update installs releases within each app's current versions.
    New major versions can change settings or data, so you review those on the app's page.`,
    at(p => p.getByText('Update installs releases within'), '', 8500)],
  [`Devices and Tasks are where new hardware comes in. We'll visit both in the next video.`,
    then(at(p => region(p, 'Devices'), '', 2400), at(p => region(p, 'Tasks'), '', 2400))],

  [`Lucia has four areas.`, then(card('Finding your way', 'Areas and search'), h => h.pause(1700),
    h => h.page.getByRole('button', { name: /^Switch workspace/ }).click())],
  [`Overview is home base.`, area(0)],
  [`Your lab holds your hardware and apps.`, area(1)],
  [`Local AI has conversations, models, and app access.`, area(2)],
  [`And Settings covers appearance, connections, and your domain.`, area(3)],
  [`Or press Control K, and find any tool by name. It searches navigation, not your data.`, async h => {
    await h.page.keyboard.press('Escape');
    await h.pause(500);
    await h.page.keyboard.press('Control+k');
  }],
  [`Let's look for DNS.`, async h => {
    await h.page.keyboard.type('dns', { delay: 220 });
    await h.pause(800);
  }],
  [`One thing first: if you skipped trusting Lucia's certificate authority in the desktop app, do that now,
    on every computer that opens Lucia.`, h => h.pause(1000)],
  [`Then connect AdGuard. Domain setup needs it, so it comes first.`, async h => {
    const link = h.page.getByRole('link', { name: /^AdGuard/ });
    await h.spotlight(link, '', 2600);
    await link.click();
    await h.page.locator('.page-intro').waitFor();
  }],

  [`Point Lucia at the AdGuard Home you already run, with its address and an account.
    Lucia uses it for local DNS, without taking over its deployment.`,
    then(card('DNS and certificates', 'AdGuard and your domain'), at(p => section(p, 'Configured connection'), 'Your existing AdGuard Home', 9500))],
  [`Lucia adds DNS rewrites for its own names, and checks that they resolve.`, h => h.pause(1000)],
  [`It can also manage AdGuard's certificate. Lucia issues one from Let's Encrypt with your domain,
    installs it for HTTPS and encrypted DNS, and renews it before it expires.`, at(p => section(p, 'HTTPS certificate'), '', 11500)],
  [`With the AdGuard Home app installed, Lucia runs a copy on every server, and copies the primary's settings to the others each minute.
    If the primary stops answering for three minutes, the most recently synced server takes over.`,
    at(p => section(p, 'AdGuard servers'), 'One AdGuard per server', 13000)],
  [`Then give your devices every address as a DNS server, in your router's DHCP settings.`,
    at(p => p.getByText('Give devices every address'), '', 5800)],

  [`Next, Domains. This page is called DNS and certificates.`, tab('Domains')],
  [`To connect a domain, its DNS must be on Cloudflare, with an API token scoped to that one zone.
    Lucia walks you through choosing names and reviewing every change,
    then issues a Let's Encrypt certificate, first from staging, then for real.`, then(at(p => p.locator('.page-intro'), '', 6500), h => h.scroll(section(h.page, 'Traefik routes'), 'start'))],
  [`Traefik routes lists each address Lucia's gateway publishes: the portal, Authentik, and your apps.`,
    at(p => section(p, 'Traefik routes').getByRole('row').nth(1), 'The portal, behind Traefik', 5000)],
  [`Your certificate covers your names, and Lucia checks for renewal on a schedule.`, at(p => section(p, 'Let’s Encrypt'), '', 5000)],
  [`AdGuard records shows the local DNS records Lucia manages, for its own names, your servers, and your apps.
    Other records are never changed. And no private address is published to public DNS.`,
    at(p => section(p, 'AdGuard records').locator('.section-note').first(), '', 11000)],

  [`Connect UniFi before you add hardware. Lucia adds DHCP reservations for the servers it manages,
    so they keep their addresses. It never changes anything else on your network.`,
    then(tab('UniFi Network'), card('Your network', 'UniFi, SSH keys, storage and registries'), at(p => p.locator('.page-intro'), '', 9500))],
  [`It pins this Spark's address, and each server's once it finishes onboarding. Reservations you made yourself are never changed.`,
    at(p => p.getByRole('table', { name: 'Lucia DHCP reservations' }), '', 6500)],
  [`Add your SSH public keys once. Every server Lucia manages accepts them for your account.`,
    then(tab('SSH keys'), at(p => section(p, 'Your keys'), '', 4600))],
  [`Paste a key, or import your public keys from GitHub, and check the fingerprints.`,
    at(p => p.getByRole('textbox', { name: 'Or import from GitHub' }), '', 4000)],
  [`Storage connects your NAS. Every server mounts its shares at the same folder, so an app can use them wherever it runs.
    Lucia only mounts shares. It never changes anything on the NAS.`,
    then(tab('Storage'), at(p => p.getByRole('table', { name: 'Shares on unas' }), 'Mounted on every server', 9800))],
  [`It works with NFS or SMB. Later, you'll pick one share for nightly app backups.`,
    at(p => p.getByRole('group', { name: 'Protocol' }), '', 4200)],
  [`Registries lets your servers pull private images. Signing in also raises Docker Hub's pull limit.`,
    then(tab('Registries'), at(p => section(p, 'Signed in'), '', 2400), at(p => p.getByText(/raises Docker Hub/), '', 2600))],
  [`And People is where you manage accounts, groups, and app access.`,
    h => h.spotlight(h.page.getByRole('navigation', { name: 'Settings navigation' }).getByRole('link', { name: 'People', exact: true }), '', 3400)],

  [`Finally, make it yours. Choose light or dark, and a theme, or pick your own color.`,
    then(tab('Appearance'), card('Make it yours', 'Appearance'), h => h.pause(1700), async h => {
      await h.page.getByRole('group', { name: 'Color mode' }).getByRole('button', { name: 'Dark' }).click();
      await h.pause(1200);
      await h.page.getByRole('group', { name: 'Theme' }).getByRole('button', { name: 'Plum' }).click();
    })],
  [`These preferences are saved on this browser.`, at(p => p.getByText('Appearance and assistant preferences are saved'), '', 3000)],
  [`That's the connections done. In the next video, we tour your lab: servers, apps, backups, and updates.`,
    h => h.chapter('Up next', 'Part 3 · Your lab', 4200)],
];
