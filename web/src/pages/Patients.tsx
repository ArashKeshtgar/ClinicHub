import { useCallback, useEffect, useState } from 'react'
import { useSearchParams } from 'react-router'
import {
  api, ApiError, can, firstError, localDateTime, mpi,
  type DuplicatePair, type DuplicateRow, type DuplicateStatus, type PatientBrief, type PatientDetail, type PatientFields, type PossibleDuplicate,
} from '../api'
import { useSession } from '../App'

const EMPTY: PatientFields = { firstName: '', lastName: '', sex: 'U', preferredLanguage: 'en', isDeceased: false }

/**
 * The master patient index: find, register and correct patients; review records that may be the same person and
 * merge them. ?id=… opens a patient, ?new=1 the registration form, ?tab=duplicates the review queue.
 */
export default function Patients() {
  const { me } = useSession()
  const [params, setParams] = useSearchParams()
  const canMerge = can(me, 'patients.merge')
  const tab = params.get('tab') === 'duplicates' && canMerge ? 'duplicates' : 'find'
  const [openCount, setOpenCount] = useState<number | null>(null)
  const countOpen = useCallback(() => { if (canMerge) mpi.duplicates('Open').then(r => setOpenCount(r.length)).catch(() => {}) }, [canMerge])
  useEffect(() => { countOpen() }, [countOpen])

  return (
    <>
      <div className="board-head">
        <div>
          <h1>Patients</h1>
          <div className="muted">{me.clinic?.name}: one record per person. Registering checks the health card and looks for the same person under another record.</div>
        </div>
      </div>
      {canMerge && (
        <div className="tabs" role="tablist">
          <button role="tab" aria-selected={tab === 'find'} className={`tab ${tab === 'find' ? 'on' : ''}`} onClick={() => setParams({})}>Find &amp; register</button>
          <button role="tab" aria-selected={tab === 'duplicates'} className={`tab ${tab === 'duplicates' ? 'on' : ''}`} onClick={() => setParams({ tab: 'duplicates' })}>
            Possible duplicates{openCount ? <span className="count">{openCount}</span> : null}
          </button>
        </div>
      )}
      {tab === 'find' ? <FindTab /> : <DuplicatesTab onChanged={countOpen} />}
    </>
  )
}

