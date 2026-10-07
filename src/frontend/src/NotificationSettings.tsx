import { useCallback, useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { deviceName, keyBytes, parseDevices } from './assistantJobs'
import type { PushDevice } from './assistantJobs'
import { Icon } from './Icon'
import './NetworkSettings.css'
import './Jobs.css'

type Props = { session: AuthenticationSession; refreshSession: () => Promise<void> }

const promptKey = 'lucia.notifications.prompt.v1'
const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })
const when = (value?: string) => value ? dateFormat.format(new Date(value)) : 'Never'
const failureText = (failure: unknown, fallback: string) => failure instanceof TypeError
  ? 'Lucia could not reach the host. Check your connection and try again.' : failure instanceof Error ? failure.message : fallback

export const pushSupported = () => 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window && isSecureContext

async function currentSubscription() {
  const registration = await navigator.serviceWorker.getRegistration('/')
  return registration ? registration.pushManager.getSubscription() : null
}

// Asks the browser for permission, subscribes with the host's key, and registers this browser as a device.
export async function enableNotifications({ session, refreshSession }: Props): Promise<PushDevice> {
  const permission = await Notification.requestPermission()
  if (permission !== 'granted') throw new Error(permission === 'denied'
    ? 'Notifications are blocked for this site. Allow them in your browser’s site settings, then try again.'
    : 'The browser didn’t turn notifications on.')
  await navigator.serviceWorker.register('/sw.js', { scope: '/' })
  const registration = await navigator.serviceWorker.ready
  const { publicKey } = await (await ownerRequest(session, refreshSession, '/api/assistant/push')).json() as { publicKey: string }
  const key = keyBytes(publicKey)
  let subscription = await registration.pushManager.getSubscription()
  const saved = subscription?.options.applicationServerKey
  // A subscription made with another key can't receive this host's messages.
  if (subscription && (!saved || new Uint8Array(saved).join() !== key.join())) {
    await subscription.unsubscribe()
    subscription = null
  }
  subscription ??= await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key })
  const { endpoint, keys } = subscription.toJSON()
  const response = await ownerRequest(session, refreshSession, '/api/assistant/push/devices', 'POST',
    { name: deviceName(navigator.userAgent), endpoint, keys })
  return parseDevices([await response.json()])[0]
}

// After sign-in, owners are asked once per browser; Not now keeps it out of the way.
export function NotificationPrompt(props: Props) {
  const [shown, setShown] = useState(() => {
    try { return pushSupported() && Notification.permission === 'default' && !localStorage.getItem(promptKey) } catch { return false }
  })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  function dismiss() {
    try { localStorage.setItem(promptKey, 'dismissed') } catch { /* It asks again next visit. */ }
    setShown(false)
  }
  async function enable() {
    setBusy(true); setError(null)
    try { await enableNotifications(props); dismiss() }
    catch (failure) { setError(failureText(failure, 'Lucia could not turn on notifications.')) }
    finally { setBusy(false) }
  }
  if (!shown) return null
  return <div className="storage-notice notification-prompt" role="region" aria-label="Notifications">
    <Icon name="chat" />
    <p>Get a notification when a job needs your approval, fails, or has something to tell you.{error && <span className="network-error" role="alert"> {error}</span>}</p>
    <button className="button secondary" disabled={busy} onClick={() => void enable()}>{busy ? 'Turning on…' : 'Turn on'}</button>
    <button className="text-link" disabled={busy} onClick={dismiss}>Not now</button>
  </div>
}

