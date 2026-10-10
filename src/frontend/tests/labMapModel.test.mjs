import assert from 'node:assert/strict'
import { ago, carServices, categoryLabel, districtValue, districtsOf, dominant, formatBps, mixTotals, portsOf, inSubnet, labCounts, layoutLab, parseLabMap, parseLabMapLive, roadPath, routeOf, searchLab, streaks, wirelessVia } from '../.checks/labMapModel.js'

assert.ok(inSubnet('192.168.0.241', '192.168.0.0/24'))
assert.ok(!inSubnet('192.168.1.5', '192.168.0.0/24'))
assert.ok(inSubnet('10.20.30.40', '10.0.0.0/8'))
assert.ok(!inSubnet('not-an-ip', '10.0.0.0/8'))
assert.ok(!inSubnet('10.0.0.1', '10.0.0.0/40'))

assert.throws(() => parseLabMap({}), /could not read/)
const empty = parseLabMap({ generatedAt: '2026-10-10T00:00:00Z' })
assert.equal(empty.unifi.state, 'not-connected')
assert.equal(empty.traffic, 'none')
assert.equal(empty.wan.name, 'Internet')
const emptyLayout = layoutLab(empty)
assert.deepEqual(routeOf(emptyLayout, 'gateway'), ['gateway', 'wan'])

const map = parseLabMap({
  generatedAt: '2026-10-10T00:00:00Z', traffic: 'full', unifi: { state: 'connected' },
  gateway: { id: 'gw', kind: 'gateway', name: 'Cloud Gateway', mac: 'aa' },
  wan: { name: 'Internet', isp: 'Fiber', health: 'ok' },
  networks: [{ id: 'lan', name: 'Default', vlan: null, subnet: '192.168.0.0/24' }, { id: 'iot', name: 'IoT', vlan: 20, subnet: '192.168.20.0/24' }, { name: 'missing id' }],
  devices: [{ id: 'gw', kind: 'gateway', name: 'Duplicate gateway' }, { id: 'sw', kind: 'switch', name: 'Core switch', parentId: 'gw' }, { id: 'ap', kind: 'ap', name: 'Hall AP', parentId: 'sw' }],
  hosts: [{ id: 'spark', kind: 'spark', name: 'Spark', address: '192.168.0.222', parentId: 'sw', health: 'ok' }, { id: 'n1', kind: 'node', name: 'lucialab01', address: '192.168.0.241', parentId: 'sw', health: 'warn' }],
  apps: [{ id: 'app:adguard', name: 'AdGuard', hostId: 'n1', health: 'failed', networkId: 'iot', containers: [{ id: 'c1', name: 'adguard', mounts: [{ source: '/mnt/nas', destination: '/data', storageId: 'nas' }] }] },
    { id: 'app:ghost', name: 'Ghost', hostId: 'n9' }, { id: 'no-host' }],
  storage: [{ id: 'nas', name: 'NAS', address: '192.168.0.10', health: 'ok', mounts: [{ hostId: 'n1', share: 'media' }] }],
  clientGroups: [{ id: 'cg:iot', networkId: 'iot', group: 'iot', online: 3, total: 4, members: [{ mac: '11', name: 'Thermostat', parentId: 'ap', online: true }, { mac: '22', name: 'Plug', parentId: 'ap' }, { mac: '33', name: 'Lamp', parentId: 'sw' }] }],
})
assert.equal(map.networks.length, 2)
assert.equal(map.apps.length, 2)

