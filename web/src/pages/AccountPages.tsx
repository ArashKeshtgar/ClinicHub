import { useEffect, useState, type FormEvent } from 'react'
import QRCode from 'qrcode'
import { api, ApiError, firstError, type Me, type Settings } from '../api'
import { Logo, useSession } from '../App'

/** Scan a QR code with an authenticator app, then confirm with one code. */
export function TwoFactorSetup({ onEnabled }: { onEnabled: (me: Me) => void }) {
  const [setup, setSetup] = useState<{ secret: string; qr: string } | null>(null)
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.twoFactorSetup()
      .then(async s => setSetup({ secret: s.secret, qr: await QRCode.toDataURL(s.otpAuthUri, { margin: 1, width: 200 }) }))
      .catch(e => setError(e instanceof ApiError ? e.message : 'Could not start the setup.'))
  }, [])

  const confirm = async (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    try {
      onEnabled(await api.twoFactorEnable(code))
    } catch (err) {
      setError(firstError(err, 'Not enabled.'))
    }
  }

  if (!setup) return error ? <p className="error">{error}</p> : <p className="muted">Preparing…</p>
  return (
    <form className="two-factor" onSubmit={confirm}>
      <ol>
        <li>Open an authenticator app on your phone (Microsoft or Google Authenticator, 1Password, Authy…).</li>
        <li>
          Scan this code, or type the key by hand.
          <div className="qr-row">
            <img src={setup.qr} alt="QR code for the authenticator app" width={200} height={200} />
            <code className="mono">{setup.secret.match(/.{1,4}/g)!.join(' ')}</code>
          </div>
        </li>
        <li>
          Enter the 6-digit code the app shows.
          <div className="code-row">
            <input inputMode="numeric" autoComplete="one-time-code" maxLength={7} value={code} onChange={e => setCode(e.target.value)}
              placeholder="123 456" aria-label="Code from the app" autoFocus required />
            <button className="primary">Turn on</button>
          </div>
        </li>
      </ol>
      {error && <p className="error">{error}</p>}
    </form>
  )
}

/** Before anything else: a person with a temporary password chooses their own, or sets up a required authenticator. */
export function PendingAction({ me, onDone, onSignOut }: { me: Me; onDone: (me: Me) => void; onSignOut: () => void }) {
  return (
    <div className="login">
      <div className="card login-card">
        <div className="brand"><Logo size={38} /> ClinicHub</div>
        <p className="muted" style={{ margin: 0 }}>Signed in as <strong>{me.displayName}</strong> ({me.userName})</p>
        {me.pendingAction === 'change-password'
          ? <ChoosePassword onDone={onDone} />
          : (
            <section style={{ display: 'grid', gap: '.6rem' }}>
              <h2>Set up two-factor sign-in</h2>
              <p className="muted small" style={{ margin: 0 }}>One of your roles requires a code from your phone each time you sign in, at every clinic.</p>
              <TwoFactorSetup onEnabled={onDone} />
            </section>
          )}
        <button className="quiet" onClick={onSignOut}>Sign out</button>
      </div>
    </div>
  )
}

function ChoosePassword({ onDone }: { onDone: (me: Me) => void }) {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [again, setAgain] = useState('')
  const [error, setError] = useState<string | null>(null)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (next !== again) return setError('The new passwords differ.')
    try {
      onDone(await api.changePassword(current, next))
    } catch (err) {
      setError(firstError(err, 'Not changed.'))
    }
  }

  return (
    <form style={{ display: 'grid', gap: '.75rem' }} onSubmit={submit}>
      <h2>Choose your own password</h2>
      <p className="muted small" style={{ margin: 0 }}>Your administrator set a temporary password. Replace it with one only you know: at least 10 characters, not containing your user name.</p>
      <label>Temporary password<input type="password" value={current} onChange={e => setCurrent(e.target.value)} autoComplete="current-password" required /></label>
      <label>New password<input type="password" value={next} onChange={e => setNext(e.target.value)} autoComplete="new-password" minLength={10} required /></label>
      <label>New password again<input type="password" value={again} onChange={e => setAgain(e.target.value)} autoComplete="new-password" minLength={10} required /></label>
      {error && <p className="error">{error}</p>}
      <button className="primary">Save and continue</button>
    </form>
  )
}

/** After signing in, someone who works at several clinics picks where they are today. Each clinic is its own database. */
export function ClinicPicker({ me, onChosen, onSignOut }: { me: Me; onChosen: (me: Me) => void; onSignOut: () => void }) {
  const [error, setError] = useState<string | null>(null)
  const choose = async (id: number) => {
    try { onChosen(await api.selectClinic(id)) } catch (e) { setError(firstError(e, 'Could not open that clinic.')) }
  }
  return (
    <div className="login">
      <div className="card login-card">
        <div className="brand"><Logo size={38} /> ClinicHub</div>
        <h2>Where are you working, {me.displayName}?</h2>
        {me.clinics.length === 0
          ? <p className="muted">You aren't a member of any clinic yet. Ask a clinic administrator to add you.</p>
          : (
            <div className="role-picks">
              {me.clinics.map(c => (
                <button key={c.id} className="role-pick" onClick={() => choose(c.id)}>
                  {c.name}<span className="mono">{c.slug}</span>
                </button>
              ))}
            </div>
          )}
        {error && <p className="error">{error}</p>}
        <button className="quiet" onClick={onSignOut}>Sign out</button>
      </div>
    </div>
  )
}

