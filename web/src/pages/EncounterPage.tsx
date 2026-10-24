import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useParams } from 'react-router'
import { AllergiesPanel } from './Allergies'
import { ApiError, can, consent, encounters, firstError, localDateTime, localTime, money, orders, prescriptions, withheld, type AlertOverride, type DrugAlert, type Medication, type Withheld, type CatalogItem, type Drug, type Prescription, type PrescriptionLine, type Codes, type Coding, type Encounter, type Order, type Soap, type Vitals, type VitalsInput } from '../api'
import { useSession } from '../App'

const PROGRAM: Record<string, string> = { HCP: 'OHIP', RMB: 'Reciprocal (other province)', WCB: 'WSIB', Uninsured: 'Uninsured · patient pays' }
const SOAP: [keyof Soap, string, string][] = [
  ['subjective', 'Subjective', 'History in the patient’s words: complaint, onset, symptoms, medications, allergies'],
  ['objective', 'Objective', 'Examination findings and point-of-care results'],
  ['assessment', 'Assessment', 'Working diagnosis and differential'],
  ['plan', 'Plan', 'Treatment, prescriptions, orders, referrals, follow-up'],
]

/** One visit, as the clinician works through it: vitals, the SOAP note, codes, signature. */
export default function EncounterPage() {
  const id = Number(useParams().id)
  const { me } = useSession()
  const [enc, setEnc] = useState<Encounter | null>(null)
  const [codes, setCodes] = useState<Codes | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [locked, setLocked] = useState<Withheld | null>(null)

  const load = useCallback(() => {
    setLocked(null)
    encounters.get(id).then(setEnc).catch(e => { const w = withheld(e); if (w) setLocked(w); else setError(e.message) })
  }, [id])
  useEffect(() => {
    load()
    encounters.codes().then(setCodes)
  }, [load])

  const run = async (f: () => Promise<Encounter>) => {
    setError(null)
    setBusy(true)
    try {
      setEnc(await f())
    } catch (e) {
      setError(e instanceof ApiError ? [e.message, ...Object.values(e.fieldErrors).flat()].filter((m, i, a) => a.indexOf(m) === i && m !== 'One or more validation errors occurred.').join(' ') : 'Not saved.')
    } finally {
      setBusy(false)
    }
  }

  const note = useNoteAutosave(id, enc)

  if (locked) return <WithheldVisit info={locked} onOpened={load} />
  if (!enc) return error ? <p className="error">{error}</p> : <p className="muted">Loading…</p>

  const open = enc.status === 'Open'
  const writeNote = open && can(me, 'encounters.document')
  const code = open && can(me, 'encounters.sign')
  const mine = me.providerId === enc.provider.id
  const p = enc.patient

  return (
    <>
      <Link to="/visits" className="back">← Visits</Link>
      <section className={`card banner ${enc.status}`}>
        <div className="banner-who">
          <h1>{p.name}</h1>
          <div className="muted">
            {p.age} y · {p.sex} · born {p.dateOfBirth} · <span className="mono">{p.mrn}</span>
            {p.healthCard && <> · health card <span className="mono">{p.healthCard}</span></>}
          </div>
          <div className="tags">
            <span className={`badge ${enc.paymentProgram === 'HCP' ? 'ohip' : enc.paymentProgram === 'Uninsured' ? 'none' : 'rmb'}`} title={enc.eligibility?.message}>{PROGRAM[enc.paymentProgram] ?? enc.paymentProgram}</span>
            <span className="badge">{enc.kind === 'WalkIn' ? 'Walk-in' : enc.kind}</span>
            {enc.reasonForVisit && <span className="badge">Reason: {enc.reasonForVisit}</span>}
          </div>
        </div>
        <div className="visit-meta">
          <div className="small muted">{enc.provider.name} · checked in {localTime(enc.checkInUtc)}{enc.room ? ` · ${enc.room}` : ''}</div>
          <div className="visit-actions">
            {open && enc.appointmentStatus === 'Roomed' && can(me, 'encounters.document') &&
              <button className="primary" disabled={busy} onClick={() => run(() => encounters.start(id))}>Start visit</button>}
            {open && enc.appointmentStatus === 'WithProvider' && can(me, 'encounters.document') &&
              <button disabled={busy} onClick={() => run(() => encounters.finish(id))}>Patient left</button>}
            <span className={`sign-state ${enc.status}`}>{enc.status === 'Signed' ? `Signed ${localDateTime(enc.signedUtc!)} · ${enc.signedBy}` : enc.status}</span>
          </div>
        </div>
      </section>

      {error && <p className="error" role="alert">{error}</p>}

      <div className="chart">
        <div className="chart-main">
          <VitalsCard enc={enc} editable={writeNote} busy={busy} onAdd={v => run(() => encounters.addVitals(id, v))} />

          <section className="card">
            <div className="panel-head">
              <h2>Note</h2>
              <span className="muted small" role="status">{open ? note.status : enc.note ? `Last edited by ${enc.note.author}` : ''}</span>
            </div>
            <div className="soap">
              {SOAP.map(([key, label, hint]) => (
                <label key={key}>
                  <span>{label}{(key === 'assessment' || key === 'plan') && open && <span className="req" title="Needed to sign"> *</span>}</span>
                  {writeNote
                    ? <textarea value={note.soap[key] ?? ''} placeholder={hint} rows={key === 'subjective' ? 4 : 3}
                        onChange={e => note.change(key, e.target.value)} onBlur={note.flush} />
                    : <div className="note-text">{enc.note?.[key] || <span className="muted">—</span>}</div>}
                </label>
              ))}
            </div>
            {enc.status === 'Signed' && <Addenda enc={enc} canAdd={can(me, 'encounters.document')} busy={busy} onAdd={t => run(() => encounters.addendum(id, t))} />}
          </section>
        </div>

        <aside className="chart-side">
          {codes && <CodingCard enc={enc} codes={codes} editable={code} busy={busy} onSave={c => run(() => encounters.saveCoding(id, c))} />}

          <OrdersCard encounterId={enc.id} canOrder={can(me, 'orders.manage') && !!me.providerId && enc.status !== 'Voided'} />

          <AllergiesPanel patientId={enc.patient.id} compact />
          <PrescriptionsCard encounterId={enc.id} patientId={enc.patient.id} canPrescribe={can(me, 'prescriptions.write') && !!me.providerId && enc.status !== 'Voided'} />

          {open && (
            <section className="card">
              <div className="panel-head"><h2>Sign</h2></div>
              <div className="pad">
                {enc.readyToSign.length > 0
                  ? <ul className="todo">{enc.readyToSign.map(m => <li key={m}>{m}</li>)}</ul>
                  : <p className="result ok" style={{ margin: 0 }}>Ready to sign. The note locks and the claim goes to billing.</p>}
                {!mine && <p className="muted small">Only {enc.provider.name} can sign this visit.</p>}
                <button className="primary wide" disabled={busy || !mine || enc.readyToSign.length > 0 || !can(me, 'encounters.sign')}
                  onClick={async () => { await note.flush(); await run(() => encounters.sign(id)) }}>Sign visit</button>
              </div>
            </section>
          )}

          {enc.billing && (
            <section className="card">
              <div className="panel-head"><h2>Billing</h2></div>
              <dl className="facts pad">
                {enc.billing.claimNumber && <><dt>OHIP claim</dt><dd><span className="mono">{enc.billing.claimNumber}</span> · {money(enc.billing.claimAmount!)} · {enc.billing.claimStatus}</dd></>}
                {enc.billing.invoiceNumber && <><dt>Invoice</dt><dd><span className="mono">{enc.billing.invoiceNumber}</span> · {money(enc.billing.invoiceTotal!)} owed</dd></>}
                {!enc.billing.claimNumber && !enc.billing.invoiceNumber && <dd className="muted">Nothing billed.</dd>}
              </dl>
            </section>
          )}

          <section className="card">
            <div className="panel-head"><h2>Earlier visits</h2><span className="muted">{enc.history.length}</span></div>
            <ul className="history">
              {enc.history.map(h => (
                <li key={h.id}>
                  <Link to={`/visits/${h.id}`}>{localDateTime(h.checkInUtc)}</Link> <span className="muted small">· {h.provider}{h.status === 'Open' ? ' · unsigned' : ''}</span>
                  {h.diagnosis && <div className="small"><strong>{h.diagnosis}</strong></div>}
                  {h.assessment && <div className="small muted clamp">{h.assessment}</div>}
                </li>
              ))}
              {enc.history.length === 0 && enc.withheldVisits === 0 && <li className="muted small">First visit at this clinic.</li>}
              {enc.withheldVisits > 0 && <li className="muted small">{enc.withheldVisits} earlier {enc.withheldVisits === 1 ? 'visit is' : 'visits are'} withheld at the patient's request.</li>}
            </ul>
          </section>
        </aside>
      </div>
    </>
  )
}

/** Keeps the note in local state and saves it 0.8 s after typing stops, on leaving a field, and before signing. */
function useNoteAutosave(id: number, enc: Encounter | null) {
  const [soap, setSoap] = useState<Soap>({})
  const [status, setStatus] = useState('')
  const dirty = useRef(false)
  const latest = useRef<Soap>({})
  const timer = useRef<number | undefined>(undefined)
  const loadedFor = useRef<number | null>(null)

  useEffect(() => {
    if (!enc || loadedFor.current === enc.id) return
    loadedFor.current = enc.id
    const s = { subjective: enc.note?.subjective, objective: enc.note?.objective, assessment: enc.note?.assessment, plan: enc.note?.plan }
    latest.current = s
    setSoap(s)
    setStatus(enc.note ? `Saved ${localTime(enc.note.updatedUtc)}` : '')
  }, [enc])

  const inFlight = useRef<Promise<void>>(Promise.resolve())

  // One save at a time: a blur and the typing timer can both fire, and each waits for the one before.
  const flush = useCallback(() => {
    window.clearTimeout(timer.current)
    inFlight.current = inFlight.current.then(async () => {
      if (!dirty.current) return
      dirty.current = false
      setStatus('Saving…')
      try {
        const { updatedUtc } = await encounters.saveNote(id, latest.current)
        setStatus(dirty.current ? 'Editing…' : `Saved ${localTime(updatedUtc)}`)
      } catch (e) {
        dirty.current = true
        setStatus(e instanceof ApiError ? `Not saved: ${e.message}` : 'Not saved, retrying when you type again.')
      }
    })
    return inFlight.current
  }, [id])

  const change = (key: keyof Soap, value: string) => {
    latest.current = { ...latest.current, [key]: value }
    setSoap(latest.current)
    dirty.current = true
    setStatus('Editing…')
    window.clearTimeout(timer.current)
    timer.current = window.setTimeout(flush, 800)
  }

  // Leaving the page saves what was typed.
  useEffect(() => () => { if (dirty.current) encounters.saveNote(id, latest.current) }, [id])

  return { soap, status, change, flush }
}

const abnormal = (v: Vitals) => ({
  bp: (v.systolicMmHg ?? 0) >= 140 || (v.diastolicMmHg ?? 0) >= 90 || (v.systolicMmHg !== undefined && v.systolicMmHg < 90),
  hr: v.heartRate !== undefined && (v.heartRate > 100 || v.heartRate < 50),
  rr: v.respiratoryRate !== undefined && (v.respiratoryRate > 20 || v.respiratoryRate < 10),
  temp: (v.temperatureC ?? 0) >= 38,
  spo2: v.spO2 !== undefined && v.spO2 < 94,
})

function VitalsCard({ enc, editable, busy, onAdd }: { enc: Encounter; editable: boolean; busy: boolean; onAdd: (v: VitalsInput) => Promise<void> }) {
  const [form, setForm] = useState<Record<string, string>>({})
  const [adding, setAdding] = useState(false)
  const last = enc.vitals[0]
  const flag = last ? abnormal(last) : null
  const fields: [keyof VitalsInput, string, string][] = [
    ['systolicMmHg', 'Systolic', 'mmHg'], ['diastolicMmHg', 'Diastolic', 'mmHg'], ['heartRate', 'Pulse', '/min'], ['respiratoryRate', 'Resp.', '/min'],
    ['temperatureC', 'Temp.', '°C'], ['spO2', 'SpO₂', '%'], ['weightKg', 'Weight', 'kg'], ['heightCm', 'Height', 'cm'],
  ]

  const save = async () => {
    const v: VitalsInput = {}
    for (const [k] of fields) if (form[k]?.trim()) v[k] = Number(form[k]) as never
    await onAdd(v)
    setForm({})
    setAdding(false)
  }

  return (
    <section className="card">
      <div className="panel-head">
        <h2>Vitals</h2>
        {last && <span className="muted small">{localTime(last.takenUtc)} · {last.takenBy}</span>}
      </div>
      {last ? (
        <div className="vitals">
          <Tile label="BP" value={last.systolicMmHg ? `${last.systolicMmHg}/${last.diastolicMmHg}` : '—'} unit="mmHg" warn={flag!.bp} />
          <Tile label="Pulse" value={last.heartRate ?? '—'} unit="/min" warn={flag!.hr} />
          <Tile label="Resp." value={last.respiratoryRate ?? '—'} unit="/min" warn={flag!.rr} />
          <Tile label="Temp." value={last.temperatureC?.toFixed(1) ?? '—'} unit="°C" warn={flag!.temp} />
          <Tile label="SpO₂" value={last.spO2 ?? '—'} unit="%" warn={flag!.spo2} />
          <Tile label="Weight" value={last.weightKg ?? '—'} unit="kg" />
          <Tile label="BMI" value={last.bmi ?? '—'} unit={last.heightCm ? `${last.heightCm} cm` : ''} />
        </div>
      ) : <p className="muted pad" style={{ margin: 0 }}>No vitals yet{enc.kind === 'Virtual' ? ' (virtual visit)' : ''}.</p>}
      {enc.vitals.length > 1 && (
        <details className="pad earlier">
          <summary className="small">{enc.vitals.length - 1} earlier set{enc.vitals.length > 2 ? 's' : ''} this visit</summary>
          <ul className="small">
            {enc.vitals.slice(1).map(v => (
              <li key={v.id}>{localTime(v.takenUtc)}: {v.systolicMmHg ? `BP ${v.systolicMmHg}/${v.diastolicMmHg}, ` : ''}{v.heartRate ? `P ${v.heartRate}, ` : ''}{v.temperatureC ? `T ${v.temperatureC}, ` : ''}{v.spO2 ? `SpO₂ ${v.spO2}%` : ''} · {v.takenBy}</li>
            ))}
          </ul>
        </details>
      )}
      {editable && (adding ? (
        <form className="vitals-form pad" onSubmit={e => { e.preventDefault(); save() }}>
          {fields.map(([k, label, unit]) => (
            <label key={k}>{label} <span className="muted small">{unit}</span>
              <input inputMode="decimal" value={form[k] ?? ''} onChange={e => setForm({ ...form, [k]: e.target.value.replace(/[^0-9.]/g, '') })} />
            </label>
          ))}
          <div className="dialog-actions" style={{ gridColumn: '1 / -1' }}>
            <button type="button" onClick={() => setAdding(false)}>Cancel</button>
            <button className="primary" disabled={busy}>Save vitals</button>
          </div>
        </form>
      ) : (
        <div className="pad" style={{ paddingTop: 0 }}><button className="small" onClick={() => setAdding(true)}>{last ? 'Record again' : 'Record vitals'}</button></div>
      ))}
    </section>
  )
}

function Tile({ label, value, unit, warn }: { label: string; value: string | number; unit: string; warn?: boolean }) {
  return (
    <div className={`tile ${warn ? 'warn' : ''}`}>
      <span className="label">{label}</span>
      <span className="value">{value}</span>
      <span className="unit">{unit}</span>
    </div>
  )
}

function CodingCard({ enc, codes, editable, busy, onSave }: { enc: Encounter; codes: Codes; editable: boolean; busy: boolean; onSave: (c: Coding) => Promise<void> }) {
  const [dxQuery, setDxQuery] = useState('')
  const [service, setService] = useState('')
  const insuredAllowed = enc.paymentProgram !== 'Uninsured'

  const current = (): Coding => ({
    diagnoses: enc.diagnoses.map(d => ({ code: d.code, isPrimary: d.isPrimary })),
    services: enc.services.map(s => ({ feeCode: s.feeCode, uninsuredServiceId: s.uninsuredServiceId, units: s.units })),
  })
  const save = (f: (c: Coding) => Coding) => onSave(f(current()))

  const dxMatches = useMemo(() => {
    const q = dxQuery.trim().toLowerCase()
    if (!q) return []
    return codes.diagnostic.filter(d => !enc.diagnoses.some(x => x.code === d.code) && (d.code.startsWith(q) || d.description.toLowerCase().includes(q))).slice(0, 8)
  }, [dxQuery, codes, enc.diagnoses])

  const addService = (value: string) => {
    if (!value) return
    const [kind, key] = value.split(':')
    save(c => ({ ...c, services: [...c.services, kind === 'fee' ? { feeCode: key, units: 1 } : { uninsuredServiceId: Number(key), units: 1 }] }))
    setService('')
  }

  return (
    <section className="card">
      <div className="panel-head"><h2>Diagnosis and billing</h2>{!editable && enc.status === 'Open' && <span className="muted small">Physician codes</span>}</div>
      <div className="pad coding">
        <h3>Diagnosis <span className="muted small">OHIP diagnostic code</span></h3>
        <ul className="code-list">
          {enc.diagnoses.map((d, i) => (
            <li key={d.code}>
              <span className="mono code">{d.code}</span>
              <span className="desc">{d.description}</span>
              {d.isPrimary ? <span className="badge ohip">Primary</span>
                : editable && <button className="quiet small" disabled={busy} onClick={() => save(c => ({ ...c, diagnoses: c.diagnoses.map((x, j) => ({ ...x, isPrimary: j === i })) }))}>Make primary</button>}
              {editable && <button className="quiet small" aria-label={`Remove ${d.description}`} disabled={busy}
                onClick={() => save(c => {
                  const rest = c.diagnoses.filter((_, j) => j !== i)
                  // The first remaining diagnosis takes over as primary.
                  if (d.isPrimary && rest.length) rest[0] = { ...rest[0], isPrimary: true }
                  return { ...c, diagnoses: rest }
                })}>✕</button>}
            </li>
          ))}
          {enc.diagnoses.length === 0 && <li className="muted small">None yet.</li>}
        </ul>
        {editable && (
          <div className="typeahead">
            <input value={dxQuery} onChange={e => setDxQuery(e.target.value)} placeholder="Search code or condition, e.g. 401 or throat" aria-label="Add a diagnosis" />
            {dxMatches.length > 0 && (
              <ul role="listbox">
                {dxMatches.map(d => (
                  <li key={d.code}>
                    <button disabled={busy} onClick={() => { save(c => ({ ...c, diagnoses: [...c.diagnoses, { code: d.code, isPrimary: c.diagnoses.length === 0 }] })); setDxQuery('') }}>
                      <span className="mono">{d.code}</span> {d.description}
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}

        <h3>Services</h3>
        <ul className="code-list">
          {enc.services.map((s, i) => (
            <li key={i}>
              <span className="mono code">{s.code}</span>
              <span className="desc">{s.description}{!s.insured && <span className="muted small"> · patient pays</span>}</span>
              {editable ? (
                <input className="units" type="number" min={1} max={99} value={s.units} aria-label="Units" disabled={busy}
                  onChange={e => { const units = Math.min(99, Math.max(1, Number(e.target.value) || 1)); save(c => ({ ...c, services: c.services.map((x, j) => j === i ? { ...x, units } : x) })) }} />
              ) : s.units > 1 && <span className="small">× {s.units}</span>}
              <span className="fee">{money(s.fee)}</span>
              {editable && <button className="quiet small" aria-label={`Remove ${s.description}`} disabled={busy} onClick={() => save(c => ({ ...c, services: c.services.filter((_, j) => j !== i) }))}>✕</button>}
            </li>
          ))}
          {enc.services.length === 0 && <li className="muted small">None yet.</li>}
        </ul>
        {editable && (
          <select value={service} onChange={e => addService(e.target.value)} aria-label="Add a service" disabled={busy}>
            <option value="">Add a service…</option>
            {insuredAllowed && (
              <optgroup label="OHIP fee codes (illustrative)">
                {codes.fee.map(f => <option key={f.code} value={`fee:${f.code}`}>{f.code} · {f.description} · {money(f.fee)}</option>)}
              </optgroup>
            )}
            <optgroup label="Uninsured (clinic price list)">
              {codes.uninsured.map(u => <option key={u.id} value={`uninsured:${u.id}`}>{u.name} · {money(u.price)}</option>)}
            </optgroup>
          </select>
        )}
        {!insuredAllowed && <p className="muted small" style={{ margin: 0 }}>No valid provincial coverage at check-in: bill the clinic’s private rate.</p>}
        <dl className="totals">
          {enc.insuredTotal > 0 && <><dt>To {enc.paymentProgram === 'RMB' ? 'OHIP (reciprocal)' : 'OHIP'}</dt><dd>{money(enc.insuredTotal)}</dd></>}
          {enc.uninsuredTotal > 0 && <><dt>Patient pays</dt><dd>{money(enc.uninsuredTotal)}</dd></>}
        </dl>
      </div>
    </section>
  )
}

function Addenda({ enc, canAdd, busy, onAdd }: { enc: Encounter; canAdd: boolean; busy: boolean; onAdd: (text: string) => Promise<void> }) {
  const [text, setText] = useState('')
  return (
    <div className="pad addenda">
      <h3>Addenda</h3>
      {enc.note?.addendum
        ? enc.note.addendum.split('\n\n').map((a, i) => <p key={i} className="addendum">{a}</p>)
        : <p className="muted small">The note is signed and locked. Corrections and late results are added here with your name and the time.</p>}
      {canAdd && (
        <form onSubmit={async e => { e.preventDefault(); await onAdd(text); setText('') }} style={{ display: 'grid', gap: '.5rem' }}>
          <textarea rows={2} value={text} onChange={e => setText(e.target.value)} placeholder="Add an addendum" aria-label="Addendum" />
          <div><button className="small" disabled={busy || !text.trim()}>Add addendum</button></div>
        </form>
      )}
    </div>
  )
}

const ORDER_STATUS: Record<string, [string, string]> = {
  Draft: ['Sending…', 'none'], Accepted: ['Received', 'ohip'], InProgress: ['In progress', 'rmb'], Completed: ['Resulted', 'ohip'],
  Cancelled: ['Cancelled', ''], Failed: ['Not delivered', 'none'], Cancelling: ['Cancelling…', 'none'],
  AwaitingResult: ['Awaiting result', 'rmb'],
}

const ORDER_TARGET: Record<string, string> = { LabFlow: 'lab', ClinicReport: 'ClinicReport', ExternalLab: 'external lab' }

/** Statuses an order can still be withdrawn from (the application decides once it has the order). */
const CANCELLABLE = ['Draft', 'Failed', 'Accepted', 'InProgress', 'AwaitingResult']

/**
 * Lab tests (LabFlow) and echo / ECG (ClinicReport) from this visit. Each goes as a FHIR ServiceRequest; the status
 * says whether the other application has it yet.
 */
function OrdersCard({ encounterId, canOrder }: { encounterId: number; canOrder: boolean }) {
  const [list, setList] = useState<Order[] | null>(null)
  const [catalog, setCatalog] = useState<CatalogItem[]>([])
  const [picking, setPicking] = useState(false)
  const [chosen, setChosen] = useState<string[]>([])
  const [priority, setPriority] = useState<'Routine' | 'Stat'>('Routine')
  const [notes, setNotes] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [cancelling, setCancelling] = useState<{ id: number; reason: string } | null>(null)
  const [cancelError, setCancelError] = useState<string | null>(null)
  const key = (i: { target: string; code: string }) => `${i.target}|${i.code}`

  const load = useCallback(() => orders.forEncounter(encounterId).then(setList).catch(() => setList([])), [encounterId])
  useEffect(() => { load(); orders.catalog().then(setCatalog).catch(() => setCatalog([])) }, [load])
  // While something is on its way, look again shortly.
  useEffect(() => {
    if (!list?.some(o => o.status === 'Draft' || o.status === 'Cancelling')) return
    const t = setTimeout(load, 4000)
    return () => clearTimeout(t)
  }, [list, load])

  const send = async () => {
    setError(null)
    try {
      setList(await orders.place(encounterId, catalog.filter(i => chosen.includes(key(i))).map(i => ({ target: i.target, code: i.code })), priority, notes))
      setChosen([]); setNotes(''); setPriority('Routine'); setPicking(false)
    } catch (e) {
      setError(firstError(e, 'Not ordered.'))
    }
  }

  const cancel = async () => {
    if (!cancelling) return
    setCancelError(null)
    try {
      setList(await orders.cancel(cancelling.id, cancelling.reason))
      setCancelling(null)
    } catch (e) {
      setCancelError(firstError(e, 'Not cancelled.'))
    }
  }

  const groups = [...new Set(catalog.map(i => `${i.target}: ${i.group}`))]
  return (
    <section className="card">
      <div className="panel-head">
        <h2>Orders</h2>
        {canOrder && !picking && <button className="small" onClick={() => setPicking(true)}>Order tests…</button>}
      </div>
      <div className="pad" style={{ display: 'grid', gap: '.6rem' }}>
        {list?.length === 0 && !picking && <p className="muted small" style={{ margin: 0 }}>Nothing ordered from this visit.</p>}
        {list && list.length > 0 && (
          <ul className="code-list">
            {list.map(o => {
              const [label, cls] = ORDER_STATUS[o.status] ?? [o.status, '']
              return (
                <li key={o.id} style={{ flexWrap: 'wrap' }}>
                  <span className="desc">{o.display}<span className="muted small"> · {ORDER_TARGET[o.target] ?? o.target}{o.priority === 'Stat' ? ' · STAT' : ''}</span></span>
                  <span className={`badge ${cls}`} title={o.lastError ?? (o.externalId ? `${o.target} order ${o.externalId}` : undefined)}>{label}</span>
                  {o.result && (
                    <div className="small" style={{ flexBasis: '100%' }}>
                      {o.result.isAbnormal && <span className="badge none" style={{ marginRight: '.3rem' }}>Abnormal</span>}
                      <span className="muted">{o.result.status} result {localDateTime(o.result.receivedUtc)}{o.result.reviewedUtc ? ' · acknowledged' : ''}</span>
                      {o.result.summary && <pre className="result-text">{o.result.summary}</pre>}
                      {o.result.externalUrl && <a href={o.result.externalUrl} target="_blank" rel="noreferrer">Full report ↗</a>}
                    </div>
                  )}
                  {canOrder && CANCELLABLE.includes(o.status) && cancelling?.id !== o.id && (
                    <button className="quiet small" onClick={() => { setCancelling({ id: o.id, reason: '' }); setCancelError(null) }}>Cancel order</button>
                  )}
                  {o.status === 'Cancelled' && o.cancelReason && <div className="muted small" style={{ flexBasis: '100%' }}>Reason: {o.cancelReason}</div>}
                  {o.statusNote && o.status !== 'Cancelled' && <div className="muted small" style={{ flexBasis: '100%' }}>{o.statusNote}</div>}
                  {cancelling?.id === o.id && (
                    <div style={{ flexBasis: '100%', display: 'grid', gap: '.4rem' }}>
                      <label className="small">Why cancel?{o.target === 'ExternalLab' ? ' (the lab is not told from here)' : ` (sent to ${o.target === 'LabFlow' ? 'the lab' : 'ClinicReport'})`}
                        <input value={cancelling.reason} maxLength={200} autoFocus placeholder="e.g. Ordered in error"
                          onChange={e => setCancelling({ id: o.id, reason: e.target.value })} />
                      </label>
                      {cancelError && <p className="error" style={{ margin: 0 }}>{cancelError}</p>}
                      <div className="dialog-actions">
                        <button onClick={() => setCancelling(null)}>Keep order</button>
                        <button className="danger" onClick={cancel}>Cancel order</button>
                      </div>
                    </div>
                  )}
                  {o.lastError && o.status !== 'Accepted' && o.status !== 'Cancelled' && (
                    <div className="muted small" style={{ flexBasis: '100%' }}>
                      Attempt {o.attempts}: {o.lastError}
                      {canOrder && <button className="quiet small" onClick={async () => { await orders.resend(o.id); load() }}>Try again now</button>}
                    </div>
                  )}
                </li>
              )
            })}
          </ul>
        )}
        {picking && (
          <>
            {groups.map(g => (
              <fieldset key={g} className="checks">
                <legend>{g}</legend>
                {catalog.filter(i => `${i.target}: ${i.group}` === g).map(i => (
                  <label key={key(i)} className="check">
                    <input type="checkbox" checked={chosen.includes(key(i))}
                      onChange={() => setChosen(chosen.includes(key(i)) ? chosen.filter(x => x !== key(i)) : [...chosen, key(i)])} />
                    <span>{i.display}{!i.connected && <span className="muted small"> (not connected yet: the order waits)</span>}</span>
                  </label>
                ))}
              </fieldset>
            ))}
            <label>Clinical information for the lab or reader
              <textarea rows={2} value={notes} maxLength={500} onChange={e => setNotes(e.target.value)} placeholder="e.g. Fatigue, ?anemia" />
            </label>
            <label className="check"><input type="checkbox" checked={priority === 'Stat'} onChange={e => setPriority(e.target.checked ? 'Stat' : 'Routine')} /> STAT</label>
            {error && <p className="error">{error}</p>}
            <div className="dialog-actions">
              <button onClick={() => { setPicking(false); setChosen([]) }}>Cancel</button>
              <button className="primary" disabled={chosen.length === 0} onClick={send}>Send {chosen.length || ''} order{chosen.length === 1 ? '' : 's'}</button>
            </div>
          </>
        )}
      </div>
    </section>
  )
}

const RX_STATUS: Record<string, [string, string]> = {
  Draft: ['Sending…', 'none'], Accepted: ['At the pharmacy', 'ohip'], Cancelled: ['Cancelled', ''], Failed: ['Not delivered', 'none'], Cancelling: ['Cancelling…', 'none'],
}

type RxForm = { din: string; directions: string; quantity: string; daysSupply: string; repeats: string; noSubstitution: boolean; note: string }
const blankRx = (): RxForm => ({ din: '', directions: '', quantity: '', daysSupply: '', repeats: '0', noSubstitution: false, note: '' })

/**
 * Electronic prescriptions from this visit, to the pharmacy (PharmaFlow) as FHIR MedicationRequests. Writing one signs
 * for it: the prescriber is the signed-in clinician. Before it goes out ClinicHub checks the patient's recorded
 * allergies, what the patient already takes and duplicates; an alert is answered (a reason for severe and major ones)
 * or the drug is not sent. A prescription already at the pharmacy can be cancelled, and the pharmacy refuses once it has
 * begun to dispense. An earlier one at the pharmacy can be renewed from the patient's medicines.
 */
function PrescriptionsCard({ encounterId, patientId, canPrescribe }: { encounterId: number; patientId: number; canPrescribe: boolean }) {
  const [list, setList] = useState<Prescription[] | null>(null)
  const [catalog, setCatalog] = useState<Drug[]>([])
  const [writing, setWriting] = useState(false)
  const [rows, setRows] = useState<RxForm[]>([blankRx()])
  const [error, setError] = useState<string | null>(null)
  const [cancelling, setCancelling] = useState<{ id: number; reason: string } | null>(null)
  const [cancelError, setCancelError] = useState<string | null>(null)
  const [alerts, setAlerts] = useState<{ items: DrugAlert[]; reasons: Record<string, string>; renewing?: number } | null>(null)
  const [meds, setMeds] = useState<{ items: Medication[]; someWithheld: boolean } | null>(null)

  const load = useCallback(() => {
    prescriptions.forEncounter(encounterId).then(setList).catch(() => setList([]))
    prescriptions.medications(patientId).then(setMeds).catch(() => setMeds(null))
  }, [encounterId, patientId])
  useEffect(() => { load(); prescriptions.catalog().then(setCatalog).catch(() => setCatalog([])) }, [load])
  // While something is on its way, look again shortly.
  useEffect(() => {
    if (!list?.some(p => p.status === 'Draft' || p.status === 'Cancelling')) return
    const t = setTimeout(load, 4000)
    return () => clearTimeout(t)
  }, [list, load])

  const unit = (din: string) => catalog.find(d => d.din === din)?.unit ?? 'unit'
  const groups = [...new Set(catalog.map(d => d.group))]
  const connected = catalog.length === 0 || catalog[0]!.connected
  const change = (i: number, patch: Partial<RxForm>) => setRows(rows.map((r, k) => (k === i ? { ...r, ...patch } : r)))
  const ready = rows.every(r => r.din && r.directions.trim().length >= 3 && Number(r.quantity) >= 1)

  const send = async () => {
    setError(null)
    const items: PrescriptionLine[] = rows.map(r => ({
      din: r.din, directions: r.directions.trim(), quantity: Number(r.quantity), daysSupply: r.daysSupply === '' ? undefined : Number(r.daysSupply),
      repeats: Number(r.repeats) || 0, noSubstitution: r.noSubstitution, note: r.note.trim() || undefined,
    }))
    try {
      setList(await prescriptions.write(encounterId, items, overrides()))
      setRows([blankRx()]); setWriting(false); setAlerts(null); load()
    } catch (e) {
      if (!showAlerts(e)) setError(firstError(e, 'Not prescribed.'))
    }
  }

  /** What the prescriber answered to each alert so far. */
  const overrides = (): AlertOverride[] | undefined =>
    alerts ? alerts.items.map(a => ({ key: a.key, reason: alerts.reasons[a.key]?.trim() || undefined })) : undefined
  /** The server stopped it with alerts: show them, keeping the reasons already typed. */
  const showAlerts = (e: unknown, renewing?: number) => {
    if (!(e instanceof ApiError) || !Array.isArray(e.data.alerts)) return false
    setAlerts({ items: e.data.alerts as DrugAlert[], reasons: alerts?.reasons ?? {}, renewing })
    setError(null)
    return true
  }
  const answered = !alerts || alerts.items.every(a => a.severity === 'Moderate' || (alerts.reasons[a.key]?.trim().length ?? 0) >= 10)

  const renew = async (m: Medication) => {
    setError(null)
    try {
      setList(await prescriptions.renew(m.id, encounterId, alerts?.renewing === m.id ? overrides() : undefined))
      setAlerts(null); load()
    } catch (e) {
      if (!showAlerts(e, m.id)) setError(firstError(e, 'Not renewed.'))
    }
  }

  const cancel = async () => {
    if (!cancelling) return
    setCancelError(null)
    try {
      setList(await prescriptions.cancel(cancelling.id, cancelling.reason))
      setCancelling(null)
    } catch (e) {
      setCancelError(firstError(e, 'Not cancelled.'))
    }
  }

  return (
    <section className="card">
      <div className="panel-head">
        <h2>Prescriptions</h2>
        {canPrescribe && !writing && <button className="small" onClick={() => setWriting(true)}>Prescribe…</button>}
      </div>
      <div className="pad" style={{ display: 'grid', gap: '.6rem' }}>
        {list?.length === 0 && !writing && <p className="muted small" style={{ margin: 0 }}>Nothing prescribed at this visit.</p>}
        {list && list.length > 0 && (
          <ul className="code-list">
            {list.map(p => {
              const [label, cls] = RX_STATUS[p.status] ?? [p.status, '']
              return (
                <li key={p.id} style={{ flexWrap: 'wrap' }}>
                  <span className="desc">{p.display}
                    <span className="muted small"> · {p.quantity} {p.unit}{p.daysSupply ? ` · ${p.daysSupply} days` : ''} · {p.repeats} refill{p.repeats === 1 ? '' : 's'}{p.noSubstitution ? ' · no substitution' : ''}</span>
                  </span>
                  <span className={`badge ${cls}`} title={p.lastError ?? (p.externalId ? `Pharmacy prescription ${p.externalId}` : undefined)}>{label}</span>
                  <div className="small" style={{ flexBasis: '100%' }}>{p.directions}</div>
                  {p.note && <div className="muted small" style={{ flexBasis: '100%' }}>To the pharmacist: {p.note}</div>}
                  {canPrescribe && p.status !== 'Cancelled' && p.status !== 'Cancelling' && cancelling?.id !== p.id && (
                    <button className="quiet small" onClick={() => { setCancelling({ id: p.id, reason: '' }); setCancelError(null) }}>Cancel prescription</button>
                  )}
                  {p.status === 'Cancelled' && p.cancelReason && <div className="muted small" style={{ flexBasis: '100%' }}>Reason: {p.cancelReason}</div>}
                  {p.statusNote && p.status !== 'Cancelled' && <div className="muted small" style={{ flexBasis: '100%' }}>{p.statusNote}</div>}
                  {cancelling?.id === p.id && (
                    <div style={{ flexBasis: '100%', display: 'grid', gap: '.4rem' }}>
                      <label className="small">Why cancel? (sent to the pharmacy)
                        <input value={cancelling.reason} maxLength={200} autoFocus placeholder="e.g. Allergic reaction reported"
                          onChange={e => setCancelling({ id: p.id, reason: e.target.value })} />
                      </label>
                      {cancelError && <p className="error" style={{ margin: 0 }}>{cancelError}</p>}
                      <div className="dialog-actions">
                        <button onClick={() => setCancelling(null)}>Keep prescription</button>
                        <button className="danger" onClick={cancel}>Cancel prescription</button>
                      </div>
                    </div>
                  )}
                  {p.lastError && p.status !== 'Accepted' && p.status !== 'Cancelled' && (
                    <div className="muted small" style={{ flexBasis: '100%' }}>
                      Attempt {p.attempts}: {p.lastError}
                      {canPrescribe && <button className="quiet small" onClick={async () => { await prescriptions.resend(p.id); load() }}>Try again now</button>}
                    </div>
                  )}
                </li>
              )
            })}
          </ul>
        )}
        {writing && (
          <>
            {!connected && <p className="muted small" style={{ margin: 0 }}>The pharmacy is not connected yet: prescriptions are kept and sent when it is.</p>}
            {rows.map((r, i) => (
              <fieldset key={i} className="checks" style={{ display: 'grid', gap: '.4rem' }}>
                <legend>Drug {rows.length > 1 ? i + 1 : ''}</legend>
                <label>Drug
                  <select value={r.din} onChange={e => change(i, { din: e.target.value })}>
                    <option value="">Choose…</option>
                    {groups.map(g => (
                      <optgroup key={g} label={g}>
                        {catalog.filter(d => d.group === g).map(d => <option key={d.din} value={d.din} disabled={rows.some((x, k) => k !== i && x.din === d.din)}>{d.display}</option>)}
                      </optgroup>
                    ))}
                  </select>
                </label>
                <label>Directions
                  <input value={r.directions} maxLength={200} placeholder="e.g. 1 capsule three times daily for 7 days" onChange={e => change(i, { directions: e.target.value })} />
                </label>
                <div style={{ display: 'flex', gap: '.6rem', flexWrap: 'wrap' }}>
                  <label>Quantity ({unit(r.din)}s)
                    <input type="number" min={1} max={9999} value={r.quantity} onChange={e => change(i, { quantity: e.target.value })} style={{ width: '6rem' }} />
                  </label>
                  <label>Days of supply
                    <input type="number" min={1} max={365} value={r.daysSupply} onChange={e => change(i, { daysSupply: e.target.value })} style={{ width: '6rem' }} />
                  </label>
                  <label>Refills
                    <input type="number" min={0} max={11} value={r.repeats} onChange={e => change(i, { repeats: e.target.value })} style={{ width: '5rem' }} />
                  </label>
                </div>
                <label className="check"><input type="checkbox" checked={r.noSubstitution} onChange={e => change(i, { noSubstitution: e.target.checked })} /> No substitution (the pharmacist may not switch to a generic)</label>
                <label>To the pharmacist
                  <input value={r.note} maxLength={500} placeholder="optional" onChange={e => change(i, { note: e.target.value })} />
                </label>
                {rows.length > 1 && <div><button className="quiet small" onClick={() => setRows(rows.filter((_, k) => k !== i))}>Remove this drug</button></div>}
              </fieldset>
            ))}
            {rows.length < 10 && <div><button className="small" onClick={() => setRows([...rows, blankRx()])}>Add another drug</button></div>}
            <p className="muted small" style={{ margin: 0 }}>You sign for it by sending it. It is checked against the patient's recorded allergies and what they already take; the pharmacy checks again. Controlled drugs (narcotics, benzodiazepines, stimulants) are not prescribed here.</p>
            {error && <p className="error">{error}</p>}
            <div className="dialog-actions">
              <button onClick={() => { setWriting(false); setRows([blankRx()]); setError(null); setAlerts(null) }}>Cancel</button>
              <button className="primary" disabled={!ready || !answered} onClick={send}>{alerts ? 'Send anyway: ' : 'Send '}{rows.length} prescription{rows.length === 1 ? '' : 's'}</button>
            </div>
          </>
        )}
        {alerts && <AlertBox alerts={alerts} onReason={(key, text) => setAlerts({ ...alerts, reasons: { ...alerts.reasons, [key]: text } })}
          onCancel={() => setAlerts(null)} onProceed={alerts.renewing ? () => renew(meds!.items.find(m => m.id === alerts.renewing)!) : undefined} proceedable={answered} />}
        {meds && meds.items.length > 0 && (
          <div style={{ display: 'grid', gap: '.3rem' }}>
            <h3 style={{ margin: '.4rem 0 0' }}>Medicines on record</h3>
            <ul className="code-list">
              {meds.items.map(m => (
                <li key={m.id} style={{ flexWrap: 'wrap' }}>
                  <span className="desc">{m.display}
                    <span className="muted small"> · {m.directions} · {m.current ? `runs out ${localDateTime(m.runsOutUtc).split(',')[0]}` : 'finished'} · {m.prescriber}</span>
                  </span>
                  {m.renewedBy && <span className="badge none">Renewed</span>}
                  {canPrescribe && m.canRenew && <button className="quiet small" onClick={() => renew(m)}>Renew at this visit</button>}
                </li>
              ))}
            </ul>
            {meds.someWithheld && <p className="muted small" style={{ margin: 0 }}>Medicines from visits withheld at the patient's request are not listed or checked.</p>}
          </div>
        )}
      </div>
    </section>
  )
}

/** The alerts the server raised: what each says, and a reason box where one is needed. */
function AlertBox({ alerts, onReason, onCancel, onProceed, proceedable }: {
  alerts: { items: DrugAlert[]; reasons: Record<string, string> }
  onReason: (key: string, text: string) => void; onCancel: () => void; onProceed?: () => void; proceedable: boolean
}) {
  return (
    <div className="result warn" style={{ display: 'grid', gap: '.5rem' }}>
      <strong>Check before this is sent</strong>
      {alerts.items.map(a => (
        <div key={a.key} style={{ display: 'grid', gap: '.25rem' }}>
          <div><span className={`badge ${a.severity === 'Moderate' ? 'none' : 'danger'}`}>{a.severity} {a.kind.toLowerCase()}</span> {a.message}</div>
          {a.severity !== 'Moderate' && (
            <label className="small">Why go ahead? (at least 10 characters, kept with the prescription)
              <input value={alerts.reasons[a.key] ?? ''} maxLength={300} onChange={e => onReason(a.key, e.target.value)} />
            </label>
          )}
        </div>
      ))}
      <div className="dialog-actions">
        <button onClick={onCancel}>Change the prescription</button>
        {onProceed && <button className="danger" disabled={!proceedable} onClick={onProceed}>Renew anyway</button>}
      </div>
    </div>
  )
}

/**
 * A visit the patient withheld from this person (PHIPA consent directive). In an emergency a clinician with the
 * override permission can open the record for an hour, giving a reason; the privacy officer sees every override.
 */
function WithheldVisit({ info, onOpened }: { info: Withheld; onOpened: () => void }) {
  const [reason, setReason] = useState('')
  const [error, setError] = useState<string | null>(null)
  const open = async () => {
    setError(null)
    try { await consent.override(info.patientId, reason); onOpened() } catch (e) { setError(firstError(e, 'Not opened.')) }
  }
  return (
    <>
      <Link to="/visits" className="back">← Visits</Link>
      <section className="card pad withheld">
        <h1>{info.patient} <span className="muted mono small">{info.mrn}</span></h1>
        <p><strong>Withheld at the patient's request.</strong> The patient has a consent directive (PHIPA) that keeps this visit from you.
          Their appointments and billing are not affected.</p>
        {info.canOverride ? (
          <div style={{ display: 'grid', gap: '.6rem', maxWidth: 560 }}>
            <label>Emergency: why do you need it now?
              <textarea rows={2} value={reason} onChange={e => setReason(e.target.value)} placeholder="e.g. Chest pain in clinic, previous ECG needed" />
            </label>
            {error && <p className="error">{error}</p>}
            <div><button className="danger" disabled={reason.trim().length < 10} onClick={open}>Override for one hour</button></div>
            <p className="muted small" style={{ margin: 0 }}>Only to prevent serious harm to the patient or others. The override and your reason are logged and reported to the privacy officer.</p>
          </div>
        ) : <p className="muted">Ask a clinician who can override in an emergency, or the privacy officer.</p>}
      </section>
    </>
  )
}