const layout = layoutLab(map)
assert.equal(layout.gatewayId, 'gw')
assert.equal(layout.nodes.get('gw').kind, 'gateway')
assert.equal(layout.nodes.get('n1').networkId, 'lan')
assert.equal(layout.nodes.get('n9').networkId, 'net:other')
// A quarter per entrance: the AP's clients live in its quarter; the core switch takes its own clients, the servers and anything unplaced.
assert.deepEqual(layout.plinths.map(plinth => plinth.id), ['quarter:sw', 'quarter:ap'])
assert.equal(layout.nodes.get('sw').arch, true)
assert.equal(layout.nodes.get('grp:ap:iot').parentId, 'ap')
assert.equal(layout.groups.get('grp:ap:iot').total, 2)
assert.equal(layout.nodes.get('client:11').quarter, 'quarter:ap')
assert.equal(layout.nodes.get('client:33').quarter, 'quarter:sw')
assert.equal(layout.nodes.get('n9').quarter, 'quarter:sw')
assert.equal(layout.nodes.get('nas').quarter, 'quarter:sw')
assert.equal(layout.nodes.get('n1').category, 'server')
assert.equal(layout.nodes.get('client:11').categoryChosen, false)
assert.ok(layout.blocks.some(block => block.id === 'hood:grp:ap:iot:other' && block.layer === 'client' && block.tint !== undefined))
for (const node of layout.nodes.values()) for (const axis of ['x', 'y', 'z']) assert.ok(Number.isFinite(node[axis]), `${node.id}.${axis}`)
const host = layout.nodes.get('n1'), app = layout.nodes.get('app:adguard')
assert.ok(Math.hypot(app.x - host.x, app.z - host.z) <= 2.8 * 3)
assert.equal(app.block, 'n1')
const inside = ([x, z], poly) => poly.reduce((hit, [x1, z1], i) => { const [x2, z2] = poly[(i + 1) % poly.length]; return (z1 > z) !== (z2 > z) && x < (x2 - x1) * (z - z1) / (z2 - z1) + x1 ? !hit : hit }, false)
const clients = [...layout.nodes.values()].filter(node => node.kind === 'client')
assert.equal(clients.length, 3)
assert.equal(layout.nodes.get('client:11').parentId, 'ap')
assert.equal(layout.nodes.get('client:33').parentId, 'sw')
for (const client of clients) assert.ok(inside([client.x, client.z], layout.blocks.find(block => block.id === client.block).polygon), `${client.id} inside its district`)
assert.ok(layout.blocks.every(block => block.polygon.length >= 3))
for (const a of layout.nodes.values()) for (const b of layout.nodes.values())
  if (a.id < b.id && a.block && a.block === b.block && a.height && b.height) assert.ok(Math.hypot(a.x - b.x, a.z - b.z) >= (a.size + b.size) / 2 * 0.9, `${a.id} clears ${b.id}`)
assert.ok([...layout.nodes.values()].every(node => node.height === undefined || node.height > 0))
assert.ok(layout.roads.some(road => road.kind === 'lane'))
assert.ok(layout.plinths.every(plinth => plinth.polygon.length >= 3))
for (const node of layout.nodes.values()) if (node.path) assert.ok(node.path.flat().every(Number.isFinite), `${node.id}.path`)
const wan = layout.nodes.get('wan'), gatePath = layout.nodes.get('gw').path
assert.ok(Math.hypot(gatePath.at(-1)[0] - wan.x, gatePath.at(-1)[1] - wan.z) < 0.01)
for (const kind of ['lane', 'street', 'avenue', 'highway']) assert.ok(layout.roads.some(road => road.kind === kind), kind)
const flow = roadPath(layout, 'n1', 'nas')
assert.ok(flow.length > 3 && flow.every(p => Number.isFinite(p[0]) && Number.isFinite(p[1])))
const sw = layout.nodes.get('sw')
assert.ok(Math.hypot(sw.path.at(-1)[0] - layout.nodes.get('gw').x, sw.path.at(-1)[1] - layout.nodes.get('gw').z) < 0.01)
assert.equal(JSON.stringify([...layoutLab(map).nodes.values()].map(node => [node.x, node.z])), JSON.stringify([...layout.nodes.values()].map(node => [node.x, node.z])))
assert.deepEqual(layout.tethers.map(line => `${line.kind}:${line.from}>${line.to}`).sort(), ['storage:nas>app:adguard'])

assert.deepEqual(routeOf(layout, 'app:adguard'), ['app:adguard', 'n1', 'sw', 'gw', 'wan'])
assert.deepEqual(routeOf(layout, 'grp:ap:iot'), ['grp:ap:iot', 'ap', 'sw', 'gw', 'wan'])
assert.deepEqual(routeOf(layout, 'client:33'), ['client:33', 'sw', 'gw', 'wan'])
assert.equal(wirelessVia(layout, 'client:11'), 'ap')
assert.equal(wirelessVia(layout, 'client:33'), null)
assert.equal(wirelessVia(layout, 'grp:ap:iot'), null)

assert.equal(searchLab(map, layout, 'adg')[0].id, 'app:adguard')
assert.equal(searchLab(map, layout, 'thermostat')[0].id, 'client:11')
assert.deepEqual(searchLab(map, layout, '  '), [])

const live = parseLabMapLive({ at: '2026-10-10T00:00:10Z', traffic: 'full', rates: { n1: { rxBps: 5000, txBps: -3 } }, health: { spark: 'failed', x: 'bogus' }, flows: [{ from: 'n1', to: 'wan', bps: 10 }, { from: 'n1' }] })
assert.deepEqual(live.rates.n1, { rxBps: 5000, txBps: 0 })
assert.equal(live.health.x, 'unknown')
assert.equal(live.flows.length, 1)
const counts = labCounts(map, layout, live)
assert.deepEqual(counts.attention.slice(0, 2).sort(), ['app:adguard', 'spark'])

