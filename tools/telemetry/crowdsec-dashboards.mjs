// Vendors CrowdSec's community Grafana dashboards (crowdsecurity/grafana-dashboards, dashboards_v5, MIT) for Lucia's
// Observability app, at a pinned commit, and adapts them to Lucia: the provisioned Prometheus datasource, the crowdsec job
// on each node's relay, and the metric names CrowdSec 1.8.1 really exports. Run it with node from the repository root;
// it rewrites src/Lucia.Homelab.Server/Telemetry/CrowdSec/*.json and the license beside them.
//
//   node tools/telemetry/crowdsec-dashboards.mjs
import { mkdir, writeFile } from 'node:fs/promises'

const commit = '0d34f686930aab1f11562bdf664ffad60c567e39'
const base = `https://raw.githubusercontent.com/crowdsecurity/grafana-dashboards/${commit}/`
const out = new URL('../../src/Lucia.Homelab.Server/Telemetry/CrowdSec/', import.meta.url)
const prometheus = { type: 'prometheus', uid: 'prometheus' }

// Upstream file, then Lucia's file name, uid and title.
const dashboards = [
  ['dashboards_v5/Crowdsec Overview.json', 'overview.json', 'crowdsec-overview', 'CrowdSec overview'],
  ['dashboards_v5/Crowdsec Details per Machine.json', 'instance.json', 'crowdsec-instance', 'CrowdSec details per server'],
  ['dashboards_v5/Crowdsec Insight.json', 'insight.json', 'crowdsec-insight', 'CrowdSec insight'],
  ['dashboards_v5/LAPI Metrics.json', 'lapi.json', 'crowdsec-lapi', 'CrowdSec local API'],
]

function fixExpr(expr) {
  return expr
    // Renamed in CrowdSec 1.5.
    .replaceAll('cs_bucket_created_total', 'cs_bucket_instantiation_total')
    // The node's relay scrapes several jobs; only CrowdSec's process counters belong here.
    .replace(/\b(process_[a-z_]+)\{instance="\$instance"\}/g, '$1{job="crowdsec", instance="$instance"}')
    // Upstream's Heartbeat panel charts logins.
    .replace('cs_lapi_request_duration_seconds_bucket{endpoint="/v1/watchers/login"}', 'cs_lapi_request_duration_seconds_bucket{endpoint="/v1/heartbeat", instance="$lapi"}')
}

// Grafana 13 has no Angular graph panel; these become time series with the same queries.
const graphOnly = ['aliasColors', 'bars', 'dashLength', 'dashes', 'fill', 'fillGradient', 'hiddenSeries', 'lines', 'linewidth', 'nullPointMode',
  'percentage', 'pointradius', 'points', 'renderer', 'seriesOverrides', 'spaceLength', 'stack', 'steppedLine', 'thresholds', 'timeRegions',
  'tooltip', 'xaxis', 'yaxes', 'yaxis', 'legend', 'options', 'pluginVersion']

function adaptPanel(panel) {
  if (panel.panels) panel.panels.forEach(adaptPanel)
  if (panel.datasource !== null && panel.datasource !== undefined) panel.datasource = prometheus
  if (panel.type === 'graph') {
    for (const key of graphOnly) delete panel[key]
    panel.type = 'timeseries'
    panel.fieldConfig = { defaults: { custom: { drawStyle: 'line', lineWidth: 1, fillOpacity: 10, showPoints: 'never', spanNulls: true } }, overrides: [] }
    panel.options = { legend: { displayMode: 'list', placement: 'bottom', showLegend: true }, tooltip: { mode: 'multi', sort: 'desc' } }
  }
  // Lucia's engine reads the gateway's access log over HTTP and runs AppSec; it has no file, journald, syslog or CloudWatch source.
  const exprs = (panel.targets ?? []).map(target => target.expr ?? '')
  if (exprs.some(expr => /cs_(filesource|journalctlsource|cloudwatch_stream|syslogsource)_hits_total/.test(expr))) {
    const selector = exprs.some(expr => expr.includes('instance="$instance"')) ? '{instance="$instance"}' : ''
    const by = exprs.some(expr => expr.includes('by (instance)')) ? ' by (instance)' : ''
    if (panel.type === 'stat') {
      panel.targets = [{ expr: `sum(increase(cs_httpsource_hits_total${selector}[$__interval]) or up * 0) + sum(increase(cs_appsec_reqs_total${selector}[$__interval]) or up * 0)`, intervalFactor: 2, refId: 'A' }]
      panel.title = 'Total requests read'
    } else {
      panel.targets = [
        { expr: `sum(increase(cs_httpsource_hits_total${selector}[$__interval]))${by}`, legendFormat: by ? '{{instance}} access log' : 'access log', intervalFactor: 2, refId: 'A' },
        { expr: `sum(increase(cs_appsec_reqs_total${selector}[$__interval]))${by}`, legendFormat: by ? '{{instance}} AppSec' : 'AppSec requests', intervalFactor: 2, refId: 'B' },
      ]
      panel.title = 'Requests read'
    }
    panel.description = 'Access-log lines from the gateway and AppSec requests from its bouncer.'
  }
  for (const target of panel.targets ?? []) {
    if (target.datasource !== undefined) target.datasource = prometheus
    if (target.expr) target.expr = fixExpr(target.expr)
    // CrowdSec creates this counter at the first line it can't parse, which Lucia's transform rarely produces; none is a zero.
    if (panel.type === 'stat' && target.expr?.startsWith('sum(increase(cs_parser_hits_ko_total') && !target.expr.includes('vector(0)')) target.expr += ' or vector(0)'
  }
  return panel
}

await mkdir(out, { recursive: true })
await writeFile(new URL('LICENSE', out), await (await fetch(base + 'LICENSE')).text())
for (const [source, file, uid, title] of dashboards) {
  const response = await fetch(base + source.replaceAll(' ', '%20'))
  if (!response.ok) throw new Error(`${source}: ${response.status}`)
  const dashboard = await response.json()
  for (const key of ['__inputs', '__requires', '__elements', 'id']) delete dashboard[key]
  Object.assign(dashboard, {
    uid, title, tags: ['lucia', 'crowdsec'], editable: false, links: [],
    // Prometheus keeps these raw for an hour; Lucia's own CrowdSec dashboard charts alerts from Loki for longer.
    time: { from: 'now-1h', to: 'now' },
    description: `CrowdSec's community dashboard (crowdsecurity/grafana-dashboards ${commit.slice(0, 7)}, MIT), adapted by Lucia.`,
  })
  for (const variable of dashboard.templating?.list ?? []) {
    if (variable.datasource !== undefined) variable.datasource = prometheus
    if (variable.query?.query) variable.query.query = fixExpr(variable.query.query)
    if (typeof variable.definition === 'string') variable.definition = fixExpr(variable.definition)
    delete variable.current
    delete variable.options
  }
  for (const annotation of dashboard.annotations?.list ?? []) if (annotation.builtIn) annotation.datasource = { type: 'grafana', uid: '-- Grafana --' }
  dashboard.panels = (dashboard.panels ?? []).map(adaptPanel)
  await writeFile(new URL(file, out), JSON.stringify(dashboard, null, 2) + '\n')
  console.log(`${file} <- ${source} @ ${commit.slice(0, 7)}`)
}
