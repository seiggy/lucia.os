import type { AuthenticationSession } from './authentication'

export async function ownerRequest(session: AuthenticationSession, refreshSession: () => Promise<void>,
  path: string, method = 'GET', body?: unknown, signal?: AbortSignal): Promise<Response> {
  if (!session.isOwner || !session.authenticated || !session.csrfToken)
    throw new Error('Sign in as a Lucia owner to manage this setting.')
  if (!path.startsWith('/api/host/') && !path.startsWith('/api/assistant/')) throw new Error('Management requests must use the local host API.')
  const response = await fetch(path, {
    method, credentials: 'same-origin', cache: 'no-store', signal,
    headers: {
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      ...(method === 'GET' ? {} : { 'X-CSRF-TOKEN': session.csrfToken }),
    },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  })
  if (response.status === 401) void refreshSession()
  if (response.ok) return response
  throw await responseError(response)
}

export async function responseError(response: Response): Promise<Error & { code?: string; certificateSha256?: string }> {
  let message = `Lucia could not complete this request (HTTP ${response.status}).`
  let code: string | undefined
  let certificateSha256: string | undefined
  try {
    const value: unknown = await response.json()
    if (value && typeof value === 'object' && 'error' in value) {
      const error = value.error
      if (typeof error === 'string') message = error
      else if (error && typeof error === 'object') {
        if ('message' in error && typeof error.message === 'string') message = error.message
        if ('code' in error && typeof error.code === 'string') code = error.code
        if ('certificateSha256' in error && typeof error.certificateSha256 === 'string' && /^[a-f\d]{64}$/.test(error.certificateSha256))
          certificateSha256 = error.certificateSha256
      }
    }
  } catch { /* A non-JSON upstream failure retains its HTTP status. */ }
  return Object.assign(new Error(message), { code, certificateSha256 })
}
