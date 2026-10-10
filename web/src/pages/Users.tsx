import { useEffect, useMemo, useState } from 'react'
import { admin, ApiError, firstError, localDateTime, type PermissionInfo, type RoleInfo, type StaffUser } from '../api'
import { useSession } from '../App'

/** The clinic's people and roles. Roles belong to this clinic; an account can also work at other clinics. */
export default function Users() {
  const { me, settings } = useSession()
  const [tab, setTab] = useState<'users' | 'roles'>('users')
  const [users, setUsers] = useState<StaffUser[]>([])
  const [roles, setRoles] = useState<RoleInfo[]>([])
  const [catalog, setCatalog] = useState<PermissionInfo[]>([])

  const load = () => Promise.all([admin.users().then(setUsers), admin.roles().then(setRoles)])
  useEffect(() => { load(); admin.permissions().then(setCatalog) }, [])

  return (
    <>
      <div className="board-head">
        <div>
          <h1>People &amp; roles</h1>
          <div className="muted">{me.clinic?.name}: who works here and what each role allows here.</div>
        </div>
      </div>
      <div className="tabs" role="tablist">
        <button role="tab" aria-selected={tab === 'users'} className={`tab ${tab === 'users' ? 'on' : ''}`} onClick={() => setTab('users')}>People ({users.length})</button>
        <button role="tab" aria-selected={tab === 'roles'} className={`tab ${tab === 'roles' ? 'on' : ''}`} onClick={() => setTab('roles')}>Roles ({roles.length})</button>
      </div>
      {settings.accountsLocked && <p className="result warn">Public demo: you can look around, but accounts, roles and passwords can't be changed here.</p>}
      {tab === 'users' ? <UsersTab users={users} roles={roles} reload={load} /> : <RolesTab roles={roles} catalog={catalog} reload={load} />}
    </>
  )
}

function UsersTab({ users, roles, reload }: { users: StaffUser[]; roles: RoleInfo[]; reload: () => Promise<unknown> }) {
  const [editing, setEditing] = useState<StaffUser | 'new' | null>(null)
  const [flash, setFlash] = useState<string | null>(null)
  const [q, setQ] = useState('')
  const shown = users.filter(u => !q || `${u.displayName} ${u.userName} ${u.roles.join(' ')}`.toLowerCase().includes(q.toLowerCase()))

  return (
    <div className="admin-layout">
      <section className="card">
        <div className="filters">
          <input type="search" placeholder="Find a person or role" value={q} onChange={e => setQ(e.target.value)} aria-label="Find person" style={{ maxWidth: 320 }} />
          <button className="primary small" style={{ marginLeft: 'auto' }} onClick={() => { setFlash(null); setEditing('new') }}>Add person</button>
        </div>
        <div className="table-wrap">
          <table className="data">
            <thead><tr><th>Name</th><th>Roles here</th><th>Security</th><th>Last sign-in</th></tr></thead>
            <tbody>
              {shown.map(u => (
                <tr key={u.id} className={`clickable ${editing !== 'new' && editing?.id === u.id ? 'selected' : ''}`} onClick={() => { setFlash(null); setEditing(u) }}>
                  <td>
                    <strong>{u.displayName}</strong>{u.credentials && <span className="muted">, {u.credentials}</span>}
                    <div className="muted small mono">{u.userName}</div>
                    {u.otherClinics.length > 0 && <div className="muted small">Also at {u.otherClinics.join(', ')}</div>}
                  </td>
                  <td><div className="badge-row">{u.roles.map(r => <span key={r} className="badge">{r}</span>)}</div></td>
                  <td>
                    <div className="badge-row">
                      {u.isLockedOut && <span className="badge none">Locked</span>}
                      {u.mustChangePassword && <span className="badge none" title="Signs in with a temporary password">Temp password</span>}
                      {u.twoFactorEnabled ? <span className="badge ohip">2FA</span> : u.twoFactorRequired ? <span className="badge none">2FA pending</span> : null}
                    </div>
                  </td>
                  <td className="muted small">{u.lastLoginUtc ? localDateTime(u.lastLoginUtc) : 'Never'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
      {editing && (
        <UserEditor key={editing === 'new' ? 'new' : editing.id} user={editing === 'new' ? null : editing} roles={roles} flash={flash}
          onClose={() => setEditing(null)}
          onSaved={async (u, message) => { await reload(); setFlash(message ?? null); setEditing(u) }} />
      )}
    </div>
  )
}

function UserEditor({ user, roles, flash, onClose, onSaved }: {
  user: StaffUser | null; roles: RoleInfo[]; flash: string | null; onClose: () => void; onSaved: (u: StaffUser | null, message?: string) => Promise<void>
}) {
  const [form, setForm] = useState({
    userName: '', password: '', displayName: user?.displayName ?? '', credentials: user?.credentials ?? '', roleIds: user?.roleIds ?? [] as number[],
  })
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(flash ? { ok: true, text: flash } : null)
  // Someone who also works elsewhere keeps their own name, password and two-factor; this clinic only sets roles here.
  const shared = (user?.otherClinics.length ?? 0) > 0

  const effective = useMemo(() => {
    const perms = new Set<string>()
    roles.filter(r => form.roleIds.includes(r.id)).forEach(r => r.permissions.forEach(p => perms.add(p)))
    return [...perms]
  }, [roles, form.roleIds])

  const save = async () => {
    setErrors({})
    setMsg(null)
    try {
      if (user) {
        if (form.roleIds.length === 0 && !window.confirm(`Remove ${user.displayName} from this clinic? Their account and records stay.`)) return
        const saved = await admin.updateUser(user.id, { displayName: form.displayName, credentials: form.credentials || null, roleIds: form.roleIds })
        await onSaved(saved ?? null, saved ? 'Saved.' : undefined)
      } else {
        const created = await admin.createUser({ userName: form.userName, displayName: form.displayName || undefined, credentials: form.credentials || null,
          roleIds: form.roleIds, password: form.password || undefined })
        await onSaved(created, created.otherClinics.length
          ? `${created.displayName} already had a ClinicHub account (${created.otherClinics.join(', ')}). They were added here and sign in with their own password.`
          : 'Account created. Give them the temporary password; they choose their own at first sign-in.')
      }
    } catch (e) {
      if (e instanceof ApiError && Object.keys(e.fieldErrors).length) setErrors(e.fieldErrors)
      else setMsg({ ok: false, text: firstError(e, 'Not saved.') })
    }
  }

  const act = async (question: string, run: () => Promise<void>, done: string) => {
    if (!window.confirm(question)) return
    try { await run(); setMsg({ ok: true, text: done }); await onSaved(user, done) } catch (e) { setMsg({ ok: false, text: firstError(e, 'Failed.') }) }
  }

  const err = (k: string) => errors[k]?.[0] && <span className="error small">{errors[k][0]}</span>

  return (
    <aside className="card pad editor">
      <div className="panel-head" style={{ padding: 0, border: 0 }}>
        <h2>{user ? user.displayName : 'Add a person'}</h2>
        <button className="quiet" onClick={onClose} aria-label="Close">✕</button>
      </div>
      {user && <div className="muted small mono">{user.userName} · account since {localDateTime(user.createdUtc)}</div>}
      {user && shared && <p className="muted small" style={{ margin: 0 }}>Also works at {user.otherClinics.join(', ')}: their name, password and two-factor are theirs; here you set their roles.</p>}

      {!user && (
        <>
          <p className="muted small" style={{ margin: 0 }}>New to ClinicHub: give a user name, their name and a temporary password. Already works at another clinic: their user name alone adds them here.</p>
          <div className="form-grid">
            <label>User name<input value={form.userName} onChange={e => setForm({ ...form, userName: e.target.value })} autoComplete="off" />{err('userName')}</label>
            <label>Temporary password<input type="password" value={form.password} onChange={e => setForm({ ...form, password: e.target.value })} autoComplete="new-password" />{err('password')}</label>
          </div>
        </>
      )}
      <div className="form-grid">
        <label>Full name<input value={form.displayName} disabled={shared} onChange={e => setForm({ ...form, displayName: e.target.value })} />{err('displayName')}</label>
        <label>Credentials<input value={form.credentials} disabled={shared} onChange={e => setForm({ ...form, credentials: e.target.value })} placeholder="MD, CCFP" /></label>
      </div>

      <fieldset className="checks">
        <legend>Roles in this clinic</legend>
        {roles.map(r => (
          <label key={r.id} className="check">
            <input type="checkbox" checked={form.roleIds.includes(r.id)}
              onChange={() => setForm({ ...form, roleIds: form.roleIds.includes(r.id) ? form.roleIds.filter(x => x !== r.id) : [...form.roleIds, r.id] })} />
            <span><strong>{r.name}</strong>{r.requireTwoFactor && <span className="badge" style={{ marginLeft: '.3rem' }}>2FA</span>} <span className="muted small">{r.description}</span></span>
          </label>
        ))}
        {err('roleIds')}
        <div className="muted small">Allows: {effective.length ? effective.join(', ') : user ? 'nothing (saving removes them from this clinic)' : 'nothing'}</div>
      </fieldset>

      {msg && <p className={`result ${msg.ok ? 'ok' : 'warn'}`}>{msg.text}</p>}
      <div className="dialog-actions"><button className="primary" onClick={save}>{user ? 'Save' : 'Add'}</button></div>

      {user && (
        <>
          <hr />
          <h3>Security</h3>
          <div className="badge-row">
            {!shared && <button className="small" onClick={async () => {
              const pw = window.prompt(`Temporary password for ${user.displayName} (at least 10 characters). They must change it at next sign-in.`)
              if (!pw) return
              try { await admin.resetPassword(user.id, pw); setMsg({ ok: true, text: 'Password reset and all their sessions ended.' }) }
              catch (e) { setMsg({ ok: false, text: firstError(e, 'Not changed.') }) }
            }}>Reset password</button>}
            {user.isLockedOut && <button className="small" onClick={() => act(`Unlock ${user.displayName}?`, () => admin.unlock(user.id), 'Unlocked.')}>Unlock</button>}
            {!shared && user.twoFactorEnabled && <button className="small" onClick={() => act(`Reset two-factor for ${user.displayName}? Use this when they lost their phone.`, () => admin.resetTwoFactor(user.id), 'Two-factor reset; their sessions ended.')}>Reset two-factor</button>}
            <button className="small" onClick={() => act(`Sign ${user.displayName} out on every device?`, () => admin.revokeSessions(user.id), 'All their sessions ended.')}>Sign out everywhere</button>
          </div>
        </>
      )}
    </aside>
  )
}

function RolesTab({ roles, catalog, reload }: { roles: RoleInfo[]; catalog: PermissionInfo[]; reload: () => Promise<unknown> }) {
  const [editing, setEditing] = useState<RoleInfo | 'new' | null>(null)
  return (
    <div className="admin-layout">
      <div style={{ display: 'grid', gap: '.6rem', alignContent: 'start' }}>
        <div><button className="primary small" onClick={() => setEditing('new')}>New role</button></div>
        <div className="role-cards">
          {roles.map(r => (
            <button key={r.id} className={`card role-card ${editing !== 'new' && editing?.id === r.id ? 'on' : ''}`} onClick={() => setEditing(r)}>
              <span className="role-title"><strong>{r.name}</strong>
                <span className="badge-row">{r.isSystem && <span className="badge">Built-in</span>}{r.requireTwoFactor && <span className="badge ohip">2FA required</span>}</span></span>
              <span className="muted small">{r.description}</span>
              <span className="small">{r.permissions.length} of {catalog.length} permissions · {r.userCount} {r.userCount === 1 ? 'person' : 'people'}</span>
            </button>
          ))}
        </div>
      </div>
      {editing && (
        <RoleEditor key={editing === 'new' ? 'new' : editing.id} role={editing === 'new' ? null : editing} catalog={catalog}
          onClose={() => setEditing(null)} onSaved={async () => { await reload(); setEditing(null) }} />
      )}
    </div>
  )
}

function RoleEditor({ role, catalog, onClose, onSaved }: { role: RoleInfo | null; catalog: PermissionInfo[]; onClose: () => void; onSaved: () => Promise<void> }) {
  const [form, setForm] = useState({ name: role?.name ?? '', description: role?.description ?? '', permissions: role?.permissions ?? [] as string[], requireTwoFactor: role?.requireTwoFactor ?? false })
  const [msg, setMsg] = useState<string | null>(null)
  const isAdministrator = role?.isSystem && role.name === 'Administrator'
  const groups = [...new Set(catalog.map(p => p.group))]

  const save = async () => {
    setMsg(null)
    const body = { ...form, description: form.description || null }
    try {
      if (role) await admin.updateRole(role.id, body)
      else await admin.createRole(body)
      await onSaved()
    } catch (e) {
      setMsg(firstError(e, 'Not saved.'))
    }
  }

  return (
    <aside className="card pad editor">
      <div className="panel-head" style={{ padding: 0, border: 0 }}>
        <h2>{role ? role.name : 'New role'}</h2>
        <button className="quiet" onClick={onClose} aria-label="Close">✕</button>
      </div>
      <div className="form-grid">
        <label>Name<input value={form.name} disabled={role?.isSystem} onChange={e => setForm({ ...form, name: e.target.value })} placeholder="e.g. Echo typist" /></label>
        <label>Description<input value={form.description} onChange={e => setForm({ ...form, description: e.target.value })} /></label>
      </div>
      {isAdministrator && <p className="muted small" style={{ margin: 0 }}>Administrator always has every permission, so the clinic can never lock itself out.</p>}
      {groups.map(g => (
        <fieldset key={g} className="checks">
          <legend>{g}</legend>
          {catalog.filter(p => p.group === g).map(p => (
            <label key={p.key} className="check">
              <input type="checkbox" disabled={isAdministrator} checked={form.permissions.includes(p.key)}
                onChange={() => setForm({ ...form, permissions: form.permissions.includes(p.key) ? form.permissions.filter(x => x !== p.key) : [...form.permissions, p.key] })} />
              <span>{p.label} <span className="muted small mono">{p.key}</span></span>
            </label>
          ))}
        </fieldset>
      ))}
      <label className="check">
        <input type="checkbox" checked={form.requireTwoFactor} onChange={e => setForm({ ...form, requireTwoFactor: e.target.checked })} />
        <span><strong>Require two-factor sign-in</strong> <span className="muted small">Members set up an authenticator app before they can work, at every clinic.</span></span>
      </label>
      {msg && <p className="error">{msg}</p>}
      <div className="dialog-actions">
        {role && !role.isSystem && <button className="quiet" onClick={async () => {
          if (!window.confirm(`Delete the role "${role.name}"?`)) return
          try { await admin.deleteRole(role.id); await onSaved() } catch (e) { setMsg(firstError(e, 'Not deleted.')) }
        }}>Delete role</button>}
        <button className="primary" onClick={save}>{role ? 'Save' : 'Create role'}</button>
      </div>
    </aside>
  )
}
