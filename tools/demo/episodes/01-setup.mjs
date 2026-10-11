// Part 1: from a freshly set-up DGX Spark to the Lucia sign-in page, with the desktop app.
// The desktop screens are the real app rendered headlessly with sample data. Refresh them with
//   dotnet run --project tests/Lucia.Desktop.UiChecks -- --screenshots artifacts/demo/desktop
export const start = new URL('../desktop.html', import.meta.url).href;

const call = (page, fn, ...args) => page.evaluate(([fn, args]) => window[fn](...args), [fn, args]);
const show = name => h => call(h.page, 'show', name);
const zoom = (...region) => h => call(h.page, 'zoom', ...region);
const spot = (region, label, ms) => async h => {
  await call(h.page, 'mark', ...region);
  await h.spotlight(h.page.locator('#mark'), label, ms);
};
const press = (region, next) => async h => {
  await call(h.page, 'mark', ...region);
  await h.page.locator('#mark').click();
  await h.pause(350);
  if (next) await call(h.page, 'show', next);
};
const then = (...steps) => async h => { for (const step of steps) await step(h); };
// A section's title card shows while its first line starts; spotlights wait for it to fade.
const card = (title, description) => h => h.chapter(title, description, 2000, 0);

// Regions in the app's 1040x840 window: x, y, width, height.
const connect = { steps: [29, 91, 183, 263], form: [260, 160, 728, 478], address: [266, 205, 716, 78],
  username: [266, 297, 716, 102], method: [266, 412, 716, 154], memory: [266, 584, 566, 28], next: [828, 724, 154, 43] };
const hostKey = { panel: [271, 399, 706, 306], fingerprint: [290, 543, 668, 43], consent: [286, 602, 336, 27], next: [819, 724, 163, 43] };
const review = { header: [270, 114, 708, 91], checks: [266, 225, 716, 150], approval: [266, 432, 716, 176],
  models: [271, 499, 706, 47], consent: [266, 566, 557, 29], next: [884, 724, 98, 43] };
const owner = { header: [270, 114, 708, 68], username: [266, 207, 716, 74], password: [266, 296, 716, 192],
  address: [266, 502, 716, 100], note: [266, 623, 716, 47], next: [863, 724, 119, 43] };
const setup = { header: [270, 114, 708, 68], status: [266, 205, 716, 116], durable: [266, 347, 716, 50],
  log: [289, 465, 670, 240], signIn: [295, 623, 520, 24] };
const ready = { header: [270, 114, 708, 91], trust: [266, 240, 716, 375], approve: [266, 450, 600, 92],
  signIn: [266, 641, 270, 56], open: [609, 725, 114, 42] }; // open is on the 780-wide compact window

