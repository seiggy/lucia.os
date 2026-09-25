import type { AuthenticationSession } from './authentication'

export async function ownerRequest(session: AuthenticationSession, refreshSession: () => Promise<void>,
  path: string, method = 'GET', body?: unknown, signal?: AbortSignal): Promise<Response> {
  if (!session.isOwner || !session.authenticated || !session.csrfToken)
    throw new Error('Sign in as a Lucia owner to manage this setting.')
  if (!path.startsWith('/api/host/')) throw new Error('Management requests must use the local host API.')
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
  let message = `Lucia could not complete this request (HTTP ${response.status}).`
  try {
    const value: unknown = await response.json()
    if (value && typeof value === 'object' && 'error' in value) {
      const error = value.error
      if (typeof error === 'string') message = error
      else if (error && typeof error === 'object' && 'message' in error && typeof error.message === 'string')
        message = error.message
    }
  } catch { /* A non-JSON upstream failure retains its HTTP status. */ }
  throw new Error(message)
}
