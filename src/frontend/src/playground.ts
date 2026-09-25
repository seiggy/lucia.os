export interface HostedModel {
  id: string
  capabilities: string[]
  backend?: string
  context_length?: number
  native_context_length?: number
  max_output_tokens?: number
  embedding_dimensions?: number
}

export interface CompletionDelta {
  text: string
  finishReason?: string
  inputTokens?: number
  outputTokens?: number
}

export const playgroundRoutes = [
  { path: '/api/playground/models', method: 'GET', target: '/v1/models' },
  { path: '/api/playground/chat', method: 'POST', target: '/v1/chat/completions' },
] as const

export function checkPlaygroundRequest(
  path: string, method: string, origin: string | undefined, host: string | undefined,
  contentType: string | undefined, marker: string | undefined,
): { status: number; message: string } | null {
  const route = playgroundRoutes.find(item => item.path === path.split('?')[0])
  if (!route) return { status: 404, message: 'This development bridge exposes model details and chat only.' }
  if (method !== route.method) return { status: 405, message: 'This method is not allowed by the development bridge.' }
  if (origin) {
    try {
      const source = new URL(origin)
      if (!host || !['http:', 'https:'].includes(source.protocol) || source.host !== new URL(`${source.protocol}//${host}`).host)
        return { status: 403, message: 'Cross-origin playground requests are not allowed.' }
    } catch {
      return { status: 403, message: 'Invalid playground origin.' }
    }
  }
  if (method === 'POST' && (contentType?.split(';')[0].trim().toLowerCase() !== 'application/json' || marker !== '1'))
    return { status: 400, message: 'Playground chat requires a JSON request from the development UI.' }
  return null
}

function record(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === 'object' && !Array.isArray(value)
}

function tokenCount(value: unknown): number | undefined {
  if (value === undefined || value === null) return undefined
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0) throw new Error('The host returned an invalid token count.')
  return value
}

export function parseModels(value: unknown): HostedModel[] {
  if (!record(value) || !Array.isArray(value.data)) throw new Error('The host did not return a model list.')
  return value.data.map(item => {
    if (!record(item) || typeof item.id !== 'string' || !Array.isArray(item.capabilities) || !item.capabilities.every(capability => typeof capability === 'string'))
      throw new Error('The host returned incomplete model metadata. Refresh after updating the host.')
    return {
      id: item.id,
      capabilities: item.capabilities,
      backend: typeof item.backend === 'string' ? item.backend : undefined,
      context_length: tokenCount(item.context_length),
      native_context_length: tokenCount(item.native_context_length),
      max_output_tokens: tokenCount(item.max_output_tokens),
      embedding_dimensions: tokenCount(item.embedding_dimensions),
    }
  })
}

export function externalApiBase(configured: string | undefined, frontendUrl: string): string | null {
  if (!configured) return null
  const url = new URL(configured)
  if (url.protocol !== 'http:' && url.protocol !== 'https:') throw new Error('The advertised API address must use HTTP or HTTPS.')
  if (['localhost', '127.0.0.1', '0.0.0.0', '[::1]', '[::]'].includes(url.hostname))
    url.hostname = new URL(frontendUrl).hostname
  url.pathname = '/v1'
  url.search = ''
  url.hash = ''
  return url.toString().replace(/\/$/, '')
}

export async function* readSseData(chunks: AsyncIterable<string>): AsyncGenerator<string> {
  let buffer = ''
  let data: string[] = []
  let dataSize = 0
  const consume = (line: string): string | undefined => {
    if (line.endsWith('\r')) line = line.slice(0, -1)
    if (line === '') {
      if (data.length === 0) return undefined
      const event = data.join('\n')
      data = []
      dataSize = 0
      return event
    }
    if (line.startsWith('data:')) {
      dataSize += line.length
      if (dataSize > 1024 * 1024) throw new Error('The host sent an unexpectedly large streaming event.')
      data.push(line.slice(5).replace(/^ /, ''))
    }
    return undefined
  }
  for await (const chunk of chunks) {
    buffer += chunk
    if (buffer.length > 1024 * 1024) throw new Error('The host sent an unexpectedly large streaming event.')
    let newline
    while ((newline = buffer.indexOf('\n')) >= 0) {
      const event = consume(buffer.slice(0, newline))
      buffer = buffer.slice(newline + 1)
      if (event !== undefined) yield event
    }
  }
  if (buffer) consume(buffer)
  if (data.length > 0) yield data.join('\n')
}

export function parseCompletionDelta(data: string): CompletionDelta {
  let value: unknown
  try { value = JSON.parse(data) } catch { throw new Error('The host sent malformed streaming JSON.') }
  if (!record(value)) throw new Error('The host returned an invalid streaming event.')
  if (record(value.error)) throw new Error(typeof value.error.message === 'string' ? value.error.message : 'The model reported a generation error.')
  if (!Array.isArray(value.choices)) throw new Error('The host returned a streaming event without choices.')
  const choice = value.choices[0]
  if (choice !== undefined && !record(choice)) throw new Error('The host returned an invalid completion choice.')
  const delta = record(choice) && record(choice.delta) ? choice.delta : {}
  if (Array.isArray(delta.tool_calls) && delta.tool_calls.length > 0)
    throw new Error('The model requested a tool call. This temporary playground supports text responses only.')
  if (delta.content !== undefined && delta.content !== null && typeof delta.content !== 'string')
    throw new Error('The model returned non-text content.')
  const usage = record(value.usage) ? value.usage : {}
  return {
    text: typeof delta.content === 'string' ? delta.content : '',
    finishReason: record(choice) && typeof choice.finish_reason === 'string' ? choice.finish_reason : undefined,
    inputTokens: tokenCount(usage.prompt_tokens),
    outputTokens: tokenCount(usage.completion_tokens),
  }
}
