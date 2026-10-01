import assert from 'node:assert/strict'
import { chatTitle, chooseModel, isChatId, isGitHubModel, messageText, newChatId, pageRoute, parseDock, parseGitHub, parseModels, parseSessions, parseTranscript,
  settledText, signInModel } from '../.checks/assistant.js'

const id = newChatId()
assert.ok(isChatId(id))
assert.notEqual(newChatId(), id)
assert.ok(!isChatId(id.toUpperCase()))
assert.ok(!isChatId(`${id}0`))

assert.equal(pageRoute('#/ai/models'), '/ai/models')
assert.equal(pageRoute('#/settings/people?view=apps'), '/settings/people')
assert.equal(pageRoute(''), '/')
assert.equal(pageRoute('#/'), '/')
assert.equal(pageRoute('#/apps/<script>'), undefined)
assert.equal(pageRoute(`#/${'a'.repeat(201)}`), undefined)

assert.equal(messageText({ parts: [{ type: 'text', text: 'Hello ' }, { type: 'reasoning', text: 'hidden' }, { type: 'text', text: 'there' }] }), 'Hello there')
assert.equal(chatTitle('  How do I add Immich?\nIt keeps failing.'), 'How do I add Immich?')
assert.equal(chatTitle('first\rsecond'), 'first')
assert.equal(chatTitle(`${'a'.repeat(78)}  ${'b'.repeat(10)}`), `${'a'.repeat(78)}…`)
assert.equal(chatTitle('x'.repeat(80)), 'x'.repeat(80))
assert.equal(chatTitle('   '), '')

assert.equal(settledText('Stacks run for you.\n\n1. '), 'Stacks run for you.')
assert.equal(settledText('Steps:\n1. Describe it.\n2.'), 'Steps:\n1. Describe it.')
assert.equal(settledText('Steps:\n1. Describe it.\n   -\n\n'), 'Steps:\n1. Describe it.')
assert.equal(settledText('Intro\n\n## '), 'Intro')
assert.equal(settledText('Quote:\n\n> -'), 'Quote:')
assert.equal(settledText('Try this:\n\n```yaml\n'), 'Try this:')
assert.equal(settledText('```yaml\nservices: {}\n```'), '```yaml\nservices: {}\n```')
assert.equal(settledText('Partial:\n\n```yaml\nservices:'), 'Partial:\n\n```yaml\nservices:')
assert.equal(settledText('The release shipped in\n2024.'), 'The release shipped in\n2024.')
assert.equal(settledText('A rule:\n\n---'), 'A rule:\n\n---')
assert.equal(settledText('1. Describe'), '1. Describe')
assert.equal(settledText('- '), '')
assert.equal(settledText(''), '')

const summary = { id, title: 'Add Immich', updated: '2026-01-01T00:00:00Z', model: 'gpt-5-mini', running: false }
assert.deepEqual(parseSessions({ sessions: [summary, { ...summary, model: null, title: '' }] }),
  [summary, { id, title: 'Chat', updated: summary.updated, running: false }])
for (const broken of [null, { sessions: {} }, { sessions: [{ ...summary, id: 'nope' }] }, { sessions: [{ ...summary, running: 'no' }] }])
  assert.throws(() => parseSessions(broken))

const models = parseModels({ connected: true, defaultModel: 'gpt-5-mini', sources: [
  { id: 'github', name: 'GitHub Copilot', models: [{ id: 'gpt-5-mini', name: 'GPT-5 mini' }, { id: 'claude sonnet', name: 'Bad id' }, { id: 'o4', name: ' ' }] },
  { id: ' litellm ', name: ' ', models: 'none', reason: `  ${'x'.repeat(500)}  ` },
  { id: 'local', name: 'Local AI', models: [{ id: 'local:qwen3', name: 'qwen3 · Spark' }, null], reason: ' ' },
  null, { id: ' ', name: 'No id' }, 'github'] })
assert.deepEqual(models, { connected: true, defaultModel: 'gpt-5-mini', sources: [
  { id: 'github', name: 'GitHub Copilot', models: [{ id: 'gpt-5-mini', name: 'GPT-5 mini' }, { id: 'o4', name: 'o4' }] },
  { id: 'litellm', name: 'litellm', models: [], reason: 'x'.repeat(400) },
  { id: 'local', name: 'Local AI', models: [{ id: 'local:qwen3', name: 'qwen3 · Spark' }] }],
  models: [{ id: 'gpt-5-mini', name: 'GPT-5 mini' }, { id: 'o4', name: 'o4' }, { id: 'local:qwen3', name: 'qwen3 · Spark' }] })
