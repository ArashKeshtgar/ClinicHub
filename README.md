# ClinicHub

A clinic platform for **Ontario** walk-in and family practice clinics: patients and health cards,
providers and sessions, booking and walk-in queues, check-in with health card validation, the visit,
OHIP billing, and orders to the lab and diagnostic services. It is the hub of three projects:

| Project | Role |
|---|---|
| **ClinicHub** (this repo) | Patients, scheduling, check-in, encounters, OHIP claims, PHIPA access log |
| [LabFlow](https://github.com/ArashKeshtgar/LabFlow) | Lab information system: specimens, results, OLIS-style reporting |
| [ClinicReport](https://github.com/ArashKeshtgar/ClinicReport) | Structured diagnostic reports (echo, ECG) typed live during the exam |

ClinicHub owns the patient and the order; LabFlow and ClinicReport own the result. They connect over
FHIR R4 rather than sharing a database — the way separate businesses in one Ontario medical building
(walk-in clinic, lab collection site, diagnostics) actually work.

## Why it exists

ClinicHub re-designs, for Ontario, a hospital information system I worked with in Iran from 2009 to
2023 (outpatient clinic, emergency, obstetrics, IVF, inpatient). Its busiest module was the outpatient
clinic — 63% of 1.24 million visits — which is exactly the setting of an Ontario walk-in clinic. The
[mapping study](docs/totalsystem-to-ontario.md) goes module by module: what each part did, what replaces
it in Ontario (OHIP instead of 108 insurers, HCV instead of national-ID inquiries, MC EDT claims and
remittance instead of a cash desk, OLIS/HRM instead of a national EHR push, PHIPA), and which design
mistakes of the old schema the new one avoids. Only schema metadata and aggregate counts were used; no
patient data leaves the offline copy.

## Status

- [x] Mapping study: [docs/totalsystem-to-ontario.md](docs/totalsystem-to-ontario.md)
- [x] Database schema: 40 tables in 8 SQL schemas, every relationship a foreign key, CHECK constraints for
      health card format, visit states, billing rules — [docs/schema.md](docs/schema.md)
- [x] One sign-in for every clinic, **one database per clinic**: a catalog of clinics, accounts and per-clinic
      roles; the clinic is chosen after signing in and can be switched —
      [docs/architecture.md](docs/architecture.md)
- [x] Account security brought over from ClinicReport: temporary passwords, two-factor (authenticator app),
      lockout, sign out everywhere, people and per-clinic roles, security log and PHIPA access log pages;
      platform administrators open new clinics with their own database
- [x] Front desk: day board, walk-in queue, check-in with simulated health card validation, rooming,
      patient flow with wait times, PHIPA access log on check-in
- [x] Two synthetic demo clinics (Harbourview: 5 providers, 400 patients; Lakeshore: 3 providers, 250 patients),
      a physician working at both, a rolling week of appointments following the legacy system's arrival curve
- [x] Visits: the clinician's worklist (ready, waiting, to sign, unsigned backlog), vitals with abnormal
      values flagged, a SOAP note saved as you type, OHIP diagnostic and fee codes, signing by the treating
      provider only. Signing locks the note (corrections become addenda), creates the OHIP claim ready for
      the next batch, and invoices anything OHIP doesn't pay for; uninsured visits bill the private rate
- [x] OHIP billing: one MC EDT-shaped claims file per billing number (reciprocal claims included),
      simulated submission and remittance advice, import of received remittance files, line-by-line
      reconciliation with explanatory codes, provider payments net of clinic overhead, resubmission of
      refused claims — [docs/ohip-billing.md](docs/ohip-billing.md)
- [x] Orders over FHIR R4: lab tests to LabFlow, echo and ECG to ClinicReport, as transaction Bundles from each
      clinic's outbox, retried until delivered, authenticated with tokens ClinicHub signs —
      [docs/architecture.md](docs/architecture.md#orders-over-fhir)
- [ ] Results back from LabFlow and ClinicReport (DiagnosticReport) and the results inbox
- [ ] Master patient index: duplicate detection and merge; PHIPA consent directives
- [x] ClinicHub as an OpenID Connect provider: code flow with PKCE, refresh, userinfo, sign-out; tokens carry the
      clinic and only the requesting application's permissions; ClinicReport and LabFlow permissions are part
      of each clinic's roles
- [x] ClinicReport connected mode: signs in through ClinicHub, one ClinicReport database per clinic, sessions
      re-checked with ClinicHub
- [x] LabFlow signing in through ClinicHub, with one lab database or one per clinic
- [ ] Clinic operations dashboard; Docker image and public demo

## Run it

Requirements: .NET 10 SDK, Node 20+, a SQL Server you can reach with Windows authentication (or set
`ConnectionStrings__Catalog` and `ConnectionStrings__ClinicServer`).

```bash
dotnet run --project src/ClinicHub.Api        # API on http://localhost:5300; creates the catalog and one database per demo clinic
```

```bash
npm --prefix web install
npm --prefix web run dev                       # UI on http://localhost:5301
```

```bash
dotnet test                                    # creates and drops throw-away catalog and clinic databases per test class
```

Development runs the demo clinics: the sign-in page offers one-click staff roles. Dr. Patel works at both clinics and
chooses one after signing in; the administrator can open a new clinic from **Clinics**.

## Notes on Ontario specifics

- Health card numbers are checked with the Luhn check digit; version codes are up to two letters.
- Health Card Validation (HCV), OHIP claim submission and the remittance advice require Ministry
  onboarding, so ClinicHub simulates them. Simulator response and explanatory codes are its own, not the
  Ministry's, and the claim files have not been certified against the Technical Specifications.
- The seeded OHIP fee and diagnostic codes are a small **illustrative** subset with approximate fees;
  a real installation imports the current Schedule of Benefits.
- Reciprocal medical billing covers insured residents of other provinces and territories except Quebec.

All people in the demo data are synthetic.

## License

[MIT](LICENSE)
