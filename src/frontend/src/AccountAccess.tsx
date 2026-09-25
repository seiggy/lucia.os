import { Icon } from './Icon'
import { signInAddress } from './authentication'
import type { AuthenticationSession } from './authentication'
import './AccountAccess.css'

export function AuthenticationPanel({ loading, error, session, retry }: {
  loading: boolean
  error: string | null
  session: AuthenticationSession | null
  retry: () => void
}) {
  const denied = session?.authenticated && !session.canAccess
  return <div className="authentication-shell">
    <a className="brand" href="#/" aria-label="Lucia Home">Lucia</a>
    <main className="authentication-panel" aria-busy={loading}>
      <span className="icon-tile tone-accent"><Icon name={denied ? 'attention' : 'home'} /></span>
      <h1>{loading ? 'Connecting to your home.' : error ? 'We cannot check your sign-in.' : denied ? 'Your account needs access.' : 'Welcome to Lucia.'}</h1>
      <p>{loading ? 'Checking your secure connection and account.'
        : error ? 'Your session could not be verified. No private data or controls have been opened.'
          : denied ? 'You are signed in, but your account has not been granted access to Lucia. Ask your lab owner to add you to the Lucia access group.'
            : 'Sign in with your home account to open Lucia and use your local AI.'}</p>
      {error && <p className="authentication-error" role="alert">{error}</p>}
      {!loading && (error ? <button className="button primary" onClick={retry}>Try again <Icon name="refresh" /></button>
        : denied ? <AccountActions session={session!} />
          : <a className="button primary" href={signInAddress(location.hash)}>Sign in with Authentik <Icon name="arrow" /></a>)}
      <p className="authentication-note">Your sign-in is handled by your own Authentik service. Lucia never asks you to paste an API key into the dashboard.</p>
    </main>
  </div>
}

export function AccountActions({ session }: { session: AuthenticationSession }) {
  if (!session.enabled || !session.authenticated) return null
  return <div className="account-actions">
    <span title={session.username ?? undefined}>{session.displayName || session.username}<small>{session.isOwner ? 'Owner' : 'Member'}</small></span>
    <form method="post" action="/auth/logout">
      <input type="hidden" name="__RequestVerificationToken" value={session.csrfToken ?? ''} />
      <button type="submit" className="text-link">Sign out</button>
    </form>
  </div>
}
