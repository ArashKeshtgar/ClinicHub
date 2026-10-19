import { createContext, useContext, useEffect, useState } from 'react'
import { NavLink, Navigate, Route, Routes, useNavigate } from 'react-router'
import { api, ApiError, can, results, type Me, type Settings } from './api'
import { Account, ClinicPicker, PendingAction } from './pages/AccountPages'
import Audit, { Clinics } from './pages/Audit'
import Billing from './pages/Billing'
import EncounterPage from './pages/EncounterPage'
import FrontDesk from './pages/FrontDesk'
import Patients from './pages/Patients'
import Results from './pages/Results'
import Users from './pages/Users'
import Visits from './pages/Visits'

type Session = { me: Me; settings: Settings; setMe: (me: Me) => void }
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

  const signOut = async () => { await api.logout(); setMe(null) }

  // Sent here by an application signing in through ClinicHub (/connect/authorize): once signed in, past any
  // pending step and with a clinic chosen, go back so it can finish the sign-in.
  const returnUrl = safeReturnUrl()
  const ready = !!me && !me.pendingAction && !!me.clinic
  useEffect(() => { if (ready && returnUrl) window.location.assign(returnUrl) }, [ready, returnUrl])

  if (!settings || me === undefined) return <p className="muted page">Loading…</p>
  if (ready && returnUrl) return <p className="muted page">Returning to {appName(returnUrl)}…</p>
  if (!me) return <Login settings={settings} onSignedIn={setMe} returnTo={returnUrl ? appName(returnUrl) : null} />
  if (me.pendingAction) return <PendingAction me={me} onDone={setMe} onSignOut={signOut} />
  if (!me.clinic) return <ClinicPicker me={me} onChosen={setMe} onSignOut={signOut} />

  return (
    <SessionContext.Provider value={{ me, settings, setMe }}>
      {/* A new key per clinic: every page reloads its data from the newly chosen clinic's database. */}
      <Shell key={me.clinic.id} me={me} settings={settings} setMe={setMe} signOut={signOut} />
    </SessionContext.Provider>
  )
}

