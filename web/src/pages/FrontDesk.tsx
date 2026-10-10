import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router'
import { api, ApiError, can, localTime, minutesSince, shiftDate, torontoToday, type AppointmentRow, type DayView, type Eligibility, type PatientBrief, type Status } from '../api'
import { useSession } from '../App'

const TYPE_COLORS: Record<string, string> = {
  amber: '#d48806', blue: '#1d5fbf', green: '#237a4b', rose: '#c2416b', violet: '#6d4bc3', teal: '#0f8a8a', slate: '#64748b',
}

const STATUS_LABEL: Record<Status, string> = {
  Booked: 'Booked', Arrived: 'Waiting', Roomed: 'In room', WithProvider: 'With provider', Completed: 'Done', NoShow: 'No-show', Cancelled: 'Cancelled',
}

const LONG_WAIT = 30

/** The front desk's day: who is coming, who is waiting, which rooms are busy. */
export default function FrontDesk() {
  const { me } = useSession()
  const [date, setDate] = useState(torontoToday())
  const [day, setDay] = useState<DayView | null>(null)
  const [provider, setProvider] = useState<number | null>(null)
  const [hideDone, setHideDone] = useState(false)
  const [now, setNow] = useState(Date.now())
  const [checkingIn, setCheckingIn] = useState<AppointmentRow | null>(null)
  const [rooming, setRooming] = useState<AppointmentRow | null>(null)
  const [walkIn, setWalkIn] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const manage = can(me, 'schedule.manage')
  const isToday = date === torontoToday()

  const load = useCallback(() => api.day(date).then(setDay).catch(e => setError(e.message)), [date])
  useEffect(() => { load() }, [load])
  useEffect(() => {
    const t = setInterval(() => { setNow(Date.now()); if (isToday) load() }, 30_000)
    return () => clearInterval(t)
  }, [load, isToday])

  const move = async (a: AppointmentRow, status: Status, roomId?: number) => {
    setError(null)
    try {
      await api.setStatus(a.id, status, roomId)
      await load()
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Not saved.')
    }
  }

  const providerName = useMemo(() => new Map(day?.providers.map(p => [p.id, p.name]) ?? []), [day])

  if (!day) return <p className="muted">Loading…</p>

  const appts = day.appointments
  const count = (s: Status) => appts.filter(a => a.status === s).length
  const waiting = appts.filter(a => a.status === 'Arrived').sort((a, b) => (a.arrivedUtc ?? '').localeCompare(b.arrivedUtc ?? ''))
  const longest = waiting.length ? minutesSince(waiting[0].arrivedUtc, now) : 0
  const inRooms = appts.filter(a => a.status === 'Roomed' || a.status === 'WithProvider')
  const shown = appts.filter(a => (provider === null || a.providerId === provider) && !(hideDone && ['Completed', 'NoShow', 'Cancelled'].includes(a.status)))
  const byHour = new Map<string, AppointmentRow[]>()
  for (const a of shown) {
    const h = localTime(a.startUtc).slice(0, 2) + ':00'
    byHour.set(h, [...(byHour.get(h) ?? []), a])
  }

  return (
    <>
      <div className="board-head">
        <div>
          <h1>{isToday ? 'Today' : new Date(date + 'T12:00:00').toLocaleDateString('en-CA', { weekday: 'long', month: 'long', day: 'numeric' })}</h1>
          <div className="muted">{day.location} · {new Date(date + 'T12:00:00').toLocaleDateString('en-CA', { dateStyle: 'full' })}</div>
        </div>
        <div className="date-nav">
          <button onClick={() => setDate(shiftDate(date, -1))} aria-label="Previous day">‹</button>
          <button onClick={() => setDate(torontoToday())} disabled={isToday}>Today</button>
          <button onClick={() => setDate(shiftDate(date, 1))} aria-label="Next day">›</button>
          {manage && isToday && <button className="primary" onClick={() => setWalkIn(true)}>+ Walk-in</button>}
        </div>
      </div>

      <div className="kpis">
        <Kpi color="var(--booked)" value={appts.filter(a => a.status !== 'Cancelled').length} label="Booked today" sub={`${appts.filter(a => a.source === 'WalkIn').length} walk-ins`} />
        <Kpi color="var(--waiting)" value={waiting.length} label="Waiting" sub={waiting.length ? `longest ${longest} min` : 'nobody waiting'} />
        <Kpi color="var(--roomed)" value={inRooms.length} label="In rooms" sub={`${day.rooms.length - new Set(inRooms.map(a => a.roomId)).size} rooms free`} />
        <Kpi color="var(--done)" value={count('Completed')} label="Seen" sub={`${Math.round(count('Completed') / Math.max(1, appts.length) * 100)}% of the day`} />
        <Kpi color="var(--noshow)" value={count('NoShow')} label="No-shows" sub={`${count('Cancelled')} cancelled`} />
      </div>

      {error && <p className="error" role="alert">{error}</p>}

      <div className="board">
        <section className="card">
          <div className="panel-head">
            <h2>Schedule</h2>
            <label className="small" style={{ flexDirection: 'row', alignItems: 'center', gap: '.4rem' }}>
              <input type="checkbox" checked={hideDone} onChange={e => setHideDone(e.target.checked)} style={{ width: 'auto', minHeight: 0 }} /> Hide finished
            </label>
          </div>
          <div className="filters" role="group" aria-label="Provider">
            <button className={`chip ${provider === null ? 'on' : ''}`} onClick={() => setProvider(null)}>All providers</button>
            {day.providers.map(p => (
              <button key={p.id} className={`chip ${provider === p.id ? 'on' : ''}`} onClick={() => setProvider(p.id)}>{p.name}</button>
            ))}
          </div>
          <ul className="schedule">
            {[...byHour].map(([hour, rows]) => (
              <li key={hour}>
                <div className="hour">{hour}</div>
                <ul className="schedule">
                  {rows.map(a => (
                    <li key={a.id} className={`row s-${a.status}`}>
                      <div className="time">{localTime(a.startUtc)}</div>
                      <div className="patient">
                        <strong>{a.patient.name}</strong>
                        <span className="muted small">{a.patient.age} y · {a.patient.sex} · <span className="mono">{a.patient.mrn}</span></span>
                        {a.encounterId && can(me, 'encounters.view') && <Link to={`/visits/${a.encounterId}`} className="small chart-link">Open visit</Link>}
                        {a.queueNumber && <div className="walkin-tag">Walk-in #{a.queueNumber}{a.reason ? ` · ${a.reason}` : ''}</div>}
                      </div>
                      <div className="small"><span className="type-dot" style={{ ['--type-color' as string]: TYPE_COLORS[a.type.color] ?? '#64748b' }} />{a.type.name}</div>
                      <div className="provider small muted">{providerName.get(a.providerId)}</div>
                      <div className="coverage"><Coverage row={a} /></div>
                      <div className="actions">
                        <span className={`status s-${a.status}`}>{STATUS_LABEL[a.status]}{a.room ? ` · ${a.room}` : ''}</span>
                      </div>
                      {manage && isToday && <RowActions a={a} onCheckIn={() => setCheckingIn(a)} onRoom={() => setRooming(a)} onMove={s => move(a, s)} now={now} />}
                    </li>
                  ))}
                </ul>
              </li>
            ))}
            {shown.length === 0 && <li className="muted" style={{ padding: '1rem' }}>No appointments.</li>}
          </ul>
        </section>

        <aside className="side">
          <section className="card">
            <div className="panel-head"><h2>Waiting room</h2><span className="muted">{waiting.length}</span></div>
            <ul className="waiting-list">
              {waiting.map(a => {
                const m = minutesSince(a.arrivedUtc, now)
                return (
                  <li key={a.id}>
                    <div>
                      <strong>{a.patient.name}</strong>
                      <div className="muted small">{providerName.get(a.providerId)} · {a.type.name}</div>
                    </div>
                    <span className={`wait ${m >= LONG_WAIT ? 'long' : ''}`} title="Minutes since check-in">{m} min</span>
                  </li>
                )
              })}
              {waiting.length === 0 && <li className="muted">Nobody waiting.</li>}
            </ul>
          </section>
          <section className="card">
            <div className="panel-head"><h2>Rooms</h2></div>
            <ul className="rooms">
              {day.rooms.map(r => {
                const a = inRooms.find(x => x.roomId === r.id)
                return (
                  <li key={r.id} className={`room ${a ? `busy s-${a.status}` : ''}`}>
                    <div className="room-name">{r.name}</div>
                    {a ? (
                      <>
                        <div className="small"><strong>{a.patient.name}</strong></div>
                        <div className="muted small">{STATUS_LABEL[a.status]} · {minutesSince(a.roomedUtc, now)} min</div>
                      </>
                    ) : <div className="muted small">Free</div>}
                  </li>
                )
              })}
            </ul>
          </section>
        </aside>
      </div>

      {checkingIn && <CheckInDialog a={checkingIn} onClose={() => { setCheckingIn(null); load() }} />}
      {rooming && (
        <RoomDialog a={rooming} rooms={day.rooms} busy={new Set(inRooms.map(x => x.roomId!))} onClose={() => setRooming(null)}
          onPick={async roomId => { await move(rooming, 'Roomed', roomId); setRooming(null) }} />
      )}
      {walkIn && <WalkInDialog onClose={() => { setWalkIn(false); load() }} />}
    </>
  )
}

function Kpi({ color, value, label, sub }: { color: string; value: number; label: string; sub: string }) {
  return (
    <div className="card kpi" style={{ ['--kpi' as string]: color }}>
      <span className="value">{value}</span>
      <span className="label">{label}</span>
      <span className="sub">{sub}</span>
    </div>
  )
}

function Coverage({ row }: { row: AppointmentRow }) {
  const e = row.eligibility
  if (e) {
    const cls = e.paymentProgram === 'HCP' ? 'ohip' : e.paymentProgram === 'RMB' ? 'rmb' : 'none'
    return <span className={`badge ${cls}`} title={e.message}>{e.paymentProgram === 'HCP' ? 'OHIP ✓' : e.paymentProgram === 'RMB' ? 'Out-of-province' : 'Patient pays'}</span>
  }
  const c = row.patient.coverage
  return <span className={`badge ${c === 'OHIP' ? '' : c === 'None' ? 'none' : 'rmb'}`}>{c === 'OHIP' ? 'OHIP card' : c === 'None' ? 'No coverage' : `${c} plan`}</span>
}

function RowActions({ a, onCheckIn, onRoom, onMove, now }: {
  a: AppointmentRow; onCheckIn: () => void; onRoom: () => void; onMove: (s: Status) => void; now: number
}) {
  const late = new Date(a.startUtc + (a.startUtc.endsWith('Z') ? '' : 'Z')).getTime() + 20 * 60000 < now
  return (
    <div className="actions" style={{ gridColumn: '1 / -1' }}>
      {a.status === 'Booked' && <button className="small primary" onClick={onCheckIn}>Check in</button>}
      {a.status === 'Booked' && late && <button className="small" onClick={() => onMove('NoShow')}>No-show</button>}
      {a.status === 'Arrived' && <button className="small primary" onClick={onRoom}>Room</button>}
      {a.status === 'Roomed' && <button className="small primary" onClick={() => onMove('WithProvider')}>Provider in</button>}
      {a.status === 'WithProvider' && <button className="small primary" onClick={() => onMove('Completed')}>Done</button>}
    </div>
  )
}

function CheckInDialog({ a, onClose }: { a: AppointmentRow; onClose: () => void }) {
  const [version, setVersion] = useState('')
  const [result, setResult] = useState<Eligibility | null>(null)
  const [error, setError] = useState<string | null>(null)
  const hasCard = a.patient.coverage === 'OHIP'

  const check = async () => {
    setError(null)
    try {
      setResult((await api.checkIn(a.id, version.trim() || undefined)).eligibility)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Check-in failed.')
    }
  }

  return (
    <div className="overlay" role="dialog" aria-modal="true" aria-label="Check in">
      <div className="card dialog">
        <h2>Check in {a.patient.name}</h2>
        <dl className="facts">
          <dt>MRN</dt><dd className="mono">{a.patient.mrn}</dd>
          <dt>Age / sex</dt><dd>{a.patient.age} · {a.patient.sex}</dd>
          <dt>Visit</dt><dd>{localTime(a.startUtc)} · {a.type.name}{a.reason ? ` · ${a.reason}` : ''}</dd>
          <dt>Coverage on file</dt><dd>{hasCard ? 'Ontario health card' : a.patient.coverage === 'None' ? 'None' : `${a.patient.coverage} provincial plan`}</dd>
        </dl>
        {!result && hasCard && (
          <label>Version code on the card in hand <span className="muted small">(leave empty to use the one on file)</span>
            <input value={version} onChange={e => setVersion(e.target.value.toUpperCase())} maxLength={2} placeholder="e.g. AB" autoFocus />
          </label>
        )}
        {result && (
          <div className={`result ${result.eligible ? 'ok' : 'warn'}`} role="status">
            {result.eligible ? '✓ ' : '! '}{result.message}
            <div className="small" style={{ fontWeight: 400 }}>Billing: {result.paymentProgram === 'HCP' ? 'OHIP' : result.paymentProgram === 'RMB' ? 'Reciprocal (other province)' : 'Uninsured, patient pays'} · validation code {result.code} (simulated)</div>
          </div>
        )}
        {error && <p className="error">{error}</p>}
        <div className="dialog-actions">
          {result
            ? <button className="primary" onClick={onClose}>Done</button>
            : <>
                <button onClick={onClose}>Cancel</button>
                <button className="primary" onClick={check}>{hasCard ? 'Validate card and check in' : 'Check in'}</button>
              </>}
        </div>
      </div>
    </div>
  )
}

function RoomDialog({ a, rooms, busy, onClose, onPick }: {
  a: AppointmentRow; rooms: DayView['rooms']; busy: Set<number>; onClose: () => void; onPick: (roomId: number) => void
}) {
  return (
    <div className="overlay" role="dialog" aria-modal="true" aria-label="Choose a room">
      <div className="card dialog">
        <h2>Room {a.patient.name}</h2>
        <div className="rooms" style={{ padding: 0 }}>
          {rooms.map(r => (
            <button key={r.id} className="room" disabled={busy.has(r.id)} onClick={() => onPick(r.id)} style={{ textAlign: 'left', justifyContent: 'flex-start' }}>
              <span>{r.name}<span className="muted small" style={{ display: 'block', fontWeight: 400 }}>{busy.has(r.id) ? 'Occupied' : 'Free'}</span></span>
            </button>
          ))}
        </div>
        <div className="dialog-actions"><button onClick={onClose}>Cancel</button></div>
      </div>
    </div>
  )
}

function WalkInDialog({ onClose }: { onClose: () => void }) {
  const [q, setQ] = useState('')
  const [results, setResults] = useState<PatientBrief[]>([])
  const [picked, setPicked] = useState<PatientBrief | null>(null)
  const [reason, setReason] = useState('')
  const [done, setDone] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const t = setTimeout(() => { if (q.trim().length >= 2) api.patients(q).then(setResults) }, 200)
    return () => clearTimeout(t)
  }, [q])

  const add = async () => {
    if (!picked) return
    try {
      setDone((await api.addWalkIn(picked.id, reason)).queueNumber)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Not added.')
    }
  }

  return (
    <div className="overlay" role="dialog" aria-modal="true" aria-label="Add a walk-in">
      <div className="card dialog">
        <h2>Add a walk-in</h2>
        {done ? (
          <div className="result ok" role="status">{picked?.name} is number {done} in the queue.</div>
        ) : (
          <>
            <label>Find the patient<input value={q} onChange={e => setQ(e.target.value)} placeholder="Last name, MRN or health card number" autoFocus /></label>
            <ul className="search-results">
              {results.map(p => (
                <li key={p.id}>
                  <button className={picked?.id === p.id ? 'on' : ''} onClick={() => setPicked(p)}>
                    <span><strong>{p.name}</strong> <span className="muted small">{p.age} y · {p.sex}</span></span>
                    <span className="mono small">{p.mrn}</span>
                  </button>
                </li>
              ))}
              {q.trim().length >= 2 && results.length === 0 && <li className="muted small" style={{ padding: '.5rem' }}>No match.</li>}
            </ul>
            <label>Reason for visit<input value={reason} onChange={e => setReason(e.target.value)} placeholder="e.g. Sore throat" /></label>
          </>
        )}
        {error && <p className="error">{error}</p>}
        <div className="dialog-actions">
          {done ? <button className="primary" onClick={onClose}>Done</button> : <>
            <button onClick={onClose}>Cancel</button>
            <button className="primary" disabled={!picked} onClick={add}>Add to queue</button>
          </>}
        </div>
      </div>
    </div>
  )
}