assert.deepEqual(parseModels({ connected: false, defaultModel: null, sources: [] }), { connected: false, sources: [], models: [] })
for (const broken of [{ connected: 'yes', sources: [] }, { connected: true, models: [] }]) assert.throws(() => parseModels(broken))
assert.equal(chooseModel(models, 'o4'), 'o4')
assert.equal(chooseModel(models, 'local:qwen3'), 'local:qwen3')
assert.equal(chooseModel(models, 'retired'), 'gpt-5-mini')
assert.equal(chooseModel(models, signInModel), 'gpt-5-mini')
assert.equal(chooseModel({ ...models, defaultModel: 'retired' }, null), 'gpt-5-mini')
assert.equal(chooseModel({ connected: true, sources: [], models: [{ id: 'o4', name: 'o4' }] }, undefined), 'o4')
assert.equal(chooseModel({ connected: true, sources: [], models: [] }, 'o4'), undefined)
// Without Copilot, BYOK models answer and the sign-in stays a choice; with nothing listed, signing in is the only one.
const byok = { connected: false, defaultModel: 'gpt-5-mini', sources: [], models: [{ id: 'litellm:llama3', name: 'llama3' }] }
assert.equal(chooseModel(byok, 'gpt-5-mini'), 'litellm:llama3')
assert.equal(chooseModel(byok, signInModel), signInModel)
assert.equal(chooseModel({ connected: false, sources: [], models: [] }, 'o4'), signInModel)
assert.equal(signInModel, 'github:')
for (const model of [undefined, 'gpt-5-mini', signInModel, 'localhost:llama3']) assert.ok(isGitHubModel(model), String(model))
for (const model of ['litellm:llama3', 'local:qwen3', 'local:ollama/llama3']) assert.ok(!isGitHubModel(model), model)

const transcript = parseTranscript({ id, title: 'Add Immich', running: false, messages: [
  { id: 'u1', role: 'user', parts: [{ type: 'text', text: 'How do I add Immich?' }] },
  { id: 'a1', role: 'assistant', parts: [{ type: 'reasoning', text: 'Think', state: 'streaming' }, { type: 'dynamic-tool', toolCallId: 't' },
    { type: 'text', text: 'Open Apps.' }], metadata: { model: 'gpt-5-mini', usage: { inputTokens: 12, outputTokens: -1 }, extra: true } },
  { id: 'u2', role: 'user', parts: [{ type: 'text', text: 'Thanks' }] },
  { id: 'a2', role: 'assistant', parts: [], metadata: { stopped: true } },
  { id: 'a3', role: 'assistant', parts: [], metadata: { stopped: false } },
  { id: 'u3', role: 'user', parts: [{ type: 'reasoning', text: 'no text' }] },
] })
assert.equal(transcript.title, 'Add Immich')
assert.deepEqual(transcript.messages.map(message => message.id), ['u1', 'a1', 'u2', 'a2'])
assert.deepEqual(transcript.messages[1].parts, [{ type: 'reasoning', text: 'Think', state: 'done' }, { type: 'text', text: 'Open Apps.', state: 'done' }])
assert.deepEqual(transcript.messages[1].metadata, { model: 'gpt-5-mini', usage: { inputTokens: 12, outputTokens: undefined } })
assert.deepEqual(transcript.messages[3].metadata, { stopped: true })
assert.equal(parseTranscript({ title: ' ', messages: [], running: true }).title, 'Chat')
for (const broken of [null, { messages: [], running: 'no' }, { messages: [{ id: 'x', role: 'system', parts: [] }], running: false },
  { messages: [{ id: '', role: 'user', parts: [] }], running: false }])
  assert.throws(() => parseTranscript(broken))

assert.deepEqual(parseDock({ open: true, side: 'left' }, true), { open: true, side: 'left' })
assert.deepEqual(parseDock({ open: true, side: 'left' }, false), { open: false, side: 'left' })
assert.deepEqual(parseDock('broken', true), { open: false, side: 'right' })
assert.deepEqual(parseDock({ open: 'yes', side: 'top' }, true), { open: false, side: 'right' })

const signIn = { state: 'pending', login: null, userCode: 'ABCD-1234', verificationUri: 'https://github.com/login/device', interval: 5, message: null }
assert.deepEqual(parseGitHub(signIn), { state: 'pending', userCode: 'ABCD-1234', verificationUri: 'https://github.com/login/device' })
for (const page of ['https://github.com.example.net/login/device', 'http://github.com/login/device', 'https://octo:cat@github.com/login/device',
  'javascript:alert(1)', null])
  assert.throws(() => parseGitHub({ ...signIn, verificationUri: page }), String(page))
for (const code of ['<b>12</b>', 'ABC', 'ABCD 1234', null]) assert.throws(() => parseGitHub({ ...signIn, userCode: code }), String(code))
for (const broken of [null, [], { state: 'signed-in' }, { state: 3 }]) assert.throws(() => parseGitHub(broken))
assert.deepEqual(parseGitHub({ state: 'connected', login: 'octo-cat', message: null }), { state: 'connected', login: 'octo-cat' })
assert.deepEqual(parseGitHub({ state: 'connected', login: '@octocat' }), { state: 'connected' })
assert.deepEqual(parseGitHub({ state: 'expired', login: 'octocat', userCode: 'ABCD-1234', message: '  The code expired.  ' }),
  { state: 'expired', message: 'The code expired.' })
assert.equal(parseGitHub({ state: 'error', message: 'x'.repeat(500) }).message.length, 400)
console.log('Assistant checks passed.')
