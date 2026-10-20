# Phase 1 — Local Core Audit

- Date: 2026-10-07 · Baseline: `9bbd74a` (Gate 1) · Audited head: `28bc0df`
- Scope: the core SaaS run locally end to end (browser → Next.js → ASP.NET Core API → SQL Server), no external integrations.
- Result: **BLOCKED** on one Phase 1 criterion: attachments (P1-02). Everything else passed.
- Update (P1-02 resolution): the attachments API was implemented and verified without any schema change (see P1-02 below). No Phase 1 blocker remains; P1-03 still needs the Manager.

## 1. Environment (reproducible on Linux)

| Part | Used |
|---|---|
| Database | SQL Server 2022 (RTM-CU27) in a throw-away Docker container (`--network host`), database `ClinicSaaS_Phase1Local`; integration tests use their own `ClinicSaaS_Test_<guid>` databases, all dropped afterwards |
| Backend | .NET SDK 10.0.112, `ASPNETCORE_ENVIRONMENT=Development`, migrate on startup, dev JWT key, `http://localhost:5056` |
| Frontend | Node 22.22, `npm ci`, `next build` + `next start` (production build, Next.js 16.4.0), `http://localhost:3000` |
| Browser | Playwright 1.56 + Chromium 1194 (headless); axe-core 4 for accessibility |

```bash
docker run -d --name phase1-sql --network host -e ACCEPT_EULA=Y -e 'MSSQL_SA_PASSWORD=<local-only>' mcr.microsoft.com/mssql/server:2022-latest
# backend tests (LocalDB stays the default; this variable points them at the container)
export CLINICSAAS_TEST_SQL_SERVER='Server=localhost,1433;User ID=sa;Password=<local-only>;TrustServerCertificate=True'
dotnet test backend/ClinicSaaS.slnx
# API on an isolated local database
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5056 \
ConnectionStrings__Default='Server=localhost,1433;User ID=sa;Password=<local-only>;TrustServerCertificate=True;Database=ClinicSaaS_Phase1Local' \
Email__DevMailbox__Directory=/tmp/mailbox dotnet run --project backend/src/ClinicSaaS.Api --no-launch-profile
# frontend
cd frontend && npm ci && npm run build && npx next start -p 3000
```

`sqlcmd` needs `-I` (QUOTED_IDENTIFIER ON) for writes, because of the filtered indexes.

## 2. External integrations

| Status | Item |
|---|---|
| Real, local | SQL Server; JWT + refresh-cookie auth; RBAC; rate limiting; audit log; health checks |
| Mocked | Email: `DevMailboxEmailSender` writes HTML files (invitations were accepted from these files) |
| Disabled / absent | SMS, WhatsApp, Nafath, payments, object storage, virus scanning: no implementation exists; nothing was contacted |

## 3. What was exercised

All checks were made with real HTTP requests against the running API, or in Chromium against the running app.

| Area | Checks | Result |
|---|---|---|
| Startup, health, migrations, EF model | 4 | pass |
| Tenant A (Professional, 2 branches, 8 members in 7 roles via real invitations) and Tenant B (Free) setup | 22 | pass |
| Patients: validation, CRUD, stale `rowVersion`, pagination, search, search with pagination, page clamp, IDs | 22 | 21 pass, 1 LOW (P1-09) |
| Appointments: availability, overlap, hours, past slot, walk-in, branch rules, forged IDs, lifecycle | 30 | pass |
| Clinical: visit, clinical block, prescription, completion, doctor ownership, nurse/reception limits, cross-tenant | 63 | pass |
| Org/branch/role escalation, platform separation, JWT tampering, refresh reuse, logout | 53 | pass |
| Error handling, DB outage, CORS, headers, lockout, 429, token expiry | 25 | 1 defect fixed (P1-01), 2 LOW |
| Browser: login, 7 pages, reload, logout, role nav, create patient, search, book, check-in, Today queue | 30 | pass |
| Responsive/i18n: 8 viewports (320–2560) × 6 pages × ar/en | 96 renders | 0 overflow, correct `dir`/`lang` |
| Accessibility: axe (WCAG 2.1 AA + best practice), keyboard, focus, dialogs | — | 1 MEDIUM (P1-06) |

Not testable (feature absent): attachment upload, download, scan status and file authorization (P1-02); patient as a user (no patient role or portal exists); visit and prescription screens in the UI (P1-05).

## 4. Performance baseline (local, 10 runs, p50)

Login 124 ms (password hash included). Every list or detail endpoint ran at 2–31 ms p50 with 2–5 SQL commands per request regardless of row count, so there is no N+1. `pageSize` is capped at 100. Full browser page loads took 0.68–0.88 s with 5–9 API calls and no duplicate requests. The one outlier is `/subscription` at 18 SQL commands (P1-13).

## 5. Findings

