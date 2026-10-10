import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router'
import {
  ApiError, billing, can, fetchText, localDateTime, money,
  type BatchRow, type BillingOverview, type ClaimDetail, type ClaimRow, type ClaimStatus, type RemittanceDetail, type RemittanceRow,
} from '../api'
import { useSession } from '../App'

const STATUS: Record<ClaimStatus, string> = {
  Draft: 'Draft', Ready: 'Ready to send', Submitted: 'Sent', Paid: 'Paid', PartiallyPaid: 'Paid in part', Rejected: 'Refused',
}
const STATUS_CLASS: Record<ClaimStatus, string> = {
  Draft: 'Booked', Ready: 'Arrived', Submitted: 'WithProvider', Paid: 'Completed', PartiallyPaid: 'Roomed', Rejected: 'NoShow',
}
type Tab = 'claims' | 'files' | 'remittance'

/** The billing clerk's month: claims waiting to go, files sent, and what OHIP paid back. */
export default function Billing() {
  const { me } = useSession()
  const manage = can(me, 'billing.manage')
  const [tab, setTab] = useState<Tab>('claims')
  const [overview, setOverview] = useState<BillingOverview | null>(null)
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const [version, setVersion] = useState(0)
  const [openRemittance, setOpenRemittance] = useState<number | null>(null)

  const refresh = useCallback(() => { billing.overview().then(setOverview); setVersion(v => v + 1) }, [])
  useEffect(() => { billing.overview().then(setOverview) }, [])

  const act = async (f: () => Promise<string>, then?: Tab) => {
    setBusy(true)
    setMessage(null)
    try {
      setMessage({ ok: true, text: await f() })
      if (then) setTab(then)
      refresh()
    } catch (e) {
      setMessage({ ok: false, text: e instanceof ApiError ? [e.message, ...Object.values(e.fieldErrors).flat()].filter(m => !m.startsWith('One or more')).join(' ') : 'Failed.' })
    } finally {
      setBusy(false)
    }
  }

  if (!overview) return <p className="muted">Loading…</p>
  const o = overview

  return (
    <>
      <div className="board-head">
        <div>
          <h1>OHIP billing</h1>
          <div className="muted">Claims go out as MC EDT files; payments come back on the remittance advice. Submission and payment are simulated.</div>
        </div>
        {manage && (
          <div className="date-nav">
            <button className="primary" disabled={busy || o.unbatched.count === 0}
              onClick={() => act(async () => {
                const files = await billing.createBatches()
                return `Created ${files.length} claim file${files.length === 1 ? '' : 's'}: ${files.map(f => f.fileName).join(', ')}. Review and send them.`
              }, 'files')}>
              Create claim files{o.unbatched.count ? ` (${o.unbatched.count})` : ''}
            </button>
            <button disabled={busy || o.submitted.count === 0} title="Generate the remittance advice OHIP would send for everything submitted"
              onClick={() => act(async () => {
                const ra = await billing.simulateRemittance()
                setOpenRemittance(ra.remittance.id)
                return `Remittance ${ra.remittance.fileName}: ${money(ra.remittance.totalPaid)} paid, ${ra.remittance.exceptions} line${ra.remittance.exceptions === 1 ? '' : 's'} to review.`
              }, 'remittance')}>
              Simulate remittance
            </button>
          </div>
        )}
      </div>

      <div className="kpis">
        <Kpi color="var(--waiting)" value={o.ready.count} label="Ready to send" sub={`${money(o.ready.amount)}${o.ready.count > o.unbatched.count ? ` · ${o.ready.count - o.unbatched.count} in unsent files` : ''}`} />
        <Kpi color="var(--provider)" value={o.submitted.count} label="Sent, awaiting payment" sub={money(o.submitted.amount)} />
        <Kpi color="var(--done)" value={o.paidLast30Days.count} label="Remittances, 30 days" sub={`${money(o.paidLast30Days.amount)} paid`} />
        <Kpi color="var(--noshow)" value={o.rejected.count + o.partiallyPaid.count} label="Refused or cut" sub={`${o.rejected.count} refused · ${o.partiallyPaid.count} paid in part`} />
        <Kpi color="var(--booked)" value={o.invoicesOwing.count} label="Patient invoices owing" sub={money(o.invoicesOwing.amount)} />
      </div>

      {message && <p className={`result ${message.ok ? 'ok' : 'warn'}`} role="status">{message.text}</p>}

      <div className="tabs" role="tablist">
        {([['claims', 'Claims'], ['files', 'Claim files'], ['remittance', 'Remittance advice']] as [Tab, string][]).map(([t, label]) => (
          <button key={t} role="tab" aria-selected={tab === t} className={`tab ${tab === t ? 'on' : ''}`} onClick={() => setTab(t)}>{label}</button>
        ))}
      </div>

      {tab === 'claims' && <Claims key={version} overview={o} manage={manage} onChanged={refresh} />}
      {tab === 'files' && <Files key={version} manage={manage} busy={busy}
        onSend={b => act(async () => { await billing.submitBatch(b.id); return `${b.fileName} sent (simulated MC EDT upload).` })} />}
      {tab === 'remittance' && <Remittances key={version} manage={manage} busy={busy} open={openRemittance} onOpen={setOpenRemittance}
        onImport={(name, content) => act(async () => {
          const ra = await billing.importRemittance(name, content)
          setOpenRemittance(ra.remittance.id)
          return `Imported ${name}: ${money(ra.remittance.totalPaid)} matched to claims, ${ra.remittance.unmatched} line${ra.remittance.unmatched === 1 ? '' : 's'} unmatched.`
        })} />}
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

function Claims({ overview, manage, onChanged }: { overview: BillingOverview; manage: boolean; onChanged: () => void }) {
  const [status, setStatus] = useState<ClaimStatus | ''>('')
  const [provider, setProvider] = useState<number | undefined>()
  const [rows, setRows] = useState<ClaimRow[] | null>(null)
  const [open, setOpen] = useState<number | null>(null)

  const load = useCallback(() => billing.claims(status || undefined, provider).then(setRows), [status, provider])
  useEffect(() => { load() }, [load])

  return (
    <section className="card">
      <div className="filters">
        {(['', 'Ready', 'Submitted', 'Paid', 'PartiallyPaid', 'Rejected'] as const).map(s => (
          <button key={s} className={`chip ${status === s ? 'on' : ''}`} onClick={() => setStatus(s)}>{s ? STATUS[s] : 'All'}</button>
        ))}
        <select className="inline-select" value={provider ?? ''} onChange={e => setProvider(e.target.value ? Number(e.target.value) : undefined)} aria-label="Provider">
          <option value="">All billing numbers</option>
          {overview.providers.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
        </select>
      </div>
      <div className="table-wrap">
        <table className="data">
          <thead>
            <tr><th>Claim</th><th>Service date</th><th>Patient</th><th>Provider</th><th>Services</th><th className="num">Claimed</th><th className="num">Paid</th><th>Status</th></tr>
          </thead>
          <tbody>
            {rows?.map(c => (
              <tr key={c.id} onClick={() => setOpen(c.id)} className="clickable">
                <td><button className="link mono" onClick={() => setOpen(c.id)}>{c.accountingNumber}</button>{c.paymentProgram !== 'HCP' && <span className="badge rmb" style={{ marginLeft: '.4rem' }}>{c.paymentProgram}</span>}</td>
                <td>{c.serviceDate}</td>
                <td>{c.patient}<div className="muted small mono">{c.mrn}</div></td>
                <td className="small">{c.provider}</td>
                <td className="mono small">{c.services}</td>
                <td className="num">{money(c.amountSubmitted)}</td>
                <td className="num">{['Paid', 'PartiallyPaid', 'Rejected'].includes(c.status) ? money(c.amountPaid) : <span className="muted">—</span>}</td>
                <td>
                  <span className={`status s-${STATUS_CLASS[c.status]}`}>{STATUS[c.status]}</span>
                  {c.explanatoryCodes.length > 0 && <span className="muted small mono" title={c.explanatoryCodes.map(x => `${x}: ${overview.explanatoryCodes[x] ?? ''}`).join('\n')}> {c.explanatoryCodes.join(' ')}</span>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {rows?.length === 0 && <p className="muted pad">No claims{status ? ` with status ${STATUS[status].toLowerCase()}` : ''}. Claims appear here when a physician signs a visit.</p>}
        {!rows && <p className="muted pad">Loading…</p>}
      </div>
      {open && <ClaimDialog id={open} manage={manage} onClose={() => setOpen(null)} onChanged={() => { setOpen(null); load(); onChanged() }} />}
    </section>
  )
}

function ClaimDialog({ id, manage, onClose, onChanged }: { id: number; manage: boolean; onClose: () => void; onChanged: () => void }) {
  const { me } = useSession()
  const [d, setD] = useState<ClaimDetail | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { billing.claim(id).then(setD).catch(e => setError(e.message)) }, [id])

  return (
    <div className="overlay" role="dialog" aria-modal="true" aria-label="Claim" onClick={e => { if (e.target === e.currentTarget) onClose() }}>
      <div className="card dialog wide-dialog">
        {!d ? <p className="muted">{error ?? 'Loading…'}</p> : (
          <>
            <h2>Claim <span className="mono">{d.claim.accountingNumber}</span> <span className={`status s-${STATUS_CLASS[d.claim.status]}`}>{STATUS[d.claim.status]}</span></h2>
            <dl className="facts">
              <dt>Patient</dt><dd>{d.claim.patient} · <span className="mono">{d.claim.mrn}</span>{d.healthCard && <> · <span className="mono">{d.healthCard}</span></>}</dd>
              <dt>Program</dt><dd>{d.claim.paymentProgram === 'HCP' ? 'OHIP (HCP)' : d.claim.paymentProgram === 'RMB' ? `Reciprocal, ${d.province}` : d.claim.paymentProgram}</dd>
              <dt>Provider</dt><dd>{d.claim.provider}</dd>
              <dt>File</dt><dd className="mono">{d.claim.batchFile ?? <span className="muted">not in a file yet</span>}</dd>
            </dl>
            <table className="data">
              <thead><tr><th>Code</th><th>Date</th><th>Dx</th><th className="num">Claimed</th><th className="num">Paid</th></tr></thead>
              <tbody>
                {d.items.map((i, n) => (
                  <tr key={n}>
                    <td><span className="mono">{i.serviceCode}</span>{i.units > 1 && ` × ${i.units}`}<div className="muted small">{i.description}</div></td>
                    <td>{i.serviceDate}</td>
                    <td className="mono">{i.diagnosticCode}</td>
                    <td className="num">{money(i.feeSubmitted)}</td>
                    <td className="num">{i.feePaid === undefined ? '—' : money(i.feePaid)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {d.items.filter(i => i.explanatoryCode).map((i, n) => (
              <p key={n} className="result warn" style={{ margin: 0 }}><span className="mono">{i.explanatoryCode}</span> · {i.explanation}</p>
            ))}
            {d.claim.status === 'Rejected' && <p className="muted small" style={{ margin: 0 }}>Fix the cause first (usually the health card at the front desk), then resubmit: the claim takes the card now on file.</p>}
            {error && <p className="error">{error}</p>}
            <div className="dialog-actions">
              {can(me, 'encounters.view') && <Link className="button" to={`/visits/${d.claim.encounterId}`}>Open visit</Link>}
              {manage && d.claim.status === 'Rejected' && (
                <button className="primary" onClick={async () => {
                  try { await billing.resubmit(id); onChanged() } catch (e) { setError(e instanceof ApiError ? e.message : 'Failed.') }
                }}>Resubmit</button>
              )}
              <button onClick={onClose}>Close</button>
            </div>
          </>
        )}
      </div>
    </div>
  )
}

function Files({ manage, busy, onSend }: { manage: boolean; busy: boolean; onSend: (b: BatchRow) => void }) {
  const [rows, setRows] = useState<BatchRow[] | null>(null)
  const [viewing, setViewing] = useState<{ name: string; text: string } | null>(null)
  useEffect(() => { billing.batches().then(setRows) }, [])

  return (
    <section className="card">
      <div className="table-wrap">
        <table className="data">
          <thead><tr><th>File</th><th>Provider</th><th className="num">Claims</th><th className="num">Total</th><th>Created</th><th>Status</th><th></th></tr></thead>
          <tbody>
            {rows?.map(b => (
              <tr key={b.id}>
                <td className="mono">{b.fileName}</td>
                <td>{b.provider}<div className="muted small">billing no. <span className="mono">{b.billingNumber}</span> · batch {b.batchNumber}</div></td>
                <td className="num">{b.claimCount}</td>
                <td className="num">{money(b.total)}</td>
                <td className="small">{localDateTime(b.createdUtc)}</td>
                <td><span className={`status s-${b.status === 'Created' ? 'Arrived' : b.status === 'Submitted' ? 'WithProvider' : b.status === 'Accepted' ? 'Completed' : 'NoShow'}`}>
                  {b.status === 'Created' ? 'Not sent' : b.status === 'Submitted' ? `Sent ${b.submittedUtc ? localDateTime(b.submittedUtc) : ''}` : b.status === 'Accepted' ? 'Settled' : b.status}</span></td>
                <td className="actions">
                  <button className="small" onClick={async () => setViewing({ name: b.fileName, text: await fetchText(billing.batchFileUrl(b.id)) })}>View</button>
                  <a className="button small" href={billing.batchFileUrl(b.id)} download={b.fileName}>Download</a>
                  {manage && b.status === 'Created' && <button className="small primary" disabled={busy} onClick={() => onSend(b)}>Send</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {rows?.length === 0 && <p className="muted pad">No claim files yet. “Create claim files” makes one per billing number from the claims ready to send.</p>}
      </div>
      {viewing && <FileDialog name={viewing.name} text={viewing.text} onClose={() => setViewing(null)}
        legend="HEB batch header · HEH claim header · HER reciprocal patient · HET service item · HEE trailer with counts" />}
    </section>
  )
}

function FileDialog({ name, text, legend, onClose }: { name: string; text: string; legend: string; onClose: () => void }) {
  return (
    <div className="overlay" role="dialog" aria-modal="true" aria-label={name} onClick={e => { if (e.target === e.currentTarget) onClose() }}>
      <div className="card dialog file-dialog">
        <h2 className="mono">{name}</h2>
        <p className="muted small" style={{ margin: 0 }}>{legend}. Fixed-width 79-character records.</p>
        <pre className="file">{text}</pre>
        <div className="dialog-actions"><button onClick={onClose}>Close</button></div>
      </div>
    </div>
  )
}

function Remittances({ manage, busy, open, onOpen, onImport }: {
  manage: boolean; busy: boolean; open: number | null; onOpen: (id: number) => void; onImport: (name: string, content: string) => void
}) {
  const [rows, setRows] = useState<RemittanceRow[] | null>(null)
  const [detail, setDetail] = useState<RemittanceDetail | null>(null)
  const [viewing, setViewing] = useState<string | null>(null)

  useEffect(() => {
    billing.remittances().then(r => { setRows(r); if (open === null && r.length) onOpen(r[0].id) })
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (open !== null) billing.remittance(open).then(setDetail) }, [open])

  return (
    <div className="ra">
      <section className="card">
        <div className="panel-head">
          <h2>Received</h2>
          {manage && (
            <label className="button small upload" title="Import a remittance advice file">
              Import file…
              <input type="file" accept=".ra,.RA,.txt,text/plain" disabled={busy} onChange={async e => {
                const f = e.target.files?.[0]
                if (f) onImport(f.name, await f.text())
                e.target.value = ''
              }} />
            </label>
          )}
        </div>
        <ul className="ra-list">
          {rows?.map(r => (
            <li key={r.id}>
              <button className={open === r.id ? 'on' : ''} onClick={() => onOpen(r.id)}>
                <span><span className="mono">{r.fileName}</span>{r.simulated && <span className="badge" style={{ marginLeft: '.4rem' }}>simulated</span>}
                  <span className="muted small" style={{ display: 'block' }}>Paid {r.paymentDate} · {r.items} lines{r.exceptions ? ` · ${r.exceptions} to review` : ''}</span></span>
                <strong>{money(r.totalPaid)}</strong>
              </button>
            </li>
          ))}
          {rows?.length === 0 && <li className="muted small pad">No remittance yet. Send claim files, then simulate the remittance.</li>}
        </ul>
      </section>

      {detail && (
        <section className="card">
          <div className="panel-head">
            <h2>{detail.remittance.fileName}</h2>
            <span className="actions">
              <button className="small" onClick={async () => setViewing(await fetchText(billing.remittanceFileUrl(detail.remittance.id)))}>View file</button>
              <a className="button small" href={billing.remittanceFileUrl(detail.remittance.id)} download={detail.remittance.fileName}>Download</a>
            </span>
          </div>
          <div className="pad" style={{ display: 'grid', gap: '1rem' }}>
            <dl className="facts">
              <dt>Payment date</dt><dd>{detail.remittance.paymentDate}</dd>
              <dt>Total paid</dt><dd><strong>{money(detail.remittance.totalPaid)}</strong></dd>
              <dt>Imported</dt><dd>{localDateTime(detail.remittance.importedUtc)}</dd>
            </dl>
            {detail.messages.length > 0 && <div className="ra-messages">{detail.messages.map((m, i) => <div key={i} className="mono small">{m}</div>)}</div>}

            <div>
              <h3>By provider</h3>
              <div className="table-wrap">
                <table className="data">
                  <thead><tr><th>Provider</th><th className="num">Claims</th><th className="num">Claimed</th><th className="num">Paid</th><th className="num">Clinic overhead</th><th className="num">To provider</th></tr></thead>
                  <tbody>
                    {detail.providers.map(p => (
                      <tr key={p.providerId}>
                        <td>{p.provider}<div className="muted small mono">{p.billingNumber}</div></td>
                        <td className="num">{p.claims}</td>
                        <td className="num">{money(p.submitted)}</td>
                        <td className="num">{money(p.paid)}</td>
                        <td className="num">{money(p.overhead)} <span className="muted small">({p.overheadPercent}%)</span></td>
                        <td className="num"><strong>{money(p.net)}</strong></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>

            <div>
              <h3>To review <span className="muted small">{detail.exceptions.length ? `${detail.exceptions.length} line${detail.exceptions.length === 1 ? '' : 's'} paid differently or not matched` : 'everything paid as claimed'}</span></h3>
              {detail.exceptions.length > 0 && (
                <div className="table-wrap">
                  <table className="data">
                    <thead><tr><th>Claim</th><th>Patient</th><th>Service</th><th className="num">Claimed</th><th className="num">Paid</th><th>Why</th></tr></thead>
                    <tbody>
                      {detail.exceptions.map((x, i) => (
                        <tr key={i}>
                          <td className="mono">{x.accountingNumber}</td>
                          <td>{x.patient ?? <span className="muted">—</span>}</td>
                          <td><span className="mono">{x.serviceCode}</span> <span className="muted small">{x.serviceDate}</span></td>
                          <td className="num">{money(x.submitted)}</td>
                          <td className="num">{money(x.paid)}</td>
                          <td className="small">{x.explanatoryCode && <span className="mono">{x.explanatoryCode} · </span>}{x.explanation}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          </div>
          {viewing && <FileDialog name={detail.remittance.fileName} text={viewing} onClose={() => setViewing(null)}
            legend="HR1 payment header · HR4 claim · HR5 service item with amount paid and explanatory code · HR8 message" />}
        </section>
      )}
    </div>
  )
}
