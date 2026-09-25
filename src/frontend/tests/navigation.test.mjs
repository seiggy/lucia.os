import assert from 'node:assert/strict'
import { areas, destinations, findDestinations, routeDestination, visibleDestinations } from '../.checks/navigation.js'
import { parseRoute } from '../.checks/dashboard.js'

assert.equal(areas.length, 4)
assert.equal(new Set(destinations.map(item => item.href)).size, destinations.length)
for (const destination of destinations)
  assert.equal(routeDestination(parseRoute(destination.href))?.page, destination.page)
assert.equal(routeDestination(parseRoute('#/ai/models/find'))?.area, 'ai')
assert.deepEqual(parseRoute('#/ai/models/find'), { page: 'ai-models', view: 'find' })
assert.equal(findDestinations('api keys', true)[0].page, 'ai-keys')
assert.equal(findDestinations('hugging face', true)[0].page, 'ai-models')
assert.equal(findDestinations('GPU', true)[0].page, 'home')
assert.equal(findDestinations('unknown future app', true).length, 0)
assert.deepEqual(visibleDestinations(false).map(item => item.page), ['home', 'ai', 'settings'])
assert.equal(findDestinations('api keys', false).length, 0)
assert.equal(findDestinations('models', false).length, 0)
assert.equal(routeDestination({ page: 'not-found' }), undefined)
console.log('Navigation checks passed: current routes, workspace grouping, role-aware search and model-library deep links.')
