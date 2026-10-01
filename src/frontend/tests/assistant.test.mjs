import assert from 'node:assert/strict'
import { approvalNote, chatTitle, chooseModel, grantText, isChatId, isDestructiveTool, isGitHubModel, isQuestionTool, messageText, newChatId, pageRoute,
  parseDock, parseGitHub, parseModels, parseSessions, parseSettings, parseTranscript, questionOf, secretOf, settledText, signInModel, toolLabel, toolNote,
  toolStatus, toolTitle, urlHost, waitingParts } from '../.checks/assistant.js'

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

// Tool calls keep what the dock shows; a part it can't show is dropped, not guessed at.
const call = { type: 'dynamic-tool', toolCallId: 'c1', toolName: 'get_app', state: 'output-available', input: { app: 'grafana' }, output: { name: 'grafana' }, extra: 1 }
const refused = { type: 'dynamic-tool', toolCallId: 'c2', toolName: 'delete_app', state: 'output-denied', input: { app: 'grafana' },
  approval: { id: 'a1', requestReason: 'This can remove data or interrupt your lab.', approved: false, reason: 'not today', isAutomatic: false, signature: 'x' } }
const failed = { type: 'dynamic-tool', toolCallId: 'c3', toolName: 'read_logs', state: 'output-error', input: {}, errorText: 'No such container.', output: 'stale' }
assert.deepEqual(parseTranscript({ title: 'Tools', running: false, messages: [{ id: 'a', role: 'assistant', parts: [call, refused, failed,
  { ...refused, toolCallId: 'c4', approval: { approved: false } }, { ...failed, toolCallId: 'c5', errorText: null }, { ...call, toolCallId: 'x'.repeat(129) },
  { ...call, toolCallId: 'c6', state: 'output-unknown' }, { ...call, toolCallId: 'c7', toolName: '' }] }] }).messages[0].parts, [
  { type: 'dynamic-tool', toolCallId: 'c1', toolName: 'get_app', state: 'output-available', input: { app: 'grafana' }, output: { name: 'grafana' } },
  { type: 'dynamic-tool', toolCallId: 'c2', toolName: 'delete_app', state: 'output-denied', input: { app: 'grafana' },
    approval: { id: 'a1', requestReason: 'This can remove data or interrupt your lab.', approved: false, reason: 'not today' } },
  { type: 'dynamic-tool', toolCallId: 'c3', toolName: 'read_logs', state: 'output-error', input: {}, errorText: 'No such container.' }])

assert.equal(toolTitle('read_logs', { node: 'lucialab02', container: 'grafana', tail: 200 }), 'Read logs of grafana on lucialab02')
assert.equal(toolTitle('app_action', { app: 'immich', action: 'restart' }), 'Restart immich')
assert.equal(toolTitle('app_action', { app: 'immich', action: 'explode' }), 'app_action')
assert.equal(toolTitle('node_action', { node: 'lucialab02', action: 'update-agent' }), 'Update Lucia’s agent on lucialab02')
assert.equal(toolTitle('set_public_route', { app: 'immich', host: 'immich.lab', publicName: 'photos.example.com' }), 'Publish immich.lab as photos.example.com')
assert.equal(toolTitle('set_public_route', { app: 'immich', host: 'immich.lab', publicName: null }), 'Take immich.lab off the internet')
assert.equal(toolTitle('read_web_page', { url: 'https://docs.docker.com/compose/', start: 0 }), 'Read docs.docker.com')
assert.equal(toolTitle('read_web_page', { url: 'not a url' }), 'read_web_page')
assert.equal(toolTitle('request_secret', { app: 'plex', name: 'PLEX_CLAIM_TOKEN' }), 'plex needs PLEX_CLAIM_TOKEN')
assert.equal(toolTitle('ask_owner', { question: '  Which\n  server?  ' }), 'Which server?')
assert.equal(toolTitle('ask_owner', { question: 'q'.repeat(300) }), `${'q'.repeat(199)}…`)
assert.equal(toolTitle('move_app', { app: 'immich' }), 'move_app')
assert.equal(toolTitle('get_node', undefined), 'get_node')
assert.equal(toolTitle('mystery', { app: 'x' }), 'mystery')
assert.equal(urlHost('https://raw.githubusercontent.com/a/b'), 'raw.githubusercontent.com')
assert.ok(isDestructiveTool('run_command') && !isDestructiveTool('app_action'))
assert.ok(isQuestionTool('request_secret') && !isQuestionTool('read_web_page'))

