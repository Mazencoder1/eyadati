# Project status

This is the **only** status and phase document for the Clinic SaaS. Other documents link here instead of
repeating numbers. Update it at the end of every gate; record only results that were actually executed.

- Last updated: 2026-10-08 — **Phase 3C (security operations and readiness): PASS WITH FINDINGS** — monitoring hooks
  (security events with stable ids, JSON logs), GD-04 and the multi-tab session loss (P3-SEC-25) fixed, technical UAT,
  go-live checklist. **Everything left before go-live is external** (hosting/residency + legal review, email provider,
  backups/restore, infrastructure, key-ring custody, monitoring/on-call, penetration test). Nothing deployed.
  Earlier: platform-admin MFA (migration 005, applied to throw-away databases only), Phase 3B (production configuration
  guards, proxy trust, log safety, SQL resilience), Phase 3A (GD-03, GD-15, refresh-token/XSS), Phase 2 MVP acceptance gate
- The product is **not production-ready** and makes **no compliance claim** (PDPL, NCA, MOH, ZATCA, SCFHS).
  Hosting, data residency and the legal review belong to the Production/Cloud phase.

## Phases

| Phase | Scope | Status |
|---|---|---|
| 1 — Local core audit | Run the core SaaS locally end to end; fix blockers (attachments API, malformed-JSON 500) | **Closed** |
| 2 — Clinical loop, local MVP | Make the product usable end to end locally: clinical workflow, SaaS controls, email boundary, repository and CI hygiene | **Active** |
| 3+ — Production / cloud | Hosting and data residency, real email provider, storage + malware scanning, backups, monitoring, deployment | **3A done** (owner decisions recorded; session revocation and browser refresh-token hardening implemented; platform-admin MFA implemented afterwards, see §19.5). **3B done** (production configuration guards, proxy trust, log safety, SQL resilience, operations docs — nothing deployed). **3C done** (monitoring hooks, GD-04, multi-tab session fix, UAT, go-live checklist — nothing deployed; record: [§21](PHASE3-PRODUCTION-PLAN.md#21-phase-3c--security-operations-and-readiness-2026-10-08)). Infrastructure phases (3D email adapter onwards) wait for the owner's provider decisions; checklist: [`operations/go-live-checklist.md`](operations/go-live-checklist.md). Plan: [`PHASE3-PRODUCTION-PLAN.md`](PHASE3-PRODUCTION-PLAN.md), record: [§19](PHASE3-PRODUCTION-PLAN.md#19-phase-3a-decisions-and-security-hardening-2026-10-08) |

Earlier documents used a 15-step numbering ("Phase 5 — Frontend…", percentages per step). That scheme is retired;
this table is the only one.

### Phase 2 gates

| Gate | Content | Status |
|---|---|---|
| A — Baseline | Toolchain, build, tests, migrations, legacy-app inventory (read-only) | Approved |
| B — Clinical implementation | Doctor queue, visit (vitals, findings, diagnosis, completion), prescriptions (D8) + print, patient history, `/auth/me` doctor identity, check-in time, D6 delete guard | Approved |
| C — SaaS, email, repository | Manual-billing subscription control, read-only expiry, immediate suspension, honest plan catalog, email production guard, legacy app removed, docs, CI | Approved |
| D — QA | Independent test + security pass: [`security/GATE-D-qa.md`](security/GATE-D-qa.md) — PASS WITH FINDINGS; approved fix pass applied (GD-01, 02, 05, 06, 08, 09, 10, 12, 13 fixed; GD-11 deferred) | **Fix pass done — awaiting owner review** |
| D+ — MVP completion | Appointment rescheduling UI, account page, audit log viewer, follow-up visibility, role-editor cleanup, committed browser regression suite, accessibility pass, production-readiness review (read-only): [`security/PHASE2-MVP-completion.md`](security/PHASE2-MVP-completion.md) | **Done — awaiting owner review** |
| MVP acceptance gate | Owner decisions applied (no prescription on a completed visit; follow-up list for reception, operational fields only; GD-11 grants untouched) + full acceptance verification: [`security/PHASE2-MVP-completion.md`](security/PHASE2-MVP-completion.md#mvp-acceptance-gate-2026-10-08) | **PASS WITH FINDINGS — awaiting owner review** |
| E — Phase 2 review | Owner review; no Phase 3 without approval | Not started |

## Verified results (Phase 3C, 2026-10-08)

Executed locally on Windows (LocalDB 17, Chromium): backend Release 0 warnings · unit 106/106 · integration 518/518
(3 new) · EF no pending changes · no vulnerable NuGet packages · frontend lint/typecheck clean, vitest 204/204 (1 new) ·
`npm audit --omit=dev` 0 · browser suite **61/61, run twice**, on a fresh throw-away database (1 new: four tabs at once keep
the session — it failed before the fix) · gitleaks full history no leaks. Migrations added/changed 0 · schema/seed/grant
changes 0 · D7 SQL not executed.
Technical UAT of every area (auth, MFA, tenancy, clinical, business states, security, frontend): [`security/PHASE3-UAT.md`](security/PHASE3-UAT.md).
Findings register (CLOSED / BLOCKED / OPEN) and the ranked remaining work: [plan §21](PHASE3-PRODUCTION-PLAN.md#21-phase-3c--security-operations-and-readiness-2026-10-08).

- **Monitoring hooks:** the security trail (lockouts, token reuse, MFA failures and changes, platform actions…) is mirrored
  to the log after it is saved, identifiers only; stable event ids 4100–4107 for alert rules; JSON console logs in
  Staging/Production tagged with application, environment and version. Alert rules A1–A17:
  [`operations/monitoring-and-alerting.md`](operations/monitoring-and-alerting.md). No log store, alert or on-call exists yet.
- **Multi-tab sessions:** tabs of one browser refresh the session in turn, so a restored browser with several tabs no
  longer signs the user out everywhere.
- **GD-04:** an invitation for a suspended clinic is refused before anything changes; it works after reactivation.

## Verified results (platform-admin MFA, 2026-10-08)

Executed locally on Windows (LocalDB 17, Chromium 1.63): backend Release 0 warnings · unit 106/106 · integration 515/515
(51 new) · EF no pending changes · no vulnerable NuGet packages · frontend lint/typecheck clean, vitest 203/203 (17 new),
build OK, `npm audit --omit=dev` 0 · browser suite 60/60 (6 new, `mfa.spec.ts`). Schema: exactly two new tables (migration 005),
proven unchanged elsewhere by a before/after fingerprint test. **Not run:** gitleaks over the new history is reported with the
commit, a penetration test, anything against a hosted environment (none exists). Details: plan §19.5.

## Verified results (Phase 3B, 2026-10-08)

Executed locally on Windows (LocalDB 17, Chromium 1.63): backend Release 0 warnings · unit 80/80 · integration 464/464
(105 new) · EF no pending changes · no vulnerable NuGet packages · frontend lint/typecheck clean, vitest 186/186, build OK,
`npm audit --omit=dev` 0 · browser suite 54/54 (run twice) · gitleaks full history no leaks (Docker not installed
locally; CI is the second line). Migrations added/changed 0 · schema changes 0 · seed changes 0 · D7 SQL not executed.
Details and the remaining blockers: [`PHASE3-PRODUCTION-PLAN.md` §20](PHASE3-PRODUCTION-PLAN.md#20-phase-3b--production-readiness-without-deployment-2026-10-08);
operations documents: [`operations/`](operations/README.md).

- **Fail-closed configuration:** every environment except Development (and the test host) refuses to start on
  development/test settings and lists the keys; "Testing" no longer switches the guards off in a deployed API.
- **Client address:** forwarded headers are trusted only from configured proxies/networks (or `NoProxy`).
- **One-time tokens:** emailed links carry the token in the URL fragment (never sent to any server or log); the web app
  strips it from the address bar; old `?token=` links still work. Log output is tested for secrets.
- **Database failures:** connection opens and plain reads are retried; writes and transactions never are; transient
  failures answer `503 TEMPORARILY_UNAVAILABLE` + `Retry-After`. A failing email provider no longer causes a 500.
- **Not implemented:** hosting provider/region, production email provider, backups (requirements only —
  *NOT IMPLEMENTED — OPERATIONAL REQUIREMENT*), infrastructure, monitoring infrastructure, distributed rate limiting.

## Verified results (Phase 3A, 2026-10-08)

Executed locally on Windows (LocalDB 17, Chromium 1.63): backend Release 0 warnings · unit 80/80 · integration 359/359
(28 new) · EF no pending changes · no vulnerable NuGet packages · frontend lint/typecheck clean, vitest 178/178, build OK,
`npm audit --omit=dev` 0 · browser suite 52/52 (5 new) · gitleaks full history no leaks (Docker not installed locally;
CI is the second line). Migrations added/changed 0 · schema changes 0 · seed changes 0 · D7 SQL not executed. What changed
and why: [`PHASE3-PRODUCTION-PLAN.md` §19](PHASE3-PRODUCTION-PLAN.md#19-phase-3a-decisions-and-security-hardening-2026-10-08).

- **GD-03 fixed:** every request re-checks that the token's session is live (clinic and platform tokens, one query merged
  with the membership check). Logout, password change/reset, ending a session, switching clinic, suspension, disabling a
  member and refresh-token reuse end the access token at once (`401 UNAUTHORIZED`).
- **GD-15 fixed:** reactivating a suspended clinic does not revive the sessions suspension ended.
- **XSS / refresh token fixed:** the web app can never receive a refresh token in a response body. Native mode
  (`X-Client-Type: mobile`) is refused for requests that carry browser headers (`403 CLIENT_TYPE_NOT_ALLOWED`) and CORS no
  longer allows the header; no native client exists in the repository.
- **Platform-admin MFA (D14 / 3A-D5): implemented, not deployed.** TOTP + 10 one-time recovery codes for `PlatformRole != None`;
  two new tables (migration 005, [005_PlatformAdminMfa.sql](database/005_PlatformAdminMfa.sql)); no session before MFA. Production needs a persisted, certificate-protected Data Protection key ring and an agreed break-glass procedure — [operations/platform-admin-mfa.md](operations/platform-admin-mfa.md), plan §19.5.
- Behaviour changes to know about: `GET /auth/me` on a suspended clinic is now `403 TENANT_SUSPENDED` (was `200` with
  `access = Suspended`); a disabled member's old token is dead on `/auth/*` too; a revoked token on a public endpoint is
  treated as anonymous; the account page no longer says an ended session may stay open for 15 minutes.

## Verified results (MVP acceptance gate, 2026-10-08)

Executed locally on Windows (LocalDB 17, Chromium 1.63): backend Release 0 warnings · unit 80/80 · integration
331/331 · EF no pending changes · no vulnerable NuGet packages · frontend lint/typecheck clean, vitest 178/178, build
OK, `npm audit --omit=dev` 0 · browser suite (`npm run test:e2e`) 47/47, axe WCAG 2.2 AA 0 violations ·
gitleaks (full history) no leaks. Schema, migrations, seed data: unchanged. Details:
[`security/PHASE2-MVP-completion.md`](security/PHASE2-MVP-completion.md).

## Verified results (Gate D fix pass, 2026-10-07)

Backend Release 0 warnings · unit 80/80 · integration 317/317 · EF no pending changes · no vulnerable packages ·
frontend lint/typecheck clean, vitest 133/133, build OK, audit 0 · gitleaks clean · browser: Gate D QA 108/109 (GD-14,
INFO), clinical regression 64/64, Gate C flow 36/36 · CI run #4 green. Details in
[`security/GATE-D-qa.md`](security/GATE-D-qa.md#fix-pass-final-status).

## Verified results (Gate C, 2026-10-07)

Executed locally in a Linux container with SQL Server 2022, and by GitHub Actions CI (run #1 on `188cd8a`: all
three jobs green).

| Check | Result |
|---|---|
| Backend build (Release) | 0 warnings, 0 errors |
| Backend unit tests | 80/80 |
| Backend integration tests (real API + SQL Server, throw-away databases) | 267/267 |
| EF Core `has-pending-model-changes` | none |
| Migrations | 001–004, unchanged since Phase 1 — no schema change in Phase 2 |
| Vulnerable NuGet packages (incl. transitive) | none |
| Frontend lint / typecheck / unit tests / production build | clean / clean / 131/131 / OK |
| `npm audit --omit=dev` | 0 vulnerabilities |
| Secret scan (gitleaks, full history) | no leaks (narrow test-password allowlist, `.gitleaks.toml`) |
| Browser E2E — Gate C (email flows via the dev mailbox, trial, catalog, read-only expiry, renewal, suspension) | 36/36 |
| Browser E2E — clinical regression (Gate B flow) | 64/64 |

## What the product does today (locally)

- **Clinics and access:** self-registration (14-day trial of the "basic" plan), invitations, roles and permissions,
  branch restriction, clinic switching, sessions, password reset and email verification (development mailbox only).
- **Front desk:** patients (Saudi ID/Iqama validation, duplicates), doctors and working hours (split shifts),
  services, appointments with double-booking protection, rescheduling to a free slot, cancellation with reason,
  walk-ins with queue tokens, check-in, Today queue with waiting time, follow-ups due (operational fields only) with
  one-click booking.
- **Every member:** "My account" — change password, signed-in devices, end another session.
- **Owner / manager:** read-only audit log of the clinic (filters: type, user, dates).
- **Clinical:** doctor's queue → visit (vitals, complaint/examination, diagnosis with optional ICD-10, plan,
  follow-up) → prescription by the treating doctor only (D8), while the visit is open → completion (locks the visit,
  no new prescription afterwards); only the treating doctor
  records, edits and completes a visit (GD-01) → printable A4
  prescription → patient history timeline. Medical content is permission-gated, tenant-, branch- and
  doctor-scoped.
- **SaaS (manual billing, D1):** platform admin changes a clinic's plan, status and billing period
  (`/platform/tenants/{id}/subscription`); an ended period makes the clinic read-only; suspension acts on the next
  request; the catalog shows only features that exist.
- **Not in the product:** attachments in the UI (D3; the secure API exists but files stay quarantined), invoicing,
  payments, SMS/WhatsApp, online booking, reports, platform-admin UI (the platform API exists), patient portal.

## Decisions

| ID | Decision |
|---|---|
| D1 | Manual billing; no online subscription payments |
| D2 | Hosting-agnostic until the Production/Cloud phase |
| D3 | Attachments hidden for the MVP; no scanner or cloud storage |
| D4 / Gate C | Development mailbox only in Development (GD-05); the API refuses to start outside Development/Testing without a real provider |
| D5 | International (E.164) patient phone numbers and clinic landlines — **OPEN, NOT IMPLEMENTED** (Saudi mobile is still required) |
| D6 | A patient with any appointment, visit, prescription or file cannot be deleted (409) |
| D7 | DATA-1 role correction — **deferred**; not executed (stored roles can't tell a customised role from one that never got the permission) |
| D8 | Only the visit's own doctor issues a prescription; patient/doctor/branch come from the visit |
| D9 | No booking override outside working hours |
| D10 | Visit amendments and prescription void — **deferred** |
| D11/D12 | Printing: prescription only, existing clinic/doctor data |
| D13 | Audit-log visibility is not a plan feature |
| D14 | Platform-admin MFA: **3A-D5 required before production — implemented** after the schema approval (§19.5); production prerequisites open |
| 3A-D1/D2/D10 | Production hosting must support Saudi data residency; provider **not** selected; prioritise in-Kingdom data (database, backups, logs, audit, storage, email where the provider permits); no compliance claim; Saudi legal/compliance review required before launch |
| 3A-D3/D4 | Server-side session validation (GD-03) and no revival of suspended sessions (GD-15) — implemented, no schema change |
| 3A-D6 | Production web and API on sibling subdomains of one registrable domain: `app.<domain>` + `api.<domain>`; no domains configured yet |
| 3A-D7/D8/D9 | No distributed rate limiting yet (single instance); no automatic retry around non-idempotent writes (connection and read-only retries allowed; later work); attachments stay disabled |
| D15 | Saudi integrations (NPHIES, Wasfaty, Nafath, CST SMS, WhatsApp, ZATCA) deferred |
| D16 | Legacy Payload application removed (Gate C); upstream MIT notice kept in `LICENSE` |
| D17 | Trial → paid: new clinics start on a 14-day "basic" trial; "free" hidden from the public catalog; no price changes |
| QA-15 | A completed visit takes no new prescription (422 `VISIT_COMPLETED`); prescriptions issued before completion stay readable and printable (MVP acceptance) |
| Follow-ups | Reception may see follow-ups due: patient, date, doctor, booked or not — never clinical content (`GET /follow-ups`, `Appointments.View`; MVP acceptance) |
| GD-11 data | Stored dormant grants stay untouched; they guard no endpoint and are hidden in the role editor (MVP acceptance) |
| Gate C | Ended subscription → read-only (reads allowed, writes 403 `SUBSCRIPTION_EXPIRED`); suspension → immediate 403 `TENANT_SUSPENDED` |

## Open items and future decisions

- Gate D: fixes applied for GD-01, GD-02, GD-05, GD-06, GD-08, GD-09, GD-10, GD-12, GD-13; **GD-03 and GD-15 fixed in
  Phase 3A**; GD-04 fixed and GD-14 closed (accepted platform behaviour) in Phase 3C. Still open as recorded: GD-11 dormant
  permissions (deferred), GD-07 (INFO) — see
  [`security/GATE-D-qa.md`](security/GATE-D-qa.md) (that report keeps its original wording).
- Platform-admin MFA (D14 / 3A-D5): implemented; alert hooks and rules exist (Phase 3C); open are the production key ring and
  certificate custody, the break-glass rehearsal, a second platform admin (no creation path) and live alerting (plan §21.3).
- Go-live: every remaining item is external and listed, with owner, verification and pre-go-live test, in
  [`operations/go-live-checklist.md`](operations/go-live-checklist.md).
- Production blockers after 3B: hosting/residency choice and Saudi legal review, a production email provider, backups with
  a tested restore, deployment infrastructure (BLOCKER); platform-admin MFA, monitoring/alerting/on-call, independent
  penetration test (HIGH) — full classified list in plan §20.6. Closed by 3B: configuration guards, key-rotation procedure,
  one-time tokens in URLs vs logs, the trusted-proxy mechanism (real-network verification still open).
- D7 role-data correction (needs a design that can tell customised roles apart).
- D10 clinical corrections (addendum, prescription void) — will need schema approval.
- D5 phone-number rules (international patients, clinic landlines).
- D6 proper patient deactivation (needs a schema change).
- Account enumeration through `EMAIL_TAKEN` (QA-13) and `ACCOUNT_LOCKED` (QA-06) — decisions pending.
- GD-11: the stored dormant grants must be reviewed (with D7) before invoicing, payments, export or financial reports
  ship — a new endpoint must not inherit them silently (a frontend guard test fails if one becomes enforced).
- Follow-up reminders by SMS/WhatsApp: Saudi integrations deferred (D15); the in-app follow-up list exists.
- No platform-admin UI; plan changes are made through the platform API.
- Production/Cloud phase: hosting and data residency review, real email provider, attachment storage and scanning,
  backups and restore drill, monitoring and alerting, distributed rate limiting, deployment.

## History

- Phase 1 audit: [`docs/security/PHASE1-local-core-audit.md`](security/PHASE1-local-core-audit.md)
- QA reports: [`docs/security/`](security/)
- Agent task log of the Phase 1 cycles: [`docs/agents/TASKS.md`](agents/TASKS.md) (historical)
- Legacy-app audit and migration plan (the legacy app is removed): [`docs/01-architecture-audit.md`](01-architecture-audit.md),
  [`docs/03-migration-plan.md`](03-migration-plan.md)
