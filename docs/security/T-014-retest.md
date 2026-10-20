# T-014 Independent re-test: T-002 fixes and the clinical-record rules

- **Agent:** qa-security · **Branch:** `agent/qa-security` · **Base:** `integration` @ `0e4f0ba`
- **Date:** 2026-10-06
- **Scope:**
  - The fixes for F1 (T-003), QA-03 (T-008), QA-01 and QA-04 (T-009), QA-02 (T-010) and F2 (T-011).
  - The new clinical-record rules: T-006 (visits and prescriptions), T-012 (own-doctor records) and T-013b (complete visit).
  - Frontend static review.
  - Not covered: T-015, which is still in progress on `agent/frontend`.
- **Rules:** new test files only. No product code was changed and no existing test was edited.

## 1. Status of the T-002 findings

| ID | T-002 severity | Fixed by | Re-test result | Evidence |
|---|---|---|---|---|
| F1 walk-in StartUtc | High | T-003 | **Verified fixed.** The validator rejects any client `StartUtc` on a walk-in, and the service always uses the server clock. Walk-ins still cannot be rescheduled. | `WalkInRulesTests` (backend agent). Code reviewed: `AppointmentService.cs` validator and `CreateAsync` |
| QA-01 branch escalation via staff | Medium | T-009 | **Verified fixed.** My two original tests now run as `[Fact]` with unchanged bodies, and both pass. `EnsureAssignableBranches` and `EnsureMemberWithinActorBranches` cover invite, update, disable and enable. | `KnownDefect_QA01_*` (now `[Fact]`) and `StaffBranchScopeTests` |
| QA-02 existing account attached without consent / profile disclosure | Medium | T-010 | **Partially fixed. Bypass found: QA-12 (open).** The invite response, GET, list and search are masked while a seat is `Invited`, and acceptance requires a sign-in as the invited account. However, disabling the pending seat removes the masking. | `QaRetestT014Tests` |
| QA-03 platform and clinic planes merged | Medium | T-008 | **Verified fixed.** A token carries one plane only, on login, switch-tenant, refresh and change-password. A platform token gets 403 on every tenant-member endpoint I tried. | `Platform_session_cannot_use_tenant_member_endpoints_and_clinic_session_never_regains_platform_permissions`, `PlatformSessionTests` |
| QA-04 branch change on a more privileged member | Low | T-009 | **Verified fixed.** | `Staff_manager_cannot_change_branch_restriction_of_a_more_privileged_member` |
| QA-05 access token outlives disable/suspend | Low (accepted) | none | **Unchanged.** This is a documented trade-off. | `CurrentBehaviour_access_token_keeps_working_*` |
| QA-06 lockout enumeration | Low | none | **Open.** | `CurrentBehaviour_lockout_response_reveals_that_an_email_is_registered` |
| F2 branch context | Decision | T-011 | **Matches the decision.** Doctors and working hours are branch-contextual; patients and services are clinic-wide. One residual path found: QA-16. | rewritten `QaBranchIsolationTests`, `DoctorBranchScopeTests` |

## 2. New findings