const tool = (state, extra = {}) => ({ type: 'dynamic-tool', toolCallId: 'c', toolName: 'delete_app', state, input: { app: 'immich' }, ...extra })
const asking = { id: 'a', requestReason: 'This can remove data or interrupt your lab.' }
const question = { ...tool('input-available'), toolName: 'ask_owner', input: { question: 'Which server?' } }
const secret = { ...tool('input-available'), toolName: 'request_secret', input: { app: 'plex', name: 'PLEX_CLAIM_TOKEN' } }
for (const [part, live, word, tone] of [
  [tool('input-streaming'), true, 'Running…', 'busy'], [tool('input-available'), false, 'Not finished', 'muted'],
  [tool('approval-requested', { approval: asking }), true, 'Needs your approval', 'waiting'],
  [tool('approval-requested', { approval: asking }), false, 'Not answered', 'muted'],
  [tool('approval-requested', { approval: { id: 'a', isAutomatic: true } }), true, 'Running…', 'busy'],
  [tool('approval-responded', { approval: { ...asking, approved: true } }), true, 'Running…', 'busy'],
  [tool('approval-responded', { approval: { ...asking, approved: false } }), true, 'Not run', 'muted'],
  [tool('output-available', { output: {} }), false, 'Done', 'done'], [tool('output-error', { errorText: 'x' }), false, 'Failed', 'failed'],
  [tool('output-error', { errorText: 'The run stopped before this finished.' }), false, 'Not finished', 'muted'],
  [tool('output-denied', { approval: { ...asking, approved: false } }), false, 'Not run', 'muted'],
  [question, true, 'Waiting for you', 'waiting'], [question, false, 'Not finished', 'muted'],
  [{ ...question, state: 'output-available', output: { answer: 'lucialab02' } }, false, 'Answered', 'done'],
  [{ ...question, state: 'output-available', output: 'The owner chose not to answer.' }, false, 'Skipped', 'muted'],
  [{ ...secret, state: 'output-available', output: { saved: true, app: 'plex' } }, false, 'Saved', 'done'],
  [{ ...secret, state: 'output-available', output: 'The owner chose not to give PLEX_CLAIM_TOKEN.' }, false, 'Not given', 'muted']])
  assert.deepEqual(toolStatus(part, live), { word, tone }, `${part.toolName} ${part.state} ${live}`)

const declined = approval => toolNote(tool('output-denied', { approval: { id: 'a', approved: false, ...approval } }))
assert.deepEqual(declined({ reason: 'not today' }), { text: 'You declined: “not today”', tone: 'muted' })
assert.equal(declined({}).text, 'You declined this.')
assert.equal(declined({ reason: 'No answer within 30 minutes.' }).text, 'No answer within 30 minutes.')
assert.equal(declined({ reason: 'The run stopped before you answered.' }).text, 'The run stopped before you answered.')
assert.equal(declined({ isAutomatic: true, reason: 'Plan mode doesn\'t change anything.' }).text, 'Plan mode doesn\'t change anything.')
assert.equal(declined({ isAutomatic: true }).text, 'Lucia’s policy doesn’t allow this.')
assert.deepEqual(toolNote(tool('output-error', { errorText: 'No app is called immich.' })), { text: 'No app is called immich.', tone: 'failed' })
assert.deepEqual(toolNote(tool('output-error', { errorText: 'The run stopped before this finished.' })),
  { text: 'The run stopped before this finished.', tone: 'muted' })
assert.equal(toolNote(tool('output-available', { output: {} })), undefined)
assert.equal(toolNote(question), undefined)
assert.equal(toolNote({ ...question, state: 'output-available', output: { answer: 'lucialab02' } }).text, 'You answered: lucialab02')
assert.equal(toolNote({ ...question, state: 'output-available', output: 'The owner chose not to answer.' }).text, 'You skipped this question.')
assert.equal(toolNote({ ...secret, state: 'output-available', output: { saved: true, app: 'plex' } }).text,
  'Saved in plex’s settings. The assistant never sees it.')