| ID | Severity | Area | Status |
|---|---|---|---|
| P1-02 | BLOCKER (Phase 1 criterion) | Attachments | **resolved** (local storage, quarantine until scanned) |
| P1-03 | HIGH | Git / integration | open, needs the Manager |
| P1-01 | MEDIUM | API errors | **fixed** `28bc0df` |
| P1-04 | MEDIUM | SaaS plans | open (product) |
| P1-05 | MEDIUM | Frontend clinical | open (cycle 2 roadmap) |
| P1-06 | MEDIUM | Accessibility | open (frontend) |
| P1-07 | LOW | Test infrastructure | **fixed** `31947a0` |
| P1-08 … P1-14 | LOW | various | open |
| P1-15 | NICE-TO-HAVE | Documentation | open |

**P1-01: malformed JSON body returns 500** (fixed)
- Observed: `POST /api/v1/patients` with body `{"x": bad`, `not json` or `[]` returned 500 `INTERNAL_ERROR`. No internals leaked; the message was generic.
- Expected: 400 `VALIDATION_FAILED`.
- Root cause: in `Program.cs` `InvalidModelStateResponseFactory`, the root model-state key `$` became empty after `TrimStart('$','.')`, and `[0]` threw. Keys that normalise to the same field could also throw on `ToDictionary`.
- Fix: an empty key maps to `body`, and duplicates are merged. `MalformedRequestTests` failed 4 of 5 cases before the fix and passes after. Schema change: NO.

**P1-02: attachments API does not exist** (BLOCKER for the Phase 1 "Attachments / File security" criteria)
- Observed: `FileAttachments` exists (migration 003) and the `Files.View`/`Files.Upload` permissions exist in role templates, but no controller, service or storage abstraction uses them. OpenAPI lists 57 paths and none of them is a file path. Upload, download, scan status, deleted-file handling and cross-tenant or cross-branch download cannot be tested.
- Expected (Phase 1): an upload/download flow with tenant, branch and doctor authorization and private storage.
- Root cause: deliberately out of scope in T-006. ROADMAP §3 lists "attachments storage: provider, hosting location, scanning service" as needing a decision.
- Recommendation: decide the storage abstraction now (local-disk provider for development, provider chosen later), the scan policy (e.g. `Pending` blocks download until `Clean`) and the size and type limits. Then a backend task for the endpoints, a frontend task, and a QA re-test.
- Schema change required: not expected (the table exists), to be confirmed when the endpoints are designed.
- **Resolution** (approved policy: local disk for development, PDF/JPG/PNG, 10 MB). Schema change: **NO**; migration: **NO**; EF model unchanged.
  - Routes: `POST /patients/{id}/attachments`, `GET /patients/{id}/attachments`, `GET /attachments/{id}`, `GET /attachments/{id}/content` (docs/05).
  - Storage: `IFileStorage` with a `LocalDiskFileStorage` provider. Random server keys `{tenant}/{yyyy}/{MM}/{random}`, a strict key pattern plus a root-containment check, atomic write that never overwrites, `0600`/`0700` on Unix. No static-file middleware, so bytes leave only through the authorized download endpoint. The API refuses to start in Production with the local provider.
  - Validation: file signature decides the type; the extension and declared MIME must agree. Empty, over 10 MB (400), and over the request limit (413) are rejected. The file name is sanitized (path segments, control/bidi characters, reserved characters, length).
  - Authorization: `Files.Upload`/`Files.View`; tenant filter on every query; a file linked to a visit follows that visit's branch scope and the T-012 own-doctor rule; an unlinked file is clinic-wide like the patient; anything invisible is 404.
  - Scan: no scanner exists. Every file stays `Pending`, and only `Clean` is downloadable (`FileAttachment.CanDownload`), so downloads are quarantined in practice. Nothing marks a file clean; tests move the state through the domain transitions only to prove the gate.
  - Failure handling: storage errors give 503 `FILE_STORAGE_UNAVAILABLE` with no internals; a metadata save failure deletes the stored bytes.
  - Tests: 47 integration tests in `AttachmentApiTests` + 10 unit tests (`AttachmentFileNameTests`). Mutation checks: removing the quarantine gate, the branch/doctor scoping or the cleanup each fails tests (6, 2 and 1). Live Kestrel smoke on the local stack passed, including a real 413.
  - **Before production:** an approved malware-scanning service and policy (scanner sets `Scanning` → `Clean`/`Rejected`/`Failed`, with retry of `Failed`); an approved storage provider and hosting location (health-data residency); backups and retention for stored bytes; enforcement of the existing `max_storage_mb` plan limit (not enforced yet); and a delete or retention policy (no `Files.Delete` permission exists).
  - **Product decisions (2026-10-07, implemented):** (1) reading (list, metadata, download) requires `Files.View` **and** `MedicalRecords.View`; (2) an own-records doctor (no `MedicalRecords.ViewAllDoctors`) sees an unlinked file only if they uploaded it (`CreatedByUserId`); linked files keep the visit/branch/own-doctor rules; (3) categories are informational only, never an authorization boundary; (4) files may be attached to a completed visit; (5) quarantine stays fail-closed, and no application path may mark a file Clean until a trusted scanner is approved.
  - Follow-up hardening (same change): local storage is refused in every environment except Development and Testing; metadata views (`file.viewed`) and lists (`file.listed`) are audited; `file.uploaded` records `visitId` when linked. Still no schema change.