function Shell({ me, settings, setMe, signOut }: { me: Me; settings: Settings; setMe: (me: Me) => void; signOut: () => void }) {
  const navigate = useNavigate()
  const clinic = me.clinic!
  const [apps, setApps] = useState<{ clientId: string; name: string; url?: string }[]>([])
  useEffect(() => { api.apps().then(setApps).catch(() => setApps([])) }, [])
  // Results waiting for this clinician: counted in the top bar, refreshed every minute and after acknowledging.
  const [waiting, setWaiting] = useState(0)
  useEffect(() => {
    if (!can(me, 'results.review')) return
    const count = () => results.inbox().then(r => setWaiting(r.length)).catch(() => {})
    count()
    const t = setInterval(count, 60_000)
    window.addEventListener('clinichub:results-changed', count)
    return () => { clearInterval(t); window.removeEventListener('clinichub:results-changed', count) }
  }, [me])
  return (
    <>
      <header className="topbar">
        <div className="brand"><Logo /> ClinicHub</div>
        {me.clinics.length > 1 ? (
          <select className="clinic-switch" value={clinic.id} aria-label="Clinic" title="Switch clinic"
            onChange={async e => { setMe(await api.selectClinic(Number(e.target.value))); navigate('/') }}>
            {me.clinics.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        ) : <span className="clinic-name">{clinic.name}</span>}
        <nav>
          {can(me, 'schedule.view') && <NavLink to="/frontdesk">Front desk</NavLink>}
          {can(me, 'patients.view') && <NavLink to="/patients">Patients</NavLink>}
          {can(me, 'encounters.view') && <NavLink to="/visits">Visits</NavLink>}
          {can(me, 'results.review') && <NavLink to="/results">Results{waiting > 0 && <span className="count">{waiting}</span>}</NavLink>}
          {can(me, 'billing.view') && <NavLink to="/billing">Billing</NavLink>}
          {can(me, 'users.manage') && <NavLink to="/people">People</NavLink>}
          {can(me, 'audit.view') && <NavLink to="/logs">Logs</NavLink>}
          {me.isPlatformAdmin && <NavLink to="/clinics">Clinics</NavLink>}
          {/* Other applications sign in through ClinicHub: opening one needs no second password. */}
          {apps.filter(a => a.url).map(a => <a key={a.clientId} href={a.url} className="app-link" title={`Open ${a.name} (signs in with ClinicHub)`}>{a.name} ↗</a>)}
        </nav>
        <div className="who">
          <NavLink to="/account" className="account-link" title="My account">
            <span className="name">{me.displayName}</span> <span className="muted">· {me.roles.join(', ')}</span>
          </NavLink>
          <button className="quiet" onClick={signOut}>Sign out</button>
        </div>
      </header>
      {settings.demo && <div className="demo-strip">Demo clinics with synthetic patients. Do not enter real patient information.</div>}
      <main className="page">
        <Routes>
          {/* Clinicians who sign start on their own worklist; billing clerks on billing; everyone else on the front desk board. */}
          <Route path="/" element={<Navigate replace to={
            can(me, 'encounters.sign') && me.providerId ? '/visits'
              : can(me, 'billing.manage') && !can(me, 'schedule.manage') ? '/billing'
              : can(me, 'schedule.view') ? '/frontdesk'
              : can(me, 'audit.view') ? '/logs'
              : '/account'} />} />
          <Route path="/frontdesk" element={<FrontDesk />} />
          <Route path="/patients" element={<Patients />} />
          <Route path="/visits" element={<Visits />} />
          <Route path="/visits/:id" element={<EncounterPage />} />
          <Route path="/billing" element={<Billing />} />
          <Route path="/results" element={<Results />} />
          <Route path="/people" element={<Users />} />
          <Route path="/logs" element={<Audit />} />
          <Route path="/clinics" element={<Clinics />} />
          <Route path="/account" element={<Account settings={settings} />} />
          <Route path="*" element={<Navigate to="/" />} />
        </Routes>
      </main>
    </>
  )
}

const DEMO: Record<string, [string, string]> = {
  frontdesk: ['Front desk', 'Harbourview: booking, check-in with health card validation, patient flow'],
  nurse: ['Nurse', 'Harbourview: rooming and vitals'],
  'dr.patel': ['Physician at two clinics', 'Dr. Nadia Patel works at Harbourview and Lakeshore: choose a clinic after signing in'],
  billing: ['Billing clerk', 'Harbourview: OHIP claims, remittance, invoices'],
  privacy: ['Privacy officer', 'Harbourview: the PHIPA access log and sign-in log'],
  admin: ['Administrator', 'Both clinics, people and roles, and opening new clinics'],
  pharmacist: ['Pharmacist', 'Harbourview: the pharmacy (PharmaFlow): reception, dispensing, stock'],
}

/** Only ClinicHub's own sign-in endpoint: anything else in returnUrl would make this page an open redirect. */
function safeReturnUrl() {
  const url = new URLSearchParams(window.location.search).get('returnUrl')
  return url && url.startsWith('/connect/authorize?') ? url : null
}

const APP_NAMES: Record<string, string> = { clinicreport: 'ClinicReport', labflow: 'LabFlow', analytics: 'Analytics' }
function appName(returnUrl: string) {
  const id = new URLSearchParams(returnUrl.slice(returnUrl.indexOf('?'))).get('client_id') ?? ''
  return APP_NAMES[id] ?? id
}

function Login({ settings, onSignedIn, returnTo }: { settings: Settings; onSignedIn: (m: Me) => void; returnTo: string | null }) {
  const [userName, setUserName] = useState('')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [needCode, setNeedCode] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const run = async (f: () => Promise<Me>) => {
    setBusy(true)
    setError(null)
    try {
      onSignedIn(await f())
    } catch (e) {
      if (e instanceof ApiError && e.data.twoFactorRequired) {
        // The first answer for a two-factor account asks for the code without showing an error.
        if (needCode) setError(e.message)
        setNeedCode(true)
        return
      }
      setError(e instanceof ApiError ? e.message : 'Could not sign in.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="login">
      <div className="card login-card">
        <div className="brand"><Logo size={38} /> ClinicHub</div>
        {returnTo
          ? <p className="result ok" style={{ margin: 0 }}>Sign in to continue to {returnTo}.</p>
          : <p className="muted" style={{ margin: 0 }}>One sign-in for every clinic you work at. Each clinic's records are kept in its own database.</p>}
        {settings.demo && (
          <>
            <h2>Sign in to the demo as…</h2>
            <div className="role-picks">
              {settings.demoUsers.map(u => (
                <button key={u} className="role-pick" disabled={busy} onClick={() => run(() => api.demoLogin(u))}>
                  {DEMO[u]?.[0] ?? u}<span>{DEMO[u]?.[1]}</span>
                </button>
              ))}
            </div>
          </>
        )}
        <form className="login-form" style={{ display: 'grid', gap: '.75rem' }}
          onSubmit={e => { e.preventDefault(); run(() => api.login(userName, password, needCode ? code : undefined)) }}>
          <label>User name<input value={userName} onChange={e => setUserName(e.target.value)} autoComplete="username" required /></label>
          <label>Password<input type="password" value={password} onChange={e => setPassword(e.target.value)} autoComplete="current-password" required /></label>
          {needCode && (
            <label>Authenticator code
              <input inputMode="numeric" autoComplete="one-time-code" maxLength={7} value={code} onChange={e => setCode(e.target.value)} autoFocus required placeholder="123 456" />
              <span className="muted small" style={{ fontWeight: 400 }}>The 6-digit code from your authenticator app.</span>
            </label>
          )}
          {error && <p className="error" role="alert">{error}</p>}
          <button className="primary" disabled={busy}>Sign in</button>
        </form>
      </div>
    </div>
  )
}
