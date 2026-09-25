import assert from 'node:assert/strict'
import { checkPlaygroundRequest, externalApiBase, parseCompletionDelta, parseModels, readSseData } from '../.checks/playground.js'

assert.equal(checkPlaygroundRequest('/api/playground/models', 'GET', undefined, 'spark:46737', undefined, undefined), null)
assert.equal(checkPlaygroundRequest('/api/playground/chat', 'POST', 'http://spark:46737', 'spark:46737', 'application/json', '1'), null)
assert.equal(checkPlaygroundRequest('/api/playground/admin', 'POST', undefined, 'spark', 'application/json', '1').status, 404)
assert.equal(checkPlaygroundRequest('/api/playground/chat/../../host/models', 'POST', undefined, 'spark', 'application/json', '1').status, 404)
assert.equal(checkPlaygroundRequest('/api/playground/chat', 'DELETE', undefined, 'spark', 'application/json', '1').status, 405)
assert.equal(checkPlaygroundRequest('/api/playground/chat', 'POST', 'http://untrusted.example', 'spark', 'application/json', '1').status, 403)
assert.equal(checkPlaygroundRequest('/api/playground/chat', 'POST', undefined, 'spark', 'text/plain', '1').status, 400)
assert.equal(checkPlaygroundRequest('/api/playground/chat', 'POST', undefined, 'spark', 'application/json', undefined).status, 400)
assert.equal(externalApiBase('http://localhost:5329', 'http://192.168.0.222:46737/#/ai'), 'http://192.168.0.222:5329/v1')
assert.equal(externalApiBase(undefined, 'http://spark:46737'), null)
assert.throws(() => externalApiBase('file:///private', 'http://spark:46737'))
assert.equal(parseModels({ data: [{ id: 'model', capabilities: ['chat'], context_length: 8192, max_output_tokens: 2048 }] })[0].context_length, 8192)
assert.throws(() => parseModels({ data: [{ id: 'model' }] }))
assert.throws(() => parseModels({ data: [{ id: 'model', capabilities: ['chat'], context_length: '8192' }] }))

async function* chunks() {
  yield ': ping\r\n'
  yield 'event: chunk\r\ndata: {"choices":[{"delta":{"content":"Hel'
  yield 'lo"},"finish_reason":null}]}\r'
  yield '\n\r\ndata: {"choices":[],"usage":{"completion_tokens":2}}\n\n'
  yield 'data: [DONE]\n\n'
}
const events = []
for await (const data of readSseData(chunks())) events.push(data)
assert.equal(events.length, 3)
assert.equal(parseCompletionDelta(events[0]).text, 'Hello')
assert.equal(parseCompletionDelta(events[1]).outputTokens, 2)
assert.equal(events[2], '[DONE]')
assert.throws(() => parseCompletionDelta('{bad json'))
assert.throws(() => parseCompletionDelta('{"error":{"message":"model unavailable"}}'), /model unavailable/)
assert.throws(() => parseCompletionDelta('{"choices":[{"delta":{"tool_calls":[{}]}}]}'), /tool call/)
console.log('Playground checks passed: proxy scope, origins, metadata, public URLs, streaming boundaries, and error handling.')
