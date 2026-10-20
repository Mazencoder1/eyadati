# Phase 2 — Gate D: independent QA

- Date: 2026-10-07 · Baseline: `b970053` (Gate C approved) · Scope: everything Phase 2 added or changed
- Method: source/design review, new adversarial API tests (`Qa/QaGateDTests.cs`, `Qa/QaEmailBoundaryTests.cs`),
  raw-HTTP probes against Kestrel, browser QA in Chromium (production build, real API, SQL Server 2022).
- No product code, schema, migration, role data or permission catalog was changed. Tests named
  `CurrentBehaviour_*` document a finding without changing behaviour.

**Verdict: PASS WITH FINDINGS** — no BLOCKER, no HIGH. Five MEDIUM items, none of which blocks Phase 2 (a local MVP);
GD-02 must be fixed before any production use.

**Final status (after the approved fix pass, `89955bd` + `da8c12b`): CLOSED — PASS.** GD-01, GD-02, GD-05, GD-06, GD-08,
GD-09, GD-10, GD-12 and GD-13 are fixed and verified; GD-11 is deferred (verified unchanged); GD-03, GD-04, GD-07, GD-14
and GD-15 remain as recorded (not in the approved fix list). See [Fix pass](#fix-pass-final-status) below.

## Findings

| ID | Severity | Area | Summary (status after the fix pass: see [Fix pass](#fix-pass-final-status)) |
|---|---|---|---|
| GD-01 | MEDIUM | Clinical authorization | Visits can be authored, edited and completed in another doctor's name by a clinic manager (no doctor profile) or a doctor with `MedicalRecords.ViewAllDoctors` |
| GD-02 | MEDIUM | Sessions | A disabled member keeps full access, including medical records, until the access token expires (≤ 15 min) |
| GD-06 | MEDIUM | Accessibility | Language switcher name doesn't contain its visible text on public pages (P1-06, still open) |
| GD-10 | MEDIUM | Product / SaaS | Subscription page shows placeholder prices (199 / 449 / 1,499 SAR) as a real offer |
| GD-11 | MEDIUM→LOW | Permission catalog | Dormant permissions are pre-granted in stored roles (latent grant when features ship); misleading in the role editor |
| GD-03 | LOW | Sessions | Logout, password change and session revoke end refresh, not an already-issued access token (≤ 15 min) |
| GD-05 | LOW | Email boundary | `ASPNETCORE_ENVIRONMENT=Testing` is exempt from the email (and storage) guard |
| GD-08 | LOW | Accessibility | Prescription page has no `h1` |
| GD-09 | LOW | UX | Empty prescription error reads "هذا الحقل مطلوب." (which field?) |
| GD-12 | LOW | UX | Doctor "Today": statistics band above the queue; "ينتظر منذ 0 د" |
| GD-13 | LOW | UX | Completed visit shows empty editable-looking boxes; subscription page repeats the period end; section header bands |
| GD-04 | INFO | SaaS | A pending invitation can be accepted while the clinic is suspended/read-only (`/auth/*` exemption); grants no access |
| GD-07 | INFO | Prescriptions | Legacy prescriptions without a visit are clinic-wide (no branch); own-doctor rule still applies; dev/test data only |
| GD-14 | INFO | Accessibility | Native modal dialog lets Tab reach the browser chrome; never page content behind the modal |
| GD-15 | INFO | SaaS | Reactivating a clinic re-enables access tokens issued before the suspension |

### GD-01 — clinical authorship in another doctor's name (MEDIUM)
- Reproduce: clinic manager → `POST /visits { patientId, doctorId: <Dr X>, clinical: {...} }` → 201; `POST /visits/{id}/complete` → 200.
  Same for a doctor holding `MedicalRecords.ViewAllDoctors` (T-012 by design). Test: `CurrentBehaviour_manager_and_ViewAllDoctors_doctor_can_author_and_complete_a_visit_in_another_doctors_name`.
- Expected (by analogy with D8): clinical content is written by the treating doctor, or the record shows who wrote it.
- Actual: the visit and its diagnosis appear under Dr X; only `CreatedBy/UpdatedBy/CompletedByUserId` (not shown in the UI) reveal the author. D8 still holds: the manager cannot prescribe on it.
- Impact: clinical attribution / medico-legal integrity. No cross-tenant or cross-branch exposure.
- Recommendation: product decision — restrict writing and completing clinical content to the visit's own doctor (like D8), or keep it and show "recorded by" on the visit. No schema change needed for either.

### GD-02 — disabled member keeps access ≤ 15 min (MEDIUM; production blocker, acceptable for Phase 2)
- Reproduce: owner disables a doctor's seat → the doctor's existing access token still reads patients and visits and creates patients (`CurrentBehaviour_disabled_member_keeps_full_access_including_medical_records_until_the_access_token_expires`).
- Exploitability: needs a token issued before the disable (a dismissed employee's open browser); window ≤ 15 min; no privilege gain beyond what they had.
- Recommendation: extend the per-request check added in Gate C (`TenantAccessResolver`) with the membership status (the `mid` claim) — same single query, no schema change. Should be done before production preparation.

### GD-03 — access token outlives logout / password change (LOW)
- Change-password and logout revoke the refresh family; the access token stays valid ≤ 15 min (`CurrentBehaviour_logout_and_password_change_end_refresh_but_not_an_already_issued_access_token`). Standard stateless-JWT trade-off. The GD-02 fix can also check the session (`sid`) for revocation.

### GD-05 — "Testing" environment exempt from the email guard (LOW)
- `ASPNETCORE_ENVIRONMENT=Testing` + `Email:Provider=DevMailbox` starts and writes one-time links to disk (`CurrentBehaviour_Testing_environment_accepts_the_development_mailbox`). Requires an operator to set that environment on a server. The automated tests don't need the exemption for email (they replace the sender). Recommendation: allow DevMailbox in Development only; consider the same for local storage.
- Edge cases that **are** refused (14 tests): `devmailbox`/`DEVMAILBOX` in Production, lowercase `production`, Staging, custom names (`Local`, `Docker`); missing, empty, padded (`" DevMailbox "`) or unknown (`Smtp`, `SendGrid`) providers.

### GD-06 — language switcher accessible name (MEDIUM, pre-existing P1-06)
- axe `label-content-name-mismatch` (serious, WCAG 2.5.3) on `/login`, `/register`, `/forgot-password`. App pages passed.

### GD-10 — placeholder prices shown to clinics (MEDIUM, product)
- `/subscription` lists 199 / 449 / 1,499 SAR per month. These are seed placeholders "pending the commercial decision" (PlanSeed). Shown to customers they read as a real offer. Recommendation: confirm prices, or hide prices until approved (application-level, no migration).

### GD-11 — dormant permissions (LOW; latent risk)
| Permission | Endpoint | Assessment |
|---|---|---|
| `Invoices.View/Create/Void/Refund`, `Payments.Record` | none | No current access. **Latent:** stored receptionist/accountant roles already hold them, so a future invoicing endpoint would be granted to every existing clinic's roles without review. Misleading in the role editor |
| `Reports.View`, `Reports.Financial` | none | No backend use. `Reports.View` only selects the "manager" layout of Today (UI) |
| `Patients.Export` | none | Dead entry; misleading |
| `Subscription.Manage` | none | Dead entry (plan changes are platform-only); misleading |
| `Platform.Plans.Manage`, `FeatureFlags.Manage`, `SystemHealth.View`, `AuditLogs.View` | none | Platform-plane only, never in clinic tokens; dead entries |
- No current authorization risk; the accountant role is effectively "patients list + subscription". Recommendation: when the catalog is revisited, hide unimplemented permissions from the editor and review role templates before shipping any feature that reuses these names.

### LOW / INFO details
- GD-08: `/prescriptions/[id]` has no `h1` (axe best-practice) — screen-reader users lose the page heading.
- GD-09: the empty-prescription alert should say "add at least one medication".
- GD-12 / GD-13: see the AI-slop / UX review below.
- GD-04: `/auth/accept-invitation` activates a pending seat in a suspended clinic; sign-in stays refused, so no access results.
- GD-07: legacy rows (pre-D8, no visit) are visible to branch-restricted readers; other doctors still get 404. Production has no such rows.

## Security results
- **Tenant isolation / IDOR:** all 79 routed endpoints swept with forged ids (expired and suspended clinics); route-id swaps on visits and prescriptions from another tenant → 404; prescription body fields `tenantId/patientId/doctorId/branchId/number/issuedAtUtc` ignored (server values stored); forged JWT (`tid` swapped) and `alg=none` → 401. Earlier isolation suites (Phase 1–Gate C) all still pass.
- **Branch / doctor scope:** cross-branch doctor (even with ViewAllDoctors) → 404; own-doctor scope hides colleagues' visits and prescriptions in lists, history and by id; ViewAllDoctors reads others but cannot prescribe on them (D8).
- **Receptionist:** 403 on every clinical endpoint (visits, prescriptions, attachments, medical block, visit edit/complete); patient record without the medical block; appointment payloads carry no diagnosis or medication.
- **Doctor:** can document, complete and prescribe on own visits; completed visit → `VISIT_COMPLETED` on any edit, `INVALID_STATUS_TRANSITION` on re-complete.
- **Owner / manager:** full visibility; cannot prescribe in a doctor's name (403 `PRESCRIBER_NOT_TREATING_DOCTOR`); can author visits in a doctor's name (GD-01).
- **Frontend:** nothing in localStorage/sessionStorage for any role; refresh cookie not readable by JavaScript; `/auth/me` is the only source of the doctor identity; direct URLs → sign-in redirect (anonymous), "no permission" screen (receptionist), "not found" (other doctor, made-up or malformed ids); no clinical text rendered on any refused page.

## SaaS results
- Only `Platform.Billing.View/Manage` holders can read or change a subscription: tenant owner, clinic manager, receptionist, the target clinic's own owner, platform Support and anonymous are all refused (GET, PUT and suspend). Unknown tenant/plan, invalid status/cycle, malformed date, missing or stale row version → 400/404/409, never 500.
- Enforcement matrix (6 cases): Trialing/Active/PastDue with time left → full; the same statuses with the period ended → read-only.
- Expired: every write endpoint refused with `SUBSCRIPTION_EXPIRED`, every read answers, counts of patients/visits/prescriptions/appointments unchanged; login, refresh, change-password and logout keep working.
- Suspended: every non-auth endpoint refused with `TENANT_SUSPENDED` using a still-valid token; login and refresh refused; platform administration unaffected.
- Path tricks on real Kestrel to reach a write through the `/api/v1/auth` exemption (`/auth/../patients`, `%2e%2e`, `%2f`, upper case, `//`, `;`) → all 403 or 404; zero rows written.

## Email results
Production boundary holds for every refused combination above; Development uses DevMailbox only when selected; an empty provider in Development leaves the non-writing sender; GD-05 is the one gap.

## Authentication results
Access tokens: HS256, 15 min, issuer/audience/signature validated; forged and `alg=none` tokens refused. Refresh rotation with reuse detection unchanged. Disabled member → GD-02; logout/password change/session revoke → GD-03; suspension immediate; password reset and invitation links single-use (Gate C E2E).

## Frontend results
- Accessibility (axe WCAG 2.0/2.1/2.2 A+AA): 0 violations on doctor Today, patient + history (doctor and receptionist), open and completed visit, prescription page, prescription dialog, receptionist Today, subscription. Public auth pages: GD-06.
- Keyboard: dialog opens from the keyboard, has an accessible name, focus moves inside and stays inside, Esc closes, focus returns to the opener. Visit form: invalid field gets `aria-invalid`, linked error text and focus; keyboard focus ring visible. One `h1` per app page, `main` landmark present.
- RTL/LTR × 375/768/1024/1440: correct `dir`, no horizontal overflow on Today, patient, open visit, completed visit, prescription (ar and en) and subscription (ar) — 44 combinations.
- Print: no app chrome in print media; A4 PDF renders on one page.

## AI-slop / UX review (findings only, no redesign)
| Severity | Observation |
|---|---|
| LOW | Doctor Today on mobile opens with a five-figure statistics band (mostly zeros) before the waiting list — the doctor's task is the queue (GD-12) |
| LOW | "ينتظر منذ 0 د" — reads oddly; "وصل للتو" / "منذ دقيقة" would be natural (GD-12) |
| LOW | Completed visit renders empty read-only inputs and textareas (with resize handles) instead of a compact read-only summary; empty date shows `mm/dd/yyyy` (GD-13) |
| LOW | Subscription page states the period end twice (line + info box) for a trial; two badges ("فترة تجريبية", "باقتك") (GD-13) |
| LOW | Visit card: each section header is a full-width band with rules above and below — heavier than needed (GD-13) |
| INFO | No gradients, decorative animation, fake metrics or icon clutter found on the new screens; one card per purpose; Arabic clinical terms consistent (العلامات الحيوية، الشكوى والفحص، التشخيص والخطة، المتابعة، الوصفة) |

## Database
- schema changed: **NO** · migrations added/modified: **NO** (0 migration files differ from `4e52ae6`) · EF: no pending model changes · no data scripts run (D7 untouched).

## Tests executed (Gate D)
| Suite | Result |
|---|---|
| Backend unit (Release) | 80/80 |
| Backend integration (Release, SQL Server 2022) | 300/300 (267 existing + 19 `QaGateDTests` + 14 `QaEmailBoundaryTests`) |
| EF `has-pending-model-changes` | none |
| Frontend lint / typecheck / vitest / audit | clean / clean / 131/131 / 0 vulnerabilities |
| Gitleaks (full history) | no leaks |
| Raw Kestrel path-bypass probes | 10 variants, 0 bypass, 0 rows written |
| Browser QA (Gate D) | 84/88 — the 4 non-passing checks are GD-06 (×3) and GD-14 (native dialog → browser chrome, not a defect) |
| Browser clinical regression (Gate B flow) | 64/64 |

## Remaining known risks
D5 (international/E.164 patient phones, clinic landlines) — OPEN, NOT IMPLEMENTED · D7 role-data correction — deferred, not executed · D10 visit amendment / prescription void — deferred · prescription-after-completion product decision · QA-06/QA-13 enumeration decisions · no platform-admin UI ·
GD-11 dormant permissions (deferred) · GD-03 access token outlives logout/password change (≤ 15 min). GD-01 and GD-02 were
fixed in the fix pass below.

## Fix pass (final status)

Approved fix pass, 2026-10-07. Commits `89955bd` (backend: GD-02, GD-01, GD-05), `da8c12b` (frontend: GD-01 UI, GD-06,
GD-08, GD-09, GD-10, GD-12, GD-13). No schema, migration, index, constraint, role-data or permission-catalog change.

| ID | Status | What changed | Verified by |
|---|---|---|---|
| GD-02 | **Fixed** | `TenantAccessMiddleware` checks on every clinic request (and `GET /auth/me`) that the token's membership is `Active` and belongs to the token's clinic and user → else 401 `NO_ACTIVE_MEMBERSHIP`. Other `/auth/*` stay reachable (sign-out, switch clinic). JWT lifetime, refresh rotation and reuse detection unchanged | `GateDFixPassTests.GD02_1..5` (active allowed; disable → same token refused incl. medical reads and `/auth/me`, refresh refused; re-enable → pending invitation, refused until accepted, then a new session works; cross-tenant unchanged and a mismatched tid/mid token refused; platform admin with a disabled clinic seat unaffected); `QaRbacAuditTests` QA-05 converted; browser: an open UI session goes to sign-in after disable; raw-path probe 15 variants, 0 bypass |
| GD-01 | **Fixed** | `VisitService` create/update/complete require the caller's doctor profile to be the visit's doctor → else 403 `VISIT_NOT_TREATING_DOCTOR` (after the visibility rules, so hidden visits stay 404). `ViewAllDoctors` is read-only. Prescription rules (D8) untouched. UI: only the treating doctor gets the form and the Complete button; others see a read-only view with a note | `GateDFixPassTests.GD01_1..9` (treating doctor edits/completes; ViewAllDoctors doctor and clinic manager cannot record, edit or complete; reads unchanged; D8 unchanged; cross-tenant 404 and branch isolation 404 unchanged); legacy tests that authored in another doctor's name now act as the treating doctor; browser GD-01 checks |
| GD-05 | **Fixed** | DevMailbox is allowed in Development only; Testing, Staging, Production and custom environments refuse it. Missing/unknown provider still refused outside Development/Testing | `QaEmailBoundaryTests` (Testing/testing/Production-like refuse DevMailbox; Testing starts without a provider; Development still accepts it); `EmailProviderGuardTests` |
| GD-06 | **Fixed** | Language switcher: no `aria-label`; accessible name = screen-reader hint + visible language name ("تغيير اللغة: English") | axe WCAG 2.2 AA clean on `/login`, `/register`, `/forgot-password` (ar) and `/login` (en); explicit name-contains-visible-text checks |
| GD-08 | **Fixed** | Prescription document title is the page's `h1` (same styling) | browser: exactly one `h1` ("وصفة طبية"); axe clean |
| GD-09 | **Fixed** | Empty prescription → "أضف دواءً واحدًا على الأقل." / "Add at least one medication." | browser (`role=alert` text), message test |
| GD-10 | **Fixed** | Plan cards show "الأسعار ستُعلن لاحقًا" / "Prices will be announced later" instead of the stored placeholder prices; DB plan data untouched | browser: no price text on `/subscription` |
| GD-12 | **Fixed** | Doctor's Today: queue before the day's counters; 0 minutes reads "وصل الآن" / "just arrived" (ICU plural) | browser DOM-order check; message tests |
| GD-13 | **Fixed** | Completed or read-only visit shows recorded values as text ("لم يُسجَّل شيء." for an empty section) — no input boxes; lighter section headers (rule + title, no band); subscription card shows one end date | browser: 0 inputs on the completed visit, 0 banded section headers, one end-date line |
| GD-11 | **Deferred** (unchanged) | Nothing — permissions and stored roles were not modified, deleted or renamed | 0 files changed under `Domain/Authorization` and `Infrastructure`; no data script run |
| GD-03, GD-04, GD-07, GD-14, GD-15 | Unchanged (not in the approved list) | — | — |

### Regression after the fix pass (executed)

| Suite | Result |
|---|---|
| Backend build (Release) | 0 warnings, 0 errors |
| Backend unit (Release) | 80/80 |
| Backend integration (Release, SQL Server 2022) | 317/317 (incl. 14 new `GateDFixPassTests`, all Gate D adversarial suites: tenant isolation, IDOR, clinical and prescription authorization, subscription enforcement, disabled-member token, email environment boundary) |
| EF `has-pending-model-changes` | none |
| Vulnerable NuGet packages (incl. transitive) | none |
| Frontend lint / typecheck / vitest / production build / `npm audit --omit=dev` | clean / clean / 133/133 / OK / 0 vulnerabilities |
| Gitleaks (full history, 56 commits) | no leaks |
| GD-02 raw-path probe (disabled member) | 15 variants, 0 bypass |
| Browser QA (Gate D + fix checks): direct URLs, storage, axe WCAG 2.2 AA, keyboard/focus/dialog, ar+en × 375/768/1024/1440, print | 108/109 — the one non-passing check is GD-14 (INFO, native dialog → browser chrome) |
| Browser clinical regression (Gate B flow, assertions updated to the GD-09/12/13 UI) | 64/64 |
| Browser SaaS/email regression (Gate C flow) | 36/36 |
| GitHub Actions CI (run #4 on `da8c12b`) | success |

### Database
schema changed: **NO** · migrations changed: **NO** · pending EF model changes: **NO** · D7 executed: **NO**.
