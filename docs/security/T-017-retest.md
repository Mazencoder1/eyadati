# T-017 Independent re-test: T-016 (QA-12, QA-16) and T-015 (QA-17)

- **Agent:** qa-security · **Branch:** `agent/qa-security` · **Base:** `integration` @ `d9aaca3`
- **Date:** 2026-10-06
- **Scope:**
  - T-016 (backend): QA-12 (disabled pending invitation reveals the account) and QA-16 (visit for a doctor hidden by branch).
  - T-015 (frontend), QA-17 part only: open redirect after sign-in. The rest of T-015 (invitation flows for existing accounts, new 403s) is **not** in this cycle.
  - Regression, tenant isolation, branch authorization, auth/redirect security, schema status.
- **Rules:** new test files only. No product code was changed and no existing test was edited.
- **Environment:** Linux container. .NET SDK 10.0.112, dotnet-ef 10.0.12, Node 22.22.0 with npm 11 (`npm ci` with npm 10 rejects the lockfile: environment difference, lockfile unchanged), SQL Server 2022 in a throw-away container. The integration tests hard-code Windows LocalDB, so for these runs only the connection string in `ApiFactory.cs` and `VisitCompletionSchemaTests.cs` was pointed at the container. That edit was never committed, and every test database was dropped afterwards.

## 1. Status of the findings

| ID | Severity | Fixed by | Re-test result | Evidence |
|---|---|---|---|---|
| QA-12 | Medium | T-016 (`a8cfa9c`) | **Verified fixed.** A Disabled seat stays masked on GET, list, search and sort unless the account joined **this** clinic. This holds across disable/enable cycles and after the victim's own failed sign-in to the prober clinic. The masked Disabled view is identical for registered and unregistered emails. An account that joined clinic A stays masked at clinic B. | `KnownDefect_QA12_*` (now `[Fact]`, body unchanged), `StaffInvitationConsentTests.QA12_*`, `QaRetestT016Tests.QA12_*` |
| QA-16 | Low | T-016 (`9e87eaa`) | **Verified fixed.** For a restricted member, a hidden doctor, a non-existent id and another clinic's doctor all return the same 404 `DOCTOR_NOT_FOUND`, with or without `branchId`. Nothing is recorded and no name leaks. Members of both branches, unrestricted owners and unscheduled doctors are unaffected (T-011 rule). | `QA16_restricted_member_cannot_record_a_visit_for_a_doctor_hidden_from_their_branch`, `QaRetestT016Tests.QA16_*` |
| QA-17 | Medium | T-015 (`1426cb2`) | **Verified fixed, including in a real browser.** | `safe-next-path.test.ts` (4 cases active), `safe-next-path.qa.test.ts` (2,023 generated inputs), Chromium E2E below |

### QA-17 in a real browser (Chromium 1194, `next start` + API on the throw-away DB)

I signed in through `/login?next=<payload>` and recorded the final URL and any request to the attacker host.

| `next` | Pre-fix build (`f191be5`) | Fixed build (`d9aaca3`) |
|---|---|---|
| `/%09/evil.example`, `/%0A/…`, `/%0D/…`, `/%09%5Cevil.example` | **navigated to evil.example** (2 requests each) | `/today`, 0 requests |
| `//evil.example`, `/%5Cevil.example`, `https://evil.example` | `/today` | `/today` |
| `/%252F/evil.example` | `/%2F/evil.example` (same origin) | `/today` |
| `/patients` (legitimate) | `/patients` | `/patients` |

This confirms that T-014's WHATWG-parser proof matched real browser behaviour, and that the fix closes it. `login-form.tsx` is the only place that redirects to a URL-supplied value. Every other `router.push/replace` and `redirect` uses fixed routes or API ids.

### Fix review notes

- **QA-12 design.** The seat has no "accepted at" column (DB-REQ-1). Acceptance is therefore read from the append-only audit log, scoped to the clinic's own id. Only three actions count: `auth.invitation.accepted`, `auth.login.succeeded` and `auth.tenant.switched`. The account owner can cause these only by accepting this clinic's invitation or while holding an active seat there. No clinic actor can write an audit row under another user's id: every explicit `userId:` audit call is in `AuthService` or tenant provisioning and uses the signed-in or registering user. The check fails closed: with no evidence, the seat is masked. Search and sort use the same rule, so ordering does not reveal the name.
- **QA-12 trade-offs (accepted, to be revisited with DB-REQ-1):**
  - The masking depends on audit retention. Purging old audit rows would re-mask genuine former members, which is safe but loses information.
  - Seats created before T-010 and later disabled are masked if their members never signed in. Only dev/test data exists today.
  - The lookup is a correlated subquery on `AuditLogs (TenantId, OccurredAtUtc)` and runs for Disabled seats only. It is fine at current scale. Review it in Phase 12.
