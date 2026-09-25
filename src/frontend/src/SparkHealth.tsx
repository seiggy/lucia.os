import { useCallback, useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { Icon } from './Icon'
import { bytes, historyPath, memoryPercent, parseSparkTelemetry, uptime } from './sparkTelemetry'
import type { SparkSample, SparkTelemetry } from './sparkTelemetry'
import './SparkHealth.css'

const percent = (value: number | null | undefined) => value == null ? 'Measuring…' : `${value.toFixed(1)}%`

function Trend({ label, history, value, end, tone }: {
  label: string; history: SparkSample[]; value: (sample: SparkSample) => number | null; end: number; tone: string
}) {
  const available = history.filter(sample => value(sample) !== null)
  const values = available.map(sample => value(sample)!)
  const description = available.length > 0
    ? `${label} ranged from ${Math.min(...values).toFixed(1)} to ${Math.max(...values).toFixed(1)} percent in the available history. Missing readings are gaps.`
    : `${label}: no history is available yet.`
  return <figure className={`spark-trend spark-trend-${tone}`}>
    <figcaption>{label}<span>{available.length > 0 ? percent(value(available[available.length - 1])) : 'No readings yet'}</span></figcaption>
    {available.length < 2 ? <p className="spark-history-empty">Collecting enough readings for a trend.</p>
      : <svg viewBox="0 0 600 100" preserveAspectRatio="none" role="img" aria-label={description}>
        <path className="spark-grid" d="M0 8 H600 M0 48 H600 M0 88 H600" />
        <path className="spark-line" d={historyPath(history, value, end)} />
      </svg>}
  </figure>
}

export function SparkHealth({ session, refreshSession }: {
  session: AuthenticationSession
  refreshSession: () => Promise<void>
}) {
  const [data, setData] = useState<SparkTelemetry | null>(null)
  const [error, setError] = useState<string | null>(null)
  const request = useRef<AbortController | null>(null)
  const refresh = useCallback(async () => {
    if (request.current || !session.isOwner) return
    const controller = new AbortController()
    request.current = controller
    try {
      const response = await fetch('/api/host/telemetry', {
        credentials: 'same-origin', cache: 'no-store', signal: controller.signal,
      })
      if (response.status === 401 && session.enabled) void refreshSession()
      if (!response.ok) throw new Error(`Spark readings could not be refreshed (HTTP ${response.status}).`)
      const next = parseSparkTelemetry(await response.json())
      if (!controller.signal.aborted) { setData(next); setError(null) }
    } catch (failure) {
      if (!controller.signal.aborted) {
        console.error('Spark telemetry could not be read.', failure)
        setError(failure instanceof Error ? failure.message : 'Spark readings are unavailable.')
      }
    } finally {
      if (request.current === controller) request.current = null
    }
  }, [session.isOwner, session.enabled, refreshSession])

  useEffect(() => {
    let interval: ReturnType<typeof setInterval> | undefined
    const visibilityChanged = () => {
      clearInterval(interval)
      if (document.visibilityState === 'hidden') request.current?.abort()
      else {
        void refresh()
        interval = setInterval(() => { void refresh() }, 10000)
      }
    }
    visibilityChanged()
    document.addEventListener('visibilitychange', visibilityChanged)
    return () => {
      clearInterval(interval)
      document.removeEventListener('visibilitychange', visibilityChanged)
      request.current?.abort()
      request.current = null
    }
  }, [refresh])

  if (!session.isOwner) return null
  const latest = data?.latest
  const attention = !!error || ['Partial', 'Attention', 'Stale', 'Unavailable'].includes(data?.state ?? '')
  const healthy = !error && data?.state === 'Healthy'
  const usedMemory = latest ? memoryPercent(latest) : null
  const current = !error && data?.state !== 'Stale'
  return <section className="surface spark-health" aria-labelledby="spark-health-title">
    <div className="spark-health-heading">
      <div><h2 id="spark-health-title">Your Spark</h2>
        <p className={`status status-${attention ? 'amber' : healthy ? 'green' : 'muted'}`} role="status">
          <Icon name={attention ? 'attention' : healthy ? 'check' : 'clock'} />
          {error ? 'Connection needs attention' : data?.message ?? 'Reading your host'}
        </p>
      </div>
      <span className="spark-updated">{latest ? <>
        {current ? 'Sampled' : 'Last known'} <time dateTime={latest.timestamp}>{new Date(latest.timestamp).toLocaleTimeString()}</time>
      </> : 'Updates every 10 seconds'}</span>
    </div>
    {error && <div className="spark-error" role="alert"><p>{error} {latest && 'Values below are from the last successful check.'}</p>
      <button className="text-link" onClick={() => void refresh()}>Try again <Icon name="refresh" /></button></div>}
    {latest && <>
      <dl className="spark-readings">
        <div><dt>CPU</dt><dd>{latest.cpuCores === null ? 'Unavailable' : percent(latest.cpuPercent)}</dd>
          <small>{latest.cpuCores === null ? 'Host counters unavailable' : `${latest.cpuCores} cores${latest.loadAverage === null ? '' : ` · Load ${latest.loadAverage.toFixed(2)}`}`}</small></div>
        <div><dt>{latest.unifiedMemory ? 'Unified memory' : 'System memory'}</dt><dd>{usedMemory === null ? 'Unavailable' : `${usedMemory.toFixed(1)}% in use`}</dd>
          <small>{latest.memoryAvailableBytes === null ? 'Memory counters unavailable' : `${bytes(latest.memoryAvailableBytes)} available`}
            {latest.memoryTotalBytes === null ? '' : ` of ${bytes(latest.memoryTotalBytes)}`}</small></div>
        <div><dt>GPU</dt><dd>{latest.gpuPercent === null ? 'Unavailable' : percent(latest.gpuPercent)}</dd>
          <small>{latest.gpuTemperatureCelsius === null ? 'Temperature unavailable' : `${latest.gpuTemperatureCelsius.toFixed(0)} °C`}
            {latest.gpuPowerWatts === null ? ' · Power unavailable' : ` · ${latest.gpuPowerWatts.toFixed(1)} W`}</small></div>
        <div><dt>Host storage</dt><dd>{latest.storageAvailableBytes === null ? 'Unavailable' : `${bytes(latest.storageAvailableBytes)} available`}</dd>
          <small>{latest.storageTotalBytes === null ? 'Volume size unavailable' : `${bytes(latest.storageTotalBytes)} host volume`}</small></div>
      </dl>
      <div className="spark-secondary">
        <p>{uptime(latest.uptimeSeconds)}{latest.gpuName ? ` · ${latest.gpuName}` : ''}</p>
        <p>{latest.networkInterface === null ? 'Network counters unavailable' : <>
          {latest.networkInterface} · Receive {latest.receiveBytesPerSecond === null ? 'measuring' : `${bytes(latest.receiveBytesPerSecond)}/s`}
          {' · '}Send {latest.transmitBytesPerSecond === null ? 'measuring' : `${bytes(latest.transmitBytesPerSecond)}/s`}</>}</p>
      </div>
      {latest.unifiedMemory && <p className="spark-note">The CPU and GPU share this memory. Separate GPU VRAM figures are not available on GB10.</p>}
      {latest.unavailable.length > 0 && <p className="spark-note spark-missing">Unavailable: {latest.unavailable.join(', ')}.</p>}
      <details className="spark-history"><summary>Last hour</summary>
        <div className="spark-history-grid">
          <Trend label="CPU" history={data.history} value={sample => sample.cpuPercent} end={Date.parse(latest.timestamp)} tone="cpu" />
          <Trend label="Memory in use" history={data.history} value={memoryPercent} end={Date.parse(latest.timestamp)} tone="memory" />
          <Trend label="GPU" history={data.history} value={sample => sample.gpuPercent} end={Date.parse(latest.timestamp)} tone="gpu" />
        </div>
        <div className="spark-history-axis"><span>1 hour before latest sample</span><span>Latest sample</span></div>
        <p className="spark-note">A rolling hour in memory, sampled every 10 seconds. History starts fresh when Lucia’s host restarts; gaps are not filled with invented readings.</p>
      </details>
    </>}
  </section>
}
