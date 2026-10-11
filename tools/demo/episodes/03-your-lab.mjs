// Part 3: a tour of Your lab: servers, new hardware, apps, backups and Spark updates.
// It adds whoami as hello-demo and deletes it again. The server keeps its empty data folder, /srv/lucia/stacks/hello-demo/.
export const start = '#/devices';

const then = (...steps) => async h => { for (const step of steps) await step(h); };
const region = (page, name) => page.getByRole('region', { name, exact: true });
const section = (page, name) => page.locator('section').filter({ has: page.getByRole('heading', { name, exact: true }) }).last();
// Scrolls smoothly to a target, then spotlights it. Targets must fit below the sticky header, about 520px.
const at = (find, label = '', ms) => async h => {
  const target = find(h.page);
  await h.scroll(target);
  await h.spotlight(target, label, ms);
};
const go = (nav, name) => async h => {
  await h.page.getByRole('navigation', { name: nav }).getByRole('link', { name, exact: true }).click();
  await h.page.locator('.page-intro').first().waitFor();
  await h.pause(900);
};
const lab = name => go('Your lab navigation', name);
const apps = name => go('Apps navigation', name);
const card = page => page.getByRole('article', { name: 'lucialab01' });
const part = name => h => card(h.page).locator(name);
const item = (page, name) => page.getByRole('listitem').filter({ has: page.getByRole('heading', { name, exact: true }) });
const compose = 'services:\n  whoami:\n    image: traefik/whoami\n    restart: unless-stopped\n';
// A section's title card shows while its first line starts; spotlights wait for it to fade.
const chapter = (title, description) => h => h.chapter(title, description, 2000, 0);

// Points at three earlier moments on the load chart, then leaves it so the card returns to Now.
const scrub = async h => {
  await h.spotlight(part('.server-charts')(h), '', 1800);
  const chart = part('svg.server-chart')(h).first();
  const { width, height } = await chart.boundingBox();
  for (const f of [0.8, 0.5, 0.2]) {
    await chart.hover({ position: { x: width * f, y: height / 2 } });
    await h.pause(600);
  }
  await part('.server-card-head')(h).hover();
};