function FindTab() {
  const { me } = useSession()
  const [params, setParams] = useSearchParams()
  const [q, setQ] = useState('')
  const [rows, setRows] = useState<PatientBrief[]>([])
  const selected = params.get('id') ? Number(params.get('id')) : null
  const creating = params.get('new') === '1'

  useEffect(() => {
    const t = setTimeout(() => { if (q.trim().length >= 2) api.patients(q).then(setRows); else setRows([]) }, 200)
    return () => clearTimeout(t)
  }, [q])

  const open = (id: number) => setParams({ id: String(id) })

  return (
    <div className="admin-layout">
      <section className="card">
        <div className="filters">
          <input type="search" placeholder="Last name, MRN, health card number or birth date (yyyy-mm-dd)" value={q} onChange={e => setQ(e.target.value)}
            aria-label="Find a patient" style={{ maxWidth: 420 }} autoFocus />
          {can(me, 'patients.edit') && <button className="primary small" style={{ marginLeft: 'auto' }} onClick={() => setParams({ new: '1' })}>Register patient</button>}
        </div>
        <div className="table-wrap">
          <table className="data">
            <thead><tr><th>Name</th><th>MRN</th><th>Age</th><th>Sex</th><th>Coverage</th></tr></thead>
            <tbody>
              {rows.map(p => (
                <tr key={p.id} className={`clickable ${selected === p.id ? 'selected' : ''}`} onClick={() => open(p.id)}>
                  <td><strong>{p.name}</strong></td><td className="mono">{p.mrn}</td><td>{p.age}</td><td>{p.sex}</td><td>{p.coverage}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {q.trim().length < 2 && <p className="muted pad">Type at least two characters. An MRN that was merged finds the record it was merged into.</p>}
          {q.trim().length >= 2 && rows.length === 0 && <p className="muted pad">No match. Check the spelling, or search by birth date.</p>}
        </div>
      </section>
      {creating && <PatientEditor key="new" patient={null} onClose={() => setParams({})} onOpen={open} />}
      {!creating && selected && <PatientLoader key={selected} id={selected} onClose={() => setParams({})} onOpen={open} />}
    </div>
  )
}

function PatientLoader({ id, onClose, onOpen }: { id: number; onClose: () => void; onOpen: (id: number) => void }) {
  const [patient, setPatient] = useState<PatientDetail | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { mpi.get(id).then(setPatient).catch(e => setError(firstError(e, 'Could not open the patient.'))) }, [id])
  if (error) return <aside className="card pad editor"><p className="error">{error}</p></aside>
  if (!patient) return <aside className="card pad editor"><p className="muted">Loading…</p></aside>
  return <PatientEditor patient={patient} onClose={onClose} onOpen={onOpen} />
}

function PatientEditor({ patient, onClose, onOpen }: { patient: PatientDetail | null; onClose: () => void; onOpen: (id: number) => void }) {
  const { me } = useSession()
  const [, setParams] = useSearchParams()
  const [current, setCurrent] = useState(patient)
  const [form, setForm] = useState<PatientFields>(patient?.fields ?? EMPTY)
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [matches, setMatches] = useState<PossibleDuplicate[] | null>(null)
  const [cardHolder, setCardHolder] = useState<PatientBrief | null>(null)
  const editable = can(me, 'patients.edit') && !current?.mergedInto

  const save = async (confirmNew = false) => {
    setErrors({}); setMsg(null); setCardHolder(null)
    try {
      if (current) {
        const saved = await mpi.update(current.id, form, current.rowVersion)
        setCurrent(saved); setForm(saved.fields)
        setMsg({ ok: true, text: saved.openDuplicates > current.openDuplicates ? 'Saved. The correction matches another record: it is waiting in Possible duplicates.' : 'Saved.' })
      } else {
        const created = await mpi.register(form, confirmNew)
        onOpen(created.id)
      }
    } catch (e) {
      if (e instanceof ApiError && Array.isArray(e.data.possibleDuplicates)) setMatches(e.data.possibleDuplicates as PossibleDuplicate[])
      else if (e instanceof ApiError && e.data.existingPatient) { setCardHolder(e.data.existingPatient as PatientBrief); setMsg({ ok: false, text: e.message }) }
      else if (e instanceof ApiError && Object.keys(e.fieldErrors).length) setErrors(e.fieldErrors)
      else setMsg({ ok: false, text: firstError(e, 'Not saved.') })
    }
  }

  return (
    <aside className="card pad editor">
      <div className="panel-head" style={{ padding: 0, border: 0 }}>
        <h2>{current ? current.name : 'Register a patient'}</h2>
        <button className="quiet" onClick={onClose} aria-label="Close">✕</button>
      </div>
      {current && (
        <div className="muted small">
          <span className="mono">{current.mrn}</span> · {current.age} y · {current.coverage} · {current.visits} {current.visits === 1 ? 'visit' : 'visits'}
          {current.lastVisitUtc && <> · last {localDateTime(current.lastVisitUtc)}</>}
        </div>
      )}
      {current?.mergedInto && (
        <p className="result warn">This record was merged into {current.mergedInto.name} (<span className="mono">{current.mergedInto.mrn}</span>).{' '}
          <button className="small" onClick={() => onOpen(current.mergedInto!.id)}>Open that record</button></p>
      )}
      {current && current.mergedFrom.length > 0 && (
        <div className="muted small">Also known as {current.mergedFrom.map(m => m.mrn).join(', ')} (merged into this record).</div>
      )}
      {current && current.openDuplicates > 0 && (
        <p className="result warn" style={{ margin: 0 }}>
          May be the same person as {current.openDuplicates === 1 ? 'another record' : `${current.openDuplicates} other records`}.{' '}
          {can(me, 'patients.merge') && <button className="small" onClick={() => setParams({ tab: 'duplicates' })}>Review</button>}
        </p>
      )}

      {matches ? (
        <div className="result warn" role="alert" style={{ display: 'grid', gap: '.5rem' }}>
          <span>This person may already be registered:</span>
          <ul className="search-results" style={{ background: 'var(--surface)' }}>
            {matches.map(m => (
              <li key={m.patient.id}>
                <button onClick={() => onOpen(m.patient.id)}>
                  <span><strong>{m.patient.name}</strong> <span className="muted small">born {m.dateOfBirth}{m.phone ? ` · ${m.phone}` : ''}</span>
                    <div className="muted small" style={{ fontWeight: 400 }}>{m.reasons.join(' · ')}</div></span>
                  <span className="mono small">{m.patient.mrn} · {m.score}%</span>
                </button>
              </li>
            ))}
          </ul>
          <div className="dialog-actions">
            <button onClick={() => setMatches(null)}>Back to the form</button>
            <button className="primary" onClick={() => { setMatches(null); save(true) }}>Someone else: register</button>
          </div>
        </div>
      ) : (
        <PatientForm form={form} setForm={setForm} errors={errors} disabled={!editable} />
      )}

      {msg && <p className={`result ${msg.ok ? 'ok' : 'warn'}`}>{msg.text}{cardHolder && <> <button className="small" onClick={() => onOpen(cardHolder.id)}>Open {cardHolder.name}</button></>}</p>}
      {editable && !matches && <div className="dialog-actions"><button className="primary" onClick={() => save()}>{current ? 'Save corrections' : 'Register'}</button></div>}
    </aside>
  )
}

function PatientForm({ form, setForm, errors, disabled }: {
  form: PatientFields; setForm: (f: PatientFields) => void; errors: Record<string, string[]>; disabled: boolean
}) {
  const set = (k: keyof PatientFields) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) =>
    setForm({ ...form, [k]: e.target.type === 'checkbox' ? (e.target as HTMLInputElement).checked : e.target.value || null })
  const err = (k: string) => errors[k]?.[0] && <span className="error small">{errors[k][0]}</span>
  const text = (k: keyof PatientFields, label: string, extra: React.InputHTMLAttributes<HTMLInputElement> = {}) => (
    <label>{label}<input value={(form[k] as string | null | undefined) ?? ''} onChange={set(k)} {...extra} />{err(k)}</label>
  )
  const hasCard = !!form.healthCardNumber?.trim()

  return (
    <fieldset disabled={disabled} className="patient-form">
      <legend>Name and birth</legend>
      <div className="form-grid">
        {text('firstName', 'First name', { autoComplete: 'off' })}
        {text('lastName', 'Last name', { autoComplete: 'off' })}
        {text('middleName', 'Middle name')}
        {text('preferredName', 'Preferred name')}
        {text('dateOfBirth', 'Date of birth', { type: 'date' })}
        <label>Sex<select value={form.sex ?? 'U'} onChange={set('sex')}>
          <option value="F">F</option><option value="M">M</option><option value="X">X</option><option value="U">Unknown</option>
        </select>{err('sex')}</label>
      </div>
      <legend>Coverage</legend>
      <div className="form-grid">
        {text('healthCardNumber', 'Ontario health card', { inputMode: 'numeric', placeholder: '1234 567 897' })}
        {text('healthCardVersion', 'Version', { maxLength: 2, placeholder: 'AB', style: { textTransform: 'uppercase' } })}
        {hasCard ? text('healthCardExpiry', 'Card expiry', { type: 'date' }) : text('coverageProvince', 'Other province (plan)', { maxLength: 2, placeholder: 'BC' })}
        {!hasCard && text('outOfProvinceNumber', "That province's number")}
      </div>
      <legend>Contact</legend>
      <div className="form-grid">
        {text('phone', 'Phone', { type: 'tel' })}
        {text('email', 'Email', { type: 'email' })}
        {text('addressLine1', 'Address')}
        {text('city', 'City')}
        {text('province', 'Province', { maxLength: 2, placeholder: 'ON' })}
        {text('postalCode', 'Postal code', { placeholder: 'M5V 0A1' })}
        <label>Language<select value={form.preferredLanguage ?? 'en'} onChange={set('preferredLanguage')}>
          <option value="en">English</option><option value="fr">French</option>
        </select></label>
      </div>
      <legend>Family doctor</legend>
      <div className="form-grid">
        {text('familyProviderName', 'Name')}
        {text('familyProviderOhipNumber', 'OHIP billing number', { inputMode: 'numeric', maxLength: 6 })}
      </div>
      <div className="form-grid">
        <label className="check" style={{ flexDirection: 'row', alignItems: 'center' }}><input type="checkbox" checked={form.isDeceased} onChange={set('isDeceased')} /> Deceased</label>
        {form.isDeceased && text('deceasedDate', 'Date of death', { type: 'date' })}
      </div>
    </fieldset>
  )
}

// ---- duplicates ----

const STATUS_LABEL: Record<DuplicateStatus, string> = { Open: 'To review', Merged: 'Merged', NotDuplicate: 'Not the same person' }

function DuplicatesTab({ onChanged }: { onChanged: () => void }) {
  const [status, setStatus] = useState<DuplicateStatus>('Open')
  const [rows, setRows] = useState<DuplicateRow[] | null>(null)
  const [selected, setSelected] = useState<number | null>(null)
  const [flash, setFlash] = useState<string | null>(null)
  const load = useCallback(() => mpi.duplicates(status).then(setRows), [status])
  useEffect(() => { setRows(null); load() }, [load])

  const scan = async () => {
    setFlash(null)
    try {
      const r = await mpi.scan()
      setFlash(r.found ? `${r.found} new ${r.found === 1 ? 'pair' : 'pairs'} found.` : 'Nothing new found.')
      if (status === 'Open') await load()
      onChanged()
    } catch (e) { setFlash(firstError(e, 'Scan failed.')) }
  }

  const decided = async (text: string) => { setSelected(null); setFlash(text); await load(); onChanged() }

  return (
    <div className="admin-layout">
      <section className="card">
        <div className="filters">
          {(['Open', 'Merged', 'NotDuplicate'] as DuplicateStatus[]).map(s => (
            <button key={s} className={`chip ${status === s ? 'on' : ''}`} onClick={() => { setSelected(null); setStatus(s) }}>{STATUS_LABEL[s]}</button>
          ))}
          <button className="small" style={{ marginLeft: 'auto' }} onClick={scan} title="Compare every record with the ones it could be">Scan all records</button>
        </div>
        {flash && <p className="result ok" style={{ margin: '.75rem' }}>{flash}</p>}
        {rows?.length === 0 && <p className="muted pad">{status === 'Open' ? 'Nothing to review.' : 'None yet.'}</p>}
        <div className="table-wrap">
          <table className="data">
            <thead><tr><th>Match</th><th>Record</th><th>Record</th><th>Why</th></tr></thead>
            <tbody>
              {rows?.map(r => (
                <tr key={r.id} className={`clickable ${selected === r.id ? 'selected' : ''}`} onClick={() => setSelected(r.id)}>
                  <td><span className={`badge ${r.score >= 80 ? 'none' : 'rmb'}`}>{r.score}%</span></td>
                  <td><strong>{r.a.name}</strong><div className="mono small muted">{r.a.mrn}</div></td>
                  <td><strong>{r.b.name}</strong><div className="mono small muted">{r.b.mrn}</div></td>
                  <td className="small">{r.reasons.join(' · ')}
                    {r.reviewedUtc && <div className="muted">{STATUS_LABEL[r.status]} {localDateTime(r.reviewedUtc)}{r.reviewedBy ? ` by ${r.reviewedBy}` : ''}</div>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
      {selected && <PairReview key={selected} id={selected} onClose={() => setSelected(null)} onDecided={decided} />}
    </div>
  )
}

const COMPARE: [string, (p: PatientDetail) => string | undefined | null][] = [
  ['MRN', p => p.mrn],
  ['Name', p => `${p.fields.lastName}, ${p.fields.firstName}${p.fields.middleName ? ` ${p.fields.middleName}` : ''}`],
  ['Preferred name', p => p.fields.preferredName],
  ['Date of birth', p => p.fields.dateOfBirth],
  ['Sex', p => p.fields.sex],
  ['Health card', p => p.fields.healthCardNumber ? `${p.fields.healthCardNumber} ${p.fields.healthCardVersion ?? ''}` : p.fields.coverageProvince ? `${p.fields.coverageProvince} plan` : null],
  ['Phone', p => p.fields.phone],
  ['Email', p => p.fields.email],
  ['Address', p => [p.fields.addressLine1, p.fields.city, p.fields.postalCode].filter(Boolean).join(', ')],
  ['Family doctor', p => p.fields.familyProviderName],
  ['Visits', p => `${p.visits}${p.lastVisitUtc ? ` (last ${localDateTime(p.lastVisitUtc)})` : ''}`],
  ['Registered', p => localDateTime(p.createdUtc)],
]

function PairReview({ id, onClose, onDecided }: { id: number; onClose: () => void; onDecided: (text: string) => Promise<void> }) {
  const [pair, setPair] = useState<DuplicatePair | null>(null)
  const [keep, setKeep] = useState<'a' | 'b'>('a')
  const [confirming, setConfirming] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    mpi.pair(id).then(p => {
      setPair(p)
      // Keep the record with the history (more visits); on a tie, the older one.
      setKeep(p.b.visits > p.a.visits ? 'b' : 'a')
    }).catch(e => setError(firstError(e, 'Could not open the pair.')))
  }, [id])

  if (error && !pair) return <aside className="card pad editor"><p className="error">{error}</p></aside>
  if (!pair) return <aside className="card pad editor"><p className="muted">Loading…</p></aside>

  const survivor = pair[keep], merged = pair[keep === 'a' ? 'b' : 'a']
  const open = pair.row.status === 'Open'
  const differentCards = !!pair.a.fields.healthCardNumber && !!pair.b.fields.healthCardNumber && pair.a.fields.healthCardNumber !== pair.b.fields.healthCardNumber

  const run = async (f: () => Promise<unknown>, text: string) => {
    setError(null)
    try { await f(); await onDecided(text) } catch (e) { setError(firstError(e, 'Not done.')); setConfirming(false) }
  }

  return (
    <aside className="card pad editor">
      <div className="panel-head" style={{ padding: 0, border: 0 }}>
        <h2>Same person? <span className="muted">{pair.row.score}%</span></h2>
        <button className="quiet" onClick={onClose} aria-label="Close">✕</button>
      </div>
      <div className="muted small">{pair.row.reasons.join(' · ')}</div>
      <table className="data compare">
        <thead>
          <tr><th></th>
            {(['a', 'b'] as const).map(k => (
              <th key={k}>{open
                ? <label className="check" style={{ flexDirection: 'row', alignItems: 'center', color: 'inherit' }}>
                    <input type="radio" name="keep" checked={keep === k} onChange={() => { setKeep(k); setConfirming(false) }} /> Keep</label>
                : k.toUpperCase()}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {COMPARE.map(([label, value]) => {
            const [a, b] = [value(pair.a) || '', value(pair.b) || '']
            const differs = label !== 'MRN' && label !== 'Registered' && label !== 'Visits' && a !== b
            return (
              <tr key={label}>
                <th>{label}</th>
                <td className={differs ? 'diff' : ''}>{a || <span className="muted">—</span>}</td>
                <td className={differs ? 'diff' : ''}>{b || <span className="muted">—</span>}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
      {open && (
        <>
          {differentCards
            ? <p className="result warn" style={{ margin: 0 }}>Two different health cards: if one is mistyped, correct it on the patient first. Otherwise these are two people.</p>
            : <p className="muted small" style={{ margin: 0 }}>
                Merging moves {merged.mrn}'s appointments, visits, orders, claims and invoices to {survivor.mrn}, and fills {survivor.mrn}'s empty fields from it.
                {' '}{merged.mrn} stays as a pointer: searching for it opens {survivor.mrn}. A merge is not undone from this screen.
              </p>}
          {error && <p className="error">{error}</p>}
          <div className="dialog-actions">
            <button onClick={() => run(() => mpi.notDuplicate(pair.row.id), 'Marked as two different people.')}>Not the same person</button>
            {!confirming
              ? <button className="primary" disabled={differentCards} onClick={() => setConfirming(true)}>Merge {merged.mrn} into {survivor.mrn}</button>
              : <button className="primary danger" onClick={() => run(() => mpi.merge(survivor.id, merged.id), `${merged.mrn} merged into ${survivor.mrn}.`)}>
                  Confirm: merge into {survivor.name}</button>}
          </div>
        </>
      )}
    </aside>
  )
}
