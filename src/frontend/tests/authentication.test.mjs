import assert from 'node:assert/strict'
import { inferenceConnection, parseAuthenticationSession, signInAddress } from '../.checks/authentication.js'

const anonymous = { enabled: true, authenticated: false, username: null, displayName: null, isOwner: false, canAccess: false, csrfToken: null }
assert.deepEqual(parseAuthenticationSession(anonymous), anonymous)
assert.throws(() => parseAuthenticationSession({}), /incomplete/)
assert.throws(() => parseAuthenticationSession({ ...anonymous, enabled: 'false' }), /incomplete/)
assert.throws(() => parseAuthenticationSession({ ...anonymous, authenticated: true }), /incomplete/)
assert.throws(() => inferenceConnection(anonymous), /Sign in/)
const owner = { ...anonymous, authenticated: true, username: 'owner', displayName: 'Owner', isOwner: true, canAccess: true, csrfToken: 'test-antiforgery' }
assert.equal(inferenceConnection(owner).models, '/v1/models')
assert.equal(inferenceConnection(owner).headers['X-CSRF-TOKEN'], 'test-antiforgery')
assert.equal(inferenceConnection({ ...anonymous, enabled: false }).models, '/api/playground/models')
assert.throws(() => inferenceConnection({ ...owner, canAccess: false }), /authorized/)
assert.equal(signInAddress('#/ai'), '/auth/login?returnUrl=%2F%23%2Fai')
assert.equal(signInAddress('https://evil.example/'), '/auth/login?returnUrl=%2F')
console.log('Authentication checks passed: strict session parsing, same-origin endpoints, CSRF, access denial, and local sign-in destinations.')
