import { useCallback, useEffect, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { parseExternalRoutes, parsePublicIngress } from './networkManagement'
import type { DomainJob, ExternalRoute, PublicIngressStatus } from './networkManagement'

type Props = { job: DomainJob; ingressAddress: string | null; session: AuthenticationSession; refreshSession: () => Promise<void> }
const failed = (failure: unknown, fallback: string) => failure instanceof Error ? failure.message : fallback
const forwards: Record<string, string> = {
  Forwarding: 'Forwards HTTPS (TCP 443) to Lucia', Adopted: 'Took over the existing HTTPS forward', Off: 'Lucia’s forward is off',
}

export function PublicAccess({ job, ingressAddress, session, refreshSession }: Props) {
  const [status, setStatus] = useState<PublicIngressStatus | null>(null)
  const [confirm, setConfirm] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const zone = job.zone ?? 'your domain'
  const changing = job.publicRequested !== null || job.phase === 'Amending'

  const load = useCallback(async () => {
    try {
      const response = await ownerRequest(session, refreshSession, '/api/host/domains/public')
      setStatus(parsePublicIngress(await response.json()))
    } catch { /* the job state still shows; the next poll retries */ }
  }, [session, refreshSession])
  useEffect(() => {
    void load()
    const interval = setInterval(() => { if (document.visibilityState !== 'hidden') void load() }, 15000)
    return () => clearInterval(interval)
  }, [load])

  async function set(enabled: boolean) {
    setBusy(true); setError(null)
    try { await ownerRequest(session, refreshSession, '/api/host/domains/public', 'PUT', { enabled }); setConfirm(false) }
    catch (failure) { setError(failed(failure, 'Public access could not be changed.')) } finally { setBusy(false) }
  }

  const turningOn = !job.public
  return <><section className="surface network-section" aria-labelledby="public-access">
    <h2 id="public-access">Public access</h2>
    <p className="section-note">{job.public
      ? <>On. Sign-in is at <a className="text-link" href={job.serviceUrls.authentik}>{job.serviceUrls.authentik.replace('https://', '')}</a>, and apps with a public name answer at <code>name.{zone}</code> through Cloudflare, which only lets its own servers in. Everything else stays on your network.</>
      : <>Off. Every address works only on your network. Turn it on to reach chosen apps from the internet at <code>name.{zone}</code>, through Cloudflare.</>}</p>
    {changing && <p className="network-warning" role="status">Turning public access {job.publicRequested === false ? 'off' : 'on'}. Lucia reissues the certificate, moves sign-in, then restarts; sign in again when this page reconnects.</p>}
    {job.publicError && <p className="network-error" role="alert">{job.publicError}</p>}
    {confirm
      ? <div className="network-warning">
        <p>{turningOn
          ? <>Lucia adds <code>*.{zone}</code> to the certificate, moves sign-in to <code>auth.{zone}</code>, publishes public names in Cloudflare and forwards HTTPS from your router. Lucia restarts, and everyone signs in again.</>
          : <>Lucia removes its Cloudflare records and router forward, and moves sign-in back to your network. Lucia restarts, and everyone signs in again.</>}</p>
        <div className="network-actions">
          <button className="button primary" disabled={busy} onClick={() => void set(turningOn)}>{busy ? 'Starting…' : turningOn ? 'Turn on public access' : 'Turn off public access'}</button>
          <button className="text-link" disabled={busy} onClick={() => setConfirm(false)}>Cancel</button>
        </div>
      </div>
      : <div className="network-actions"><button className="button secondary" disabled={changing || job.phase !== 'Active'}
        onClick={() => { setConfirm(true); setError(null) }}>{job.public ? 'Turn off…' : 'Turn on public access…'}</button></div>}
    {error && <p className="network-error" role="alert">{error}</p>}
    {job.public && status && <>
      <dl className="network-facts">
        <div><dt>Internet address</dt><dd>{status.wanAddress ?? 'Not found'}</dd></div>
        <div><dt>Router</dt><dd className={status.forwardError ? 'network-error' : undefined}>{status.forwardError
          ?? (status.forward ? forwards[status.forward] ?? status.forward : `UniFi isn’t connected. Forward TCP 443 to ${ingressAddress ?? 'Lucia'}:8445 on your router.`)}</dd></div>
      </dl>
      {status.recordsError && <p className="network-error">{status.recordsError}</p>}
      {status.records.length > 0 && <table className="network-table"><caption className="network-table-caption">Public names in Cloudflare</caption>
        <thead><tr><th>Name</th><th>Status</th></tr></thead>
        <tbody>{status.records.map(record => <tr key={record.name}>
          <th scope="row">{record.name}</th>
          <td data-label="Status" className={record.state === 'Conflict' ? 'network-error' : undefined}>{record.state}
            {record.detail && <span className="network-cell-note">{record.detail}</span>}</td>
        </tr>)}</tbody>
      </table>}
      <p className="section-note">Checked {new Date(status.checkedAt).toLocaleString()}. Give an app a public name on its page under Apps.</p>
    </>}
  </section>
    <ExternalRoutes zone={job.zone} publicOn={job.public} session={session} refreshSession={refreshSession} />
  </>
}

type Row = { host: string; address: string; port: string; public: string }
const row = (route: ExternalRoute): Row => ({ host: route.host, address: route.address, port: String(route.port), public: route.public ?? '' })

function ExternalRoutes({ zone, publicOn, session, refreshSession }: { zone: string | null; publicOn: boolean; session: AuthenticationSession; refreshSession: () => Promise<void> }) {
  const [routes, setRoutes] = useState<ExternalRoute[] | null>(null)
  const [rows, setRows] = useState<Row[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    void ownerRequest(session, refreshSession, '/api/host/routes/external')
      .then(async response => setRoutes(parseExternalRoutes(await response.json())))
      .catch(failure => setError(failed(failure, 'Other services could not be read.')))
  }, [session, refreshSession])

  async function save() {
    if (!rows) return
    setBusy(true); setError(null)
    try {
      const response = await ownerRequest(session, refreshSession, '/api/host/routes/external', 'PUT', {
        routes: rows.map(item => ({ host: item.host.trim(), address: item.address.trim(), port: Number(item.port), public: item.public.trim() || null })),
      })
      setRoutes(parseExternalRoutes(await response.json())); setRows(null)
    } catch (failure) { setError(failed(failure, 'Other services could not be saved.')) } finally { setBusy(false) }
  }
  const change = (index: number, key: keyof Row, value: string) => setRows(current => current!.map((item, at) => at === index ? { ...item, [key]: value } : item))

  return <section className="surface network-section" aria-labelledby="external-routes">
    <h2 id="external-routes">Other services on your network</h2>
    <p className="section-note">Devices Lucia doesn’t run, such as Home Assistant or a NAS page, can have an address too. Lucia’s gateway forwards to them over plain HTTP; they don’t sign in through Authentik, so only make public what already has its own login.</p>
    {routes && !rows && <>
      {routes.length > 0 && <table className="network-table"><caption className="network-table-caption">Other services</caption>
        <thead><tr><th>Name</th><th>Forwards to</th><th>Public</th></tr></thead>
        <tbody>{routes.map(route => <tr key={route.host}>
          <th scope="row">{route.host}</th><td data-label="Forwards to">{route.address}:{route.port}</td>
          <td data-label="Public">{route.public ? `${route.public}.${zone}${publicOn ? '' : ' (waits for public access)'}` : 'Internal only'}</td>
        </tr>)}</tbody>
      </table>}
      <div className="network-actions"><button className="button secondary" onClick={() => setRows(routes.map(row))}>{routes.length ? 'Edit services…' : 'Add a service…'}</button></div>
    </>}
    {rows && <form className="external-routes" onSubmit={event => { event.preventDefault(); void save() }}>
      {rows.map((item, index) => <fieldset key={index} className="external-route" disabled={busy}>
        <legend className="network-table-caption">Service {index + 1}</legend>
        <label>Name<input value={item.host} onChange={event => change(index, 'host', event.target.value.toLowerCase())} required maxLength={63}
          pattern="[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?" autoComplete="off" spellCheck={false} placeholder="homeassistant" /></label>
        <label>Address<input value={item.address} onChange={event => change(index, 'address', event.target.value)} required maxLength={15}
          inputMode="decimal" autoComplete="off" spellCheck={false} placeholder="192.168.1.20" /></label>
        <label>Port<input value={item.port} onChange={event => change(index, 'port', event.target.value)} required type="number" min={1} max={65535} /></label>
        <label>Public name<input value={item.public} onChange={event => change(index, 'public', event.target.value.toLowerCase())} maxLength={63}
          pattern="[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?" autoComplete="off" spellCheck={false} placeholder="Internal only" /></label>
        <button type="button" className="text-link" onClick={() => setRows(rows.filter((_, at) => at !== index))}>Remove</button>
      </fieldset>)}
      <div className="network-actions">
        <button type="button" className="button secondary" disabled={busy || rows.length >= 32}
          onClick={() => setRows([...rows, { host: '', address: '', port: '80', public: '' }])}>Add a service</button>
        <button className="button primary" disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
        <button type="button" className="text-link" disabled={busy} onClick={() => { setRows(null); setError(null) }}>Cancel</button>
      </div>
    </form>}
    {error && <p className="network-error" role="alert">{error}</p>}
  </section>
}
