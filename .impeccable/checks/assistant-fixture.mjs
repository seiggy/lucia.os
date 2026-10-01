import { EventEmitter, once } from 'node:events'
import { randomUUID } from 'node:crypto'

// Synthetic /api/assistant for UI checks. Mirrors the host's contract and chunk order; no model, Copilot, or disk.
const seedId = '5eed'.padEnd(31, '0') + '1'
const models = [{ id: 'gpt-5-mini', name: 'GPT-5 mini' }, { id: 'claude-sonnet-4.5', name: 'Claude Sonnet 4.5' }, { id: 'gpt-5', name: 'GPT-5' }]
const spark = 'Qwen3.8-27B-TurboFCFusion-735-882-Here-Uncen-NEO-CODER-MAX-MTP-Q6_K'
const local = [{ id: 'local:' + spark, name: spark + ' · Spark' }, { id: 'local:local-ai/qwen3-8b', name: 'qwen3-8b · lucialab02' }]
const thinking = 'The owner wants the moving parts of a custom stack. Explain the stack file first, then where its logs show up.'
const answer = `Custom app stacks are **Compose projects** that Lucia runs for you.

1. Describe the services, volumes, and ports in a stack file.
2. Lucia checks the file, pulls the images, and starts each service.
3. Each service's status and logs appear under **Stacks**.

\`\`\`yaml
services:
  whoami:
    image: traefik/whoami:v1.10
    restart: unless-stopped
\`\`\`

| Setting | Why it matters |
| --- | --- |
| \`restart\` | Brings a crashed service back |
| \`mem_limit\` | Keeps one app from starving the host |

> Start with one service, confirm it answers, then add the rest.`
const seedReply = `## Check the failing service first

A stack that keeps restarting usually has **one service** exiting. Open **Stacks**, pick the app, and read the newest lines of that service's log before restarting anything.`

// Mode 'tools' runs these the way the host's broker does: reads at once, the policy settles some calls, the rest wait in the chat.
const toolTurn = [
  { name: 'list_apps', input: {}, output: { apps: [{ name: 'media', node: 'lucialab02', state: 'restarting' }, { name: 'whoami', node: 'spark', state: 'running' }] } },
  { name: 'read_logs', input: { node: 'lucialab02', container: 'jellyfin' }, error: 'jellyfin isn\'t running on lucialab02, so it has no live log.' },
  { name: 'app_action', tier: 'change', input: { app: 'whoami', action: 'restart' }, output: { app: 'whoami', state: 'running' } },
  { name: 'read_web_page', tier: 'web', input: { url: 'https://docs.linuxserver.io/images/docker-jellyfin/' },
    output: { title: 'jellyfin - LinuxServer.io', text: 'Hardware acceleration needs the container in the render group.' } },
  { name: 'delete_app', tier: 'destructive', input: { app: 'whoami' }, output: { deleted: 'whoami' } },
  { name: 'ask_owner', input: { question: 'Which server should run Jellyfin?', choices: ['lucialab01', 'lucialab02'] } },
  { name: 'request_secret', tier: 'secret', input: { app: 'media', name: 'PLEX_CLAIM_TOKEN',
    description: 'A claim token from plex.tv/claim links this server to your Plex account. It expires after 4 minutes.' } },
]
const toolReply = 'Jellyfin moves to **lucialab02** once Plex is claimed. I kept whoami, as you asked.'
// Mode 'danger' asks for the two calls whose warnings matter most: going public, and a script run as root.
const gpuCheck = 'set -e\nlspci -nn | grep -i nvidia\nnvidia-smi --query-gpu=name,driver_version --format=csv,noheader'
const dangerTurn = [
  { name: 'set_public_route', tier: 'destructive', input: { app: 'whoami', host: 'whoami.lab.example', publicName: 'whoami.example.com' },
    output: { app: 'whoami', routes: [{ host: 'whoami.lab.example', public: 'whoami.example.com' }] } },
  { name: 'run_command', tier: 'destructive', input: { node: 'lucialab02', command: gpuCheck },
    output: { exitCode: 0, output: '01:00.0 VGA compatible controller [0300]: NVIDIA Corporation GA102 [GeForce RTX 3090] [10de:2204]\nNVIDIA GeForce RTX 3090, 550.120' } },
]
const dangerReply = 'whoami answers at **whoami.example.com**, and lucialab02 sees its RTX 3090 with driver 550.120.'
// Mirrors AssistantTools.Warning for the destructive calls these turns make.
const warning = (name, { app, node, host, publicName }) => ({
  delete_app: `Lucia stops ${app} and removes it from its server. Its data directory stays there.`,
  set_public_route: publicName ? `Anyone on the internet will be able to reach ${publicName}.` : `Anyone using ${host} from outside your network loses access.`,
  run_command: `This runs the command below as root on ${node}. It can change anything there.`,
})[name]
const planReply = 'The plan: restart **whoami**, then delete it. Switch to Execute when you want me to run it.'
const tools = [
  ['list_apps', 'read', 'Lists the lab\'s apps.'], ['read_web_page', 'web', 'Reads a public web page.'],
  ['request_secret', 'secret', 'Asks the owner to type a secret for a custom app.'], ['save_custom_app', 'change', 'Creates or changes a custom app.'],
  ['install_catalog_app', 'change', 'Installs a catalog app.'], ['app_action', 'change', 'Starts, stops, restarts or updates an app.'],
  ['move_app', 'change', 'Moves an app to another server.'], ['run_backup', 'change', 'Backs up an app.'],
  ['upgrade_app_images', 'change', 'Upgrades an app\'s images.'], ['check_node_updates', 'change', 'Checks a server for updates.'],
  ['delete_app', 'destructive', 'Deletes an app.'],
].map(([name, tier, description]) => ({ name, tier, description }))
const defaultHosts = ['docs.docker.com', 'hub.docker.com', 'github.com', 'raw.githubusercontent.com']

