import { useState } from 'react'
import type { PointerEvent } from 'react'
import type { ManagedNodeSummary, NodeUsageSample } from './onboarding'

type UsageKey = 'cpu' | 'memory' | 'gpu' | 'cpuTemperature' | 'gpuTemperature'
interface Metric { key: UsageKey; label: string; unit: '%' | '°C'; tone: 'cpu' | 'memory' | 'gpu'; min: number; max: number }

const metrics: Metric[] = [
  { key: 'cpu', label: 'CPU', unit: '%', tone: 'cpu', min: 0, max: 100 },
  { key: 'memory', label: 'Memory', unit: '%', tone: 'memory', min: 0, max: 100 },
  { key: 'gpu', label: 'GPU', unit: '%', tone: 'gpu', min: 0, max: 100 },
  { key: 'cpuTemperature', label: 'CPU temp', unit: '°C', tone: 'cpu', min: 20, max: 100 },
  { key: 'gpuTemperature', label: 'GPU temp', unit: '°C', tone: 'gpu', min: 20, max: 100 },
]
const loadMetrics = metrics.slice(0, 3), temperatureMetrics = metrics.slice(3)
const hour = 3_600_000
const clamp = (value: number) => Math.max(0, Math.min(1, value))
const heat = (metric: Metric, value: number) => metric.unit !== '°C' ? metric.tone : value >= 90 ? 'hot' : value >= 80 ? 'warm' : metric.tone

/** The node's latest reading: its newest heartbeat this hour, or its stored status after a host restart. */
function latestUsage(node: ManagedNodeSummary): NodeUsageSample | null {
  const status = node.status
  if (node.history.length) return node.history[node.history.length - 1]
  if (!status || !node.lastSeenAt) return null
  return { at: node.lastSeenAt, cpu: status.cpuPercent, gpu: status.gpuPercent, cpuTemperature: status.cpuTemperatureCelsius,
    gpuTemperature: status.gpuTemperatureCelsius, memory: 100 * (status.memoryTotalBytes - status.memoryAvailableBytes) / status.memoryTotalBytes }
}

/** An agent that predates CPU and temperature readings: it never sends them, and has an update waiting. */
function needsAgentForUsage(node: ManagedNodeSummary): boolean {
  return node.agentUpdateAvailable !== false && !node.history.some(sample => sample.cpu !== null) && node.status?.cpuPercent == null
}

const time = (value: string) => new Date(value).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })

function polar(radius: number, degrees: number) {
  const angle = (degrees - 90) * Math.PI / 180
  return `${(32 + radius * Math.cos(angle)).toFixed(2)} ${(32 + radius * Math.sin(angle)).toFixed(2)}`
}
const arc = `M${polar(26, -135)} A26 26 0 1 1 ${polar(26, 135)}`
const arcLength = 2 * Math.PI * 26 * 0.75

function Gauge({ metric, value, missing }: { metric: Metric; value: number | null; missing: string }) {
  const rounded = value === null ? null : Math.round(value)
  return <figure className="server-gauge">
    <svg viewBox="0 0 64 58" role="img" aria-label={rounded === null ? `${metric.label}: ${missing}`
      : `${metric.label} ${rounded}${metric.unit === '%' ? ' percent' : ' degrees Celsius'}`}>
      <path className={`server-gauge-track${value === null ? ' is-empty' : ''}`} d={arc} />
      {value === null ? <text className="is-empty" x="32" y="37">—</text> : <>
        <path className={`server-gauge-value tone-${heat(metric, value)}`} d={arc} strokeDasharray={arcLength}
          strokeDashoffset={arcLength * (1 - clamp((value - metric.min) / (metric.max - metric.min)))} />
        <text x="32" y="37">{rounded}<tspan className="server-gauge-unit" dx="1">{metric.unit}</tspan></text>
      </>}
    </svg>
    <figcaption>{value === null && missing === 'No GPU' ? 'No GPU' : metric.label}</figcaption>
  </figure>
}

function linePath(history: NodeUsageSample[], key: UsageKey, start: number, min: number, max: number): string {
  let path = '', previous: number | null = null
  for (const sample of history) {
    const value = sample[key], at = Date.parse(sample.at)
    if (value === null || at < start) { previous = null; continue }
    const point = `${((at - start) / hour * 300).toFixed(1)} ${(100 - clamp((value - min) / (max - min)) * 100).toFixed(1)}`
    // A gap of more than two missed heartbeats breaks the line rather than drawing across it.
    path += previous === null || at - previous > 120_000 ? `M${point}h0` : `L${point}`
    previous = at
  }
  return path
}