| ID | Severity | Location | Finding and reproduction | Owner | Status / test |
|---|---|---|---|---|---|
| **QA-12** | **Medium** | `backend/src/ClinicSaaS.Application/Staff/StaffService.cs:364` (`ToDto` masks only `Invited`), `:85` (search), `:206-207` | **Bypass of the QA-02 fix.** Steps: (1) clinic B invites a registered email; (2) it calls `POST /staff/{seat}/disable`; (3) it calls `GET /staff/{seat}`. The response returns the account's real `fullName`, `phoneNumber`, `emailConfirmed`, `hasPassword` (true means the email is registered), `lastLoginAtUtc` and `userId`. A `?status=Disabled&search=<real name>` query also finds the seat. The account owner never accepted. The schema does not record whether a seat was ever accepted, which the code comment at `SetStatusAsync` admits, so a Disabled seat is treated as a former member. | backend (may need database: an "accepted at" marker) | **Confirmed defect.** `KnownDefect_QA12_disabling_a_pending_invitation_must_not_reveal_the_invited_account` fails when run |
| **QA-17** | **Medium** | `frontend/src/features/auth/auth-card.tsx:31-34` (`safeNextPath`) | **Open redirect after login.** The function blocks `//` and `/\`, but not a path with an ASCII tab, LF or CR after the first `/`. Example: `/login?next=/%09/evil.example`. The browser URL parser removes those characters, so the value resolves to `//evil.example`, a different origin. `router.replace(next)` runs right after sign-in, or immediately if the user is already signed in. That makes this a phishing vector, for example a fake "session expired, sign in again" page. Proven with the WHATWG URL parser (`new URL`), which follows the same rules the browser applies. I did not drive a real browser. | frontend | **Confirmed defect.** `safe-next-path.test.ts`: 4 `KnownDefect QA-17` cases fail when run |
| QA-14 | Medium (decision needed) | `backend/src/ClinicSaaS.Application/Prescriptions/PrescriptionService.cs:114-128` | **Prescriber identity is not bound for non-doctors.** T-012 binds `doctorId` only for users who have a doctor profile and lack `ViewAllDoctors`. A `clinic_manager` (no doctor profile) or the owner can issue a prescription, which is a legal document, in any doctor's name. They can also link it to a visit of a different doctor: only the patient is checked against the visit. By contrast, visits must match their appointment's doctor. The named doctor then sees it as one of their own prescriptions. | product decision, then backend | `CurrentBehaviour_non_doctor_with_Prescriptions_Create_issues_in_any_doctors_name_on_any_doctors_visit` |
| QA-15 | Low (decision needed) | `PrescriptionService.cs:118` | After `POST /visits/{id}/complete`, new prescriptions can still be linked to the completed visit. The visit itself is locked (`VISIT_COMPLETED`), and a second visit for the appointment is refused. This may be intended, since prescriptions are separate immutable documents. | product decision | `CurrentBehaviour_a_completed_visit_can_still_receive_new_prescriptions` |
| QA-16 | Low | `backend/src/ClinicSaaS.Application/Visits/VisitService.cs:203`, `:227` | A branch-restricted member with `Visits.Create` can record a visit that has no appointment. The visit can name a doctor who is hidden from their branch: `GET /doctors/{id}` returns 404, yet the visit returns 201 with the doctor's name. Nothing checks that the doctor works at the visit's branch. Exploiting it requires knowing the doctor's id. | backend | `CurrentBehaviour_restricted_member_can_record_a_visit_for_a_doctor_hidden_from_their_branch` |
| QA-13 | Low (pre-existing) | `backend/src/ClinicSaaS.Application/Auth/AuthService.cs:49` | `POST /auth/register` answers `409 EMAIL_TAKEN` for any known email. That includes addresses that exist only because a clinic invited them. This is an anonymous account-existence check, limited only by the per-IP rate limit. QA-02 closed the same signal inside the staff API, but this one remains. | backend / product | `CurrentBehaviour_public_registration_reveals_that_an_email_is_registered` |
| I-6 | Info | `DoctorService.cs` (T-011) | `SCHEDULE_OVERLAP` against hidden windows lets a restricted manager probe when a shared doctor works in other branches. It is accepted in `DoctorBranchScopeTests` and is a small leak. | backend (note) | existing `Restricted_manager_schedule_update_still_prevents_overlap_with_hidden_windows` |

### Verified to hold (adversarial tests, all pass)

- **Invitation links**
  - Rebinding a link to another seat (same random part, other seat id) fails.
  - Stripping the seat id fails ("legacy" link for an existing account).
  - A one-character change fails.
  - Reuse after acceptance fails.
  - Accepting while signed in as a different account fails.
  - The anonymous "set password" path cannot be used for an existing account.
- **Pending seats:** edit, resend, enable, and disable followed by enable never activate a pending seat without the account owner.
- **T-012 own-doctor records:** doctor D2 cannot reach D1's visit or prescriptions through any route:
  - List filters `doctorId`, `patientId`, `appointmentId` and `branchId`, on both `/visits` and `/patients/{id}/visits|prescriptions`.
  - GET, PUT and complete on D1's visit.
  - Linking a prescription to D1's visit, or issuing one in D1's name.
  - Recording a visit for D1's appointment.
- **T-013b complete visit:** concurrent edit and complete with the same row version never both succeed, over 6 rounds. If complete wins, the edit never lands on the completed visit. Completing twice, a stale complete, other tenant, other branch and other doctor are covered by `VisitCompletionApiTests`.
- **Tenant isolation of the new endpoints:**
  - Complete and edit of another clinic's visit return 404.
  - Visits created with another clinic's appointment, patient, doctor or branch are refused.
  - Prescriptions created with another clinic's visit or patient are refused.

## 3. Assessment of the tests the backend agent replaced (compared with 86f1ac8)

| Test | Verdict |
|---|---|
| `Restricted_member_cannot_list_or_read_doctors_working_only_in_another_branch` (was `CurrentBehaviour_…can_list_and_read…`) | **Correct inversion, not weakened.** It asserts NotContain for both list queries, 404 `DOCTOR_NOT_FOUND`, and the booking refusal is kept. |
| `Restricted_manager_cannot_replace_schedule_or_deactivate_a_doctor_of_another_branch` | **Stronger.** It asserts 404 on all three writes, and the row version, active flag and windows are unchanged. The row version is now read through the owner, which is necessary because the manager can no longer read the doctor. |
| `Restricted_manager_can_edit_clinic_wide_services_and_patients_but_not_other_branches` | **Correct per the F2 decision.** The service and patient assertions are unchanged, and the branch edit is now 403 `BRANCH_ACCESS_DENIED` with no change to the name. |
| `KnownDefect_QA01_*` (attribute → `[Fact]`) | Bodies are unchanged, as the convention requires. The `KnownDefect_` prefix remains in the name, which is cosmetic only. |
| `Staff_manager_cannot_change_branch_restriction_of_a_more_privileged_member` | **Correct.** It asserts 403 `PERMISSION_ESCALATION` and that nothing changed. |
| `Platform_admin_who_is_also_a_clinic_member_never_gets_one_token_for_both_planes` | **Stronger.** Both planes are checked in both directions. |
| `Adding_an_existing_account_discloses_nothing_and_does_not_change_its_login` | **Acceptable, with one weak assertion.** `fullName NotBe("د. مالك")` would also pass if the prober's own typed name were echoed. The exact masked value (the lower-case invited email) is asserted in `StaffInvitationConsentTests.Invite_response_is_identical_for_registered_and_unregistered_emails`, so overall coverage is not weakened. This test, like the backend agent's, never checks the Disabled state, which is how QA-12 went unnoticed. |
| `Login_or_switch_…` and `StaffAndRolesTests.One_person_in_two_clinics…` | Only the setup changed (invite + accept through `AddExistingAccountAsync`), as T-010 requires. Assertions are unchanged. |

