# T-002 Security Audit: tenant isolation, IDOR/BOLA, RBAC, branch restriction

- **Agent:** qa-security · **Branch:** `agent/qa-security` · **Base:** `integration` @ `9964da4`
- **Date:** 2026-10-06
- **Scope:** the backend as merged on integration (T-001 / commit 651811a included). No product code was changed.

## 1. Scope and method

| Area | Method |
|---|---|
| T-001 review (TenantId as concurrency token, `ThrowIfCrossTenant`) | Read `AppDbContext.cs`, `GlobalExceptionHandler.cs`, `ClinicalRecordsIsolationTests.cs`. Grepped `backend/src` for `ExecuteUpdate`, `ExecuteDelete`, `IgnoreQueryFilters`, `SqlQuery`, `ExecuteSql`, `FromSql`, `Attach/Update/Remove`. Added data-layer tests for join tables, the sync save path, missing rows and soft-deleted rows |
| Tenant isolation / IDOR / BOLA | Every id-taking endpoint in `docs/05-api-reference.md`, and every id inside a request body (`patientId`, `doctorId`, `branchId`, `serviceId`, `membershipId`, `roleIds`, `branchIds`, `registeredBranchId`, schedule `branchId`), checked against the code and the existing tests. Wrote tests only for gaps that existing tests did not cover |
| RBAC / privilege escalation | `RoleService`, `StaffService`, `PermissionAuthorization`, `AuthService` (login, refresh, switch-tenant), `PlatformTenantService` |
| Branch restriction (F2) | `ClinicContext.EnsureBranchAccess` / `RestrictedBranchIds` and every service that does or does not call them |

Tests are new files only, under `backend/tests/ClinicSaaS.IntegrationTests/Qa/`. No existing test was edited.

## 2. Findings

Severity is the impact inside this product's threat model (multi-tenant clinic SaaS, health data).
**Owner** is the agent that should fix it. **Status:** `Confirmed defect` = regression test written that fails today.
`Decision needed` = behaviour documented by a passing `CurrentBehaviour_` test; whether it changes is a product/security call.

