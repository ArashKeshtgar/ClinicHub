import { useCallback, useEffect, useState } from 'react'
import { useNavigate } from 'react-router'
import { encounters, localTime, localDateTime, minutesSince, shiftDate, torontoToday, type Status, type Worklist, type WorklistRow } from '../api'
import { useSession } from '../App'

const WHERE: Record<Status, string> = {
  Booked: 'Booked', Arrived: 'Waiting', Roomed: 'In room', WithProvider: 'With provider', Completed: 'Seen', NoShow: 'No-show', Cancelled: 'Cancelled',
}

/** The clinician's day: who is waiting for them, what is documented, what is left to sign. */
export default function Visits() {
  const { me } = useSession()
  const navigate = useNavigate()
  const [date, setDate] = useState(torontoToday())
  const [who, setWho] = useState<number | 'all'>(me.providerId ?? 'all')
  const [list, setList] = useState<Worklist | null>(null)
  const [now, setNow] = useState(Date.now())
  const [error, setError] = useState<string | null>(null)
  const isToday = date === torontoToday()

  const load = useCallback(() => encounters.worklist(date, who).then(setList).catch(e => setError(e.message)), [date, who])
  useEffect(() => { load() }, [load])
  useEffect(() => {
    const t = setInterval(() => { setNow(Date.now()); if (isToday) load() }, 30_000)
    return () => clearInterval(t)
  }, [load, isToday])

  if (!list) return <p className="muted">Loading…</p>

  const visits = list.visits
  const ready = visits.filter(v => v.status === 'Open' && (v.appointmentStatus === 'Roomed' || v.appointmentStatus === 'WithProvider'))
  const toSign = visits.filter(v => v.status === 'Open').length + list.olderUnsigned.length
  const providerName = new Map(list.providers.map(p => [p.id, p.name]))

  return (
    <>
      <div className="board-head">
        <div>
          <h1>{who === 'all' ? 'All visits' : who === me.providerId ? 'My visits' : providerName.get(who)}</h1>
          <div className="muted">{new Date(date + 'T12:00:00').toLocaleDateString('en-CA', { dateStyle: 'full' })}</div>
        </div>
        <div className="date-nav">
          <button onClick={() => setDate(shiftDate(date, -1))} aria-label="Previous day">‹</button>
          <button onClick={() => setDate(torontoToday())} disabled={isToday}>Today</button>
          <button onClick={() => setDate(shiftDate(date, 1))} aria-label="Next day">›</button>
        </div>
      </div>

      <div className="kpis kpis-4">
        <Kpi color="var(--roomed)" value={ready.length} label="Ready for you" sub={ready.length ? `longest ${Math.max(...ready.map(v => minutesSince(v.roomedUtc ?? v.checkInUtc, now)))} min in room` : 'no one in a room'} />
        <Kpi color="var(--waiting)" value={visits.filter(v => v.appointmentStatus === 'Arrived').length} label="In the waiting room" sub="checked in, not roomed" />
        <Kpi color="var(--done)" value={visits.filter(v => v.status === 'Signed').length} label="Signed" sub={`of ${visits.length} today`} />
        <Kpi color="var(--noshow)" value={toSign} label="To sign" sub={list.olderUnsigned.length ? `${list.olderUnsigned.length} from earlier days` : 'nothing older'} />
      </div>

      {error && <p className="error" role="alert">{error}</p>}

      <section className="card">
        <div className="filters" role="group" aria-label="Provider">
          {me.providerId && <button className={`chip ${who === me.providerId ? 'on' : ''}`} onClick={() => setWho(me.providerId!)}>Mine</button>}
          <button className={`chip ${who === 'all' ? 'on' : ''}`} onClick={() => setWho('all')}>All providers</button>
          {list.providers.filter(p => p.id !== me.providerId).map(p => (
            <button key={p.id} className={`chip ${who === p.id ? 'on' : ''}`} onClick={() => setWho(p.id)}>{p.name}</button>
          ))}
        </div>
        <VisitTable rows={visits} now={now} live={isToday} showProvider={who === 'all'} providerName={providerName} onOpen={id => navigate(`/visits/${id}`)} />
        {visits.length === 0 && <p className="muted" style={{ padding: '1rem' }}>No checked-in patients.</p>}
      </section>

      {list.olderUnsigned.length > 0 && (
        <section className="card" style={{ marginTop: '1rem' }}>
          <div className="panel-head"><h2>Unsigned from earlier days</h2><span className="muted">{list.olderUnsigned.length}</span></div>
          <VisitTable rows={list.olderUnsigned} now={now} live={false} showProvider={who === 'all'} providerName={providerName} onOpen={id => navigate(`/visits/${id}`)} older />
        </section>
      )}
    </>
  )
}

function VisitTable({ rows, now, live, showProvider, providerName, onOpen, older }: {
  rows: WorklistRow[]; now: number; live: boolean; showProvider: boolean; providerName: Map<number, string>; onOpen: (id: number) => void; older?: boolean
}) {
  return (
    <ul className="visit-list">
      {rows.map(v => {
        const where = v.appointmentStatus ?? 'Arrived'
        const waitFrom = where === 'Roomed' ? v.roomedUtc : where === 'Arrived' ? v.checkInUtc : undefined
        return (
          <li key={v.id}>
            <button className={`visit-row s-${where}`} onClick={() => onOpen(v.id)}>
              <span className="time">{older ? localDateTime(v.checkInUtc) : localTime(v.checkInUtc)}</span>
              <span className="patient">
                <strong>{v.patient.name}</strong>
                <span className="muted small">{v.patient.age} y · {v.patient.sex} · <span className="mono">{v.patient.mrn}</span></span>
              </span>
              <span className="reason small">{v.reason ?? <span className="muted">{v.kind === 'Virtual' ? 'Virtual visit' : 'No reason given'}</span>}
                {showProvider && <span className="muted" style={{ display: 'block' }}>{providerName.get(v.providerId)}</span>}</span>
              <span>
                <span className={`status s-${where}`}>{WHERE[where]}{v.room && (where === 'Roomed' || where === 'WithProvider') ? ` · ${v.room}` : ''}</span>
                {live && waitFrom && <span className="muted small" style={{ display: 'block', marginTop: '.2rem' }}>{minutesSince(waitFrom, now)} min</span>}
              </span>
              <span className="checks" aria-label="Documentation">
                <Check on={v.hasVitals} label="Vitals" /><Check on={v.hasNote} label="Note" /><Check on={v.coded > 0} label="Coded" />
              </span>
              <span className={`sign-state ${v.status}`}>{v.status === 'Signed' ? 'Signed' : v.paymentProgram === 'Uninsured' ? 'Open · private pay' : 'Open'}</span>
            </button>
          </li>
        )
      })}
    </ul>
  )
}

function Check({ on, label }: { on: boolean; label: string }) {
  return <span className={`check ${on ? 'on' : ''}`} title={`${label}${on ? ' recorded' : ' missing'}`}>{on ? '✓' : '·'} {label}</span>
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