**Conclusion:** no assertion was weakened in a way that hides a defect.

## 4. Frontend static review (integration @ 0e4f0ba)

| Check | Result |
|---|---|
| XSS sinks | No `dangerouslySetInnerHTML`, `innerHTML`, `eval` or `new Function` anywhere in `src`. Link targets are built from API ids (`/patients/${id}`) and fixed routes. |
| Access token | Kept in memory only: `ApiClient.accessToken` in `src/lib/api/client.ts`. The refresh token is an httpOnly cookie (`credentials: "include"`), so JavaScript never sees it. No auth data goes to `localStorage` or `sessionStorage`. |
| `localStorage` | Used once: `today.doctor.<tenantId>.<userId>`, which stores the selected doctor id (`src/features/today/today-page.tsx:77-85`). It is not secret and the server stays authoritative. The tenant and user ids are readable by other scripts on the origin, which is low impact. |
| Open redirect | **QA-17** (see above). `app-shell.tsx` builds `next` from the current `pathname` and encodes it. That part is fine. |
| CSP (`src/proxy.ts`) | Good: a per-request nonce with `'strict-dynamic'`, `object-src 'none'`, `base-uri 'self'`, `form-action 'self'` and `frame-ancestors 'none'`. `connect-src` allows only self and the API origin. Minor points: `style-src 'unsafe-inline'` is needed for React style attributes but allows CSS injection, and `'unsafe-eval'` is enabled in development only. There is no `report-uri`/`report-to`. |
| Other headers (`next.config.ts`) | nosniff, DENY, strict-origin-when-cross-origin, Permissions-Policy, COOP, and HSTS in production. |
| Locale cookie | `samesite=lax`, not HttpOnly, and it holds no sensitive value. Fine. |

## 5. Tests added in T-014

`backend/tests/ClinicSaaS.IntegrationTests/Qa/QaRetestT014Tests.cs`:

- **Pass (10):**
  - `Invitation_link_cannot_be_rebound_to_another_seat_or_tampered`
  - `Pending_seat_cannot_be_activated_by_staff_management_without_the_account_owner`
  - `CurrentBehaviour_public_registration_reveals_that_an_email_is_registered`
  - `Platform_session_cannot_use_tenant_member_endpoints_and_clinic_session_never_regains_platform_permissions`
  - `Doctor_cannot_reach_another_doctors_visit_or_prescription_through_any_filter_or_link`
  - `CurrentBehaviour_non_doctor_with_Prescriptions_Create_issues_in_any_doctors_name_on_any_doctors_visit`
  - `Concurrent_edit_and_complete_with_the_same_row_version_never_both_succeed`
  - `CurrentBehaviour_a_completed_visit_can_still_receive_new_prescriptions`
  - `Tenant_B_cannot_complete_edit_or_reference_tenant_A_clinical_records`
  - `CurrentBehaviour_restricted_member_can_record_a_visit_for_a_doctor_hidden_from_their_branch`
- **Intentionally failing (skipped by default):** `KnownDefect_QA12_disabling_a_pending_invitation_must_not_reveal_the_invited_account`

`frontend/src/features/auth/safe-next-path.test.ts`:

- **Pass:** 11 cases (allowed paths and blocked values).
- **Intentionally failing (skipped by default):** 4 `KnownDefect QA-17` cases.

Known-defect tests are skipped unless `CLINIC_RUN_KNOWN_DEFECTS=1` is set:

```powershell
$env:CLINIC_RUN_KNOWN_DEFECTS = '1'
dotnet test backend/tests/ClinicSaaS.IntegrationTests --filter "Status=KnownDefect"
cd frontend; npx vitest run src/features/auth/safe-next-path.test.ts
```

## 6. Not checked

- **T-015** (accept-invitation, staff and platform-session UI on `agent/frontend`): not merged, not reviewed. The accept-invitation page in integration still posts `{token, password}` for every case.
- **Real-browser checks:** I did not drive a browser (Playwright) for QA-17 or for XSS/CSP. The review is static plus unit-level only.
- **Audit-log contents after acceptance:** `InvitationAccepted` includes `newAccount`, which is only visible after consent. I did not test it.
- **Infrastructure:** rate-limit effectiveness, JWT key rotation, and production configuration.
- **Branch and doctor bypasses through the frontend today view:** only the server rules were tested.
- **Mutation testing:** none of the new tests were mutation-tested.