export default [
  h => h.chapter('Lucia', 'Part 1 · Set up your Spark', 3400, 1000),
  `Welcome to Lucia, a self-managing home lab platform. Lucia turns an NVIDIA DGX Spark into the home base for your lab:
   a private dashboard in your browser, one account across your lab, local AI, and an assistant that helps you run it all.`,
  `In this first video, we go from a freshly set-up Spark to the Lucia sign-in page.
   The desktop app does the setup over SSH, from your own computer.`,
  `A quick note before we start. Today you build the app from source, and a signed download is coming.
   What you see here is the real app, shown with sample data.`,

  ['The app has five steps: connect, review, your account, set up, and ready.',
    then(card('Connect', 'Step 1 of 5'), spot(connect.steps, 'From first contact to sign-in', 4200))],
  [`First, finish NVIDIA's own first-boot setup on the Spark, and note its address.
    You'll also need a Windows, Mac, or Linux computer on the same network.`, zoom(...connect.form)],
  [`Enter that address here, with the username you created during NVIDIA's setup.`, then(spot(connect.address, '', 2400), spot(connect.username))],
  [`You can connect with that account's password, or with an SSH key file.`, spot(connect.method, '', 3200)],
  ['Those credentials stay in memory for this session only, and nothing is installed while the app checks the connection.',
    spot(connect.memory, 'Credentials stay in memory', 4600)],
  [`When you connect, the app shows the Spark's SSH host key before it sends any credentials.`,
    then(zoom(), press(connect.next, 'hostkey-light'), zoom(...hostKey.panel))],
  ['Compare its fingerprint with the one on your Spark, or another record you trust.',
    spot(hostKey.fingerprint, 'Checked before any password is sent', 4000)],
  ['Once you confirm it, Lucia pins that key. If it ever changes, the app refuses to connect.', spot(hostKey.consent, 'The key is pinned', 3800)],
  then(zoom(), press(hostKey.next, 'review-light')),
  ['Review is a read-only check of your Spark. Nothing changes yet.', then(card('Review', 'Step 2 of 5'), spot(review.header, '', 3400))],
  [`Lucia checks the operating system, Docker and Compose, NVIDIA's container device interface for the GPU,
    Python and OpenSSL, the ports it needs, and at least twelve gigabytes of free disk.`,
    then(zoom(...review.checks), spot(review.checks, 'A read-only preflight', 6000))],
  [`If the .NET SDK or Aspire is missing, it offers to install pinned, checksum-verified copies just for your user.
    It never installs GPU drivers or changes NVIDIA's Docker engine.`, zoom()],
  [`If you already keep models on the Spark, this advanced option points Lucia at that folder.
    Leave it empty for a new one. Setup never downloads model weights.`,
    then(zoom(...review.approval), spot(review.models, 'Optional: an existing model folder', 5000))],
  ['When the list looks right, approve it, and continue.', spot(review.consent, 'Nothing changes without your approval', 3000)],
  then(zoom(), press(review.next, 'owner-light')),
  [`Next, make Lucia yours. This is your everyday account for the dashboard and Authentik, separate from the Spark's own login.`,
    then(card('Your account', 'Step 3 of 5'), spot(owner.header, '', 4000))],
  ['Pick a username, and a password of at least fourteen characters. A long passphrase works well.',
    then(show('owner-filled-light'), spot(owner.username, '', 1600), spot(owner.password, '14 characters or more', 3600))],
  ['Then enter the address for your identity services. It goes into your certificate.', spot(owner.address, '', 3600)],
  [`Your account lives in Lucia's LDAP directory, with administrator access in Authentik.`, zoom(...owner.note)],
  [`The password travels over the verified SSH connection. It's never written to logs, and it isn't saved on this computer.`,
    spot(owner.note, 'Never logged, never saved locally', 5000)],
  then(zoom(), press(owner.next, 'progress-light')),
  ['Now the Spark does the work. Setup runs as a durable job on the Spark itself.',
    then(card('Set up', 'Step 4 of 5'), spot(setup.header, '', 3600))],
  ['It deploys a private certificate authority, an LDAP directory, and Authentik for sign-in, behind Traefik.', spot(setup.status, '', 5000)],
  `Then it enrolls you as the owner, registers Lucia's sign-in app, and deploys the managed host and dashboard.`,
  [`You don't have to watch. If you close the app or lose the connection, setup keeps going.
    Reconnect later to pick up where it is, without starting a second job.`, spot(setup.durable, 'Setup keeps running if you close the app', 6000)],
  ['Setup details lists every step as it happens.', then(show('progress-details-light'), zoom(...setup.log))],
  ['Before it finishes, it verifies a real sign-in with your new account.', spot(setup.signIn, '', 3800)],
  then(zoom(), show('finish-light')),
  ['When the host is healthy and your account checks out, Lucia is ready to open.',
    then(card('Ready', 'Step 5 of 5'), spot(ready.header, '', 3800))],
  ['One last step on this computer. Your Spark has its own private certificate authority, and your browser needs to trust it.',
    zoom(...ready.trust)],
  [`That's a separate, explicit approval. Tick the box, and the app adds only the public root certificate to your user's trusted store.
    Or export it, and import it yourself.`, spot(ready.approve, 'Trust is your call', 6500)],
  ['The private keys never leave the Spark.', zoom()],
  ['Then sign in with the account you just created.', spot(ready.signIn, '', 3000)],
  [`By the way, the app follows your system's light or dark theme, and fits smaller windows.`, show('finish-compact-dark')],
  'Running it again later is safe, too. The installer keeps your existing secrets and certificates.',
  ['Now, open Lucia.', press(ready.open)],

  [`This is the sign-in page on a live Spark. Sign in with Authentik, using the account you just created.`, async h => {
    await h.page.context().clearCookies(); // this recording's browser only; the saved session is untouched
    await h.open(h.base);
    await h.spotlight(h.page.getByText('Sign in with Authentik'), 'Your new account', 4000);
  }],
  ['In the next video, we sign in, and take a tour of your first hour with Lucia.', h => h.chapter('Up next', 'Part 2 · Your first hour', 4200)],
];