let mode = 'ready'
let sessions = new Map()
let github = {}
let settings = {}
const runs = new Map()
const signedIn = { state: 'connected', login: 'octocat' }

export function resetAssistantFixture(value) {
  mode = value
  github = value === 'disconnected' || value === 'byok' ? { state: 'disconnected' } : signedIn
  settings = { autoTools: ['app_action'], hosts: [...defaultHosts] }
  runs.clear()
  sessions = new Map([[seedId, {
    id: seedId, title: 'Why does my media stack keep restarting?', updated: '2026-09-24T18:30:00Z', model: 'gpt-5-mini',
    messages: [
      { id: 'seed-question', role: 'user', parts: [{ type: 'text', text: 'Why does my media stack keep restarting?' }] },
      { id: 'seed-answer', role: 'assistant', parts: [{ type: 'text', text: seedReply, state: 'done' }],
        metadata: { model: 'gpt-5-mini', usage: { inputTokens: 812, outputTokens: 64 } } },
    ],
  }]])
}
resetAssistantFixture('ready')

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms))
const running = id => runs.get(id)?.done === false
const title = text => { const line = text.trim().split(/[\r\n]/)[0]; return line.length > 80 ? line.slice(0, 79).trimEnd() + '…' : line }
async function readJson(request) {
  let text = ''
  for await (const chunk of request) text += chunk
  try { return JSON.parse(text) } catch { return undefined }
}

// The host's answer to an approval: a refusal ends the call as denied.
function respond(send, part, approved, reason) {
  part.approval = { ...part.approval, approved, ...(reason ? { reason } : {}) }
  part.state = approved ? 'approval-responded' : 'output-denied'
  send({ type: 'tool-approval-response', approvalId: part.approval.id, approved, ...(reason ? { reason } : {}) })
  if (!approved) send({ type: 'tool-output-denied', toolCallId: part.toolCallId })
}

function result(send, part, output, errorText) {
  Object.assign(part, errorText ? { state: 'output-error', errorText } : { state: 'output-available', output })
  send(errorText ? { type: 'tool-output-error', toolCallId: part.toolCallId, errorText, dynamic: true }
    : { type: 'tool-output-available', toolCallId: part.toolCallId, output, dynamic: true })
}

