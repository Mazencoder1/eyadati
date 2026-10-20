# Phase 2 — MVP completion pass (after Gate D)

- Date: 2026-10-08 · Branch: `claude/phase-2-gap-audit-s02iba` · Baseline: `6c50c83` (Gate D fix pass)
- Scope: close the local-MVP gaps found by the post-Gate-D audit, harden, verify; read-only production-readiness review.
- Database: **no schema, migration, index, constraint or seed change.** EF reports no pending model changes. D7 not run.
- Backend product code: **unchanged.** New backend files are tests only.

## Delivered

| Item | What | Commit |
|---|---|---|
| P1.1 Rescheduling UI | Menu action for Scheduled/Confirmed non-walk-in appointments (`Appointments.Edit`); slots from the existing availability endpoint (same doctor/branch/service); 409/422/404/stale handling; shared `SlotPicker`; menu returns focus to its button | `919a1a9` |
| P1.2 Account page | Change password; signed-in devices with "end session" (not for the current device); honest note on the 15-minute access token (GD-03) | `53f79b3` |
| P1.2 client fix | **Defect:** a wrong current password (401 `INVALID_CREDENTIALS`) was refreshed and re-sent, so one typo counted as two failed attempts against the sensitive rate limit and the account lockout. The client no longer retries that 401 | `53f79b3` |
| P1.3 Browser suite | `frontend/e2e` (Playwright): owner, reception, doctor, security, account, layout (ar/en × 375/768/1024/1440, axe), accessibility — 38 tests; data built through the public API only | `45bb61b` … |
| P1.4 Appointments | Audit: no defect. Added concurrency tests for rescheduling (moves into one slot, move vs booking, parallel moves with one rowVersion) | `fdf4336` |
| P1.5 / P1.6 Patients, clinical | Audit: no defect. Browser coverage for duplicate warning / duplicate ID refusal and visit follow-up | `527e8ce` |
| P2.1 Audit log viewer | Read-only, `AuditLogs.View`; filters the API supports (type prefix, user, clinic-local date range); labels for all 49 action codes (guard test) | `29f6469` |
| P2.2 Follow-up | Follow-up date per visit and "next follow-up" in the patient history (existing data, clinical roles only) | `455b9ef` |
| P2.3 Role editor | Hides the 8 permissions no endpoint enforces; display only, stored roles untouched (browser-verified); guard test fails if one starts being enforced | `409c116` |
| P2.4 Accessibility | Every main dialog axe-clean with focus in/out and Esc; skip link; mobile nav; first-invalid-field focus; language switch | `045215e` |

Defects found and fixed by the new checks: the API-client double attempt (above); menu dialogs lost focus on close
(fixed in `919a1a9`); the audit log overflowed horizontally below 1440 px (fixed before commit by default-hidden IP and
details columns).

## Audit notes (no change made)

**Appointments (P1.4).** Frontend status/permission mirror matches `Appointment.Transitions` and the controller
permissions. Availability skips past and busy times and respects branch scope. Concurrency: per-doctor
`sp_getapplock`, filtered unique index and rowVersion — now also tested for rescheduling.
- *Limitation:* availability counts the appointment being moved as busy, so a move that overlaps its own current time
  (e.g. 15 minutes later for a 30-minute appointment) is not offered, although the server would accept it. A fix needs
  an additive availability parameter (`excludeAppointmentId`) — API change, not made.
- *Limitation:* an appointment without a service but with a custom duration gets slots of the doctor's default length;
  a too-short slot is refused by the server (409) and the dialog reloads.
- *Observation:* check-in is accepted on any day (no same-day rule) — product decision, not changed.

**Patients (P1.5).** Duplicate national ID/Iqama refused (unique index, 409); shared mobile only warns (families).
ID number masked for users without `Patients.Edit`. Receptionist sees no medical block. Layout/axe clean.

**Clinical (P1.6).** GD-01 and D8 hold (API + UI). Medical alerts shown on the visit. Completion locks the visit.
Prescription after completion was allowed at this point; decided and changed at the acceptance gate below (QA-15).

**UX (P2.5).** No gradients, fake metrics, decorative animation or badge clutter found. Minor, unchanged: the
owner/manager Today opens with a five-number summary (real counts; the doctor's Today shows the queue first since
GD-12); the audit log repeats "تم" in the result column for successful events.

**Documentation conflict found:** `STATUS.md` said hiding dormant permissions "touches stored role data". It does not
need to (the editor sends stored permissions back unchanged); STATUS was corrected.

## Production-readiness review (read-only, P3)

