# From an Iranian hospital HIS to an Ontario clinic platform

**Status:** mapping study, October 2026. **Author:** Arash Keshtgar.

This document maps a legacy hospital information system I worked with in Iran ("Totalsystem", SQL
Server, in production 2009–2023) onto how the same work is done in an Ontario community clinic, and
defines what **ClinicHub** — a portfolio project — will build. It is the design input for ClinicHub and
for connecting it to two existing projects: [LabFlow](https://github.com/ArashKeshtgar/LabFlow) (lab
information system) and [ClinicReport](https://github.com/ArashKeshtgar/ClinicReport) (structured
diagnostic reporting).

> **Data rule.** The legacy database contains real patients. Everything here comes from schema metadata
> and aggregate counts run on an offline copy; no record-level data was read into this document, and none
> will leave that machine. ClinicHub's demo data is synthetic, shaped by the aggregate patterns below.

---

## 1. The legacy system at a glance

| | |
|---|---|
| Scope | One private hospital: emergency, outpatient clinic ("darmangah"), obstetrics, IVF, about 20 inpatient and procedure units, OR, cath lab, lithotripsy, dialysis, endoscopy |
| Size | 873 tables (**340 hold data**, 533 are empty), 425 stored procedures, 176 views, ~19 GB |
| Volume | ~100,000 outpatient/ED visits a year at its peak (1.24 M visits 2009–2023), ~20,000 admissions a year, 374,000 person records, 5.7 M national-insurance inquiry rows |
| Integrity | **No foreign keys** — every relationship lives in application code |
| Types | Dates are `varchar` Jalali strings (values from year 0398 to 1888 exist); money is `float`; text is mostly `varchar` in an Arabic code page |
| Identity | Patient demographics are copied into each visit row; 374,000 person rows carry only 144,000 distinct national codes |

### Encounter mix (outpatient side, all years)

| Centre | Visits | Share |
|---|---:|---:|
| Outpatient clinic (darmangah) | 788,868 | 63% |
| Emergency | 354,412 | 29% |
| Obstetrics | 90,040 | 7% |
| IVF | 9,085 | 1% |

Arrivals peak 08:00–11:00 and again 15:00–18:00 (morning and afternoon clinic sessions); the emergency
department carries the night. In the last full year the most frequent billed items were sub-specialist
visits, anaesthesia pre-op clinic visits, outpatient nursing care, injections and IV starts, specialist
visits, Pap smears, ECGs, and obstetric NST/CTG monitoring. Triage used five levels (most patients level
3, then 2), the same structure as CTAS.

**Takeaway:** the busiest part of this "hospital" system was a multi-specialty walk-in clinic with
nursing procedures and diagnostics — exactly the setting ClinicHub targets in Ontario.

---

## 2. Module map

