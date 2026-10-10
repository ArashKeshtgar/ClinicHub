# Architecture: one sign-in, one database per clinic

ClinicHub is the hub of three applications (ClinicHub, [ClinicReport](https://github.com/ArashKeshtgar/ClinicReport),
[LabFlow](https://github.com/ArashKeshtgar/LabFlow)). This document is the plan they follow and where each
part of it stands. It exists because the plan was first only a diagram, and a diagram does not say what is done.

```mermaid
flowchart TB
    subgraph Hub[ClinicHub]
        Catalog[(Catalog database<br/>clinics · accounts · roles per clinic · security log)]
        H1[(ClinicHub_harbourview)]
        H2[(ClinicHub_lakeshore)]
        H3[(ClinicHub_…)]
    end
    Browser -->|one account, picks a clinic| Hub
    Hub -.->|OpenID Connect sign-in, per-app permissions| CR[ClinicReport]
    Hub -.->|OpenID Connect sign-in, per-app permissions| LF[LabFlow]
    Hub <-->|FHIR R4: orders and results| CR
    Hub <-->|FHIR R4: orders and results| LF
```

## Decisions

1. **ClinicHub is where people sign in.** Accounts, passwords, two-factor, lockout, roles and the security
   log live in ClinicHub's catalog database. ClinicReport and LabFlow will hold no passwords: they will
   accept ClinicHub's sign-in (OpenID Connect) and check permissions ClinicHub grants for them.
2. **One database per clinic.** The catalog knows the clinics and where each one's database is. Patients,
   visits, billing and the PHIPA access log stay in the clinic's own database; a query can't reach another
   clinic's patients because it runs on a different database.
3. **The clinic is chosen after signing in.** One account per person; roles are per clinic. Someone who
   works at two clinics (Dr. Patel in the demo, Wednesdays and Fridays at Lakeshore) picks one, and can
   switch. Their OHIP billing number is the same everywhere; each clinic's group number goes on its claims.
4. **ClinicReport keeps working on its own.** It is a separate open-source project with a live site, so it
   keeps its own sign-in as a *standalone* mode and gains a *connected* mode that uses ClinicHub.

## Phases

| Phase | What | Status |
|---|---|---|
| 1 | Catalog database, database per clinic, clinic picker and switcher; ClinicReport's account security brought into ClinicHub: temporary passwords, two-factor (TOTP), lockout, sign out everywhere, people and per-clinic roles, security log; platform administrators open clinics | **Done** |
| 2 | ClinicHub as an OpenID Connect provider: registered applications, per-application permissions in roles (`report.*`, `lab.*`), the clinic in the token | Not started |
| 3 | ClinicReport connected mode: sign-in through ClinicHub, its users mapped to ClinicHub accounts, standalone mode unchanged | Not started |
| 4 | LabFlow signs in through ClinicHub (today it has only a development sign-in) | Not started |

## How a request finds its database (phase 1)

- The sign-in cookie holds the account id, its security stamp and the chosen clinic id. On every request
  the account, its roles **in that clinic** and its provider link are re-read, so a role change applies at
  once and a new stamp ends every session.
- `ClinicDatabases` resolves the clinic's connection string from the catalog (cached), and the request's
  `ClinicHubDbContext` opens that database. Without a chosen clinic there are no permissions, so no clinic
  endpoint runs.
- Clinics share one SQL Server by default (`ConnectionStrings:ClinicServer`, database
  `{Clinics:DatabasePrefix}{slug}`); a clinic can instead have its own server, stored encrypted.
- Each clinic database keeps a slim copy of its members (`sec.User`: the account id and name, no
  credentials), so "who signed this visit" stays a foreign key inside the clinic's database. It is
  refreshed when someone joins, leaves or enters the clinic.
- On start-up the catalog is migrated, then every active clinic's database, and each clinic's built-in roles
  are completed (Administrator always gets every permission, including new ones).

## Who may change what

- A **clinic administrator** (permission `users.manage` in that clinic) adds people, sets their roles there,
  and edits the clinic's roles. Adding an existing user name gives that person a role here; they keep their
  own password and two-factor.
- An account's name, password and two-factor can be reset by a clinic's administrator only when that
  clinic is the only one the person works at; otherwise by the person or a platform administrator.
- A **platform administrator** opens clinics. Opening one creates its catalog entry and roles, creates and
  migrates its database, adds a first location, and makes the creator its administrator.
- Every clinic must always keep at least one person with `users.manage`.