**P1-03: `origin/integration` does not contain Gate 1**
- Observed: `origin/integration` = `f191be5`. The Gate 1 commits (T-016 `9e87eaa`/`a8cfa9c`, T-015 `1426cb2`, T-017 `f8f0bec`, FE-SEC-1 `cebaaa7`, merges up to `9bbd74a`) exist only on this session's branch `claude/affectionate-goldberg-e7mfgw`.
- Expected: the aggregation branch carries the verified Gate 1 state.
- Risk: building from `origin/integration` gives Next.js 16.2.6 (1 Critical + 2 High advisories) and none of the QA-12, QA-16 or QA-17 fixes.
- Recommendation: the Manager fast-forwards `integration` to this branch (no rebase or force needed: `f191be5` is an ancestor). Schema change: NO.

**P1-04: no way to change a clinic's plan**
- Observed: new clinics start on Free (1 user, 1 doctor, 1 branch). No API or UI changes `Subscriptions.PlanId`, so multi-user and multi-branch workflows need a direct database update. That is what `ApiFactory.UsePlanAsync` does, and it is what this audit did on the local database only.
- Recommendation: a super-admin "change plan" endpoint (already listed as remaining in Phase 4). Schema change: NO.

**P1-05: no clinical screens in the frontend**
- Observed: there are no visit, prescription or patient-history pages; a doctor can only document a visit through the API. The appointment check-in and Today flows work in the UI.
- Recommendation: the doctor visit screen proposed for cycle 2. Schema change: NO.

**P1-06: the language switcher's accessible name doesn't include its visible text** (WCAG 2.5.3, axe `label-content-name-mismatch`, serious)
- Observed: `button[aria-label="Change language"]` shows "العربية" / "English" on every page. Voice-control users saying the visible word won't hit it.
- Recommendation: include the visible text in the name (e.g. `aria-label="Change language: English"`) or drop the `aria-label` and use visible text plus an icon. Schema change: NO.

**P1-07: integration tests could only run on Windows LocalDB** (fixed)
- `ApiFactory` and `VisitCompletionSchemaTests` hard-coded `(localdb)\MSSQLLocalDB`. `CLINICSAAS_TEST_SQL_SERVER` now overrides the server, and LocalDB stays the default.

**LOW**
- P1-08: the clinic phone at registration accepts Saudi mobiles only (`0112345678` is rejected), but clinics commonly use landlines. Product decision.
- P1-09: an unknown `sort` field is ignored silently (the default sort applies) instead of returning 400. It's safe (allowlist), but docs/05 implies rejection.
- P1-10: a database outage gives a generic 500 after the 30 s command timeout. Readiness took 60 s to report 503 because the health check has no timeout. Recommend a short health-check timeout.
- P1-11: the API sends `Server: Kestrel` (minor fingerprinting). Recommend `AddServerHeader = false`.
- P1-12: logout sets "signed out" (and navigates) before `POST /auth/logout` completes, so the browser aborts the request. In testing the server still revoked the session (logout 204, then refresh 401), but the order should be: await the request, then navigate.
- P1-13: `/subscription` runs 18 SQL commands (one `TenantFeatureOverrides` query per feature key). Batch it into one query.
- P1-14: each anonymous load of `/login` logs one console 401 from the silent refresh attempt. Expected behaviour, just noise.
- Confirmed known (QA-05): an access token stays valid for up to 15 minutes after the member is disabled. It's a documented trade-off.

**P1-15: documentation discrepancies** (NICE-TO-HAVE; not rewritten during the audit)
- `PROGRESS.md` "التشغيل" quotes 140 tests (now 70 unit + 193 integration). `ROADMAP.md` §1–2 still describe cycle-1 state (140/140 at `6316dca`, "Node.js not installed"). `claude.md` §3 says the frontend build is unverified.
- `backend/README.md` covers LocalDB only, with no Linux/container instructions (see §1 above). `frontend/README.md` is the create-next-app boilerplate.
- docs/05 lists `GET /branches/{id}`, but only `GET /branches` and `PUT /branches/{id}` exist.

## 6. Database

No schema change, no migration, and no pending model changes. Migrations applied locally: 001–004 only. The only data written outside the API was the documented test fixture (Tenant A `Subscriptions.PlanId` set to `professional`) on `ClinicSaaS_Phase1Local`. No production or shared database was touched.