assert.equal(toolNote({ ...secret, state: 'output-available', output: 'Nothing was saved.' }).text, 'You chose not to give it. Nothing was saved.')
assert.equal(approvalNote(tool('output-available', { output: {}, approval: { id: 'a', approved: true } })), 'You approved this.')
assert.equal(approvalNote(tool('output-available', { output: {}, approval: { id: 'a', approved: true, isAutomatic: true,
  reason: 'Runs automatically in your assistant settings.' } })), 'Runs automatically in your assistant settings.')
assert.equal(approvalNote(tool('output-available', { output: {} })), undefined)

// Only the newest answer, while it streams, can be waiting on the owner.
const waiting = [{ role: 'user', parts: [{ type: 'text', text: 'Delete immich' }] }, { role: 'assistant', parts: [{ type: 'text', text: 'Asking first.' },
  tool('approval-requested', { approval: asking }), tool('approval-requested', { toolCallId: 'auto', approval: { id: 'b', isAutomatic: true } }),
  question, secret, { ...question, toolName: 'get_app' }, { ...question, state: 'output-available', output: { answer: 'x' } }] }]
assert.deepEqual(waitingParts(waiting, true).map(part => part.toolName), ['delete_app', 'ask_owner', 'request_secret'])
assert.deepEqual(waitingParts(waiting, false), [])
assert.deepEqual(waitingParts([...waiting, { role: 'user', parts: [] }], true), [])
assert.deepEqual(waitingParts([], true), [])

const settings = { autoTools: ['app_action'], hosts: ['docs.docker.com'], tools: [{ name: 'app_action', tier: 'change', description: 'Starts an app.' },
  { name: 'run_command', tier: 'destructive', description: 'Runs a command.' }] }
assert.deepEqual(parseSettings(settings), settings)
for (const broken of [null, { ...settings, hosts: 'docs.docker.com' }, { ...settings, autoTools: [3] }, { ...settings, tools: null },
  { ...settings, tools: [{ name: 'x', tier: 'risky', description: '' }] }, { ...settings, tools: [{ name: '', tier: 'read', description: '' }] }])
  assert.throws(() => parseSettings(broken))
assert.equal(toolLabel(settings.tools[0]), 'Start, stop, restart and update apps')
assert.equal(toolLabel({ name: 'new_tool', tier: 'change', description: 'Does a new thing.' }), 'Does a new thing.')
assert.equal(grantText(tool('approval-requested', { toolName: 'app_action', input: { app: 'immich', action: 'restart' }, approval: asking })),
  'Approve, and let it start, stop, restart and update apps for the rest of this chat')
assert.equal(grantText(tool('approval-requested', { toolName: 'read_web_page', input: { url: 'https://docs.linuxserver.io/images/plex' }, approval: asking })),
  'Approve, and let it read docs.linuxserver.io for the rest of this chat')
assert.equal(grantText(tool('approval-requested', { toolName: 'read_web_page', input: { url: 'not a url' }, approval: asking })), undefined)
assert.equal(grantText(tool('approval-requested', { approval: asking })), undefined)
assert.equal(grantText(tool('approval-requested', { toolName: 'new_tool', approval: asking })), undefined)
assert.deepEqual(questionOf({ question: '  Which server?  ', choices: ['spark', ' ', 'lab02 '], allowFreeform: false }),
  { question: 'Which server?', choices: ['spark', 'lab02'], freeform: false })
assert.deepEqual(questionOf({ question: 'Name it?', choices: [], allowFreeform: false }), { question: 'Name it?', choices: [], freeform: true })
assert.deepEqual(questionOf({ question: 'Which?', choices: ['a', 3] }), { question: 'Which?', choices: [], freeform: true })
assert.deepEqual(questionOf(null), { question: '', choices: [], freeform: true })
assert.deepEqual(secretOf({ app: 'plex', name: 'PLEX_CLAIM_TOKEN', description: ' From plex.tv/claim. ' }),
  { app: 'plex', name: 'PLEX_CLAIM_TOKEN', description: 'From plex.tv/claim.' })
assert.deepEqual(secretOf('broken'), { app: '', name: '', description: '' })

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
