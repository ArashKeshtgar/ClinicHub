import { useEffect, useState } from 'react'
import { admin, consent, CONSENT_SCOPES, firstError, localDateTime, type AccessRow, type AuditRow, type ClinicInfo, type OverrideRow } from '../api'
import { useSession } from '../App'

const TYPES: Record<string, string> = {
  LoginSucceeded: 'Signed in', LoginFailed: 'Sign-in failed', LockedOut: 'Account locked', Logout: 'Signed out',
  ClinicSelected: 'Entered clinic', PasswordChanged: 'Changed password', PasswordReset: 'Password reset',
  TwoFactorEnabled: 'Two-factor on', TwoFactorDisabled: 'Two-factor off', TwoFactorReset: 'Two-factor reset',
  SessionsRevoked: 'Sessions ended', UserCreated: 'Account created', UserUpdated: 'Roles changed', UserUnlocked: 'Account unlocked',
  MemberAdded: 'Joined clinic', MemberRemoved: 'Left clinic', RoleCreated: 'Role created', RoleUpdated: 'Role changed',
  RoleDeleted: 'Role deleted', ClinicCreated: 'Clinic opened',
}
const WARN = new Set(['LoginFailed', 'LockedOut', 'PasswordReset', 'TwoFactorReset', 'TwoFactorDisabled', 'MemberRemoved'])

/** Two trails: sign-ins and account changes (catalog), and who opened which patient's record (this clinic's database). */
export default function Audit() {
  const { me } = useSession()
  const [tab, setTab] = useState<'access' | 'overrides' | 'security'>('access')
  return (
    <>
      <div className="board-head">
        <div>
          <h1>Logs</h1>
          <div className="muted">{me.clinic?.name}. Entries can't be edited or deleted.</div>
        </div>
      </div>
      <div className="tabs" role="tablist">
        <button role="tab" aria-selected={tab === 'access'} className={`tab ${tab === 'access' ? 'on' : ''}`} onClick={() => setTab('access')}>Patient record access (PHIPA)</button>
        <button role="tab" aria-selected={tab === 'overrides'} className={`tab ${tab === 'overrides' ? 'on' : ''}`} onClick={() => setTab('overrides')}>Consent overrides</button>
        <button role="tab" aria-selected={tab === 'security'} className={`tab ${tab === 'security' ? 'on' : ''}`} onClick={() => setTab('security')}>Sign-ins and accounts</button>
      </div>
      {tab === 'access' ? <AccessLog /> : tab === 'overrides' ? <Overrides /> : <SecurityLog />}
    </>
  )
}

function AccessLog() {
  const [rows, setRows] = useState<AccessRow[]>([])
  const [more, setMore] = useState(false)
  useEffect(() => { admin.access().then(r => { setRows(r); setMore(r.length === 200) }) }, [])
  return (
    <section className="card">
      <p className="muted small pad" style={{ margin: 0 }}>Every opening, check-in, signature or claim view of a patient's record, kept in this clinic's own database, so a patient can be told who has looked at their record.</p>
      <div className="table-wrap">
        <table className="data">
          <thead><tr><th>When</th><th>Who</th><th>Patient</th><th>What</th><th>IP</th></tr></thead>
          <tbody>
            {rows.map(r => (
              <tr key={r.id}>
                <td className="muted small">{localDateTime(r.atUtc)}</td>
                <td>{r.user}</td>
                <td>{r.patient} <span className="muted small mono">{r.mrn}</span></td>
                <td><span className="badge">{r.action}</span> {r.override && <span className="badge none" title="Opened under an emergency override of a consent directive">Override</span>} <span className="small">{r.context}</span></td>
                <td className="mono muted small">{r.ipAddress}</td>
              </tr>
            ))}
            {rows.length === 0 && <tr><td colSpan={5} className="muted">Nothing recorded yet.</td></tr>}
          </tbody>
        </table>
      </div>
      {more && <div className="pad"><button className="small" onClick={async () => {
        const next = await admin.access(rows[rows.length - 1].id); setRows([...rows, ...next]); setMore(next.length === 200)
      }}>Older entries</button></div>}
    </section>
  )
}