| Area | Finding |
|---|---|
| Auth / sessions | JWT 15 min, refresh rotation + reuse detection, lockout, per-request membership check (GD-02). Open: GD-03 (token outlives logout/password change/session end ≤ 15 min), GD-15 (reactivation re-enables old tokens), GD-04, enumeration (QA-06/QA-13), platform-admin MFA (D14) |
| Tenant / branch isolation | Query filters + SaveChanges guard + stored-TenantId WHERE + composite FKs; re-verified by integration suites and the browser security spec |
| Rate limiting | In-process per instance (configurable `RateLimiting:*`); needs a distributed limiter with more than one instance |
| Headers / errors | nosniff, DENY, CSP, no-referrer, HSTS outside Development; 500s return a generic message + correlation id, details only in logs |
| Logging | Serilog to console, text template with correlation/user/tenant ids; production needs a structured (JSON) sink, retention and alerting |
| Database | Command timeout 30 s; **no transient-fault retry** (`EnableRetryOnFailure`) — adding it needs execution-strategy wrapping of the explicit transactions (booking lock); audit log indexed `(TenantId, OccurredAtUtc)`; patient name search is `contains` without full-text *(correction, Phase 3C: the name search is a **prefix** match (`StartsWith`); contains/full-text is later work — plan Appendix A1)* |
| Health | `/health/live`, `/health/ready` (database) |
| Email | Provider abstraction; DevMailbox Development-only (GD-05); non-Development start refused without a real provider — provider not selected (Production phase) |
| Attachments | Still hidden in the UI (D3); API quarantines files; no storage/scanning — unchanged |
| Frontend | Access token in memory, refresh cookie httpOnly; CSP with nonce; production build OK; dev-only `braces` advisory (5 high, `eslint-config-next` toolchain) |
| CI | Backend/frontend/gitleaks jobs; the browser suite is local-only (needs API + web + SQL Server together) |

## Verification (executed 2026-10-08)

| Check | Result |
|---|---|
| Backend build (Release) | 0 warnings, 0 errors |
| Backend unit / integration | 80/80 · 324/324 (317 existing + 4 reschedule contract + 3 reschedule concurrency) |
| EF `has-pending-model-changes` | none |
| Vulnerable NuGet packages (incl. transitive) | none |
| Frontend lint / typecheck / vitest / build | clean / clean / 172/172 / OK |
| `npm audit --omit=dev` | 0 vulnerabilities (full audit: 5 high, dev-only, unchanged) |
| Browser suite (`npm run test:e2e`, Chromium) | 38/38; axe WCAG 2.2 AA: 0 violations (19 pages × ar/en × 4 widths + 8 dialogs × ar/en) |
| gitleaks 8.30.1 (full history) | no leaks |
| Schema / migrations / seed | unchanged |

## MVP acceptance gate (2026-10-08)

### Owner decisions applied

| Decision | Implementation | Commit |
|---|---|---|
| A completed visit takes no new prescription | `PrescriptionService.CreateAsync` takes the per-visit completion lock and refuses a completed visit (422 `VISIT_COMPLETED`); "New prescription" shown only on an open visit; issued prescriptions stay readable/printable. Three tests that encoded the old behaviour were updated deliberately; new race test (prescriptions vs completion) | `55dad4d` |
| Reception may see follow-ups due, operational fields only | `GET /follow-ups` (`Appointments.View`): patient, MRN, date, doctor, branch, booked + next appointment — no visit id, no clinical content; tenant/branch/own-doctor scope; "Follow-ups" page with prefilled booking | `740ddac` |
| GD-11 stored grants unchanged | Nothing changed. The hidden permissions guard no endpoint (frontend guard test reads the backend sources) | — |

### Acceptance checklist (how each item was verified)

| Area | Verified by |
|---|---|
| Registration, invitation, sign-in/out, password reset, change password, sessions, end session | browser `lifecycle.spec`, `account.spec`; integration auth suites |
| Disabled member, suspended clinic | browser `security.spec`, `lifecycle.spec`; integration `GateDFixPassTests`, `SaasControlsTests` |
| Expired subscription (read-only) | integration only (`SaasControlsTests`, Gate D enforcement matrix) — the platform API can't set a past period end, so the browser suite can't produce it |
| Owner: branches, services, staff, doctors, hours, subscription, audit log, roles | browser `owner.spec`, layout sweep |
| Reception: patients, duplicates, booking, reschedule, cancel, walk-in, check-in, follow-ups, no clinical access | browser `reception.spec`, `followups.spec`, `security.spec` |
| Doctor: Today, own visit, vitals, diagnosis, treatment, follow-up, prescription + print, completion, history | browser `doctor.spec` |
| Clinical safety: treating doctor only, owner/other doctor refused, receptionist refused, completed visit locked, no prescription after completion, existing ones readable | browser `doctor.spec`, `security.spec`; integration `GateDFixPassTests`, `VisitCompletionApiTests`, `QaRetestT014Tests`, `Phase2ClinicalWorkflowTests` |
| Follow-up privacy and isolation | integration `FollowUpListTests` (6); browser `followups.spec` |
| Isolation, ids, rowVersion, conflicts, forged identity fields | browser `security.spec`; integration tenant/branch/IDOR suites, `RescheduleConcurrencyTests` |
| UI: ar/en, RTL/LTR, 375–1440, overflow, keyboard, dialogs, focus, labels, states | browser `layout.spec` (20 pages), `accessibility.spec`; vitest page tests (empty/loading/error/read-only) |

### Results

| Check | Result |
|---|---|
| Backend build (Release) | 0 warnings, 0 errors |
| Backend unit / integration | 80/80 · 331/331 |
| EF `has-pending-model-changes` | none |
| Vulnerable NuGet packages | none (6/6 projects) |
| Frontend lint / typecheck / vitest / build | clean / clean / 178/178 / OK |
| `npm audit --omit=dev` | 0 vulnerabilities |
| Browser suite | 47/47; axe WCAG 2.2 AA 0 violations |
| gitleaks 8.30.1 | git history: no leaks; working-directory findings only in ignored local files (development mailbox one-time links, `.next` build output) |
| Migrations added / modified · schema · seed · D7 · stored role grants | 0 / 0 · unchanged · unchanged · not executed · unchanged |
