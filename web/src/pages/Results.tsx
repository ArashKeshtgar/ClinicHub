import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router'
import { firstError, localDateTime, results, type ResultRow } from '../api'

/** Results back from LabFlow and ClinicReport, waiting for the ordering clinician to acknowledge them. */
export default function Results() {
  const [all, setAll] = useState(false)
  const [reviewed, setReviewed] = useState(false)
  const [rows, setRows] = useState<ResultRow[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => results.inbox(all, reviewed).then(setRows).catch(e => setError(e.message)), [all, reviewed])
  useEffect(() => { load() }, [load])

  const acknowledge = async (r: ResultRow) => {
    try { await results.review(r.id); load(); window.dispatchEvent(new Event('clinichub:results-changed')) }
    catch (e) { setError(firstError(e, 'Not acknowledged.')) }
  }

  return (
    <>
      <div className="board-head">
        <div>
          <h1>Results</h1>
          <div className="muted">Lab results from LabFlow and reports from ClinicReport for orders placed here. Abnormal first.</div>
        </div>
      </div>
      <div className="filters" style={{ borderRadius: 'var(--radius)', marginBottom: '1rem', border: '1px solid var(--border)' }}>
        <button className={`chip ${!all ? 'on' : ''}`} onClick={() => setAll(false)}>My orders</button>
        <button className={`chip ${all ? 'on' : ''}`} onClick={() => setAll(true)}>Whole clinic</button>
        <span style={{ width: '1rem' }} />
        <button className={`chip ${!reviewed ? 'on' : ''}`} onClick={() => setReviewed(false)}>To acknowledge</button>
        <button className={`chip ${reviewed ? 'on' : ''}`} onClick={() => setReviewed(true)}>Acknowledged</button>
      </div>
      {error && <p className="error">{error}</p>}
      {rows?.length === 0 && <p className="muted">{reviewed ? 'Nothing acknowledged yet.' : 'Nothing waiting. Results appear here when LabFlow or ClinicReport sends them.'}</p>}
      <div className="results">
        {rows?.map(r => (
          <article key={r.id} className={`card result-card ${r.isAbnormal ? 'abnormal' : ''}`}>
            <header>
              <div>
                <strong>{r.order}</strong>
                <span className="muted small"> · {r.target === 'ExternalLab' ? 'External lab' : r.target} · {localDateTime(r.receivedUtc)}</span>
                <div className="small">{r.patient} <span className="mono muted">{r.mrn}</span> · ordered by Dr. {r.requester}</div>
              </div>
              <div className="badge-row">
                {r.isAbnormal && <span className="badge none">Abnormal</span>}
                <span className={`badge ${r.status === 'Final' ? 'ohip' : 'rmb'}`}>{r.status}</span>
              </div>
            </header>
            {r.withheld && <p className="muted small" style={{ margin: 0 }}>Withheld at the patient's request (consent directive). Open the visit to override in an emergency.</p>}
            {r.summary && <pre className="result-text">{r.summary}</pre>}
            <footer>
              <Link to={`/visits/${r.encounterId}`}>Open visit</Link>
              {r.externalUrl && <a href={r.externalUrl} target="_blank" rel="noreferrer">Full report ↗</a>}
              {r.reviewedUtc
                ? <span className="muted small">Acknowledged {localDateTime(r.reviewedUtc)}{r.reviewedBy ? ` by ${r.reviewedBy}` : ''}</span>
                : !r.withheld && <button className="primary small" onClick={() => acknowledge(r)}>Acknowledge</button>}
            </footer>
          </article>
        ))}
      </div>
    </>
  )
}
