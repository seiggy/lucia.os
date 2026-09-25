import { useCallback, useEffect, useRef, useState } from 'react'
import { parseAuthenticationSession } from './authentication'
import type { AuthenticationSession } from './authentication'

export function useAuthentication() {
  const [session, setSession] = useState<AuthenticationSession | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const request = useRef<AbortController | null>(null)

  const refresh = useCallback(async () => {
    request.current?.abort()
    const controller = new AbortController()
    request.current = controller
    setLoading(true)
    setError(null)
    try {
      const response = await fetch('/api/auth/session', { credentials: 'same-origin', cache: 'no-store', signal: controller.signal })
      if (!response.ok) throw new Error(`Lucia could not check sign-in (HTTP ${response.status}).`)
      const status = parseAuthenticationSession(await response.json())
      if (!controller.signal.aborted) setSession(status)
    } catch (failure) {
      if (!controller.signal.aborted) {
        console.error('Could not check Lucia sign-in.', failure)
        setSession(null)
        setError(failure instanceof Error ? failure.message : 'Lucia could not check your sign-in.')
      }
    } finally {
      if (!controller.signal.aborted) setLoading(false)
    }
  }, [])

  useEffect(() => {
    void refresh()
    return () => request.current?.abort()
  }, [refresh])

  return { session, loading, error, refresh }
}