| ID | Severity | Location | Finding | Owner | Status / test |
|---|---|---|---|---|---|
| **QA-01** | **Medium** | `backend/src/ClinicSaaS.Application/Staff/StaffService.cs:106`, `:157`, `:244` (`ValidateBranchesAsync`) | **Branch escalation through staff management.** A member restricted to branch Y who holds `Staff.Manage` (e.g. `clinic_manager`) can invite staff with no branch restriction (= every branch) or with branch X, and can remove another member's restriction. Only "the branch exists in this tenant" is checked, never the actor's own `RestrictedBranchIds`. The new account then reads and changes branch X appointments, which the actor cannot do. This breaks CLAUDE.md §6 and the security model's branch rule. | backend | **Confirmed defect.** `KnownDefect_QA01_restricted_manager_cannot_invite_staff_with_wider_branch_access`, `KnownDefect_QA01_restricted_manager_cannot_widen_an_existing_members_branch_access` (both fail when run: 201/200 instead of 403) |
| **QA-02** | **Medium** | `StaffService.cs:112-129` (existing account attached), `:294-299` (`ToDto`) | **Cross-tenant disclosure of global accounts, and membership without consent.** Any clinic with `Staff.Manage` can enter any email. If an account exists, it is attached to the clinic immediately (no acceptance step). The response and the staff list return that account's real `fullName`, `phoneNumber`, `emailConfirmed`, `hasPassword` and `lastLoginAtUtc`. These belong to a person who may be another clinic's user. `hasPassword` true/false also tells the prober whether the account exists. The victim's next login changes to `TENANT_SELECTION_REQUIRED`. | backend (+ product decision) | Decision needed. `CurrentBehaviour_adding_an_existing_account_discloses_its_profile_and_changes_its_login` |
| **QA-03** | **Medium** | `AuthService.cs:117-118`, `IssueSessionAsync` (platform + clinic permissions merged); `Api/Authorization/PermissionAuthorization.cs:50` | **Platform and clinic planes merge for a platform user who is also a clinic member.** With one membership, login automatically opens the clinic session. The clinic-bound token (`tid` set) also carries every platform permission, so it reaches `/platform/tenants` (suspend/reactivate) and clinic data. `docs/04-security-model.md` §3 says a clinic token never reaches `/platform`. Through QA-02, any clinic can create such a membership for a platform admin's email. | backend | Decision needed (doc/code conflict). `CurrentBehaviour_platform_admin_who_is_also_a_clinic_member_gets_one_token_for_both_planes` |
| **QA-04** | Low | `StaffService.cs:145-168` (`UpdateAsync`) vs `:171-175` (`SetStatusAsync`) | **Inconsistent guard.** A `Staff.Manage` holder cannot disable a member whose roles exceed their own permissions (`PERMISSION_ESCALATION`). The same actor can still change that member's branch restriction, for example confining a doctor to one branch, because `UpdateAsync` only checks the roles being added or removed. | backend | Decision needed. `CurrentBehaviour_staff_manager_can_change_branch_restriction_of_a_more_privileged_member` |
| **QA-05** | Low (documented trade-off) | `Program.cs` JWT setup; `HttpCurrentUser.cs` (claims only) | **An access token outlives disable and suspension.** Permissions, tenant and branches come only from the JWT, and there is no per-request membership or tenant-status check. Disabling a member or suspending a clinic revokes refresh tokens only, so an already-issued access token keeps working for up to 15 minutes. This matches what `docs/04-security-model.md` §3 states. Recorded here so the risk is accepted on purpose. | backend / manager | Decision needed. `CurrentBehaviour_access_token_keeps_working_after_disable_or_suspension_until_it_expires` |
| **QA-06** | Low | `AuthService.cs:88` | **Account enumeration via lockout.** An unknown email always gets `INVALID_CREDENTIALS`. A real account gets `423 ACCOUNT_LOCKED` after 5 failures, which confirms the email exists. It also lets anyone lock any known account for 15 minutes (DoS). The per-IP rate limit slows this down but does not stop it. | backend | Decision needed. `CurrentBehaviour_lockout_response_reveals_that_an_email_is_registered` |
| **F2** | Decision needed | `Doctors/DoctorService.cs:96`, `:112`, `:138`, `:153`; `ClinicServiceCatalog` (same file); `Organization/OrganizationServices.cs:122`; `Patients/PatientService.cs` | **No branch restriction outside appointments.** A member restricted to branch Y can list and read doctors who work only in X, including their branch-X working hours. A restricted manager can replace or wipe such a doctor's schedule and deactivate the doctor, edit services, read and edit patients registered at X, and edit branch X itself. Services have **no branch in the data model** (`Domain/Clinical/Doctor.cs:45`), while CLAUDE.md §6 lists them as branch-scoped. Booking with an X-only doctor is still refused, because appointments are restricted. | product decision → backend (and database if services become branch-scoped) | `CurrentBehaviour_restricted_member_can_list_and_read_doctors_working_only_in_another_branch`, `CurrentBehaviour_restricted_manager_can_replace_schedule_and_deactivate_a_doctor_of_another_branch`, `CurrentBehaviour_restricted_manager_can_edit_services_patients_and_other_branches` |
| I-1 | Info | `Persistence/Configurations/IdentityConfigurations.cs:50`, `:64`, `:95` | Join-table primary keys (`MembershipRoles`, `MembershipBranches`, `RolePermissions`) do not include `TenantId`. A raw insert that collides with another tenant's key fails with PK error 2627 before the composite FK check (547). No endpoint can reach this. It only matters as a DB-level existence oracle. | database (note only) | Covered indirectly by `T001_join_tables_cannot_be_forged_or_cross_linked_between_tenants` |
| I-2 | Info | `AppDbContext.cs:144-151` | `ThrowIfCrossTenant` answers 404 for a foreign row and 409 for a missing row. At the data layer this is an existence oracle. No endpoint writes an entity it did not load first (verified by grep: every `Update`/`Remove` follows a filtered load), so the API cannot reach it today. Keep load-before-write as a rule for T-006. | backend (rule) | `T001_forged_write_to_a_missing_row_stays_a_concurrency_conflict_and_soft_deleted_foreign_rows_stay_protected` |
| I-3 | Info | `Saas/SubscriptionQueries.cs:55` | `AuditLog` is not `ITenantOwned` (nullable `TenantId`, no global filter). Isolation relies on one hand-written `Where`. It is correct today but fragile: any new audit-log query must repeat it. | backend | Existing `Audit_log_is_a_plan_feature_and_only_shows_own_clinic` |
| I-4 | Info | `Patients/PatientService.cs:155` | A `Patients.View` holder sees identity numbers masked, but can search by a full 10-digit identity number. A hit confirms that the number belongs to the patient (lookup oracle). | backend | none (behaviour noted) |
| I-5 | Info | `Errors/GlobalExceptionHandler.cs:31` | FK/CHECK violations (SQL 547) surface as `500 INTERNAL_ERROR`. No data leaks (logged with correlation id only), but a cross-tenant reference that slipped past the service layer would look like a server error rather than 404/400. | backend | none |