export function NotificationSettings(props: Props) {
  const { session, refreshSession } = props
  const [devices, setDevices] = useState<PushDevice[] | null>(null)
  const [mine, setMine] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  const [renaming, setRenaming] = useState<{ id: string; name: string } | null>(null)
  const [removing, setRemoving] = useState<string | null>(null)
  const supported = pushSupported()
  const permission = supported ? Notification.permission : 'default'

  const load = useCallback(async (signal?: AbortSignal) => {
    const list = parseDevices(await (await ownerRequest(session, refreshSession, '/api/assistant/push/devices', 'GET', undefined, signal)).json())
    const subscription = supported ? await currentSubscription() : null
    if (signal?.aborted) return
    setDevices(list)
    setMine(subscription?.endpoint ?? null)
  }, [session, refreshSession, supported])
  useEffect(() => {
    const controller = new AbortController()
    load(controller.signal).catch(failure => { if (!controller.signal.aborted) setError(failureText(failure, 'Lucia could not list your devices.')) })
    return () => controller.abort()
  }, [load])

  async function act(work: () => Promise<unknown>, done: string, fallback: string) {
    setBusy(true); setError(null); setNotice('')
    try { await work(); await load(); setNotice(done); return true }
    catch (failure) { setError(failureText(failure, fallback)); return false }
    finally { setBusy(false) }
  }
  const request = (path: string, method: string, body?: unknown) => ownerRequest(session, refreshSession, `/api/assistant/push/devices${path}`, method, body)
  const thisDevice = devices?.find(device => device.endpoint === mine)
  function rename(event: FormEvent) {
    event.preventDefault()
    if (!renaming) return
    void act(() => request(`/${renaming.id}`, 'PUT', { name: renaming.name.trim() }), 'Renamed.', 'Lucia could not rename this device.')
      .then(saved => { if (saved) setRenaming(null) })
  }
  async function turnOff(device: PushDevice) {
    await (await currentSubscription())?.unsubscribe()
    await request(`/${device.id}`, 'DELETE')
  }

  if (!session.isOwner) return <div className="page-intro"><h1>Owner access is needed.</h1><p>Only lab owners get the assistant’s notifications.</p></div>
  return <>
    <div className="page-intro"><h1>Notifications</h1><p>The assistant notifies these devices when a job needs your approval, fails, or has something to tell you. Tapping a notification opens its chat.</p></div>
    {error && <p className="network-error" role="alert">{error}</p>}
    <p className="network-notice" role="status">{devices ? notice : 'Reading your devices…'}</p>
    <section className="surface network-section">
      <h2>This device</h2>
      {!supported ? <p>This browser can’t receive notifications. On iPhone and iPad, add Lucia to your Home Screen and open it from there.</p>
        : permission === 'denied' ? <p>Notifications are blocked for this site. Allow them in your browser’s site settings, then reload this page.</p>
          : thisDevice ? <>
            <p>Notifications are on as <strong>{thisDevice.name}</strong>.</p>
            <div className="network-actions">
              <button className="button secondary" disabled={busy} onClick={() => void act(() => request(`/${thisDevice.id}/test`, 'POST'), 'Test sent. It should arrive in a few seconds.', 'Lucia could not send a test.')}>Send a test</button>
              <button className="text-link" disabled={busy} onClick={() => void act(() => turnOff(thisDevice), 'Notifications are off on this device.', 'Lucia could not turn notifications off.')}>Turn off on this device</button>
            </div>
          </> : <>
            <p>Notifications are off on this device.</p>
            <div className="network-actions">
              <button className="button primary" disabled={busy || !devices} onClick={() => void act(() => enableNotifications(props), 'Notifications are on for this device.', 'Lucia could not turn on notifications.')}>Turn on notifications</button>
            </div>
          </>}
    </section>
    <section className="surface network-section">
      <h2>Your devices</h2>
      {!devices ? null : !devices.length ? <p className="section-note">No devices yet. Turn notifications on above, on each phone or computer you want to hear from.</p>
        : <ul className="network-review-list job-devices">{devices.map(device => <li key={device.id}>
          {renaming?.id === device.id ? <form className="job-rename" onSubmit={rename}>
            <label>Device name<input value={renaming.name} maxLength={60} autoFocus required disabled={busy}
              onChange={event => setRenaming({ id: device.id, name: event.target.value })} /></label>
            <button className="button secondary" disabled={busy || !renaming.name.trim()}>Save</button>
            <button type="button" className="text-link" onClick={() => setRenaming(null)}>Cancel</button>
          </form> : <div className="ssh-key-row">
            <div><strong>{device.name}{device.endpoint === mine && ' (this device)'}</strong>
              <span>Added {when(device.added)} · Last notified {when(device.lastUsed)}</span></div>
            <div className="job-row-actions">
              <button className="text-link" disabled={busy} onClick={() => void act(() => request(`/${device.id}/test`, 'POST'), `Test sent to ${device.name}.`, 'Lucia could not send a test.')}>Send a test<span className="visually-hidden"> to {device.name}</span></button>
              <button className="text-link" disabled={busy} onClick={() => setRenaming({ id: device.id, name: device.name })}>Rename<span className="visually-hidden"> {device.name}</span></button>
              <button className="text-link" disabled={busy} aria-expanded={removing === device.id} onClick={() => setRemoving(current => current === device.id ? null : device.id)}>Remove<span className="visually-hidden"> {device.name}</span></button>
            </div>
          </div>}
          {removing === device.id && <div className="job-confirm" role="group" aria-label={`Remove ${device.name}`}>
            <p>Remove {device.name}? It stops getting notifications{device.endpoint === mine ? ', and this browser forgets its subscription' : ''}.</p>
            <button className="button secondary" disabled={busy} onClick={() => void act(() => device.endpoint === mine ? turnOff(device) : request(`/${device.id}`, 'DELETE'), `${device.name} won’t get notifications now.`, 'Lucia could not remove this device.')
              .then(done => { if (done) setRemoving(null) })}>Remove device</button>
            <button className="text-link" autoFocus onClick={() => setRemoving(null)}>Cancel</button>
          </div>}
        </li>)}</ul>}
    </section>
  </>
}