function SecurityLog() {
  const [rows, setRows] = useState<AuditRow[]>([])
  const [type, setType] = useState('')
  const [more, setMore] = useState(false)
  useEffect(() => { admin.audit({ type: type || undefined }).then(r => { setRows(r); setMore(r.length === 200) }) }, [type])
  return (
    <section className="card">
      <div className="filters">
        <select className="inline-select" style={{ marginLeft: 0 }} value={type} onChange={e => setType(e.target.value)} aria-label="Event type">
          <option value="">All events</option>
          {Object.entries(TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
        </select>
        <span className="muted small">Events in this clinic, and sign-ins and password changes of its people.</span>
      </div>
      <div className="table-wrap">
        <table className="data">
          <thead><tr><th>When</th><th>Event</th><th>By</th><th>About</th><th>Details</th><th>IP</th></tr></thead>
          <tbody>
            {rows.map(r => (
              <tr key={r.id}>
                <td className="muted small">{localDateTime(r.atUtc)}</td>
                <td><span className={`badge ${WARN.has(r.type) ? 'none' : ''}`}>{TYPES[r.type] ?? r.type}</span></td>
                <td>{r.actor ?? <span className="muted">—</span>}</td>
                <td>{r.target}</td>
                <td className="small">{r.detail}</td>
                <td className="mono muted small">{r.ipAddress}</td>
              </tr>
            ))}
            {rows.length === 0 && <tr><td colSpan={6} className="muted">Nothing recorded for this filter.</td></tr>}
          </tbody>
        </table>
      </div>
      {more && <div className="pad"><button className="small" onClick={async () => {
        const next = await admin.audit({ type: type || undefined, before: rows[rows.length - 1].id }); setRows([...rows, ...next]); setMore(next.length === 200)
      }}>Older entries</button></div>}
    </section>
  )
}

/** Platform administrators: every clinic, each with its own database. */
export function Clinics() {
  const { settings } = useSession()
  const [rows, setRows] = useState<ClinicInfo[]>([])
  const [slug, setSlug] = useState('')
  const [name, setName] = useState('')
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const load = () => admin.clinics().then(setRows)
  useEffect(() => { load() }, [])

  return (
    <>
      <div className="board-head">
        <div>
          <h1>Clinics</h1>
          <div className="muted">Each clinic has its own database: its patients, visits and billing never share tables with another clinic's.</div>
        </div>
      </div>
      <div className="admin-layout">
        <section className="card">
          <div className="table-wrap">
            <table className="data">
              <thead><tr><th>Clinic</th><th>Database</th><th className="num">People</th><th>Opened</th></tr></thead>
              <tbody>
                {rows.map(c => (
                  <tr key={c.id}>
                    <td><strong>{c.name}</strong><div className="muted small mono">{c.slug}</div></td>
                    <td className="mono small">{c.databaseName}</td>
                    <td className="num">{c.members}</td>
                    <td className="muted small">{localDateTime(c.createdUtc)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
        <form className="card pad editor" onSubmit={async e => {
          e.preventDefault()
          setBusy(true)
          setMsg(null)
          try {
            const c = await admin.createClinic(slug, name)
            setMsg({ ok: true, text: `${c.name} is open with its own database ${c.databaseName}. You are its administrator: switch to it from the clinic menu to set it up.` })
            setSlug(''); setName('')
            await load()
          } catch (err) {
            setMsg({ ok: false, text: firstError(err, 'Not created.') })
          } finally {
            setBusy(false)
          }
        }}>
          <h2>Open a clinic</h2>
          {settings.accountsLocked && <p className="muted small" style={{ margin: 0 }}>Not available in the public demo.</p>}
          <label>Name<input value={name} onChange={e => setName(e.target.value)} placeholder="Riverside Medical Clinic" required /></label>
          <label>Short name <span className="muted small">lower-case letters, digits, dashes; names the database</span>
            <input value={slug} onChange={e => setSlug(e.target.value.toLowerCase())} placeholder="riverside" pattern="[a-z0-9-]{2,40}" required /></label>
          {msg && <p className={`result ${msg.ok ? 'ok' : 'warn'}`}>{msg.text}</p>}
          <div className="dialog-actions"><button className="primary" disabled={busy || settings.accountsLocked}>{busy ? 'Creating the database…' : 'Open clinic'}</button></div>
        </form>
      </div>
    </>
  )
}

/** Every emergency override of a consent directive, for the privacy officer to follow up. */
function Overrides() {
  const [rows, setRows] = useState<OverrideRow[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { consent.overrides().then(setRows).catch(e => setError(firstError(e, 'Could not load.'))) }, [])
  return (
    <section className="card">
      <p className="muted small pad" style={{ margin: 0 }}>A clinician opened a record the patient had withheld, saying why. Review each one; what they opened is in the access log, marked Override.</p>
      {error && <p className="error pad">{error}</p>}
      <div className="table-wrap">
        <table className="data">
          <thead><tr><th>When</th><th>Who</th><th>Patient</th><th>Directive</th><th>Reason</th></tr></thead>
          <tbody>
            {rows?.map(r => (
              <tr key={r.id}>
                <td className="muted small">{localDateTime(r.atUtc)}<div>until {localDateTime(r.expiresUtc)}</div></td>
                <td>{r.user}</td>
                <td>{r.patient} <span className="muted small mono">{r.mrn}</span></td>
                <td><span className="badge">{CONSENT_SCOPES[r.scope] ?? r.scope}</span></td>
                <td className="small">{r.reason}</td>
              </tr>
            ))}
            {rows?.length === 0 && <tr><td colSpan={5} className="muted">No overrides.</td></tr>}
          </tbody>
        </table>
      </div>
    </section>
  )
}
