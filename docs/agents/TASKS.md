> **Historical** — task log of the Phase 1 agent cycles. Phase, gates and current status live only in
> [`docs/STATUS.md`](../STATUS.md); numbers below are as of each merge and are not maintained.

# Agent Task Board

Maintained by the Manager on the `integration` branch only. Agents read it with `git show integration:docs/agents/TASKS.md`.

Status: `todo` · `in-progress` · `review` (agent reported done) · `merged` (in integration, verify passed) · `blocked`

| ID | Owner | Task | Scope (paths) | Depends on | Acceptance criteria | Status |
|---|---|---|---|---|---|---|
| T-001 | database | Migration 003 ClinicalRecords + stored-TenantId write guard (commit 651811a from branch `backend-agent`, merged into `agent/database` on 2026-10-06) | `backend/src/ClinicSaaS.{Domain,Infrastructure}/**`, `backend/tests/**`, `docs/database/**`, `docs/04-security-model.md` | none | `verify.ps1 -Scope backend` passes in the database worktree; cross-tenant isolation tests pass; QA review of the TenantId concurrency-token change; no migration applied to any non-test DB | merged (b89f6ef, backend verify 140/140 on 2026-10-06; QA review → T-002) |
| T-002 | qa-security | Independent review of T-001 (TenantId concurrency token); audit tenant isolation, IDOR/BOLA, RBAC; audit branch restriction of doctors (F2: audit + report only, behaviour change needs a product decision) | `backend/tests/**` (new files only: `QaSecurityAuditTests.cs`, …), `docs/security/**` | none | Findings with severity, file:line, reproduction, owner; regression tests for each confirmed defect; failing tests listed explicitly; no existing test weakened | merged (3bc4b51; backend verify on ef9e390: unit 64/64, integration 127 passed + 2 KnownDefect skipped, EF no pending changes) |
| T-003 | backend | F1: a walk-in's start time is set by the server. `StartUtc` on a `WalkIn` create → 400 `VALIDATION_FAILED` (`startUtc`). Regression test written red first, then green | `backend/src/ClinicSaaS.Application/Appointments/**`, `backend/tests/ClinicSaaS.IntegrationTests/WalkInRulesTests.cs`, `docs/05-api-reference.md` | none | Red→green evidence; walk-in still gets T-NN token at server "now"; regular bookings keep working-hours + overlap rules; concurrency tests still pass; frontend already sends `startUtc: null` (no consumer break) | merged (46461d4 via ef9e390; red→green: 2/4 failed before fix; backend verify passed as above) |
| T-004 | frontend | Node.js check, then lint/typecheck/tests/build; fix issues; RTL/LTR, responsive and accessibility review | `frontend/**` | Node.js on PATH | `verify.ps1 -Scope frontend` passes (documented output) | merged (dfdf15a via a140546; lockfile regenerated with --package-lock-only, no version changes) |
| T-005 | frontend | Role-based "Today" views (reception queue, doctor's day, owner summary) using existing API contracts only | `frontend/**` | T-004 | No API contract change; ar default + en; empty/loading/error states; verify passes | merged (ceda2d9 via a140546; E2E Playwright 36/36 steps on agent worktree) |
| T-006 | backend | Visits + Prescriptions (+ items) API on the Migration 003 schema. No migration, no table/column/index/constraint change. Attachments out of scope | `backend/src/ClinicSaaS.{Application,Api}/**`, `backend/tests/**` (new files), `docs/05-api-reference.md` | T-003 (same worktree, sequential) | Permissions (`Visits.*`, `Prescriptions.*`, `MedicalRecords.View` for clinical content); cross-tenant + branch isolation tests; record linkage validated (patient/doctor/appointment/visit same tenant & consistent); `has-pending-model-changes` = none; verify passes | merged (8ad32b0 via ef9e390; 23 new tests; backend verify passed as above) |
| T-007 | manager | Sync `TASKS.md`, `PROGRESS.md`, `claude.md` with verified reality; create `docs/agents/ROADMAP.md` (existing / implemented / proposed) | `docs/agents/**`, `PROGRESS.md`, `claude.md` | none | Only verified numbers; unverifiable items marked as such | review (docs synced; ROADMAP created — finalised after cycle merges) |

| T-008 | backend | QA-03: clinic tokens never carry platform permissions; `/platform` unreachable with a clinic token (login auto-select, switch-tenant, refresh) | `backend/**` | none | Regression tests: clinic token → 403 on `/platform/*`; platform session still works; existing tests intact | merged (via bd8e002) |
| T-009 | backend | QA-01: branch-restricted manager cannot invite/edit staff with branches outside own scope or all-branches; also QA-04 (cannot change branch restriction of a more-privileged member) | `backend/**` | T-008 (same worktree) | QA-01 KnownDefect tests become `[Fact]` and pass unchanged; new regression tests | merged (via bd8e002) |
| T-010 | backend | QA-02: existing account joins only via explicit invitation + acceptance; `POST /staff` response identical for registered/unregistered email (no enumeration, no foreign profile data) | `backend/**`, `docs/05-api-reference.md` | T-009 | API contract change documented; tests prove identical responses and no membership before acceptance; STOP if schema insufficient | merged (via bd8e002) |
| T-011 | backend | F2 per product decision: doctors + working hours branch-contextual for restricted members; patients stay clinic-scoped; services stay clinic-wide (no branch column → branch availability not possible without schema, documented) | `backend/**` | T-010 | Restricted member: other-branch doctor 404, schedule edits limited to own branches; tests | merged (via bd8e002) |
| T-012 | backend | T-006 rules: doctor users see/create only own visits & prescriptions unless they hold a new explicit permission; prescription read requires `MedicalRecords.View`; visit creation never completes the appointment | `backend/**` | T-011 | Tests for own-vs-other doctor, permission override, prescription read gating | merged (via bd8e002) |
| T-013a | database | Migration 004 (approved 2026-10-06, additive): Visits.Status (Open/Completed, default Open) + CompletedAtUtc + CompletedByUserId; domain transition; changelog + schema.md + idempotent SQL | `backend/src/ClinicSaaS.{Domain,Infrastructure}/**` (Visit only), `backend/tests/**` (new files), `docs/database/**` | none | Up→Down→Up on a test DB; no drop/rename/data change; not applied to any non-test DB | merged (c4d192a via 19e451b; backend verify: unit 70/70, integration 133 + 2 skipped, EF no changes; round-trip Up→Down→Up on throw-away DB passed) |
| T-013b | backend | `POST /visits/{id}/complete`: closes visit (locks clinical edits) + completes linked appointment in one transaction | `backend/src/ClinicSaaS.{Application,Api}/**`, tests | T-012, T-013a merged | Tests incl. permissions, branch/doctor rules, already-completed, no appointment | merged (ba52537 via 4afa59b) |
| T-015 | frontend | Adapt to merged contracts: accept-invitation for existing accounts (sign in then accept, `INVITATION_SIGN_IN_REQUIRED`, optional newPassword), masked pending staff seats + `Invited` status, platform-user login (no auto clinic entry), new 403s for branch-restricted staff/doctor/branch actions | `frontend/**` | bd8e002 | Gate green; E2E of invitation flows (new + existing account) in ar/en | in-progress — **QA-17 part merged** (1426cb2 via d9aaca3; verified by T-017 incl. Chromium E2E). Invitation flows / new 403s still open |
| T-016 | backend | QA-12 (bypass of approved QA-02 fix): disabling/revoking a pending seat must never unmask the invited account (GET, list, search). No schema change unless approved — STOP and report if required | `backend/**` | 7fdbc10 | QA-12 KnownDefect test → [Fact], unchanged body, passes; no regression. Extended: QA-16 (visit for a doctor hidden by branch) | merged (9e87eaa QA-16, a8cfa9c QA-12 via 7eef681; no schema change — acceptance read from the clinic-scoped audit log, DB-REQ-1 remains the long-term fix) |
| T-014 | qa-security | Independent re-test of T-008..T-012 on integration; adversarial tests | `backend/tests/**` (new files), `docs/security/**` | T-008..T-012 merged | Report + tests | merged (1df2fe7 via 7fdbc10; new defects QA-12, QA-17 as KnownDefect) |
| T-017 | qa-security | Re-test of T-016 (QA-12, QA-16) and the QA-17 part of T-015; regression, isolation, branch authz, redirect security, schema status | `backend/tests/**` (new files), `frontend/**/*.test.ts` (new files), `docs/security/**` | 7eef681, d9aaca3 | Report + tests | merged (f8f0bec via 98bf187; QA-12/16/17 verified fixed incl. real-browser proof; new QA-18 Low) |

## File-conflict plan (cycle 1)

- `agent/backend` runs T-003 then T-006 sequentially (one worktree, one agent). T-003 is committed separately so it can merge first.
- QA adds **new** test files only; it does not edit `AppointmentTests.cs` or other existing test files that backend may touch.
- `docs/05-api-reference.md` is edited by backend only; the Manager does not touch it in T-007.
- Merge order: backend (T-003) → backend (T-006) → qa-security (T-002), full verify after each.

## Cycle 1 merge log

| Merge | Content | Gate result |
|---|---|---|
| 3bc4b51 | agent/qa-security (T-002) | Backend tests 64/64 + 100 passed / 2 skipped. EF check failed to start (`dotnet` not on PATH) → fixed in aa3f5a2 |
| ef9e390 | agent/backend (T-003, T-006) | Backend: build OK, unit 64/64, integration 127 passed + 2 skipped (QA-01 KnownDefect), EF: no pending model changes. Frontend: `npm ci` fails — `frontend/package-lock.json` out of sync with `package.json` (`@emnapi/*`), pre-existing on integration, owned by T-004 |
| 19e451b | agent/database (T-013a, migration 004 VisitCompletion — additive) | Backend: unit 70/70, integration 133 passed + 2 skipped, EF: no pending model changes. ALL CHECKS PASSED (backend). Not applied to any non-test DB |
| a140546 | agent/frontend (T-004, T-005) | **Full gate ALL CHECKS PASSED (all)**: backend unit 70/70, integration 133 + 2 skipped, EF no changes; frontend npm ci (461 pkgs), lint 0 problems, typecheck OK, vitest 79/79 (6 files), build OK |
| bd8e002 | agent/backend (T-008..T-012) | **Full gate ALL CHECKS PASSED (all)**: unit 70/70, integration 161/161 (0 skipped — QA-01 KnownDefect tests now [Fact] and passing), EF no changes; frontend 79/79, lint/typecheck/build OK |
| 4afa59b | agent/backend (T-013b complete visit) | **Full gate ALL CHECKS PASSED (all)**: unit 70/70, integration 169/169 (0 skipped), EF no changes; frontend 79/79, lint/typecheck/build OK |
| 7fdbc10 | agent/qa-security (T-014 re-test) | **Full gate ALL CHECKS PASSED (all)**: unit 70/70, integration 179 passed + 1 skipped (QA-12 KnownDefect), EF no changes; frontend 90 passed + 4 skipped (QA-17 KnownDefect), lint/typecheck/build OK |
| 7eef681 | agent/backend (T-016: QA-16, QA-12) | **Full gate ALL CHECKS PASSED (all)** (Linux container: SQL Server 2022 throw-away container, npm 11): unit 70/70, integration 182/182 (0 skipped), EF no changes; frontend 90 passed + 4 skipped (QA-17 not merged yet), lint/typecheck/build OK |
| d9aaca3 | agent/frontend (T-015 QA-17 part) | **Full gate ALL CHECKS PASSED (all)**: unit 70/70, integration 182/182, EF no changes; frontend **112/112** (0 skipped), lint/typecheck/build OK |
| 98bf187 | agent/qa-security (T-017 re-test) | **Full gate ALL CHECKS PASSED (all)**: unit 70/70, integration 188/188, EF no changes; frontend 115/115, lint/typecheck/build OK |
| f386b08 | agent/frontend (FE-SEC-1: next + eslint-config-next 16.2.6 → 16.4.0, owner-approved) | **Full gate ALL CHECKS PASSED (all)**: unit 70/70, integration 188/188, EF no changes; frontend 115/115, lint/typecheck/build OK; Chromium runtime smoke (login, next redirect, 7 pages) + QA-17 redirect E2E pass. `npm audit --omit=dev`: 0 |

## Open findings from T-002 (need decisions — not scheduled)

| ID | Severity | Summary | Owner |
|---|---|---|---|
| QA-01 | Medium | Branch-restricted manager can grant wider/unrestricted branch access when inviting/editing staff. Confirmed; 2 `KnownDefect` tests (skipped by default) | backend (fix proposed for cycle 2) |
| QA-02 | Medium | Existing account attached by email without consent; response exposes profile fields (enumeration) | product + backend |
| QA-03 | Medium | Platform user with one clinic membership gets a clinic token carrying platform permissions → reaches `/platform` (contradicts docs/04 §3) | backend |
| QA-04 | Low | Staff manager can change branch restriction of a more-privileged member | backend |
| QA-05 | Low | Access token valid ≤15 min after disable/suspend (documented trade-off) | decision |
| QA-06 | Low | `ACCOUNT_LOCKED` reveals registered emails; lockout DoS | decision |
| F2 | Decision | Doctors/schedules/services/patients not branch-scoped (conflicts with claude.md §6) | product |
| T-006-1 | Decision | Recording a visit does not complete the appointment | product |
| T-006-2 | Decision | Any doctor can be named on a visit/prescription (no "own records" rule) | product |
| T-006-4 | Decision | Prescription content needs `Prescriptions.View` only, not `MedicalRecords.View` | product |
| ~~FE-SEC-1~~ | High (decision) | **Closed** (cebaaa7 via f386b08, owner-approved upgrade to next/eslint-config-next 16.4.0): production dependencies 0 vulnerabilities (was 1 Critical + 2 High). **Remaining dev-only (DEV-SEC-1, accepted, re-check on every upgrade):** 5 High entries = one advisory, braces GHSA-vfj7-8cjw-p6xm (ReDoS-style stack exhaustion on deeply nested glob patterns) via eslint-config-next → @next/eslint-plugin-next → fast-glob 3.3.1 pin; no upstream fix (braces 3.0.3 is latest); lint-time only on the repo's own globs, not shipped | frontend |
| API-REQ-1 | Product | Doctor cannot map self to doctor profile: `/auth/me` lacks membershipId/doctorId (frontend falls back to a one-time choice stored in localStorage — convenience only) | backend |
| API-REQ-2 | Product | `AppointmentDto` lacks `checkedInAtUtc` → no real waiting time in the reception queue | backend |
| DATA-1 | Medium (decision) | Stored roles of existing tenants lack `MedicalRecords.ViewAllDoctors`: an owner who is also a doctor now sees only own visits/prescriptions until a data update grants it to stored `tenant_owner`/`clinic_manager` roles. Only dev/test data exists today | database (data script, needs approval) |
| DB-REQ-1 | Low (optional) | `VerificationCode.MembershipId` + `TenantMembership.AcceptedAtUtc` would allow precise invite revocation and re-enable without re-consent | database (schema, needs approval) |
| ~~QA-17~~ | Medium | **Closed** (T-015 1426cb2, verified by T-017 incl. Chromium E2E). Open redirect after login: `/login?next=/%09/evil.example` | frontend |
| QA-14 | Medium (decision) | Non-doctor (owner/clinic_manager) can issue a prescription in any doctor's name and link it to another doctor's visit (`PrescriptionService.cs:114-128`) | product → backend |
| QA-15 | Low (decision) | Completed visit can still receive linked prescriptions (T-013b chose to allow) | product |
| ~~QA-16~~ | Low | **Closed** (T-016 9e87eaa, verified by T-017). Restricted member could record a visit for a doctor hidden from their branches | backend |
| ~~QA-12~~ | Medium | **Closed** (T-016 a8cfa9c, verified by T-017). Disabling a pending invitation revealed the invited account | backend |
| QA-18 | Low (decision) | Restricted member can issue a prescription (no visit) in the name of a doctor hidden from their branches (`PrescriptionService.cs:114`); prescriptions have no branch, so the rule needs a decision. Related to QA-14. `CurrentBehaviour_QA18_*` | product → backend |
| QA-13 | Low (pre-existing) | `POST /auth/register` → `EMAIL_TAKEN` enables unauthenticated enumeration | product → backend |
