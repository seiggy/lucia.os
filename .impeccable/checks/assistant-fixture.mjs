import { EventEmitter, once } from 'node:events'
import { randomUUID } from 'node:crypto'

// Synthetic /api/assistant for UI checks. Mirrors the host's contract and chunk order; no model, Copilot, or disk.
const seedId = '5eed'.padEnd(31, '0') + '1'
const models = [{ id: 'gpt-5-mini', name: 'GPT-5 mini' }, { id: 'claude-sonnet-4.5', name: 'Claude Sonnet 4.5' }, { id: 'gpt-5', name: 'GPT-5' }]
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

let mode = 'ready'
let sessions = new Map()
let github = {}
const runs = new Map()
const signedIn = { state: 'connected', login: 'octocat' }

export function resetAssistantFixture(value) {
  mode = value
  github = value === 'disconnected' ? { state: 'disconnected' } : signedIn
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

async function produce(session, run, messageId) {
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
  let text = ''
  for (const word of answer.split(/(?<=\s)/)) {
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
  if (path === '/api/assistant/models') {
    if (github.state !== 'connected') return json({ connected: false, defaultModel: null, models: [] }), true
    if (mode === 'refused') return fail(503, 'copilot_unavailable', 'Copilot did not accept @octocat. Check that the account has GitHub Copilot, then sign in again.')
    return json({ connected: true, defaultModel: 'gpt-5-mini', models }), true
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
    if (github.state !== 'connected') return fail(503, 'assistant_not_connected', 'Sign in with GitHub to use the assistant.')
    if (mode === 'busy' || running(body.sessionId)) return fail(409, 'run_active', 'This chat is still answering. Stop it or wait for it to finish.')
    let session = sessions.get(body.sessionId)
    if (!session) sessions.set(body.sessionId, session = { id: body.sessionId, title: title(body.text), messages: [] })
    session.updated = new Date().toISOString()
    session.model = body.model
    const retried = session.messages.findLastIndex(message => message.role === 'user')
    if (retried >= 0 && session.messages[retried].id === body.messageId) session.messages.splice(retried + 1)
    else session.messages.push({ id: body.messageId, role: 'user', parts: [{ type: 'text', text: body.text }] })
    const run = { chunks: [], done: false, stopped: false, events: new EventEmitter() }
    runs.set(session.id, run)
    void produce(session, run, randomUUID().replaceAll('-', ''))
    await follow(response, run)
    return true
  }
  const match = path.match(/^\/api\/assistant\/sessions\/([a-f0-9]{32})(\/stream|\/stop)?$/)
  if (!match) return fail(404, 'not_found', 'Not found.')
  const [, id, action] = match
  if (action === '/stream' && request.method === 'GET') return running(id) ? (await follow(response, runs.get(id)), true) : empty()
  if (action === '/stop' && request.method === 'POST') {
    if (running(id)) runs.get(id).stopped = true
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