| Legacy module (main tables) | What it did | Ontario equivalent | Where it goes |
|---|---|---|---|
| Person index (`informat`, `Centralized_Admission`) | One row per person, national code, insurer | **Master patient index**: OHIP health card number + version code, MRN, demographics, matching and merge | ClinicHub core |
| Outpatient/ED reception (`OJ_PRV`, `OJ_Parvande`) | Visit registration with insurer, doctor, payer shares | **Encounter / check-in** with health card validation (HCV) | ClinicHub core |
| Clinics and doctors (`Doctors`, `Parts`, `Tbl_DoctorInOjCenter`) | Doctor list, rooms, daily visit caps, bank account for payouts | **Providers** (CPSO number, OHIP billing number, specialty), locations, rooms | ClinicHub core |
| Visit caps, IVF and special queues (`Doctors.maxnobat`, `IvfTurn`, `Avab_specialWork_turn`) | Daily ticket numbers per doctor | **Scheduling**: appointment slots, walk-in queue, online booking | ClinicHub core |
| Services and tariffs (`OJ_Actions`, `CaliforniaCodes`, `Works`, `Bw`) | Iranian RVU book with K-coefficients; price per insurer | **OHIP Schedule of Benefits** fee codes (e.g. A007 intermediate assessment) plus a clinic price list for uninsured services | ClinicHub billing |
| Insurers and shares (`Bimeh` — 108 insurers, `OJ_UseActions` shares, franchise %, supplementary insurer) | Split every line into insurer / supplementary / patient shares | **OHIP** pays insured services in full (no co-pay); **uninsured** services are invoiced to the patient or a third party (WSIB, employer, private plan) | ClinicHub billing |
| National EHR and eligibility (`Tbl_SPS_*`, `W_thritaEHR_*`, `HID_Generate`) | SEPAS submissions, insurance eligibility inquiries | **HCV** for eligibility; **OLIS** for lab results, **HRM** to deliver reports to family doctors, **ConnectingOntario** viewer; FHIR R4 (CA Core+) | Interfaces |
| Cashier (`cashok`, `Pregive`, `TBL_PosTransaction`, `BillSafe`) | Receipts for every visit, deposits, card terminal | Receipts for uninsured services only; card payments; **OHIP claim submission (MC EDT) and remittance advice reconciliation** replace most cashier work | ClinicHub billing |
| Doctor payouts (`Doctors.saham`, `AccPayToDr`, `Acc_*`) | Hospital collects, pays doctors a share | Physicians are paid by OHIP directly; the clinic charges an **overhead percentage** — a report, not a ledger | ClinicHub reports |
| Diagnoses (`Disease`, `W_thritaEHR_ICD10`, ICPC-2) | ICD-10 on visits and discharges | **OHIP diagnostic codes** (3-digit) on claims; SNOMED CT CA on the problem list; ICD-10-CA is hospital-side | ClinicHub core |
| Triage (`Tbl_Teryaj`, `Triage_PRV`) | Five-level triage, vitals, chief complaint | **CTAS** 1–5 (urgent-care clinics) | ClinicHub, optional |
| Consult requests (`tbl_moshavereh` — 569,000 rows) | In-house specialist consults | **Referrals and eConsult** (Ocean eReferral, Ontario eConsult) | ClinicHub, later |
| Clinic drugs and supplies (`OJ_Drug`, `OJ_UseDrug`) | Mostly consumables used in the clinic (swabs, gloves, IV sets) charged per visit | Supplies are overhead, not billed; in-clinic injections recorded on a medication administration record; prescriptions via **PrescribeIT**; dispensing is a separate pharmacy (ODB) | Out of scope (MAR later) |
| Lab and imaging orders (`OJ_Center.HasLabUse/HasRadioUse`, `ParaClinic*`) | Paraclinical requests | Orders to the lab (OLIS) and to diagnostic services | **LabFlow**, **ClinicReport** via FHIR |
| ECG, echo, NST/CTG (`OJ_Actions`, legacy Echo app) | Billed items; reports typed in a separate tool | Structured reports signed by a physician | **ClinicReport** |
| Inpatient (`turn` — ~400 columns, `bed`, `besbed`, `Tbl_Diet`, `tbl_bedreq`) | Admissions, beds, diet, discharge accounting | Hospital systems (Epic, Meditech) | Out of scope |
| Operating room (`OpRoomWork*`, RVU tables) | OR billing and staffing | Hospitals / community surgical centres | Out of scope |
| Users and access (`Users`, `Access`, `User_Group`, `Tbl_Users_LoginLog`) | Menu-level rights, login log | Role-based access, two-factor sign-in, **PHIPA** access audit | ClinicHub (same model as ClinicReport) |

---

## 3. Concept changes that matter

1. **One public payer instead of 108 insurers.** The legacy billing engine (franchise percentages,
   supplementary insurers, per-insurer tariffs, patient share, deductions) collapses. Insured services
   go to OHIP as claims; what remains is uninsured services (sick notes, forms, cosmetic procedures,
   out-of-province or uninsured patients) and third parties such as WSIB.
