import { useCallback, useEffect, useState } from 'react'
import { ApiError, allergies, can, firstError, type AllergyList } from '../api'
import { useSession } from '../App'

/**
 * A patient's allergy list. What is on it is checked when a drug is prescribed. "No known allergies" is recorded apart
 * from nothing having been asked, and an entry is ended with a reason, never deleted.
 */
export function AllergiesPanel({ patientId, compact = false }: { patientId: number; compact?: boolean }) {
  const { me } = useSession()
  const canEdit = can(me, 'encounters.document')
  const [list, setList] = useState<AllergyList | null>(null)
  const [allergens, setAllergens] = useState<{ key: string; label: string }[]>([])
  const [adding, setAdding] = useState(false)
  const [form, setForm] = useState({ key: '', other: '', severity: 'Moderate', reaction: '' })
  const [ending, setEnding] = useState<{ id: number; reason: string } | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => allergies.get(patientId).then(setList).catch(() => setList(null)), [patientId])
  useEffect(() => { load(); allergies.allergens().then(setAllergens).catch(() => setAllergens([])) }, [load])

  const run = async (f: () => Promise<AllergyList>) => {
    setError(null)
    try { setList(await f()); return true } catch (e) { setError(e instanceof ApiError ? firstError(e, e.message) : 'Not saved.'); return false }
  }
  const add = async () => {
    const ok = await run(() => allergies.add(patientId, {
      allergenKey: form.key && form.key !== 'other' ? form.key : undefined, substance: form.key === 'other' ? form.other : undefined,
      reaction: form.reaction.trim() || undefined, severity: form.severity,
    }))
    if (ok) { setAdding(false); setForm({ key: '', other: '', severity: 'Moderate', reaction: '' }) }
  }

  return (
    <section className="card">
      <div className="panel-head">
        <h2>Allergies</h2>
        {canEdit && !adding && <button className="small" onClick={() => setAdding(true)}>Add allergy…</button>}
      </div>
      <div className="pad" style={{ display: 'grid', gap: '.5rem' }}>
        {list && !list.recorded && <p className="muted small" style={{ margin: 0 }}>Not recorded yet: nothing has been asked, so nothing is checked when drugs are prescribed.</p>}
        {list?.noKnownAllergies && <p className="small" style={{ margin: 0 }}>No known allergies (asked and recorded).</p>}
        {list && list.items.length > 0 && (
          <ul className="code-list">
            {list.items.map(a => (
              <li key={a.id} style={{ flexWrap: 'wrap' }}>
                <span className="desc">{a.substance}
                  <span className="muted small"> · {a.severity}{a.reaction ? ` · ${a.reaction}` : ''}{compact ? '' : ` · recorded by ${a.recordedBy}`}</span>
                </span>
                {canEdit && ending?.id !== a.id && <button className="quiet small" onClick={() => setEnding({ id: a.id, reason: '' })}>End…</button>}
                {ending?.id === a.id && (
                  <div style={{ flexBasis: '100%', display: 'grid', gap: '.4rem' }}>
                    <label className="small">Why end it? (kept in the history)
                      <input value={ending.reason} maxLength={200} autoFocus placeholder="e.g. Entered in error" onChange={e => setEnding({ id: a.id, reason: e.target.value })} />
                    </label>
                    <div className="dialog-actions">
                      <button onClick={() => setEnding(null)}>Keep it</button>
                      <button className="danger" disabled={!ending.reason.trim()} onClick={async () => { if (await run(() => allergies.end(a.id, ending.reason))) setEnding(null) }}>End entry</button>
                    </div>
                  </div>
                )}
              </li>
            ))}
          </ul>
        )}
        {canEdit && list && !list.recorded && !adding && <div><button className="small" onClick={() => run(() => allergies.none(patientId))}>Record: no known allergies</button></div>}
        {adding && (
          <fieldset className="checks" style={{ display: 'grid', gap: '.4rem' }}>
            <legend>New allergy</legend>
            <label>Allergic to
              <select value={form.key} onChange={e => setForm({ ...form, key: e.target.value })}>
                <option value="">Choose…</option>
                {allergens.map(a => <option key={a.key} value={a.key}>{a.label}</option>)}
                <option value="other">Something else (write it)</option>
              </select>
            </label>
            {form.key === 'other' && <label>What
              <input value={form.other} maxLength={100} placeholder="e.g. Latex, or a drug's name" onChange={e => setForm({ ...form, other: e.target.value })} />
            </label>}
            <label>How bad
              <select value={form.severity} onChange={e => setForm({ ...form, severity: e.target.value })}>
                <option>Mild</option><option>Moderate</option><option>Severe</option>
              </select>
            </label>
            <label>Reaction (optional)
              <input value={form.reaction} maxLength={200} placeholder="e.g. Hives, swelling" onChange={e => setForm({ ...form, reaction: e.target.value })} />
            </label>
            <div className="dialog-actions">
              <button onClick={() => { setAdding(false); setError(null) }}>Cancel</button>
              <button className="primary" disabled={!form.key || (form.key === 'other' && form.other.trim().length < 2)} onClick={add}>Record allergy</button>
            </div>
          </fieldset>
        )}
        {error && <p className="error" style={{ margin: 0 }}>{error}</p>}
      </div>
    </section>
  )
}