// Like the host's end of a turn: unanswered approvals are refused and unfinished calls fail, so a saved chat never waits.
function settle(send, parts) {
  for (const part of parts.filter(part => part.type === 'dynamic-tool')) {
    if (part.state === 'approval-requested') respond(send, part, false, 'The run stopped before you answered.')
    else if (part.state === 'input-available' || part.state === 'approval-responded') result(send, part, undefined, 'The run stopped before this finished.')
  }
}

// Resolves with the owner's answer to an approval (keyed by its id) or a question (by its call); undefined when the turn stops first.
const answerOf = (run, key, kind) => new Promise(resolve => run.waits.set(key, { kind, resolve }))

async function useTools(session, run, send, parts, plan, steps) {
  const grants = session.grants ??= new Set()
  for (const step of steps) {
    const tier = step.tier ?? 'read'
    if (run.stopped) return
    if (plan && !['list_apps', 'app_action', 'delete_app'].includes(step.name)) continue
    const toolCallId = 'call_' + randomUUID().replaceAll('-', '').slice(0, 24)
    const part = { type: 'dynamic-tool', toolCallId, toolName: step.name, state: 'input-available', input: step.input }
    parts.push(part)
    send({ type: 'tool-input-available', toolCallId, toolName: step.name, input: step.input, dynamic: true })
    // The broker's verdict: plan mode refuses changes, destructive calls always ask, the owner's settings and grants skip asking.
    const host = tier === 'web' ? new URL(step.input.url).host : undefined
    const grant = tier === 'change' ? 'tool:' + step.name : host && 'host:' + host
    const granted = grants.has(grant)
    const ask = { destructive: warning(step.name, step.input), web: `${host} isn't on the assistant's allowed sites.`, change: 'This changes your lab.' }[tier]
    const denied = plan && ['change', 'destructive', 'secret'].includes(tier)
    const allowed = !denied && tier !== 'destructive' && (settings.autoTools.includes(step.name) || granted || settings.hosts.includes(host))
    const approvalId = 'approval-' + randomUUID().replaceAll('-', '')
    if (denied || (ask && allowed)) {
      part.state = 'approval-requested'
      part.approval = { id: approvalId, isAutomatic: true }
      send({ type: 'tool-approval-request', approvalId, toolCallId, isAutomatic: true })
      respond(send, part, !denied, denied ? 'Plan mode doesn\'t change anything. Switch to Execute to run it.' : granted ? 'You allowed this for this chat.'
        : host ? `${host} is on the assistant's allowed sites.` : 'Runs automatically in your assistant settings.')
      if (denied) continue
    } else if (ask) {
      part.state = 'approval-requested'
      part.approval = { id: approvalId, requestReason: ask }
      send({ type: 'tool-approval-request', approvalId, toolCallId, reason: ask })
      const answer = await answerOf(run, approvalId, 'approval')
      if (!answer) return
      if (answer.approved && answer.always && grant) grants.add(grant)
      respond(send, part, answer.approved, answer.reason)
      if (!answer.approved) continue
    }
    await sleep(150)
    if (run.stopped) return
    let output = step.output
    if (step.name === 'ask_owner' || step.name === 'request_secret') {
      const reply = await answerOf(run, toolCallId, step.name === 'ask_owner' ? 'answer' : 'secret')
      if (!reply) return
      const { app, name } = step.input
      output = step.name === 'ask_owner'
        ? reply.value != null ? { answer: reply.value } : 'The owner chose not to answer. Go on without it if you can; otherwise say what you need and stop.'
        : reply.value != null ? { saved: true, app, name, note: 'Lucia delivers it within a minute; check get_app or list_apps, and read_logs if it fails.' }
        : `The owner chose not to give ${name}. Nothing was saved.`
    }
    result(send, part, output, step.error)
  }
}

