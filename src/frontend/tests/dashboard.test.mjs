import assert from 'node:assert/strict'
import { contrast, defaultPreferences, parseRoute, themeColors, themes, validatePreferences } from '../.checks/dashboard.js'

assert.deepEqual(parseRoute('#/'), { page: 'home' })
assert.deepEqual(parseRoute('#/ai'), { page: 'ai' })
assert.deepEqual(parseRoute('#/tasks'), { page: 'tasks' })
assert.deepEqual(parseRoute('#/devices'), { page: 'devices' })
for (const hash of ['#/tasks/backup-repair', '#/tasks/update-check', '#/tasks/media-update', '#/devices/spark', '#/devices/home-server', '#/devices/media-server'])
  assert.deepEqual(parseRoute(hash), { page: 'not-found' }, 'Retired fixtures must not remain accessible')
assert.deepEqual(parseRoute('#/devices/unknown'), { page: 'not-found' })
assert.throws(() => validatePreferences({ ...defaultPreferences, customAccent: 'red' }))
assert.throws(() => validatePreferences({ ...defaultPreferences, appearance: 'unknown' }))
for (const scheme of ['light', 'dark']) {
  for (const theme of themes) {
    const colors = themeColors({ ...defaultPreferences, theme: theme.id }, scheme)
    assert.ok(contrast(colors.accent, colors.onAccent) >= 4.5)
  }
  for (const customAccent of ['#ffffff', '#000000', '#ffff00', '#ff00ff', '#777777', '#00ffff']) {
    const colors = themeColors({ ...defaultPreferences, theme: 'custom', customAccent }, scheme)
    assert.ok(contrast(colors.accent, colors.onAccent) >= 4.5)
    assert.ok(contrast(colors.accentInk, scheme === 'light' ? '#f3f5f9' : '#242c3c') >= 4.5)
  }
}
console.log('Dashboard checks passed: clean routes, retired simulation links, preserved preferences, and theme contrast.')