type Msg = { ok: boolean; text: string } | null

/** The signed-in person's own account: shared by every clinic they work at. */
export function Account({ settings }: { settings: Settings }) {
  const { me, setMe } = useSession()
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [msg, setMsg] = useState<Msg>(null)
  const [settingUp, setSettingUp] = useState(false)
  const [twoMsg, setTwoMsg] = useState<Msg>(null)
  const [sessionMsg, setSessionMsg] = useState<Msg>(null)

  const changePassword = async (e: FormEvent) => {
    e.preventDefault()
    try {
      setMe(await api.changePassword(current, next))
      setCurrent('')
      setNext('')
      setMsg({ ok: true, text: 'Password changed. Your other sessions were signed out.' })
    } catch (err) {
      setMsg({ ok: false, text: firstError(err, 'Not changed.') })
    }
  }

  const disableTwoFactor = async () => {
    const password = window.prompt('Enter your password to turn two-factor sign-in off:')
    if (!password) return
    try {
      setMe(await api.twoFactorDisable(password))
      setTwoMsg({ ok: true, text: 'Two-factor sign-in is off.' })
    } catch (err) {
      setTwoMsg({ ok: false, text: firstError(err, 'Not changed.') })
    }
  }

  return (
    <>
      <div className="board-head"><h1>My account</h1></div>
      {settings.accountsLocked && <p className="result warn">Public demo: passwords and two-factor can't be changed here.</p>}
      <div className="account-grid">
        <section className="card pad account-card">
          <div><strong>{me.displayName}</strong>{me.credentials && `, ${me.credentials}`}</div>
          <div className="muted mono small">{me.userName}</div>
          <div className="small">One account for every clinic you work at:</div>
          <ul className="small" style={{ margin: 0, paddingLeft: '1.1rem' }}>
            {me.clinics.map(c => <li key={c.id}>{c.name}{c.id === me.clinic?.id && <strong> · here now ({me.roles.join(', ')})</strong>}</li>)}
          </ul>
        </section>

        <section className="card pad account-card">
          <h2>Two-factor sign-in</h2>
          {me.twoFactorEnabled ? (
            <>
              <p style={{ margin: 0 }}><span className="badge ohip">On</span> A code from your authenticator app is needed at every sign-in.</p>
              {me.twoFactorRequired
                ? <p className="muted small" style={{ margin: 0 }}>A role you hold requires it, so it can't be turned off. If you lose your phone, ask an administrator to reset it.</p>
                : <div><button onClick={disableTwoFactor}>Turn off</button></div>}
            </>
          ) : settingUp ? (
            <TwoFactorSetup onEnabled={m => { setMe(m); setSettingUp(false); setTwoMsg({ ok: true, text: 'Two-factor sign-in is on.' }) }} />
          ) : (
            <>
              <p className="muted" style={{ margin: 0 }}>Protect your account with a code from your phone in addition to your password.</p>
              <div><button className="primary" onClick={() => setSettingUp(true)}>Set up</button></div>
            </>
          )}
          {twoMsg && <p className={`result ${twoMsg.ok ? 'ok' : 'warn'}`}>{twoMsg.text}</p>}
        </section>

        <form onSubmit={changePassword} className="card pad account-card">
          <h2>Change password</h2>
          <label>Current password<input type="password" value={current} onChange={e => setCurrent(e.target.value)} autoComplete="current-password" required /></label>
          <label>New password<input type="password" value={next} onChange={e => setNext(e.target.value)} autoComplete="new-password" minLength={10} required /></label>
          {msg && <p className={`result ${msg.ok ? 'ok' : 'warn'}`}>{msg.text}</p>}
          <div><button className="primary">Change password</button></div>
        </form>

        <section className="card pad account-card">
          <h2>Sessions</h2>
          <p className="muted" style={{ margin: 0 }}>Signed in on a shared or lost computer? End every session except this one.</p>
          <div><button onClick={async () => {
            try { await api.revokeOtherSessions(); setSessionMsg({ ok: true, text: 'Signed out on every other browser and device.' }) }
            catch (err) { setSessionMsg({ ok: false, text: firstError(err, 'Failed.') }) }
          }}>Sign out everywhere else</button></div>
          {sessionMsg && <p className={`result ${sessionMsg.ok ? 'ok' : 'warn'}`}>{sessionMsg.text}</p>}
        </section>
      </div>
    </>
  )
}