function Chart({ history, lines, min, max, grid, height, label, cursor, onScrub }: {
  history: NodeUsageSample[]; lines: Metric[]; min: number; max: number; grid: number[]; height: number; label: string
  cursor: number | null; onScrub: (index: number | null) => void
}) {
  const end = Date.parse(history[history.length - 1].at), start = end - hour
  const x = (index: number) => (Date.parse(history[index].at) - start) / hour * 300
  function scrub(event: PointerEvent<SVGSVGElement>) {
    const box = event.currentTarget.getBoundingClientRect()
    const at = start + clamp((event.clientX - box.left) / box.width) * hour
    let nearest = 0
    history.forEach((sample, index) => {
      if (Math.abs(Date.parse(sample.at) - at) < Math.abs(Date.parse(history[nearest].at) - at)) nearest = index
    })
    onScrub(nearest)
  }
  return <svg className="server-chart" style={{ height }} viewBox="0 0 300 100" preserveAspectRatio="none" role="img" aria-label={label}
    onPointerMove={scrub} onPointerDown={scrub} onPointerLeave={() => onScrub(null)}>
    <path className="server-chart-grid" d={grid.map(value => `M0 ${(100 - (value - min) / (max - min) * 100).toFixed(1)}H300`).join('')} />
    {lines.map(metric => <path key={metric.key} className={`server-chart-line tone-${metric.tone}`} d={linePath(history, metric.key, start, min, max)} />)}
    {cursor !== null && <line className="server-chart-cursor" x1={x(cursor)} x2={x(cursor)} y1="0" y2="100" />}
  </svg>
}

function Legend({ lines }: { lines: Metric[] }) {
  return <span className="server-legend">{lines.map(metric =>
    <span key={metric.key} className={`tone-${metric.tone}`}><i />{metric.label}</span>)}</span>
}

/** Five gauges for the chosen moment and two scrubbable hour charts: load and temperature. */
export function ServerUsage({ node, hasGpu }: { node: ManagedNodeSummary; hasGpu: boolean }) {
  const [cursor, setCursor] = useState<number | null>(null)
  const history = node.history
  const index = cursor !== null && cursor < history.length ? cursor : null
  const sample = index === null ? latestUsage(node) : history[index]
  const oldAgent = needsAgentForUsage(node)
  const missing = (metric: Metric) => metric.tone === 'gpu' && !hasGpu ? 'No GPU' : oldAgent ? 'Update the agent' : 'Not reported'
  const present = (lines: Metric[]) => lines.filter(metric => history.some(entry => entry[metric.key] !== null))
  const load = present(loadMetrics), temperatures = present(temperatureMetrics)
  const moment = index !== null ? time(history[index].at) : node.state === 'Online' ? 'Now' : sample ? `Last reading ${time(sample.at)}` : 'No readings'
  return <>
    <div>
      <div className="server-gauges">{metrics.map(metric =>
        <Gauge key={metric.key} metric={metric} value={sample?.[metric.key] ?? null} missing={missing(metric)} />)}</div>
      {oldAgent && node.status && <p className="server-note">CPU and temperatures appear after an agent update.</p>}
    </div>
    <div className="server-charts">
      <div>
        <div className="server-chart-head"><strong>Last hour · <span className="server-moment">{moment}</span></strong><Legend lines={load} /></div>
        {history.length > 1 ? <Chart history={history} lines={load} min={0} max={100} grid={[0, 50, 100]} height={72} cursor={index}
          onScrub={setCursor} label={`Last hour of ${load.map(metric => metric.label).join(', ')} use. Point at the chart to read an earlier moment.`} />
          : <p className="server-chart-empty" style={{ height: 72 }}>{node.state === 'Online' ? 'Collecting readings. The hour fills in as the agent reports.' : 'No readings this hour.'}</p>}
      </div>
      <div>
        <div className="server-chart-head"><strong>Temperature</strong><Legend lines={temperatures} /></div>
        {history.length > 1 && temperatures.length ? <Chart history={history} lines={temperatures} min={20} max={100} grid={[20, 60, 100]}
          height={48} cursor={index} onScrub={setCursor} label={`Last hour of ${temperatures.map(metric => metric.label).join(' and ')}, 20 to 100 °C.`} />
          : <p className="server-chart-empty" style={{ height: 48 }}>{oldAgent ? 'Update the agent to report temperatures.'
            : history.length > 1 ? 'This machine doesn’t report temperatures.' : 'Collecting readings.'}</p>}
      </div>
    </div>
  </>
}