- **QA-16.** The visibility rule now lives in `ClinicContext.VisibleDoctors` and is shared by the doctors API and `POST /visits`, so the two cannot drift. One existing test (`VisitsApiTests.Branch_restricted_doctor_only_sees_and_records_visits_of_their_branch`) depended on the hole. Its setup gained a schedule window in the member's branch. All of its assertions are unchanged, so this is not a weakening.

## 2. New findings

| ID | Severity | Location | Finding | Owner | Status / test |
|---|---|---|---|---|---|
| **QA-18** | Low | `backend/src/ClinicSaaS.Application/Prescriptions/PrescriptionService.cs:114` | **Same class as QA-16, on prescriptions.** A branch-restricted member with `Prescriptions.Create` can issue a prescription without a visit in the name of a doctor hidden from their branches. `GET /doctors/{id}` returns 404, but the prescription is created with 201 and returns the doctor's name. Prescriptions have no branch column, so the right rule needs a decision: apply the T-011 visibility rule to the named doctor, or require the linked visit's branch. Related to QA-14. | product decision → backend | `CurrentBehaviour_QA18_restricted_member_can_issue_a_prescription_in_the_name_of_a_hidden_doctor` |

Still open from earlier reports, unchanged by this cycle: QA-05, QA-06, QA-13, QA-14, QA-15, I-6, FE-SEC-1 (npm audit, needs a dependency upgrade decision), DATA-1, DB-REQ-1.

## 3. Regression, isolation and authorization

| Check | Result |
|---|---|
| Full gate on `integration` `7eef681` (after T-016) | Backend unit 70/70, integration 182/182 (0 skipped), EF: no pending model changes. Frontend lint, typecheck OK, vitest 90 passed + 4 skipped (QA-17 not merged yet), build OK |
| Full gate on `integration` `d9aaca3` (after T-015) | Backend unit 70/70, integration 182/182 (0 skipped), EF: no pending model changes. Frontend lint, typecheck OK, vitest **112/112** (0 skipped), build OK |
| With this report's tests (`agent/qa-security`) | See the merge log in `docs/agents/TASKS.md` |
| Tenant isolation | Existing suites pass unchanged (`TenantIsolationTests`, `ClinicalRecordsIsolationTests`, `QaTenantIsolationAuditTests`, `Tenant_B_cannot_complete_edit_or_reference_tenant_A_clinical_records`). The QA-12 evidence query is filtered by the clinic's id: acceptance or sign-in at another clinic never counts (tested both ways). A foreign doctor id on `POST /visits` returns `DOCTOR_NOT_FOUND`. |
| Branch authorization | `DoctorBranchScopeTests`, `StaffBranchScopeTests`, `QaBranchIsolationTests` (QA-01 `[Fact]`s) and the QA-16 tests all pass |
| Auth / redirect | QA-17 above. Invitation-link tampering and rebinding tests (T-014) still pass |
| Mutation checks | Without the T-016 code: 3 tests fail (QA-12 ×2, QA-16). Without the T-015 code: 15 cases in `safe-next-path.test.ts` and the 2,023-input property in `safe-next-path.qa.test.ts` fail |
| Weakened tests | None. One `CurrentBehaviour_` test that asserted QA-16 was replaced by its regression test (documented in `9e87eaa`). One setup-only change, described above |

## 4. Database schema status

- No file changed under `Persistence/Migrations`, `ClinicSaaS.Domain` or `docs/database` between `f191be5` and `d9aaca3`.
- `dotnet ef migrations has-pending-model-changes` reports no changes.
- No migration was applied to any non-test database. The only databases used were per-run `ClinicSaaS_Test_*` databases and one E2E database, all in a throw-away container and all dropped afterwards.

## 5. Tests added in T-017

- `backend/tests/ClinicSaaS.IntegrationTests/Qa/QaRetestT016Tests.cs`: 6 tests, all pass. One is a `CurrentBehaviour_` test (QA-18).
- `frontend/src/features/auth/safe-next-path.qa.test.ts`: 3 tests (a property over 2,023 generated inputs, a single-slash output invariant, and deep links that must keep working), all pass.