### Doc/code conflicts (per CLAUDE.md §2: reported, not resolved)

1. CLAUDE.md §6 says doctors, working hours and services are branch-scoped. In the code only appointments are, and services have no branch at all (F2).
2. `docs/04-security-model.md` §3 says a clinic token does not reach `/platform`. That is false for a platform user with a clinic membership (QA-03).
3. CLAUDE.md §20 says about 115 tests. Before this task, verify ran 64 unit + 76 integration = 140. After this task: 64 unit + 102 integration (100 run, 2 skipped).

## 3. T-001 review (TenantId concurrency token): verdict **sound**

| Question | Result |
|---|---|
| Which entities get the guard? | Every root entity implementing `ITenantOwned`, including the join tables `RolePermission`, `MembershipRole`, `MembershipBranch`, plus `NumberSequence`, `TenantFeatureOverride` and `DoctorSchedule`. Owned types are skipped (`!t.IsOwned()`), but there are none in the model today. Forged deletes and updates of join rows were proven to raise `TenantIsolationViolationException`, and cross-linking them was proven to be refused by the composite FKs (new test). |
| Tenant data in non-`ITenantOwned` entities? | `AuditLog` (I-3) and `RefreshToken` (scoped by `UserId` / hashed token; tenant id only used for revocation). `User` and `VerificationCode` are global by design. |
| Bulk operations bypassing SaveChanges | `ExecuteUpdateAsync` appears twice (`AuthService.cs:398`, `StaffService.cs:203`), both on `VerificationCodes` (global, filtered by `UserId`). There is no `ExecuteDelete`. |
| Raw SQL | `SequenceGenerator` (`Infrastructure/Services/CoreServices.cs`): parameterised, tenant id from the tenant context. `AcquireLockAsync`: parameterised. Nothing is built from strings. |
| `IgnoreQueryFilters` | `AuthService.cs:341` lifts the tenant filter only, scoped to the caller's own `UserId` and active memberships. `PlatformTenantService.cs:38-39` is read-only counts behind `Platform.Tenants.View`. `AppointmentService.cs:369` lifts soft-delete only, so the tenant filter still applies to every joined table. All acceptable. |
| Same-tenant concurrency still 409? | Yes (`Same_tenant_concurrency_conflicts_and_deletes_behave_as_before`, `Concurrent_edits_are_detected_instead_of_silently_overwritten`). |
| Deleted rows | A missing row gives `DbUpdateConcurrencyException` (409). A soft-deleted row of another tenant still gives an isolation violation (new test). |
| Sync `SaveChanges` path | It works the same as the async path (new test). |
| Information leak | The response is a generic `404 NOT_FOUND`. The log is `Critical` with the entity type and state only: no ids, no values, no tokens. |

## 4. Tests added (all new files)

`backend/tests/ClinicSaaS.IntegrationTests/Qa/`:

