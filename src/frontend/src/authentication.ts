export interface AuthenticationSession {
  enabled: boolean
  authenticated: boolean
  username: string | null
  displayName: string | null
  isOwner: boolean
  canAccess: boolean
  csrfToken: string | null
}

export function parseAuthenticationSession(value: unknown): AuthenticationSession {
  if (!value || typeof value !== 'object') throw new Error('Lucia returned an invalid sign-in status.')
  const data = value as Record<string, unknown>
  for (const name of ['enabled', 'authenticated', 'isOwner', 'canAccess']) {
    if (typeof data[name] !== 'boolean') throw new Error('Lucia returned an incomplete sign-in status.')
  }
  for (const name of ['username', 'displayName', 'csrfToken']) {
    if (data[name] !== null && typeof data[name] !== 'string') throw new Error('Lucia returned an incomplete sign-in status.')
  }
  if (data.enabled && data.authenticated && (!data.username || !data.csrfToken))
    throw new Error('Your sign-in session is incomplete. Sign in again.')
  return {
    enabled: data.enabled as boolean, authenticated: data.authenticated as boolean,
    username: data.username as string | null, displayName: data.displayName as string | null,
    isOwner: data.isOwner as boolean, canAccess: data.canAccess as boolean,
    csrfToken: data.csrfToken as string | null,
  }
}

export function signInAddress(hash: string): string {
  const returnUrl = hash.startsWith('#/') ? '/' + hash : '/'
  return '/auth/login?returnUrl=' + encodeURIComponent(returnUrl)
}

export function inferenceConnection(session: AuthenticationSession): { models: string; chat: string; headers: Record<string, string> } {
  if (!session.enabled)
    return { models: '/api/playground/models', chat: '/api/playground/chat', headers: { 'X-Lucia-Playground': '1' } }
  if (!session.authenticated || !session.canAccess || !session.csrfToken)
    throw new Error('Sign in with an authorized Lucia account before using local AI.')
  return { models: '/v1/models', chat: '/v1/chat/completions', headers: { 'X-CSRF-TOKEN': session.csrfToken } }
}
