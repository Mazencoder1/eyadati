# Phase 3 — technical UAT (local, 2026-10-08)

> Local technical acceptance run, **not** a staging validation, a penetration test or a compliance assessment. Environment:
> Windows, LocalDB 17, Chromium (Playwright), production builds of the API (Release) and web app, a **fresh throw-away
> database** (`ClinicSaaS_E2E_3c`, created by the existing migrations 001–005). No shared or production database was used.

Every area of the UAT brief is mapped to tests that **ran and passed** in this session. "API" = backend integration suite
(518 tests), "Browser" = Playwright suite (61 tests, run twice), "Unit" = backend unit (106) and frontend vitest (204).

## Results

| Area | Item | Evidence (executed) | Result |
|---|---|---|---|
| Authentication | login, logout | Browser `lifecycle.spec` (register → sign out → protected pages need sign-in), `sessions.spec` (logout ends the access token at once); API `AuthSecurityTests` | PASS |
| | password change | Browser `account.spec` (wrong current password on the field; success ends other devices), `sessions.spec`; API `AccountAndPlatformTests` | PASS |
| | refresh | Browser `refresh-token.spec` (cookie only; in-page script cannot get a token; **four tabs at once keep the session — failed before the 3C fix, passes after**); API `RefreshTokenExposureTests`, `SessionRevocationTests` | PASS (after fix) |
| | session revocation | Browser `account.spec` (end another session), `sessions.spec`; API `SessionRevocationTests` | PASS |
| | disabled account | Browser `security.spec` (disabled member loses access immediately); API Gate D suites | PASS |
| | suspended clinic | Browser `lifecycle.spec` (one clear screen), `sessions.spec` (reactivation keeps old sessions dead); API `SaasControlsTests`, GD-04 test | PASS |
| | MFA enrolment, verification, recovery codes, removal | Browser `mfa.spec` (6 tests, ar + en, axe); API `PlatformMfaTests` (40) | PASS |
| | MFA replay, lockout | API `PlatformMfaTests` (time-step replay, parallel guesses capped, lockout and its reset rules) | PASS |
| Tenancy | clinic isolation | Browser `security.spec` (another clinic gets 404 for every record; UI shows nothing); API `TenantIsolationTests`, `ClinicalRecordsIsolationTests` | PASS |
| | branch isolation | Browser `security.spec`, `followups.spec`; API `StaffBranchScopeTests`, `DoctorBranchScopeTests` | PASS |
| | doctor isolation | Browser `security.spec` (only the treating doctor documents/completes/prescribes); API `ClinicalRecordsDoctorScopeTests` | PASS |
| | owner/manager permissions | Browser `owner.spec` (staff, roles, audit log); API `AuthorizationTests`, `StaffAndRolesTests` | PASS |
| | platform admin permissions | API `AccountAndPlatformTests` (`Platform_and_tenant_permissions_never_cross`), `PlatformSessionTests` | PASS |
| Clinical | patients | Browser `reception.spec` (register, shared-mobile warning, duplicate ID refused); API `PatientRulesTests` | PASS |
| | appointments, check-in | Browser `reception.spec` (book, reschedule, cancel, walk-in, check-in); API `AppointmentTests`, `WalkInRulesTests`, `RescheduleConcurrencyTests` | PASS |
| | visits, diagnosis | Browser `doctor.spec` (vitals, findings, diagnosis, plan, follow-up; completion locks the visit); API `VisitsApiTests`, `VisitCompletionApiTests` | PASS |
| | prescriptions, printing | Browser `doctor.spec` (issue + print view; completed visit takes no new prescription); API `PrescriptionsApiTests` | PASS |
| | previous visits, follow-ups | Browser `doctor.spec` (history), `followups.spec` (reception books from the list, operational fields only) | PASS |
| Business | plans, trial, active, past due, expired, suspension, downgrade, manual billing | API `SaasControlsTests` (trial start, platform plan/status/period change audited, ended period → read-only, suspension, downgrade keeps data), `QaGateDTests` (status × period matrix incl. PastDue); Browser `owner.spec` (subscription shown without prices) | PASS |
| Security | IDOR | Browser `security.spec` (stale rowVersion, made-up ids, forged identity fields refused or ignored); API isolation suites | PASS |
| | unauthorized endpoints, role enforcement | API `AuthorizationTests` (fallback policy, permission matrix); Browser `security.spec` (receptionist vs audit log / clinical records by API and URL) | PASS |
| | stale/revoked sessions, disabled users | API `SessionRevocationTests`; Browser `sessions.spec`, `security.spec` | PASS |
| | CSRF | API `ProxyOriginAndKeyTests` (foreign `Origin` refused on `/auth/refresh` and `/auth/logout`; `SameSite=Strict` cookie checked in the browser) | PASS |
| | refresh-token rules | API `RefreshTokenExposureTests` (rotation, reuse detection, native vs browser) | PASS |
| | rate limiting | API `PlatformMfaTests` (auth policy on MFA), `ProxyOriginAndKeyTests` (per real address), `MonitoringEventsTests` (refusal logged) | PASS |
| | audit logs | Browser `owner.spec` (read + filter); API `SaasControlsTests`, `PlatformMfaTests` (audit content), `MonitoringEventsTests` | PASS |
| | sensitive data in logs / URLs | API `SensitiveLoggingTests`, `OneTimeLinkTests`, `MonitoringEventsTests`; Browser `one-time-links.spec` (token never sent in a URL) | PASS |
| Frontend | Arabic RTL, English LTR, mobile/tablet/desktop | Browser `layout.spec` (public, owner and doctor pages × ar/en × 375/768/1024/1440: one h1, no horizontal overflow, axe WCAG 2.2 AA) | PASS |
| | accessibility, keyboard | Browser `accessibility.spec` (dialogs: axe, focus in, Esc, focus back; skip link; mobile navigation; first invalid field; language switch) | PASS |
| | empty / error / loading states | Unit (vitest component tests); Browser specs assert empty lists and refusal messages; UI review screenshots | PASS |
| | print | Browser `doctor.spec` (prescription print view) | PASS |

## Defects found and fixed during UAT

| # | Defect | Severity | Fix | Regression test |
|---|---|---|---|---|
| UAT-1 | Several tabs loading at the same moment (browser restore, links opened together) ended the user's session everywhere: each tab presented the same refresh cookie, the first rotation turned the others into "reuse" | HIGH (availability for clinic staff) | Web client refreshes take turns across tabs (Web Locks API); server unchanged | `refresh-token.spec` multi-tab test; vitest lock test |
| UAT-2 | `mfa.spec` waited for "an alert" that also matched Next.js's empty route announcer, so it could race the server's answer | test defect | Alerts scoped to `<main>` | the spec itself (run 3×) |
| UAT-3 | English patient-search placeholder clipped mid-word at desktop width | LOW (cosmetic) | Shorter English copy | — (visual) |
| UAT-4 | GD-04: an invitation could be accepted while the clinic was suspended | LOW | Refused with 403 before any change | `QaGateDTests` GD-04 |

## Not covered locally (needs staging / infrastructure)

Real email delivery, real TLS and domains, client IP through the real proxy, multi-instance behaviour, backup/restore,
alert delivery, load and latency baselines, a penetration test — see `docs/operations/go-live-checklist.md`.