export default [
  h => h.chapter('Lucia', 'Part 3 · Your lab', 3400, 1000),
  [`Welcome back. This is Your lab, where your servers and apps live.
    In this video, we tour your servers, add an app and remove it again, then look at backups and updates.`,
    h => h.spotlight(h.page.getByRole('navigation', { name: 'Your lab navigation' }), '', 6000)],

  [`Devices shows each server Lucia manages: its CPU, memory and GPU, and how many apps it runs.`,
    then(h => h.scroll(card(h.page), 'start'), h => h.spotlight(part('.server-hardware')(h), '', 6400))],
  [`Each server gets a name in your local DNS. Sign in over SSH with your Lucia account, and sudo asks for your Lucia password.`,
    h => h.spotlight(part('.server-card-head')(h), '', 8400)],
  [`The gauges show what's in use right now. The agent on each server reports every thirty seconds.`,
    h => h.spotlight(part('.server-gauges')(h), '', 6400)],
  [`The charts cover the last hour. Point at a moment, and the gauges show the readings from then.`, scrub],
  [`The footer flags anything waiting, like package updates. Manage opens the details.`, async h => {
    await h.spotlight(part('.server-card-foot')(h), '', 3400);
    await card(h.page).getByRole('button', { name: 'Manage' }).click();
    await h.pause(1200);
  }],
  [`Lucia checks each server for updates every six hours. You decide when to install them, and when to restart.
    The assistant can do it too, with your approval.`,
    at(p => p.locator('#server-detail').getByRole('region', { name: 'Updates', exact: true }), '', 8800)],
  [`For a GPU, pick the CUDA line that apps use on this server. Lucia doesn't change it when the driver updates.`,
    at(p => p.getByRole('group', { name: 'CUDA line', exact: true }), '', 6800)],
  [`And when you retire a machine, remove it from Lucia. Nothing on the machine itself changes.`,
    at(p => p.locator('#server-detail .hardware-reject'), '', 4800)],

  [`New hardware comes in through discovery, which stays off until you choose Add hardware. Here's how it works.`,
    then(h => h.page.locator('#server-detail').getByRole('button', { name: 'Close', exact: true }).click(),
      chapter('New hardware', 'Discovery and installation tasks'), at(p => region(p, 'Hardware discovery'), '', 4400), async h => {
        await h.page.getByRole('button', { name: 'How adding hardware works' }).click();
        await h.pause(600);
      })],
  [`Add hardware opens a thirty-minute window for read-only discovery. Network-boot the new machine, and it shows up here,
    with a verification code that matches the one on its screen.`, at(p => p.locator('#hardware-help'), '', 10600)],
  [`Approving an install is deliberate. You confirm the code, choose a disk, add a recovery SSH key, and type ERASE,
    because every internal disk is wiped. Debian goes on the disk you chose.`,
    at(p => p.getByText(/^Compare the reported hardware/), '', 11600)],
  [`Installation is still being qualified. Today, that means x86 machines with UEFI, and Secure Boot already off.`,
    at(p => p.getByText(/^This development profile/), '', 7800)],
  [`Installation tasks follows each install you approve, with status from the host, not an estimated progress bar.`,
    then(lab('Installation tasks'), at(p => p.locator('.page-intro'), '', 5800))],
  [`Here's our first server: approved, given its one-time installation grant, and managed six minutes later.`,
    then(h => h.page.getByRole('button', { name: /^Show finished/ }).click(), at(p => p.locator('article.hardware-entry').first(), '', 5400))],

  [`Apps are the containers Lucia runs on your servers. Each one is a Docker Compose file, placed on one server.`,
    then(lab('Apps'), chapter('Apps', 'What runs on your servers'),
      at(p => p.getByRole('table', { name: 'Apps and their state' }).getByRole('row').nth(1), '', 5800))],
  [`Every six hours, Lucia checks for newer images, and flags the apps with updates waiting.`,
    at(p => p.locator('.stack-updates-summary'), '', 5000)],
  [`Let's open FlareSolverr.`, async h => {
    const link = h.page.getByRole('link', { name: 'flaresolverr', exact: true });
    await h.scroll(link);
    await link.click();
    await h.page.locator('.stack-heading').waitFor();
  }],
  [`Each app has a page of its own. Restart it, update its images, or stop it, and its data is kept.`,
    at(p => p.getByRole('group', { name: 'App actions' }), '', 6200)],
  [`Containers shows what's running, with each container's logs.`, at(p => section(p, 'Containers'), '', 4000)],
  [`FlareSolverr came from the catalog, so Lucia writes its compose file from a few settings, and keeps it current.`,
    at(p => p.getByText(/^Installed from the catalog as/), '', 5800)],
  [`Prefer to edit the compose file yourself? You can, but Lucia stops managing it.
    Settings and catalog updates no longer apply, and you can't convert it back.`, at(p => p.locator('.stack-convert'), '', 9800)],
  [`Every app is backed up each night. Most keep running while it happens.
    Apps with busy databases can pause briefly, so the copy is consistent.`,
    at(p => p.getByRole('group', { name: /^How it.s backed up/ }), '', 7800)],
  [`Restore any snapshot. Lucia sets the current data aside first, so nothing is deleted.`,
    at(p => p.getByRole('table', { name: /^Snapshots of/ }).getByRole('row').nth(1), '', 5000)],
  [`Moving an app to another server copies its folder across. It's offline while the data copies.`,
    at(p => section(p, 'Move to another server'), '', 5800)],
  [`And deleting an app removes its containers and compose file. Its data stays on the server until you remove it.`,
    at(p => section(p, 'Delete this app'), '', 6800)],

  [`The catalog lists apps Lucia knows how to set up. Pick one and a server, and Lucia writes the configuration, and keeps it current.`,
    then(lab('Apps'), apps('Catalog'), chapter('Add an app', 'From the catalog'), at(p => p.locator('.page-intro'), '', 4800))],
  [`Each one says what it needs, and which of your servers can run it.
    Local AI, for example, needs an NVIDIA GPU, and a CUDA line chosen in Devices.`, at(p => item(p, 'Local AI'), '', 9800)],
  [`Let's set up Node-RED, which wires home automations together as flows.`, async h => {
    await at(p => item(p, 'Node-RED'), '', 3600)(h);
    await h.page.locator('a[href="#/apps/install/node-red"]').click();
    await h.page.locator('.page-intro').waitFor();
  }],
  [`Choose a server. If one can't run the app, it says why.`, at(p => p.getByRole('group', { name: 'Server', exact: true }), '', 2400)],
  [`Then a few settings: a name, a web address under your domain, and a port.`,
    at(p => p.getByRole('group', { name: 'Settings', exact: true }), '', 5000)],
  [`And before you install, read the exact compose file Lucia will write. Every image is pinned to a version and digest.`,
    then(async h => {
      await h.page.locator('details.stack-compose-details summary').click();
      await h.pause(400);
    }, at(p => p.locator('details.stack-compose-details pre'), '', 6200))],
  [`We'll leave it there, and cancel.`, async h => {
    await h.page.getByRole('link', { name: 'Cancel', exact: true }).click();
    await h.page.waitForFunction(() => location.hash === '#/apps/catalog');
  }],

  [`Not in the catalog? Bring any app with a Docker Compose file. Lucia runs it, restarts it, and reports on it.`,
    then(chapter('Your own app', 'Any Docker Compose file'), at(p => p.locator('li.stack-catalog-custom'), '', 5600), async h => {
      await h.page.getByRole('link', { name: 'Paste a compose file' }).click();
      await h.page.locator('.page-intro').waitFor();
    })],
  [`Give it a name, and paste the compose file. We'll use whoami, a tiny web server.`, async h => {
    await h.page.getByRole('textbox', { name: /^Name/ }).pressSequentially('hello-demo', { delay: 90 });
    await h.page.getByRole('textbox', { name: /^Docker Compose file/ }).pressSequentially(compose, { delay: 25 });
  }],
  [`Choose where it runs: any server that fits, or a specific one. Lucia picks the one running the fewest apps, and tells you which.`,
    at(p => p.getByRole('group', { name: 'Where it runs' }), '', 7800)],
  [`Lucia saves what you asked for, and the agent on that server picks it up.
    It writes the files, pulls the images, and runs Compose itself, then reports back.`, async h => {
    await h.page.getByRole('button', { name: 'Add and start' }).click();
    await h.page.waitForFunction(() => location.hash === '#/apps/hello-demo');
  }],
  [`So the web host never needs a Docker socket, and can't run arbitrary Docker commands on your servers.`, h => h.pause(500)],
  [`Its container names start with Lucia, then the app's name, so they're easy to spot.`,
    h => h.cut(() => h.page.getByText(/container running on/).first().waitFor({ timeout: 90000 }))],
  [`It's running, with one container on the server Lucia picked.`, at(p => p.locator('.page-intro'), '', 3400)],
  [`Open its logs, and there's whoami, starting up.`, async h => {
    await h.page.getByRole('button', { name: 'Logs', exact: true }).first().click();
    await h.page.locator('.stack-logs pre').waitFor();
    await h.spotlight(h.page.locator('.stack-logs'), '', 2800);
  }],
  [`Now let's delete it. Its containers come down, and it's gone from the list.`, async h => {
    const remove = section(h.page, 'Delete this app');
    await h.scroll(remove);
    await h.page.getByRole('button', { name: 'Delete app…' }).click();
    await h.spotlight(remove, '', 2000);
    await h.page.getByRole('button', { name: 'Delete hello-demo', exact: true }).click();
    await h.page.waitForFunction(() => location.hash === '#/apps');
  }],

  [`Containers and ports shows everything running on a server, including containers Lucia didn't start, and the ports in use.`,
    then(apps('Containers & ports'), chapter('Keeping it running', 'Containers, backups and updates'), at(p => p.locator('.page-intro'), '', 6000))],
  [`Each container shows the app it belongs to, and its logs are a click away.`,
    at(p => p.locator('li.stack-container').first(), '', 4800)],
  [`Every night, Lucia backs up each app's folder to your NAS with restic, encrypted and deduplicated.`,
    then(apps('Backups'), at(p => p.locator('.page-intro'), '', 4800))],
  // Scrolling to the end keeps the backup history below it off-screen.
  [`Backups run at three in the morning, and Lucia keeps seven daily, four weekly, and six monthly snapshots.`,
    then(h => h.scroll(region(h.page, 'Where backups go'), 'end'), h => h.spotlight(region(h.page, 'Where backups go'), '', 6000))],
  [`Spark updates keeps the Spark itself current: Ubuntu, and the DGX platform underneath Lucia.`,
    then(lab('Spark updates'), at(p => p.locator('.updates-summary'), '', 4800))],
  [`Update everything installs them all in one pass, then restarts the services using them, and the Spark only if it must.`,
    at(p => region(p, 'Update everything'), '', 6800)],
  [`Needs attention lists anything waiting on you, like services still using old files.`, at(p => region(p, 'Needs attention'), '', 4800)],
  [`Everyday updates are safe to install while you work.`,
    at(p => region(p, 'Everyday updates').locator('.section-note').first(), '', 2800)],
  [`Platform updates, like the kernel, NVIDIA driver and CUDA, are best installed on their own, after hours.
    Lucia unloads your models first, and reloads them when it's done.`,
    at(p => region(p, 'Platform updates').locator('.section-note').first(), '', 9200)],
  [`Installing needs administrator rights, so a small update service runs on the Spark as root.
    It accepts only six fixed tasks, never commands, and stops if an install would remove a package.`, h => h.pause(800)],
  [`Lucia also reports whether Ubuntu's automatic security updates are on. It never changes that for you.`,
    at(p => section(p, 'Automatic security updates'), '', 6400)],
  [`That's your lab: servers, apps, backups and updates. In the next video, we explore Local AI:
    chatting with your models, finding new ones, and connecting your apps.`,
    then(h => h.pause(3200), h => h.chapter('Up next', 'Part 4 · Local AI', 6800))],
];
