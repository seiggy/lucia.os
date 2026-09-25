// Run with an isolated playwright-cli session against Vite on 127.0.0.1:5283.
async page => {
  const assert = (condition, message) => { if (!condition) throw new Error(message) }
  const id = '13c7e7c6-12af-4ba1-b862-5701f857a8d2'
  const diskId = '/dev/disk/by-id/nvme-Example_123'
  const now = new Date().toISOString()
  const expiry = new Date(Date.now() + 30 * 60000).toISOString()
  const owner = { enabled: true, authenticated: true, canAccess: true, isOwner: true, username: 'test-owner', displayName: 'Test owner', csrfToken: 'browser-test-only' }
  const device = {
    id, verificationCode: 'ABCD-1234-EF56-7890', phase: 'Discovered', inventoryRevision: 1, discoveredAt: now, updatedAt: now, lastSeenAt: now,
    lastHeartbeatAt: null, heartbeatFreshness: 'Unknown', discoveryExpiresAt: expiry, taskId: null, statusMessage: null,
    hardware: {
      architecture: 'x86_64', bootMode: 'uefi', secureBoot: false, manufacturer: 'Example', model: 'Test server',
      serialNumber: 'TEST-DEVICE-123', hardwareUuid: null, cpuModel: 'Test CPU', logicalCpuCount: 16, memoryBytes: 34359738368,
      interfaces: [{ name: 'eth0', macAddress: null, addresses: ['192.168.0.25'] }],
      disks: [
        { id: diskId, path: '/dev/nvme0n1', model: 'Test SSD', serial: 'TEST-DISK-123', sizeBytes: 1000000000000, isReadOnly: false, isRemovable: false },
        { id: '/dev/disk/by-id/usb-Example', path: '/dev/sdb', model: 'Removable boot disk', serial: 'BOOT-ONLY', sizeBytes: 16000000000, isReadOnly: false, isRemovable: true },
        { id: null, path: '/dev/sdc', model: 'Unidentified disk', serial: null, sizeBytes: 500000000000, isReadOnly: false, isRemovable: false },
        { id: null, path: '/dev/sdd', model: 'Another unidentified disk', serial: null, sizeBytes: 500000000000, isReadOnly: false, isRemovable: false },
      ],
    },
  }
  let state = { window: { isOpen: false, expiresAt: null }, readiness: { canDiscover: false, canInstall: false, reasons: ['Boot and enrollment are not qualified.'] }, devices: [], tasks: [] }
  let gets = 0
  let status = 200
  const writes = []
  await page.route('**/api/auth/session', route => route.fulfill({ json: owner }))
  await page.route('**/api/host/**', async route => {
    const request = route.request()
    if (request.method() === 'GET') { gets++; await route.fulfill({ status, json: status === 200 ? state : { error: { message: 'Test connection failure.' } } }); return }
    assert(request.headers()['x-csrf-token'] === owner.csrfToken, 'A write omitted CSRF.')
    writes.push({ path: request.url().replace(/^https?:\/\/[^/]+/, ''), method: request.method(), body: request.postDataJSON() })
    if (request.url().endsWith('/window')) state = { ...state, window: { isOpen: request.method() !== 'DELETE', expiresAt: request.method() === 'DELETE' ? null : expiry } }
    if (request.url().endsWith('/approve-install')) state = { ...state, devices: [{ ...device, phase: 'Approved' }] }
    await route.fulfill({ json: {} })
  })
  await page.goto('http://127.0.0.1:5283/')
  await page.evaluate(async session => {
    const React = (await import('/node_modules/.vite/deps/react.js')).default
    const { createRoot } = (await import('/node_modules/.vite/deps/react-dom_client.js')).default
    const { HardwareOnboarding } = await import('/src/HardwareOnboarding.tsx')
    document.getElementById('root').style.display = 'none'
    const main = document.createElement('main')
    document.body.append(main)
    const root = createRoot(main)
    const refreshSession = async () => {}
    window.renderHardwareCheck = (view = 'devices', nextSession = session) =>
      root.render(React.createElement(HardwareOnboarding, { view, session: nextSession, refreshSession }))
    window.renderHardwareCheck()
  }, owner)
  await page.getByText('Setup is needed before discovery.').waitFor()
  assert(await page.getByRole('button', { name: 'Add hardware', exact: true }).isDisabled(), 'Unready discovery could be opened.')
  state = { ...state, readiness: { canDiscover: true, canInstall: true, reasons: [] }, devices: [device] }
  await page.getByRole('button', { name: 'Refresh', exact: true }).click()
  await page.getByRole('heading', { name: 'Example Test server' }).waitFor()
  await page.getByText('ABCD-1234-EF56-7890', { exact: true }).waitFor()
  await page.getByRole('button', { name: 'Add hardware', exact: true }).click()
  await page.getByRole('button', { name: 'Stop discovery' }).waitFor()
  assert(writes[0].body.minutes === 30, 'Discovery did not use a bounded 30-minute window.')
  await page.getByText('Review installation or reject this device', { exact: true }).click()
  const approve = page.getByRole('button', { name: 'Erase selected disk and install' })
  assert(await approve.isDisabled(), 'Installation was enabled before approval.')
  assert(await page.getByRole('combobox', { name: 'Disk to erase', exact: true }).inputValue() === '', 'A disk was selected by default.')
  assert(await page.getByRole('combobox', { name: 'Disk to erase', exact: true }).locator('option').count() === 2, 'A removable or unidentified disk was eligible.')
  for (const width of [320, 390, 768, 1280]) {
    await page.setViewportSize({ width, height: 950 })
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)
    assert(!overflow, `Horizontal overflow at ${width}px.`)
  }
  await page.getByLabel('Device hostname', { exact: false }).fill('dev-server')
  await page.getByRole('combobox', { name: 'Disk to erase', exact: true }).selectOption(diskId)
  await page.getByLabel('I have checked this physical device').check()
  await page.getByLabel('Type ERASE to confirm').fill('ERASE')
  assert(await approve.isEnabled(), 'Explicit valid approval did not enable installation.')
  await page.emulateMedia({ reducedMotion: 'reduce' })
  await page.screenshot({ path: 'src\\frontend\\.checks\\onboarding-desktop.png', fullPage: true })
  await page.setViewportSize({ width: 390, height: 950 })
  await page.evaluate(() => document.documentElement.dataset.scheme = 'dark')
  await page.screenshot({ path: 'src\\frontend\\.checks\\onboarding-mobile.png', fullPage: true })
  await approve.click()
  await page.getByText('Installation was approved for dev-server.', { exact: false }).waitFor()
  const approval = writes.find(write => write.path.endsWith('/approve-install'))
  assert(JSON.stringify(approval.body) === JSON.stringify({ hostname: 'dev-server', diskId, confirmation: 'ERASE' }), 'Installation request scope was wrong.')
  await page.getByRole('button', { name: 'Stop discovery' }).click()
  await page.getByText('Hardware discovery was stopped.', { exact: false }).waitFor()
  assert(state.devices[0].phase === 'Approved', 'Closing discovery changed approved installation status.')
  status = 503
  await page.getByRole('button', { name: 'Refresh', exact: true }).click()
  await page.getByText('Test connection failure.').waitFor()
  assert(await page.getByRole('button', { name: 'Add hardware', exact: true }).isDisabled(), 'A stale snapshot allowed changes.')
  assert(await page.getByText('No devices have been reported.', { exact: true }).count() === 0, 'An error appeared as empty inventory.')
  status = 200
  await page.evaluate(() => window.renderHardwareCheck('tasks'))
  await page.getByText('No installation tasks have been reported.', { exact: true }).waitFor()
  const beforeMember = gets
  await page.evaluate(session => window.renderHardwareCheck('devices', { ...session, isOwner: false }), owner)
  await page.getByRole('heading', { name: 'Owner access is needed.' }).waitFor()
  assert(gets === beforeMember, 'A non-owner fetched hardware status.')
  await page.unroute('**/api/auth/session')
  await page.unroute('**/api/host/**')
  return { result: 'Passed', widths: [320, 390, 768, 1280], checks: 'Readiness, explicit disk approval, scoped CSRF requests, stop semantics, stale/error separation, empty tasks, non-owner denial.' }
}