async function produce(session, run, messageId, plan) {
  const send = chunk => { run.chunks.push(JSON.stringify(chunk)); run.events.emit('change') }
  const slow = mode === 'slow'
  const parts = []
  send({ type: 'start', messageId })
  await sleep(slow ? 600 : 150)
  if (!run.stopped) {
    send({ type: 'reasoning-start', id: 'r0' })
    send({ type: 'reasoning-delta', id: 'r0', delta: thinking })
    send({ type: 'reasoning-end', id: 'r0' })
    parts.push({ type: 'reasoning', text: thinking, state: 'done' })
  }
  if (mode === 'tools' || mode === 'danger') await useTools(session, run, send, parts, plan, mode === 'danger' ? dangerTurn : toolTurn)
  let text = ''
  for (const word of (mode === 'danger' ? dangerReply : mode !== 'tools' ? answer : plan ? planReply : toolReply).split(/(?<=\s)/)) {
    if (run.stopped || (mode === 'failing' && text.length > 80)) break
    if (!text) send({ type: 'text-start', id: 't1' })
    text += word
    send({ type: 'text-delta', id: 't1', delta: word })
    await sleep(slow ? 60 : 8)
  }
  if (text) {
    send({ type: 'text-end', id: 't1' })
    parts.push({ type: 'text', text, state: 'done' })
  }
  settle(send, parts)
  const metadata = { model: session.model ?? 'gpt-5-mini', usage: { inputTokens: 640, outputTokens: text.split(/\s+/).length } }
  if (run.stopped) {
    metadata.stopped = true
    send({ type: 'message-metadata', messageMetadata: metadata })
    send({ type: 'abort' })
  } else if (mode === 'failing') {
    metadata.error = 'The model stopped responding.'
    send({ type: 'message-metadata', messageMetadata: metadata })
    send({ type: 'error', errorText: metadata.error })
  } else send({ type: 'finish', finishReason: 'stop', messageMetadata: metadata })
  session.messages.push({ id: messageId, role: 'assistant', parts, metadata })
  session.updated = new Date().toISOString()
  run.chunks.push('[DONE]')
  run.done = true
  run.events.emit('change')
}

// Replays the turn from its first chunk, then follows it, like the host.
async function follow(response, run) {
  response.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-store', 'x-vercel-ai-ui-message-stream': 'v1' })
  const left = new AbortController()
  response.on('close', () => left.abort())
  let next = 0
  try {
    while (true) {
      while (next < run.chunks.length) response.write(`data: ${run.chunks[next++]}\n\n`)
      if (run.done) break
      await once(run.events, 'change', { signal: left.signal })
    }
  } catch { /* The browser left; the turn keeps running. */ }
  response.end()
}

