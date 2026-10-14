import { useEffect, useRef, useState } from 'react'
import type { AuthenticationSession } from './authentication'
import { ownerRequest } from './managementApi'
import { parseSkillList, skillsChangedEvent } from './skills'

// How many skills the assistant drafted that wait for the owner. It checks on load, on navigation and focus, and each minute while the tab is open.
export function useSkillsToReview(session: AuthenticationSession | null | undefined, refresh: () => Promise<void>): number {
  const [count, setCount] = useState(0)
  const auth = useRef({ session, refresh })
  useEffect(() => { auth.current = { session, refresh } })
  const eligible = !!session && session.isOwner && session.authenticated && !!session.csrfToken
  useEffect(() => {
    if (!eligible) return
    let controller: AbortController | undefined
    const check = () => {
      if (document.visibilityState === 'hidden' || !auth.current.session) return
      controller?.abort()
      const own = controller = new AbortController()
      ownerRequest(auth.current.session, auth.current.refresh, '/api/assistant/skills', 'GET', undefined, own.signal)
        .then(response => response.json())
        .then(value => { if (!own.signal.aborted) setCount(parseSkillList(value).needsReview) })
        .catch(() => { /* The badge simply stays as it was. */ })
    }
    const changed = (event: Event) => {
      const detail = (event as CustomEvent<unknown>).detail
      if (typeof detail === 'number') setCount(detail)
      else check()
    }
    check()
    const timer = window.setInterval(check, 60_000)
    window.addEventListener('hashchange', check)
    window.addEventListener('focus', check)
    window.addEventListener(skillsChangedEvent, changed)
    return () => {
      controller?.abort()
      window.clearInterval(timer)
      window.removeEventListener('hashchange', check)
      window.removeEventListener('focus', check)
      window.removeEventListener(skillsChangedEvent, changed)
    }
  }, [eligible])
  return eligible ? count : 0
}
