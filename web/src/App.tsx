import { createContext, useContext, useEffect, useState } from 'react'
import { NavLink, Navigate, Route, Routes } from 'react-router'
import { api, ApiError, type Me, type Settings } from './api'
import FrontDesk from './pages/FrontDesk'

type Session = { me: Me; settings: Settings }
const SessionContext = createContext<Session | null>(null)
export const useSession = () => useContext(SessionContext)!

export function Logo({ size = 30 }: { size?: number }) {
  return (
    <svg className="brand-mark" viewBox="0 0 32 32" width={size} height={size} aria-hidden="true">
      <rect width="32" height="32" rx="9" fill="#1d5fbf" />
      <path d="M13 7h6v6h6v6h-6v6h-6v-6H7v-6h6z" fill="#fff" />
    </svg>
  )
}

export default function App() {
  const [settings, setSettings] = useState<Settings | null>(null)
  const [me, setMe] = useState<Me | null | undefined>(undefined)

  useEffect(() => {
    api.settings().then(setSettings)
    api.me().then(m => setMe(m ?? null)).catch(() => setMe(null))
    const out = () => setMe(null)
    window.addEventListener('clinichub:signed-out', out)
    return () => window.removeEventListener('clinichub:signed-out', out)
  }, [])

  if (!settings || me === undefined) return <p className="muted page">Loading…</p>
  if (!me) return <Login settings={settings} onSignedIn={setMe} />

  return (
    <SessionContext.Provider value={{ me, settings }}>
      <header className="topbar">
        <div className="brand"><Logo /> ClinicHub</div>
        <nav>
          <NavLink to="/" end>Front desk</NavLink>
        </nav>
        <div className="who">
          <span><span className="name">{me.displayName}</span> <span className="muted">· {me.roles.join(', ')}</span></span>
          <button className="quiet" onClick={async () => { await api.logout(); setMe(null) }}>Sign out</button>
        </div>
      </header>
      {settings.demo && <div className="demo-strip">Demo clinic with synthetic patients. Do not enter real patient information.</div>}
      <main className="page">
        <Routes>
          <Route path="/" element={<FrontDesk />} />
          <Route path="*" element={<Navigate to="/" />} />
        </Routes>
      </main>
    </SessionContext.Provider>
  )
}

const DEMO: Record<string, [string, string]> = {
  frontdesk: ['Front desk', 'Booking, check-in with health card validation, patient flow'],
  nurse: ['Nurse', 'Rooming and vitals'],
  'dr.patel': ['Physician', 'Dr. Nadia Patel, family practice'],
  billing: ['Billing clerk', 'OHIP claims and invoices'],
  privacy: ['Privacy officer', 'Consent directives and the PHIPA access log'],
  admin: ['Administrator', 'Everything'],
}

function Login({ settings, onSignedIn }: { settings: Settings; onSignedIn: (m: Me) => void }) {
  const [userName, setUserName] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)

  const run = async (f: () => Promise<Me>) => {
    setError(null)
    try { onSignedIn(await f()) } catch (e) { setError(e instanceof ApiError ? e.message : 'Could not sign in.') }
  }

  return (
    <div className="login">
      <div className="card login-card">
        <div className="brand"><Logo size={38} /> ClinicHub</div>
        <p className="muted" style={{ margin: 0 }}>Clinic platform for Ontario: scheduling, check-in, visits, OHIP billing.</p>
        {settings.demo && (
          <>
            <h2>Sign in to the demo clinic as…</h2>
            <div className="role-picks">
              {settings.demoUsers.map(u => (
                <button key={u} className="role-pick" onClick={() => run(() => api.demoLogin(u))}>
                  {DEMO[u]?.[0] ?? u}<span>{DEMO[u]?.[1]}</span>
                </button>
              ))}
            </div>
          </>
        )}
        <form className="login-form" style={{ display: 'grid', gap: '.75rem' }} onSubmit={e => { e.preventDefault(); run(() => api.login(userName, password)) }}>
          <label>User name<input value={userName} onChange={e => setUserName(e.target.value)} autoComplete="username" /></label>
          <label>Password<input type="password" value={password} onChange={e => setPassword(e.target.value)} autoComplete="current-password" /></label>
          {error && <p className="error" role="alert">{error}</p>}
          <button className="primary">Sign in</button>
        </form>
      </div>
    </div>
  )
}
