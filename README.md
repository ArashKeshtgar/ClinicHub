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
- [x] Sign-in with roles and permissions (front desk, nurse, physician, billing clerk, privacy officer, admin)
- [x] Front desk: day board, walk-in queue, check-in with simulated health card validation, rooming,
      patient flow with wait times, PHIPA access log on check-in
- [x] Synthetic demo clinic: 5 providers, 400 patients, a rolling week of appointments following the
      legacy system's arrival curve
- [x] Visits: the clinician's worklist (ready, waiting, to sign, unsigned backlog), vitals with abnormal
      values flagged, a SOAP note saved as you type, OHIP diagnostic and fee codes, signing by the treating
      provider only. Signing locks the note (corrections become addenda), creates the OHIP claim ready for
      the next batch, and invoices anything OHIP doesn't pay for; uninsured visits bill the private rate
- [ ] OHIP claims: MC EDT batch file, simulated remittance advice, reconciliation
- [ ] Orders to LabFlow and ClinicReport over FHIR, results inbox
- [ ] Master patient index: duplicate detection and merge; PHIPA consent directives
- [ ] Two-factor sign-in and user administration (ported from ClinicReport)
- [ ] Clinic operations dashboard; Docker image and public demo

## Run it

Requirements: .NET 10 SDK, Node 20+, a SQL Server you can reach with Windows authentication (or set
`ConnectionStrings__ClinicHub`).

```bash
dotnet run --project src/ClinicHub.Api        # API on http://localhost:5300; creates and seeds the ClinicHub database
```

```bash
npm --prefix web install
npm --prefix web run dev                       # UI on http://localhost:5301
```

```bash
dotnet test                                    # creates and drops a throw-away database per test class
```

Development runs the demo clinic: the sign-in page offers one-click staff roles.

## Notes on Ontario specifics

- Health card numbers are checked with the Luhn check digit; version codes are up to two letters.
- Health Card Validation (HCV) and OHIP claim submission require Ministry onboarding, so ClinicHub
  simulates them. Simulator response codes are its own, not the Ministry's.
- The seeded OHIP fee and diagnostic codes are a small **illustrative** subset with approximate fees;
  a real installation imports the current Schedule of Benefits.
- Reciprocal medical billing covers insured residents of other provinces and territories except Quebec.

All people in the demo data are synthetic.

## License

[MIT](LICENSE)