export async function assistantFixture(request, response, path, sessionMode, json) {
  if (!path.startsWith('/api/assistant/')) return false
  const fail = (status, code, message) => { json({ error: { code, message } }, status); return true }
  const empty = () => { response.writeHead(204, { 'Cache-Control': 'no-store' }); response.end(); return true }
  if (sessionMode !== 'owner') return fail(403, 'forbidden', 'Synthetic Owner access is required.')
  if (request.method !== 'GET' && request.headers['x-csrf-token'] !== 'fixture-csrf-request')
    return fail(403, 'invalid_csrf_token', 'Synthetic CSRF check failed.')
  // A device code stays pending until a check approves it with ?approve, standing in for the owner on github.com.
  if (path === '/api/assistant/github' && request.method === 'GET') {
    if (github.state === 'pending' && new URL(request.url, 'http://127.0.0.1').searchParams.has('approve')) github = signedIn
    const { state, login = null, userCode = null, verificationUri = null, interval = null, message = null } = github
    return json({ state, login, userCode, verificationUri, interval, message }), true
  }
  if (path === '/api/assistant/github/device' && request.method === 'POST') {
    github = { state: 'pending', userCode: 'WDJB-MJHT', verificationUri: 'https://github.com/login/device', interval: 5, before: github }
    return json({ state: 'pending', login: null, userCode: github.userCode, verificationUri: github.verificationUri, interval: 5, message: null }), true
  }
  if (path === '/api/assistant/github/device' && request.method === 'DELETE') {
    if (github.state === 'pending') github = github.before.state === 'connected' ? github.before : { state: 'disconnected' }
    return empty()
  }
  if (path === '/api/assistant/github' && request.method === 'DELETE') {
    github = { state: 'disconnected' }
    return empty()
  }
  // Copilot's sign-in gates only Copilot's models; LiteLLM and Local AI list theirs either way.
  if (path === '/api/assistant/models') {
    const connected = github.state === 'connected' && mode !== 'refused'
    const copilot = github.state !== 'connected' ? { models: [], reason: 'Sign in with GitHub to use Copilot\'s models.' }
      : connected ? { models } : { models: [], reason: 'Copilot did not accept @octocat. Check that the account has GitHub Copilot, then sign in again.' }
    const offline = mode === 'disconnected' || mode === 'refused'
    const sources = [
      { id: 'github', name: 'GitHub Copilot', ...copilot },
      { id: 'litellm', name: 'LiteLLM', ...(mode === 'byok' ? { models: [{ id: 'litellm:llama3', name: 'llama3' }] }
        : { models: [], reason: 'Install the LiteLLM app to use its models.' }) },
      { id: 'local', name: 'Local AI', ...(offline ? { models: [], reason: 'Local AI on lucialab02 has no chat model loaded.' } : { models: local }) },
    ]
    return json({ connected, defaultModel: 'gpt-5-mini', sources, models: sources.flatMap(source => source.models) }), true
  }
  if (path === '/api/assistant/sessions')
    return json({ sessions: [...sessions.values()].sort((a, b) => b.updated.localeCompare(a.updated))
      .map(({ id, title, updated, model }) => ({ id, title, updated, model, running: running(id) })) }), true
  if (path === '/api/assistant/chat' && request.method === 'POST') {
    let text = ''
    for await (const chunk of request) { text += chunk; if (text.length > 256 * 1024) return fail(413, 'request_too_large', 'The message is too large.') }
    let body
    try { body = JSON.parse(text) } catch { return fail(400, 'invalid_request', 'The request body is invalid.') }
    if (!/^[a-f0-9]{32}$/.test(body?.sessionId) || !/^[A-Za-z0-9_-]{1,64}$/.test(body.messageId) || !['plan', 'execute'].includes(body.mode)
      || (body.model != null && !/^[A-Za-z0-9._:/-]{1,100}$/.test(body.model)))
      return fail(400, 'invalid_request', 'The request body is invalid.')
    if (typeof body.text !== 'string' || !body.text.trim() || body.text.length > 32768)
      return fail(400, 'invalid_message', 'Messages must be between 1 and 32,768 characters.')
    if (!/^(litellm|local):/.test(body.model ?? '') && github.state !== 'connected')
      return fail(503, 'assistant_not_connected', 'Sign in with GitHub to use Copilot\'s models.')
    if (mode === 'busy' || running(body.sessionId)) return fail(409, 'run_active', 'This chat is still answering. Stop it or wait for it to finish.')
    let session = sessions.get(body.sessionId)
    if (!session) sessions.set(body.sessionId, session = { id: body.sessionId, title: title(body.text), messages: [] })
    session.updated = new Date().toISOString()
    session.model = body.model
    const retried = session.messages.findLastIndex(message => message.role === 'user')
    if (retried >= 0 && session.messages[retried].id === body.messageId) session.messages.splice(retried + 1)
    else session.messages.push({ id: body.messageId, role: 'user', parts: [{ type: 'text', text: body.text }] })
    const run = { chunks: [], done: false, stopped: false, events: new EventEmitter(), waits: new Map() }
    runs.set(session.id, run)
    void produce(session, run, randomUUID().replaceAll('-', ''), body.mode === 'plan')
    await follow(response, run)
    return true
  }
  if (path === '/api/assistant/settings') {
    if (request.method === 'PUT') {
      const body = await readJson(request)
      const change = tools.filter(tool => tool.tier === 'change').map(tool => tool.name)
      const auto = body?.autoTools ?? []
      if (!Array.isArray(auto) || new Set(auto).size !== auto.length || auto.some(name => !change.includes(name)))
        return fail(400, 'invalid_settings', 'Automatic tools must be the assistant\'s change tools, each listed once.')
      const entries = Array.isArray(body?.hosts) ? body.hosts : []
      const hosts = entries.map(host => typeof host === 'string' ? host.trim().replace(/\.$/, '').toLowerCase() : '')
      if (hosts.length > 50) return fail(400, 'invalid_sites', 'Add at most 50 sites.')
      const bad = hosts.findIndex(host => !/^[a-z0-9-]+(\.[a-z0-9-]+)*$/.test(host) || /^[\d.]+$/.test(host))
      if (bad >= 0) {
        const entry = typeof entries[bad] === 'string' ? entries[bad].trim() : ''
        return fail(400, 'invalid_sites', !entry ? 'Each site needs a name, like docs.docker.com.'
          : `${entry} ${/^[\d.]+$|:.*:/.test(entry) ? 'is an address, not a site name' : 'isn\'t a site name'}. Add sites by name, like docs.docker.com.`)
      }
      settings = { autoTools: auto, hosts: [...new Set(hosts)] }
    } else if (request.method !== 'GET') return fail(405, 'method_not_allowed', 'Method not allowed.')
    return json({ ...settings, tools }), true
  }
  const match = path.match(/^\/api\/assistant\/sessions\/([a-f0-9]{32})(\/stream|\/stop|\/approvals\/[^/]+|\/answers\/[^/]+)?$/)
  if (!match) return fail(404, 'not_found', 'Not found.')
  const [, id, action] = match
  if (action === '/stream' && request.method === 'GET') return running(id) ? (await follow(response, runs.get(id)), true) : empty()
  if (action === '/stop' && request.method === 'POST') {
    const run = running(id) && runs.get(id)
    if (run) {
      run.stopped = true
      for (const { resolve } of run.waits.values()) resolve(undefined)
      run.waits.clear()
    }
    return empty()
  }
  // The owner's answers to the cards in the chat, validated like the host's.
  if (action?.startsWith('/approvals/') && request.method === 'POST') {
    const body = await readJson(request)
    if (typeof body?.approved !== 'boolean') return fail(400, 'invalid_request', 'The request body is invalid.')
    if (typeof body.reason === 'string' && body.reason.length > 500) return fail(400, 'invalid_reason', 'Keep the reason under 500 characters.')
    const key = action.slice('/approvals/'.length)
    const waiting = running(id) && runs.get(id).waits.get(key)
    if (waiting?.kind !== 'approval') return fail(404, 'approval_not_found', 'This request was already answered, or its chat has moved on.')
    runs.get(id).waits.delete(key)
    waiting.resolve({ approved: body.approved, reason: typeof body.reason === 'string' ? body.reason.trim() || undefined : undefined, always: body.always === true })
    return empty()
  }
  if (action?.startsWith('/answers/') && request.method === 'POST') {
    const body = await readJson(request)
    const text = typeof body?.answer === 'string' ? body.answer.trim() : undefined
    const secret = typeof body?.secret === 'string' ? body.secret.trim() : undefined
    const key = action.slice('/answers/'.length)
    if ((text !== undefined) + (secret !== undefined) + (body?.declined === true) !== 1 || key.length > 128)
      return fail(400, 'invalid_request', 'Send an answer, a secret or a decline.')
    if (text !== undefined && (!text || text.length > 2000)) return fail(400, 'invalid_answer', 'Answers are 1 to 2,000 characters.')
    if (secret !== undefined && !/^[A-Za-z0-9._~+/=-]{1,4096}$/.test(secret))
      return fail(400, 'invalid_secret', 'Lucia saves it unquoted: up to 4,096 letters, digits and . _ ~ + / = -')
    const kind = text !== undefined ? 'answer' : secret !== undefined ? 'secret' : undefined
    const waiting = running(id) && runs.get(id).waits.get(key)
    if (!waiting || waiting.kind === 'approval' || (kind && waiting.kind !== kind))
      return fail(404, 'question_not_found', 'This question was already answered, or its chat has moved on.')
    runs.get(id).waits.delete(key)
    waiting.resolve({ value: text ?? secret ?? null })
    return empty()
  }
  if (!action && request.method === 'DELETE') {
    if (running(id)) return fail(409, 'run_active', 'Stop this chat before deleting it.')
    sessions.delete(id)
    runs.delete(id)
    return empty()
  }
  if (!action && request.method === 'GET') {
    const session = sessions.get(id)
    if (!session) return fail(404, 'session_not_found', 'This chat no longer exists.')
    return json({ id, title: session.title, messages: session.messages, running: running(id) }), true
  }
  return fail(405, 'method_not_allowed', 'Method not allowed.')
}