| File | Tests | Expected |
|---|---|---|
| `QaSupport.cs` | `KnownDefectFactAttribute` (skips unless `CLINIC_RUN_KNOWN_DEFECTS=1`), `QaScenarios` helpers (two-branch clinic, invite with explicit roles, platform user) | n/a |
| `QaTenantIsolationAuditTests.cs` | `Tenant_B_cannot_read_or_change_tenant_A_doctor_profile_schedule_or_availability`, `Tenant_B_cannot_change_tenant_A_services_branches_or_roles`, `Tenant_B_cannot_read_or_manage_tenant_A_staff_and_cannot_assign_A_roles_or_branches`, `Tenant_B_cannot_drive_tenant_A_appointment_through_any_transition_or_filter_on_A_ids`, `Tenant_B_cannot_reference_tenant_A_membership_branch_service_or_doctor_in_its_own_writes`, `T001_join_tables_cannot_be_forged_or_cross_linked_between_tenants`, `T001_synchronous_SaveChanges_maps_a_forged_cross_tenant_write_to_an_isolation_violation`, `T001_forged_write_to_a_missing_row_stays_a_concurrency_conflict_and_soft_deleted_foreign_rows_stay_protected` | pass (8) |
| `QaRbacAuditTests.cs` | `Role_editor_cannot_add_or_remove_permissions_they_do_not_hold_on_existing_roles`, `Clinic_manager_without_roles_manage_cannot_create_edit_or_delete_roles`, `Staff_manager_cannot_disable_or_strip_a_more_privileged_member_nor_change_self_or_owner`, `Login_or_switch_into_a_clinic_without_active_membership_or_while_suspended_is_refused`, + 5 `CurrentBehaviour_` (QA-02, QA-03, QA-04, QA-05, QA-06) | pass (9) |
| `QaBranchIsolationTests.cs` | `Restricted_member_cannot_reschedule_or_transition_an_appointment_of_another_branch`, `Restricted_member_list_filters_never_reveal_another_branchs_appointments`, `Restricted_member_cannot_move_or_book_into_another_branch`, `Restricted_member_availability_shows_only_own_branch_slots`, + 3 `CurrentBehaviour_` (F2) | pass (7) |
| `QaBranchIsolationTests.cs` | `KnownDefect_QA01_restricted_manager_cannot_invite_staff_with_wider_branch_access`, `KnownDefect_QA01_restricted_manager_cannot_widen_an_existing_members_branch_access` (`[Trait("Status","KnownDefect")]`) | **fail** when run. Skipped in the default run |

To run the known-defect tests:

```powershell
$env:CLINIC_RUN_KNOWN_DEFECTS = '1'
dotnet test backend/tests/ClinicSaaS.IntegrationTests --filter "Status=KnownDefect"
```

When QA-01 is fixed, the owner replaces `[KnownDefectFact("QA-01")]` with `[Fact]`. The test is never deleted.
When a `Decision needed` item is decided, the matching `CurrentBehaviour_` test is rewritten to assert the new rule.

**Branch-restriction result for appointments (required):** no bypass was found. Get, list (every filter), reschedule (from, into or out of another branch), confirm, check-in, complete, cancel, no-show, walk-in, booking and availability all respect `br` claims.

## 5. Not checked

- The walk-in `StartUtc` defect (F1). The backend agent owns it in T-003. It will be re-tested after merge.
- Visits, Prescriptions and Files endpoints (T-006 not merged yet). Their data-layer isolation is covered by `ClinicalRecordsIsolationTests`.
- Frontend (Node.js not installed): XSS, token storage, CSRF on the cookie refresh flow.
- Rate-limit effectiveness. Tests run with limits set to 100000, and the in-process limiter does not hold across nodes.
- Refresh-cookie attributes beyond the existing registration test. JWT algorithm confusion or `kid` handling. Key rotation.
- Invitation token entropy and timing. Email-change flows (none exist). Password-reset race conditions.
- Concurrency and mutation testing of the new tests (no product mutation was applied, because product code is out of scope). The appointment double-booking protections were not re-tested beyond the existing suite.
- Logs at runtime (only the code paths were read). Security headers beyond the existing test.
- Production configuration: secrets, HTTPS, forwarded headers, CORS origins.

## 6. Status update (T-014 re-test, 2026-10-06, integration @ 0e4f0ba)

| ID | Status |
|---|---|
| F1 | Fixed (T-003), verified |
| QA-01 | Fixed (T-009), verified (the KnownDefect tests are now `[Fact]` and pass) |
| QA-02 | Partially fixed (T-010). **Open bypass QA-12**: disabling a pending seat unmasks the account |
| QA-03 | Fixed (T-008), verified |
| QA-04 | Fixed (T-009), verified |
| QA-05 | Accepted trade-off, unchanged |
| QA-06 | Open |
| F2 | Decided and implemented (T-011), verified. Residual: QA-16 |

Details and the new findings (QA-12 to QA-17) are in `docs/security/T-014-retest.md`.
