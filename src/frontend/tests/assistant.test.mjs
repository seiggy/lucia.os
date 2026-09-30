import assert from 'node:assert/strict'
import { chatTitle, chooseModel, isChatId, messageText, newChatId, pageRoute, parseDock, parseModels, parseSessions, parseTranscript, settledText }
  from '../.checks/assistant.js'

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

const models = parseModels({ connected: true, defaultModel: 'gpt-5-mini', models: [{ id: 'gpt-5-mini', name: 'GPT-5 mini' },
  { id: 'claude sonnet', name: 'Bad id' }, { id: 'o4', name: ' ' }] })
assert.deepEqual(models, { connected: true, defaultModel: 'gpt-5-mini', models: [{ id: 'gpt-5-mini', name: 'GPT-5 mini' }, { id: 'o4', name: 'o4' }] })
assert.deepEqual(parseModels({ connected: false, defaultModel: null, models: [] }), { connected: false, models: [] })
assert.throws(() => parseModels({ connected: 'yes', models: [] }))
assert.equal(chooseModel(models, 'o4'), 'o4')
assert.equal(chooseModel(models, 'retired'), 'gpt-5-mini')
assert.equal(chooseModel({ ...models, defaultModel: 'retired' }, null), 'gpt-5-mini')
assert.equal(chooseModel({ connected: true, models: [{ id: 'o4', name: 'o4' }] }, undefined), 'o4')
assert.equal(chooseModel({ connected: false, models: [] }, 'o4'), undefined)

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
console.log('Assistant checks passed.')
