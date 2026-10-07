import assert from 'node:assert/strict'
import { cronOf, deviceName, emptyJob, keyBytes, parseDevices, parseJob, parseJobs, parseNext, parseRuns, runStatusText, scheduleOf, scheduleText,
  scopeText } from '../.checks/assistantJobs.js'
import { parseRoute } from '../.checks/dashboard.js'

const id = 'a'.repeat(32)
const run = { id: 'r1', sessionId: 'b'.repeat(32), trigger: 'manual', started: '2025-01-01T07:00:00Z', finished: null, status: 'running', error: null }
const job = { id, name: 'Triage', prompts: ['Scan apps'], cron: '0 7 * * *', timeZone: 'America/Chicago', model: null, enabled: true,
  tools: ['app_action'], hosts: [], created: 'x', updated: 'x', next: '2025-01-02T13:00:00Z', last: run, running: true }

const parsed = parseJob(job)
assert.equal(parsed.name, 'Triage')
assert.equal(parsed.model, undefined)
assert.equal(parsed.last?.trigger, 'manual')
assert.equal(parsed.last?.finished, undefined)
assert.equal(parseJobs([job, { ...job, last: null }])[1].last, undefined)
assert.throws(() => parseJob({ ...job, id: '../x' }))
assert.throws(() => parseJob({ ...job, prompts: 'Scan' }))
assert.throws(() => parseRuns([{ ...run, status: 'exploded' }]))
assert.throws(() => parseRuns([{ ...run, sessionId: 'nope' }]))
assert.equal(parseRuns([{ ...run, trigger: 'other', status: 'failed', error: 'x'.repeat(500) }])[0].error?.length, 400)
assert.equal(runStatusText({ ...parsed.last, status: 'succeeded' }), 'Finished')
assert.equal(parseRuns([{ ...run, status: 'unanswered' }])[0].status, 'unanswered')
assert.equal(runStatusText({ ...parsed.last, status: 'unanswered' }), 'Approval expired')
assert.equal(parseJob({ ...job, waiting: '2025-01-01T07:30:00Z' }).waiting, '2025-01-01T07:30:00Z')
assert.equal(parsed.waiting, undefined)
for (const cron of ['0 * * * *', '0 */6 * * *', '0 7 * * *', '30 6 * * 1-5', '15 18 * * 0', '5 9 * * 6'])
  assert.equal(cronOf(scheduleOf(cron)), cron)
assert.deepEqual(scheduleOf('30 6 * * 1-5'), { frequency: 'weekdays', time: '06:30', day: 1 })
assert.equal(scheduleOf('0 7 1 * *').frequency, 'custom')
assert.equal(scheduleOf('0 24 * * *').frequency, 'custom')
assert.equal(cronOf({ frequency: 'custom', time: '07:00', day: 1 }), undefined)
assert.equal(cronOf({ frequency: 'daily', time: '', day: 1 }), undefined)
assert.equal(scheduleText('0 7 * * 1'), 'Mondays at 7:00')
assert.equal(scheduleText('30 18 * * 1-5'), 'Weekdays at 18:30')
assert.equal(scheduleText('0 7 1 * *'), '0 7 1 * *')
assert.equal(scopeText({ tools: [], hosts: [] }, () => false), 'Asks before every change')
assert.equal(scopeText({ tools: ['app_action', 'run_command'], hosts: ['docs.docker.com'] }, tool => tool === 'run_command'), '2 changes without asking · 1 risky · 1 site')
assert.deepEqual(parseNext({ next: ['a', 'b'] }), ['a', 'b'])
assert.throws(() => parseNext({ next: 'a' }))
assert.deepEqual(parseDevices([{ id: 'd', name: 'Phone', endpoint: 'https://x', added: 'a', lastUsed: null }]), [{ id: 'd', name: 'Phone', endpoint: 'https://x', added: 'a' }])
assert.deepEqual(emptyJob('UTC').prompts, [''])
assert.deepEqual([...keyBytes('AQID_-8')], [1, 2, 3, 255, 239])
assert.equal(deviceName('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0 Safari/537.36 Edg/130.0'), 'Edge on Windows')
assert.equal(deviceName('Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1'), 'Safari on iOS')
assert.deepEqual(parseRoute('#/?chat=' + id), { page: 'home' })
assert.deepEqual(parseRoute('#/ai/jobs'), { page: 'jobs' })
assert.deepEqual(parseRoute('#/jobs'), { page: 'not-found' })
console.log('Jobs checks passed.')