assert.equal(formatBps(0), 'Idle')
assert.equal(formatBps(512), '512 b/s')
assert.equal(formatBps(1_500_000), '1.5 Mb/s')
assert.equal(formatBps(250_000_000), '250 Mb/s')
assert.equal(formatBps(3e12), '3000 Gb/s')
assert.deepEqual(streaks(0), { count: 0, speed: 0 })
assert.ok(streaks(1e9).count <= 10 && streaks(1e9).count > streaks(1e4).count)
assert.equal(ago(null, 0), 'Never')
assert.equal(ago('2026-10-10T00:00:00Z', Date.parse('2026-10-10T00:02:00Z')), '2 min ago')

const typed = parseLabMapLive({ at: '2026-10-10T00:00:10Z', traffic: 'full',
  mix: { 'client:11': { web: 600, streaming: 400, bogus: 50, dns: -5 } },
  listening: { n1: [{ port: 53, protocol: 'udp', service: 'dns', bps: 900 }, { port: 0 }, { port: 443, service: 'web' }] },
  flows: [{ from: 'n1', to: 'dest:1.2.3.0/24', bps: 10, service: 'nope' }],
  destinations: [{ id: 'dest:1.2.3.0/24', name: 'Example', category: 'streaming', bps: 2000, mix: { streaming: 2000 } }, { id: 'dest:x', category: 'weird' }, { name: 'no id' }] })
assert.deepEqual(typed.mix['client:11'], { web: 600, streaming: 400, other: 50 })
assert.deepEqual(typed.listening.n1.map(item => `${item.port}/${item.protocol}`), ['53/udp', '443/tcp'])
assert.equal(typed.flows[0].service, 'other')
assert.equal(typed.destinations.length, 2)
assert.equal(typed.destinations[1].category, 'weird')
assert.equal(typed.destinations[1].chosen, false)
assert.equal(districtValue(' dev   tools '), 'dev tools')
assert.equal(districtValue('Chat and calls'), 'comms')
assert.deepEqual(districtsOf(['other', 'Dev tools', 'updates', 'cloud', 'Dev tools', 'Arcade']), ['cloud', 'updates', 'Arcade', 'Dev tools', 'other'])
assert.equal(categoryLabel('Dev tools'), 'Dev tools')
assert.equal(dominant(typed.destinations[0].mix), 'streaming')
const cars = carServices({ web: 600, streaming: 400 }, 5)
assert.equal(cars.length, 5)
assert.equal(cars.filter(item => item === 'web').length, 3)
assert.deepEqual(carServices({}, 4), [])
assert.deepEqual(portsOf(layout, typed, 'n1').map(item => item.port), [53, 443])

const sites = [{ id: 'dest:1.2.3.0/24', name: 'Example', category: 'streaming', service: 'streaming', peak: 2000, org: null, domains: [] },
  { id: 'dest:9.9.9.0/24', name: 'Quad9', category: 'other', service: 'dns', peak: 0, org: 'Quad9', domains: ['dns.quad9.net'] }]
const city = layoutLab(map, sites)
const site = city.nodes.get('dest:1.2.3.0/24'), region = city.nodes.get(site.block)
assert.equal(site.kind, 'site')
assert.equal(region.kind, 'region')
assert.ok(site.x < city.nodes.get('wan').x, 'the Internet city sits west of the WAN')
assert.ok(site.height > 0 && Number.isFinite(site.tint))
assert.ok(site.path.flat().every(Number.isFinite))
assert.ok(Math.hypot(site.path.at(-1)[0] - city.nodes.get('wan').x, site.path.at(-1)[1] - city.nodes.get('wan').z) < 0.01)
assert.ok(city.blocks.some(block => block.id === region.id))
for (const node of city.nodes.values()) if (node.kind === 'site') assert.ok(inside([node.x, node.z], city.blocks.find(block => block.id === node.block).polygon), `${node.id} inside its district`)
const crowd = layoutLab(map, Array.from({ length: 7 }, (_, k) => ({ ...sites[0], id: `dest:${k}`, name: `Site ${k}`, category: k % 2 ? 'cloud' : 'cdn' })))
const placed = [...crowd.nodes.values()].filter(node => node.kind === 'site')
for (const a of placed) for (const b of placed) if (a.id < b.id) assert.ok(Math.hypot(a.x - b.x, a.z - b.z) >= (a.size + b.size) / 2 * 1.2, `${a.id} clears ${b.id}`)
assert.equal(JSON.stringify(city.nodes.get('n1')), JSON.stringify(layout.nodes.get('n1')), 'the home city does not move when destinations appear')
const totals = mixTotals(city, typed)
assert.equal(totals.get('ap').web, 600)
assert.equal(totals.get('wan').streaming, 400)
assert.equal(totals.get(region.id).streaming, 2000)

console.log('labMapModel tests passed')
