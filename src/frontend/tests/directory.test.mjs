import assert from 'node:assert/strict'
import { describeChange, generatePassword, initials, parseChange, parseDirectory, passwordProblems, pendingFor } from '../.checks/directory.js'
import { parseRoute } from '../.checks/dashboard.js'
import { findDestinations } from '../.checks/navigation.js'

const directory = {
  ready: true, checkedAt: '2026-01-01T00:00:00Z', passwordChange: true, actor: 'zackw',
  users: [{ username: 'zackw', name: 'Zack Way', email: null, active: true, protected: true, synced: true, groups: ['lucia-owners'] }],
  groups: [{ name: 'lucia-owners', description: '', kind: 'directory', protected: true, members: ['zackw'] },
    { name: 'lucia-users', description: 'Can open Lucia.', kind: 'lucia', protected: true, members: [] }],
  apps: [{ slug: 'lucia', name: 'Lucia', launchUrl: null, fixed: true, groups: ['lucia-owners', 'lucia-users'] },
    { slug: 'lucia-app-immich', name: 'Immich', launchUrl: 'https://photos.example.com/', fixed: false, groups: ['lucia-owners'] }],
  changes: [{ id: '20260101000000000-' + 'a'.repeat(32), action: 'setAppGroups', target: 'lucia-app-immich', state: 'pending', message: null, requestedAt: '2026-01-01T00:00:00Z' }],
}
const parsed = parseDirectory(directory)
assert.equal(parsed.users[0].protected, true)
assert.equal(describeChange(parsed.changes[0], parsed.apps), 'Change access to Immich')
assert.equal(describeChange({ action: 'createUser', target: 'sam' }), 'Add sam')
assert.ok(pendingFor(parsed.changes, 'lucia-app-immich'))
assert.ok(!pendingFor(parsed.changes, 'zackw'))
for (const broken of [null, { ...directory, users: [{ username: 'x' }] }, { ...directory, groups: [{ ...directory.groups[0], kind: 'ldap' }] },
  { ...directory, changes: [{ ...directory.changes[0], state: 'running' }] }, { ...directory, ready: 'yes' }])
  assert.throws(() => parseDirectory(broken))
assert.equal(parseChange(directory.changes[0]).target, 'lucia-app-immich')

for (let run = 0; run < 200; run++) {
  const password = generatePassword()
  assert.equal(password.length, 20)
  assert.deepEqual(passwordProblems(password), [])
  assert.ok(!/[0O1lI]/.test(password))
}
let counter = 0
assert.deepEqual(passwordProblems(generatePassword(limit => counter++ % limit)), [])
assert.deepEqual(passwordProblems('short'), ['14 or more characters', 'an uppercase letter', 'a digit', 'a symbol'])
assert.deepEqual(passwordProblems('Correct horse 9 battery~'), [])
assert.equal(initials('Zack Way'), 'ZW')
assert.equal(initials('sam'), 'S')

assert.deepEqual(parseRoute('#/settings/people'), { page: 'people-settings', view: 'people' })
assert.deepEqual(parseRoute('#/settings/people/groups'), { page: 'people-settings', view: 'groups' })
assert.deepEqual(parseRoute('#/settings/people/apps'), { page: 'people-settings', view: 'apps' })
assert.equal(parseRoute('#/settings/people/other').page, 'not-found')
assert.equal(findDestinations('reset password', true)[0]?.page, 'people-settings')
assert.equal(findDestinations('groups', false).length, 0)
console.log('Directory checks passed.')