2. **Billing is a claims cycle, not a cash desk.** Claims are batched in the MC EDT format, the
   remittance advice comes back monthly, and rejected claims are corrected and resubmitted. The
   cashier module's job becomes reconciliation.
3. **Identity is the health card.** A health card number has a check digit and a version code, and
   eligibility is confirmed through HCV at check-in. Patients without OHIP coverage are expected and
   need a clear self-pay path.
4. **No national EHR "push" for everything.** Instead of one SEPAS submission, specific feeds go to
   specific places: labs to OLIS, reports to the family doctor via HRM, referrals through eReferral.
   ClinicHub talks FHIR R4 internally and leaves those provincial gateways as documented adapters.
5. **Privacy law is PHIPA.** The clinic is a health information custodian: every view of a patient's
   record is logged, access follows "need to know", and patients can ask who looked at their record.
6. **The clinic is a set of businesses.** In a typical Ontario medical building, the walk-in clinic, the
   lab collection site, the pharmacy and diagnostic imaging are separate organisations. ClinicHub
   therefore integrates separate systems instead of owning one database.

---

## 4. Lessons from the legacy schema → design rules for ClinicHub

| Legacy problem | ClinicHub rule |
|---|---|
| No foreign keys; 533 empty tables | Every relationship is a foreign key; a table exists only with a feature that uses it |
| Jalali dates as strings, impossible years | `date` / `datetime2` in UTC, displayed in America/Toronto |
| Money as `float` | `decimal(10,2)`, amounts in cents in claim files |
| Demographics copied into every visit; 374,000 person rows for 144,000 distinct national codes | One patient record; encounters reference it; duplicate detection and merge with an audit trail |
| 400-column admission table | Narrow tables per concern; JSON only for genuinely variable data |
| Patient names and national codes repeated in cashier and triage tables | PHI lives in one schema; other modules hold the patient id only |
| Rights per menu item | Permissions per action, roles made of permissions, per-service scope (ClinicReport's model) |
| Everything in one database | Separate services joined by FHIR and shared sign-on |

---

## 5. Target architecture

```
                         ClinicHub (Ontario walk-in / multi-specialty clinic)
     patients + health cards · providers · scheduling · check-in (HCV) · encounters · OHIP claims
                 uninsured invoices · referrals · reports · users, roles, PHIPA audit
                        │                                   │
              FHIR ServiceRequest                 FHIR ServiceRequest
              ←  DiagnosticReport                  ← DiagnosticReport
                        │                                   │
                    LabFlow                            ClinicReport
          (lab: specimens, results, OLIS)      (echo, ECG, other structured reports)
```

All three keep their own database; ClinicHub owns the patient and the order, the other two own the
result. Users sign in once.

## 6. ClinicHub version 1 — scope

1. **Patients**: OHIP health card (check digit, version code), MRN, demographics, duplicate detection.
2. **Providers and locations**: CPSO and OHIP billing numbers, specialty, rooms, session templates.
3. **Scheduling**: booked appointments and a walk-in queue, shaped on the legacy arrival curve.
4. **Check-in**: simulated HCV eligibility response; uninsured path.
5. **Encounters**: reason for visit, OHIP diagnostic code, fee codes, notes.
6. **OHIP claims**: batch file generation in the MC EDT layout and a simulated remittance advice with
   reconciliation (claim formats to be verified against the current Ministry technical specifications).
7. **Orders**: lab orders to LabFlow and ECG/echo orders to ClinicReport over FHIR; results return to the
   encounter.
8. **Security**: roles and permissions, two-factor sign-in, PHIPA access log.
9. **Synthetic data**: a generator that reproduces the legacy volume profile (visit mix, hourly curve,
   service mix) with fictional Ontario patients.
10. **Reporting**: clinic operations dashboard (visits, wait times, OHIP billed vs. paid, rejections).

Out of scope for version 1: inpatient care, OR, pharmacy dispensing, real connections to provincial
systems (they require Ministry and OntarioMD onboarding).
