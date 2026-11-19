import assert from 'node:assert/strict'
import { areas, breadcrumbs, destinations, findDestinations, routeDestination, visibleAreas, visibleDestinations, workspaceLinks } from '../.checks/navigation.js'
import { parseRoute } from '../.checks/dashboard.js'

assert.deepEqual(areas.map(area => area.id), ['lab', 'apps', 'network', 'ai', 'assistant', 'settings'])
assert.equal(new Set(destinations.map(item => item.href)).size, destinations.length)
for (const destination of destinations)
  assert.equal(routeDestination(parseRoute(destination.href))?.page, destination.page)
for (const area of areas)
  assert.equal(routeDestination(parseRoute(area.href))?.area, area.id)

assert.equal(routeDestination(parseRoute('#/ai/models/find'))?.area, 'ai')
assert.deepEqual(parseRoute('#/ai/models/find'), { page: 'ai-models', view: 'find' })
assert.deepEqual(parseRoute('#/settings/people/groups'), { page: 'people-settings', view: 'groups' })
assert.deepEqual(parseRoute('#/apps/containers'), { page: 'apps', view: 'containers' })
assert.equal(findDestinations('api keys', true)[0].page, 'ai-keys')
assert.equal(findDestinations('hugging face', true)[0].page, 'ai-models')
assert.equal(findDestinations('GPU', true)[0].page, 'home')
assert.equal(findDestinations('unknown future app', true).length, 0)
assert.equal(findDestinations('heimdall', true)[0].page, 'assistant-model-sources')
assert.equal(findDestinations('heimdall', false).length, 0)
assert.equal(findDestinations('docker compose', true)[0].page, 'apps')
assert.equal(findDestinations('scanner', true)[0].page, 'security')
assert.equal(findDestinations('sandbox', true)[0].page, 'assistant-skills')
assert.equal(findDestinations('passwordless', true)[0].page, 'ssh-key-settings')
assert.deepEqual(visibleAreas(false).map(area => area.id), ['ai', 'settings'])
assert.deepEqual(visibleDestinations(false).map(item => item.id), ['home', 'playground', 'appearance'])
assert.equal(findDestinations('api keys', false).length, 0)
assert.equal(findDestinations('models', false).length, 0)
assert.equal(routeDestination({ page: 'not-found' }), undefined)

const appsLinks = hash => workspaceLinks('apps', parseRoute(hash), true)
assert.deepEqual(appsLinks('#/apps').map(item => item.label), ['Your apps', 'Catalog', 'Containers & ports', 'Backups', 'Container registries'])
assert.equal(appsLinks('#/apps/containers').find(item => item.current)?.href, '#/apps/containers')
assert.equal(appsLinks('#/apps/backups').find(item => item.current)?.href, '#/apps/backups')
assert.equal(appsLinks('#/apps/install/adguard').find(item => item.current)?.current, 'location')
assert.equal(appsLinks('#/apps/install/adguard').find(item => item.current)?.href, '#/apps/catalog')
assert.equal(appsLinks('#/apps/example').find(item => item.current)?.href, '#/apps')
assert.deepEqual(workspaceLinks('apps', parseRoute('#/apps'), false), [])
assert.deepEqual(workspaceLinks('ai', parseRoute('#/ai'), false).map(item => item.label), ['Playground'])
assert.equal(workspaceLinks('ai', parseRoute('#/ai/models/on/server-1/find'), true).find(item => item.current)?.current, 'location')
assert.equal(workspaceLinks('settings', parseRoute('#/settings/people/groups'), true).find(item => item.current)?.current, 'location')

assert.deepEqual(breadcrumbs(parseRoute('#/apps/catalog')), [
  { label: 'Apps', href: '#/apps' },
  { label: 'App catalog' },
])
assert.deepEqual(breadcrumbs(parseRoute('#/settings/people/groups')), [
  { label: 'Settings', href: '#/settings' },
  { label: 'People & access', href: '#/settings/people' },
  { label: 'Groups' },
])
assert.deepEqual(breadcrumbs(parseRoute('#/ai/models/on/server-1/find')), [
  { label: 'Local AI', href: '#/ai' },
  { label: 'Local models', href: '#/ai/models' },
  { label: 'server-1 models', href: '#/ai/models/on/server-1' },
  { label: 'Find & download' },
])
assert.deepEqual(breadcrumbs(parseRoute('#/settings/assistant/models')), [
  { label: 'Assistant', href: '#/settings/assistant' },
  { label: 'Model connections' },
])
console.log('Navigation checks passed: workspace grouping, role-aware visibility, legacy routes and contextual breadcrumbs.')
