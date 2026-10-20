using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests.Qa;

/// <summary>
/// T-014: independent, adversarial re-test of the T-002 fixes (QA-01..QA-04, F1, F2) and of the new clinical-record
/// rules (T-006 visits/prescriptions, T-012 own-doctor records, T-013b complete visit). Findings and decisions are in
/// docs/security/T-014-retest.md. <c>KnownDefect_</c> tests fail until fixed (skipped by default, see
/// <see cref="KnownDefectFactAttribute"/>); <c>CurrentBehaviour_</c> tests document behaviour awaiting a decision.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QaRetestT014Tests(ApiFactory factory)
{
    // ------------------------------------------------------------------ QA-02 re-test: invitation consent / enumeration

    [Fact]
    public async Task KnownDefect_QA12_disabling_a_pending_invitation_must_not_reveal_the_invited_account()
    {
        // Bypass of the QA-02 fix: ToDto masks only Status == Invited. Invite a registered email, disable the pending
        // seat, and the clinic sees the global account (real name, password state, last login, user id) — the profile
        // of a person who never accepted, and whether the email is registered at all.
        var victim = await factory.RegisterClinicAsync();
        await factory.LoginAsync(victim.Email); // sets LastLoginAtUtc
        var prober = await ProfessionalClinicAsync();
        var seat = (await (await prober.Client.PostAsJsonAsync("/api/v1/staff", new
        {
            fullName = "x", email = victim.Email, roleIds = new[] { await prober.Client.RoleIdAsync("staff") },
        })).DataAsync(201)).GetProperty("membershipId").GetGuid();

        await (await prober.Client.PostAsync($"/api/v1/staff/{seat}/disable", null)).EnsureStatusAsync(204);

        var member = await (await prober.Client.GetAsync($"/api/v1/staff/{seat}")).DataAsync(200);
        member.GetProperty("fullName").GetString().Should().NotBe("د. مالك", "a never-accepted seat must stay masked after it is disabled");
        member.GetProperty("hasPassword").GetBoolean().Should().BeFalse("password state reveals that the email is registered");
        member.GetProperty("userId").GetGuid().Should().Be(Guid.Empty);
        (member.TryGetProperty("lastLoginAtUtc", out var last) && last.ValueKind != JsonValueKind.Null).Should().BeFalse();

        var byName = await (await prober.Client.GetAsync("/api/v1/staff?status=Disabled&search=" + Uri.EscapeDataString("د. مالك"))).DataAsync(200);
        byName.GetArrayLength().Should().Be(0, "search by the real name must not find a never-accepted seat");
    }

    [Fact]
    public async Task Invitation_link_cannot_be_rebound_to_another_seat_or_tampered()
    {
        var victim = await factory.RegisterClinicAsync();
        var victimClient = await factory.LoginAsync(victim.Email);
        var clinicA = await ProfessionalClinicAsync();
        var clinicB = await ProfessionalClinicAsync();
        await InviteAsync(clinicA.Client, victim.Email, "staff");
        var tokenA = factory.Emails.TokenFor(victim.Email, "/accept-invitation");
        var seatB = await InviteAsync(clinicB.Client, victim.Email, "staff");

        // Same random part, clinic B's seat id: the stored hash covers the whole value, so the link is unknown.
        var rebound = $"{seatB:N}{tokenA[32..]}";
        (await (await victimClient.PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token = rebound })).ErrorCodeAsync())
            .Should().Be("INVALID_OR_EXPIRED_TOKEN");
        // Seat id stripped (a "legacy" link) for an existing account: refused, never treated as a password link.
        (await (await victimClient.PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token = tokenA[33..] })).ErrorCodeAsync())
            .Should().Be("INVALID_OR_EXPIRED_TOKEN");
        // One character changed.
        var flipped = tokenA[..^1] + (tokenA[^1] == 'A' ? 'B' : 'A');
        (await (await victimClient.PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token = flipped })).ErrorCodeAsync())
            .Should().Be("INVALID_OR_EXPIRED_TOKEN");

        (await (await clinicB.Client.GetAsync($"/api/v1/staff/{seatB}")).DataAsync(200)).GetProperty("status").GetString().Should().Be("Invited");
        (await (await victimClient.GetAsync("/api/v1/auth/tenants")).DataAsync(200)).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Pending_seat_cannot_be_activated_by_staff_management_without_the_account_owner()
    {
        var victim = await factory.RegisterClinicAsync();
        var clinic = await ProfessionalClinicAsync();
        var seat = await InviteAsync(clinic.Client, victim.Email, "receptionist");
        var rv = await clinic.Client.RowVersionAsync($"/api/v1/staff/{seat}");

        // Editing roles/branches, resending, enabling, disabling + enabling: the seat never becomes Active.
        await (await clinic.Client.PutAsJsonAsync($"/api/v1/staff/{seat}", new { roleIds = new[] { await clinic.Client.RoleIdAsync("staff") }, rowVersion = rv }))
            .EnsureStatusAsync(200);
        await (await clinic.Client.PostAsync($"/api/v1/staff/{seat}/resend-invitation", null)).EnsureStatusAsync(202);
        await (await clinic.Client.PostAsync($"/api/v1/staff/{seat}/enable", null)).EnsureStatusAsync(204);
        await (await clinic.Client.PostAsync($"/api/v1/staff/{seat}/disable", null)).EnsureStatusAsync(204);
        await (await clinic.Client.PostAsync($"/api/v1/staff/{seat}/enable", null)).EnsureStatusAsync(204);

        (await (await clinic.Client.GetAsync($"/api/v1/staff/{seat}")).DataAsync(200)).GetProperty("status").GetString().Should().Be("Invited");
        (await (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = victim.Email, password = TestApi.Password, tenantId = clinic.TenantId }))
            .ErrorCodeAsync()).Should().Be("NO_ACTIVE_MEMBERSHIP");
    }

    [Fact]
    public async Task CurrentBehaviour_public_registration_reveals_that_an_email_is_registered()
    {
        // QA-13 (Low, pre-existing): /auth/register answers 409 EMAIL_TAKEN for any known address, including one
        // that only exists because a clinic invited it. Anonymous, rate-limited per IP only.
        var known = await factory.RegisterClinicAsync();
        var res = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new
        {
            clinicName = "x", city = "الرياض", clinicPhone = "0501234567", ownerFullName = "x", email = known.Email, password = TestApi.Password, acceptTerms = true,
        });
        (await res.ErrorCodeAsync()).Should().Be("EMAIL_TAKEN", "CURRENT behaviour: registration is an account-existence oracle");
    }

    // ------------------------------------------------------------------ QA-03 re-test: one plane per token

    [Fact]
    public async Task Platform_session_cannot_use_tenant_member_endpoints_and_clinic_session_never_regains_platform_permissions()
    {
        var adminEmail = await factory.CreatePlatformUserAsync(PlatformRole.SuperAdmin);
        var clinic = await ProfessionalClinicAsync();
        await factory.AddExistingAccountAsync(clinic.Client, adminEmail, await clinic.Client.RoleIdAsync("receptionist"));

        var platform = await LoginAsync(adminEmail, tenantId: null);
        foreach (var url in new[] { "/api/v1/settings", "/api/v1/branches", "/api/v1/services", "/api/v1/appointments", "/api/v1/visits" })
            (await platform.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden, url);

        // Clinic session via switch-tenant, then a password change (which re-issues a session): still clinic-only.
        var switched = await (await platform.PostAsJsonAsync("/api/v1/auth/switch-tenant", new { tenantId = clinic.TenantId })).DataAsync(200);
        var clinicClient = factory.WithToken(switched.GetProperty("accessToken").GetString()!);
        var changed = await (await clinicClient.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = TestApi.Password, newPassword = "N3w-Passw0rd-1" }))
            .DataAsync(200);
        changed.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().NotContain(p => p!.StartsWith("Platform."));
        var afterChange = factory.WithToken(changed.GetProperty("accessToken").GetString()!);
        (await afterChange.GetAsync("/api/v1/platform/tenants")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await afterChange.GetAsync("/api/v1/appointments")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------ T-012 re-test: own-doctor records

    [Fact]
    public async Task Doctor_cannot_reach_another_doctors_visit_or_prescription_through_any_filter_or_link()
    {
        var c = await factory.BookableClinicAsync();
        var appt = await c.Owner.Client.CheckedInAppointmentAsync(c);
        var visitOfD1 = await c.Owner.Client.RecordVisitAsync(c, appt, ClinicalScenarios.Clinical());
        var v1 = visitOfD1.GetProperty("id").GetGuid();
        var rv1 = visitOfD1.GetProperty("rowVersion").GetString();
        await (await c.Owner.Client.PostAsJsonAsync("/api/v1/prescriptions", new
        {
            patientId = c.PatientId, doctorId = c.DoctorId, visitId = v1, items = new[] { new { medicationName = "Amoxicillin" } },
        })).EnsureStatusAsync(201);

        var (d2, d2Seat, _) = await factory.InviteAndAcceptAsync(c.Owner, "doctor"); // profile created next; scope resolves per request
        var d2Id = (await c.Owner.Client.CreateDoctorAsync(d2Seat, Scenarios.Week(c.BranchId, "06:00:00", "07:00:00"))).GetProperty("id").GetGuid();

        foreach (var q in new[] { $"doctorId={c.DoctorId}", $"patientId={c.PatientId}", $"appointmentId={appt}", $"branchId={c.BranchId}", "pageSize=100" })
            (await (await d2.GetAsync($"/api/v1/visits?{q}")).ReadJsonAsync()).GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(0, q);
        (await (await d2.GetAsync($"/api/v1/patients/{c.PatientId}/visits?doctorId={c.DoctorId}")).ReadJsonAsync())
            .GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(0);
        (await (await d2.GetAsync($"/api/v1/patients/{c.PatientId}/prescriptions")).ReadJsonAsync())
            .GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(0);

        (await d2.GetAsync($"/api/v1/visits/{v1}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await d2.PutAsJsonAsync($"/api/v1/visits/{v1}", new { clinical = ClinicalScenarios.Clinical("tampered"), rowVersion = rv1 }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await d2.PostAsJsonAsync($"/api/v1/visits/{v1}/complete", new { rowVersion = rv1 })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Linking a prescription to D1's visit, in D2's name or in D1's name.
        (await (await d2.PostAsJsonAsync("/api/v1/prescriptions", new
        {
            patientId = c.PatientId, doctorId = d2Id, visitId = v1, items = new[] { new { medicationName = "x" } },
        })).ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");
        // D8: without a visit there is no prescription at all, whatever doctor the body names.
        (await (await d2.PostAsJsonAsync("/api/v1/prescriptions", new
        {
            patientId = c.PatientId, doctorId = c.DoctorId, items = new[] { new { medicationName = "x" } },
        })).ErrorCodeAsync()).Should().Be("VALIDATION_FAILED");
        // Recording a visit for D1's appointment, in either name.
        (await (await d2.PostAsJsonAsync("/api/v1/visits", new { patientId = c.PatientId, doctorId = c.DoctorId, appointmentId = appt })).ErrorCodeAsync())
            .Should().Be("DOCTOR_NOT_FOUND");
        (await d2.PostAsJsonAsync("/api/v1/visits", new { patientId = c.PatientId, doctorId = d2Id, appointmentId = appt }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "the appointment belongs to another doctor");

        var after = await (await c.Owner.Client.GetAsync($"/api/v1/visits/{v1}")).DataAsync(200);
        after.GetProperty("rowVersion").GetString().Should().Be(rv1);
        after.GetProperty("status").GetString().Should().Be("Open");
    }

    [Fact]
    public async Task QA14_fixed_non_doctor_with_Prescriptions_Create_cannot_issue_in_any_doctors_name()
    {
        // QA-14 (Medium) — fixed by D8: the prescriber is the signed-in caller and must be the visit's own doctor. A
        // clinic_manager (no doctor profile; ViewAllDoctors; Prescriptions.Create) is refused, whatever doctor the body
        // names, and nothing is issued.
        var c = await factory.BookableClinicAsync();
        var appt = await c.Owner.Client.CheckedInAppointmentAsync(c);
        var v1 = (await c.Owner.Client.RecordVisitAsync(c, appt)).GetProperty("id").GetGuid();
        var (_, d2Seat, d2Email) = await factory.InviteAndAcceptAsync(c.Owner, "doctor");
        var d2Id = (await c.Owner.Client.CreateDoctorAsync(d2Seat, Scenarios.Week(c.BranchId, "06:00:00", "07:00:00"))).GetProperty("id").GetGuid();
        var (manager, _, _) = await factory.InviteAndAcceptAsync(c.Owner, "clinic_manager");

        foreach (var doctorId in new[] { d2Id, c.DoctorId })
        {
            var res = await manager.PostAsJsonAsync("/api/v1/prescriptions", new
            {
                patientId = c.PatientId, doctorId, visitId = v1, items = new[] { new { medicationName = "Tramadol" } },
            });
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await res.ErrorCodeAsync()).Should().Be("PRESCRIBER_NOT_TREATING_DOCTOR");
        }

        (await (await c.Owner.Client.GetAsync($"/api/v1/patients/{c.PatientId}/prescriptions")).ReadJsonAsync())
            .GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(0);
        var d2 = await factory.LoginAsync(d2Email);
        (await (await d2.GetAsync($"/api/v1/patients/{c.PatientId}/prescriptions")).ReadJsonAsync())
            .GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(0);
    }

    // ------------------------------------------------------------------ T-013b re-test: complete visit

    [Fact]
    public async Task Concurrent_edit_and_complete_with_the_same_row_version_never_both_succeed()
    {
        var c = await factory.BookableClinicAsync();
        // Several fresh visits so both interleavings get a chance to occur.
        for (var round = 0; round < 6; round++)
        {
            var appt = await c.Owner.Client.CheckedInAppointmentAsync(c, daysAhead: 1, hour: 9 + round);
            var visit = await c.Owner.Client.RecordVisitAsync(c, appt, ClinicalScenarios.Clinical("original"));
            var id = visit.GetProperty("id").GetGuid();
            var rv = visit.GetProperty("rowVersion").GetString();

            var edit = factory.WithToken(c.Owner.AccessToken).PutAsJsonAsync($"/api/v1/visits/{id}", new { clinical = ClinicalScenarios.Clinical("late edit"), rowVersion = rv });
            var complete = factory.WithToken(c.Owner.AccessToken).PostAsJsonAsync($"/api/v1/visits/{id}/complete", new { rowVersion = rv });
            var results = await Task.WhenAll(edit, complete);
            results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1, "both writes carried the same row version");

            var final = await (await c.Owner.Client.GetAsync($"/api/v1/visits/{id}")).DataAsync(200);
            var diagnosis = final.GetProperty("clinical").GetProperty("diagnosis").GetString();
            if (results[1].StatusCode == HttpStatusCode.OK)
            {
                final.GetProperty("status").GetString().Should().Be("Completed");
                diagnosis.Should().Be("original", "an edit must never land on a completed visit");
            }
            else
            {
                final.GetProperty("status").GetString().Should().Be("Open");
                diagnosis.Should().Be("late edit");
            }
        }
    }

    [Fact]
    public async Task A_completed_visit_receives_no_new_prescription_and_keeps_its_existing_ones()
    {
        // QA-15, decided at MVP acceptance: a completed visit is clinically locked, prescriptions are written while it
        // is open. Prescriptions issued before completion stay readable.
        var c = await factory.BookableClinicAsync();
        var appt = await c.Owner.Client.CheckedInAppointmentAsync(c);
        var visit = await c.Owner.Client.RecordVisitAsync(c, appt);
        var id = visit.GetProperty("id").GetGuid();
        var issued = await (await c.Owner.Client.PostAsJsonAsync("/api/v1/prescriptions", new
        {
            visitId = id, items = new[] { new { medicationName = "before completion" } },
        })).DataAsync(201);
        var current = await (await c.Owner.Client.GetAsync($"/api/v1/visits/{id}")).DataAsync(200);
        await (await c.Owner.Client.PostAsJsonAsync($"/api/v1/visits/{id}/complete", new { rowVersion = current.GetProperty("rowVersion").GetString() }))
            .EnsureStatusAsync(200);

        var after = await c.Owner.Client.PostAsJsonAsync("/api/v1/prescriptions", new
        {
            patientId = c.PatientId, doctorId = c.DoctorId, visitId = id, items = new[] { new { medicationName = "x" } },
        });
        after.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await after.ErrorCodeAsync()).Should().Be("VISIT_COMPLETED");

        var existing = await (await c.Owner.Client.GetAsync($"/api/v1/prescriptions/{issued.GetProperty("id").GetGuid()}")).DataAsync(200);
        existing.GetProperty("items")[0].GetProperty("medicationName").GetString().Should().Be("before completion");
        var list = await (await c.Owner.Client.GetAsync($"/api/v1/patients/{c.PatientId}/prescriptions")).ReadJsonAsync();
        list.GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(1, "the refused request created nothing");
        // A second visit for the same appointment remains impossible.
        (await (await c.Owner.Client.PostAsJsonAsync("/api/v1/visits", new { patientId = c.PatientId, doctorId = c.DoctorId, appointmentId = appt }))
            .ErrorCodeAsync()).Should().Be("VISIT_ALREADY_RECORDED");
    }

    // ------------------------------------------------------------------ tenant isolation of the new endpoints

    [Fact]
    public async Task Tenant_B_cannot_complete_edit_or_reference_tenant_A_clinical_records()
    {
        var a = await factory.BookableClinicAsync();
        var b = await factory.BookableClinicAsync();
        var apptA = await a.Owner.Client.CheckedInAppointmentAsync(a);
        var visitA = await a.Owner.Client.RecordVisitAsync(a, apptA, ClinicalScenarios.Clinical());
        var vA = visitA.GetProperty("id").GetGuid();
        var rvA = visitA.GetProperty("rowVersion").GetString();
        var apptB = await b.Owner.Client.CheckedInAppointmentAsync(b);

        (await (await b.Owner.Client.PostAsJsonAsync($"/api/v1/visits/{vA}/complete", new { rowVersion = rvA })).ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");
        (await (await b.Owner.Client.PutAsJsonAsync($"/api/v1/visits/{vA}", new { clinical = ClinicalScenarios.Clinical("x"), rowVersion = rvA })).ErrorCodeAsync())
            .Should().Be("VISIT_NOT_FOUND");

        // Body ids of A inside B's writes.
        (await (await b.Owner.Client.PostAsJsonAsync("/api/v1/visits", new { patientId = b.PatientId, doctorId = b.DoctorId, appointmentId = apptA })).ErrorCodeAsync())
            .Should().Be("APPOINTMENT_NOT_FOUND");
        (await (await b.Owner.Client.PostAsJsonAsync("/api/v1/visits", new { patientId = a.PatientId, doctorId = b.DoctorId, branchId = b.BranchId })).ErrorCodeAsync())
            .Should().Be("PATIENT_NOT_FOUND");
        (await (await b.Owner.Client.PostAsJsonAsync("/api/v1/visits", new { patientId = b.PatientId, doctorId = a.DoctorId, branchId = b.BranchId })).ErrorCodeAsync())
            .Should().Be("DOCTOR_NOT_FOUND");
        (await (await b.Owner.Client.PostAsJsonAsync("/api/v1/visits", new { patientId = b.PatientId, doctorId = b.DoctorId, branchId = a.BranchId })).ErrorCodeAsync())
            .Should().Be("BRANCH_NOT_FOUND");
        (await (await b.Owner.Client.PostAsJsonAsync("/api/v1/prescriptions", new
        {
            patientId = b.PatientId, doctorId = b.DoctorId, visitId = vA, items = new[] { new { medicationName = "x" } },
        })).ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");
        // D8: A's patient named in B's body on B's own visit is ignored — the prescription is for the visit's patient.
        var vB = await b.Owner.Client.VisitIdAsync(b);
        (await (await b.Owner.Client.PostAsJsonAsync("/api/v1/prescriptions", new
        {
            patientId = a.PatientId, doctorId = a.DoctorId, visitId = vB, items = new[] { new { medicationName = "x" } },
        })).DataAsync(201)).GetProperty("patientId").GetGuid().Should().Be(b.PatientId);
        // Control: B's own appointment still works with the same body shape.
        await (await b.Owner.Client.PostAsJsonAsync("/api/v1/visits", new { patientId = b.PatientId, doctorId = b.DoctorId, appointmentId = apptB })).EnsureStatusAsync(201);

        var after = await (await a.Owner.Client.GetAsync($"/api/v1/visits/{vA}")).DataAsync(200);
        after.GetProperty("status").GetString().Should().Be("Open");
        after.GetProperty("rowVersion").GetString().Should().Be(rvA);
    }

    // ------------------------------------------------------------------ F2 re-test: hidden doctors through clinical records

    [Fact]
    public async Task QA16_restricted_member_cannot_record_a_visit_for_a_doctor_hidden_from_their_branch()
    {
        // QA-16 (Low, fixed in T-016): T-011 hides doctors without windows in the member's branches ("not found" on
        // /doctors). Recording a visit without an appointment used to accept any active doctor of the clinic and return
        // the doctor's name; it now applies the same visibility rule. (Was CurrentBehaviour_restricted_member_can_record_
        // a_visit_for_a_doctor_hidden_from_their_branch, which asserted the defect.)
        var t = await factory.TwoBranchClinicAsync();
        var (mainOnlyClient, seat, _) = await factory.InviteAndAcceptAsync(t.Owner, "doctor");
        var mainOnly = (await t.OwnerClient.CreateDoctorAsync(seat,
            new[] { new { branchId = t.MainBranchId, dayOfWeek = "Sunday", startTime = "08:00:00", endTime = "12:00:00" } })).GetProperty("id").GetGuid();
        var (manager, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "clinic_manager", [t.SecondBranchId]);

        (await manager.GetAsync($"/api/v1/doctors/{mainOnly}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var hidden = await manager.PostAsJsonAsync("/api/v1/visits", new { patientId = t.PatientId, doctorId = mainOnly, branchId = t.SecondBranchId });
        hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await hidden.Content.ReadAsStringAsync()).Should().NotContain("سارة", "the hidden doctor's name must not leak");
        (await hidden.ErrorCodeAsync()).Should().Be("DOCTOR_NOT_FOUND", "a doctor hidden on /doctors is \"not found\" here too");
        (await (await t.OwnerClient.GetAsync($"/api/v1/visits?doctorId={mainOnly}")).ReadJsonAsync())
            .GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(0, "nothing was recorded");

        // Controls: a doctor visible in the member's branch passes the visibility rule (the manager is then refused only
        // because they are not that doctor — GD-01), and the main-branch doctor still records their own visit.
        (await (await manager.PostAsJsonAsync("/api/v1/visits", new { patientId = t.PatientId, doctorId = t.DoctorId, branchId = t.SecondBranchId }))
            .ErrorCodeAsync()).Should().Be("VISIT_NOT_TREATING_DOCTOR");
        (await (await mainOnlyClient.PostAsJsonAsync("/api/v1/visits", new { patientId = t.PatientId, doctorId = mainOnly, branchId = t.MainBranchId }))
            .DataAsync(201)).GetProperty("doctorId").GetGuid().Should().Be(mainOnly);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<ClinicSession> ProfessionalClinicAsync()
    {
        var owner = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(owner.TenantId, "professional");
        return owner;
    }

    private static async Task<Guid> InviteAsync(HttpClient inviter, string email, string roleKey) =>
        (await (await inviter.PostAsJsonAsync("/api/v1/staff", new { fullName = "x", email, roleIds = new[] { await inviter.RoleIdAsync(roleKey) } }))
            .DataAsync(201)).GetProperty("membershipId").GetGuid();

    private async Task<HttpClient> LoginAsync(string email, Guid? tenantId)
    {
        var data = await factory.LoginDataAsync(factory.CreateClient(), email, tenantId: tenantId);
        return factory.WithToken(data.GetProperty("accessToken").GetString()!);
    }
}
