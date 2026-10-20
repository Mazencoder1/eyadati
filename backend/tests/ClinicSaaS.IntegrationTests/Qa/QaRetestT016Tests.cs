using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests.Qa;

/// <summary>
/// QA re-test of T-016 (QA-12, QA-16) on integration d9aaca3 — adversarial cases beyond the fix's own tests.
/// Report: docs/security/T-017-retest.md. <c>CurrentBehaviour_</c> tests document behaviour that is accepted or awaits a decision.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QaRetestT016Tests(ApiFactory factory)
{
    // ------------------------------------------------------------------ QA-12

    [Fact]
    public async Task QA12_disable_enable_cycles_and_the_victims_own_failed_sign_in_never_unmask_a_pending_seat()
    {
        var victim = await factory.RegisterClinicAsync();
        var prober = await ProfessionalClinicAsync();
        var seat = await InviteAsync(prober.Client, victim.Email);

        // The victim tries to sign in to the prober's clinic (writes audit rows for the victim, with no active seat there).
        var attempt = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = victim.Email, password = TestApi.Password, tenantId = prober.TenantId });
        (await attempt.ErrorCodeAsync()).Should().Be("NO_ACTIVE_MEMBERSHIP");

        for (var round = 0; round < 3; round++)
        {
            await (await prober.Client.PostAsync($"/api/v1/staff/{seat}/disable", null)).EnsureStatusAsync(204);
            await AssertMaskedAsync(prober.Client, seat, victim.Email, "Disabled");
            await (await prober.Client.PostAsync($"/api/v1/staff/{seat}/enable", null)).EnsureStatusAsync(204);
            await AssertMaskedAsync(prober.Client, seat, victim.Email, "Invited");
        }
    }

    [Fact]
    public async Task QA12_masked_disabled_seat_is_identical_for_registered_and_unregistered_emails()
    {
        // No oracle through the disabled state either: the same masked shape whether or not the email had an account.
        var registered = await factory.RegisterClinicAsync();
        var fresh = TestApi.UniqueEmail("fresh");
        var prober = await ProfessionalClinicAsync();
        var a = await InviteAsync(prober.Client, registered.Email);
        var b = await InviteAsync(prober.Client, fresh);
        await (await prober.Client.PostAsync($"/api/v1/staff/{a}/disable", null)).EnsureStatusAsync(204);
        await (await prober.Client.PostAsync($"/api/v1/staff/{b}/disable", null)).EnsureStatusAsync(204);

        var da = await (await prober.Client.GetAsync($"/api/v1/staff/{a}")).DataAsync(200);
        var db = await (await prober.Client.GetAsync($"/api/v1/staff/{b}")).DataAsync(200);
        Shape(da, registered.Email).Should().Be(Shape(db, fresh));
    }

    [Fact]
    public async Task CurrentBehaviour_QA12_a_real_former_member_is_visible_when_disabled_and_masked_while_re_invited()
    {
        // Accepted once → the clinic legitimately knew the person. Re-enabling makes it a new pending invitation
        // (masked again, QA-02); disabling it again shows the former member, not a never-accepted seat.
        var clinic = await ProfessionalClinicAsync();
        var (_, seat, _) = await factory.InviteAndAcceptAsync(clinic, "staff");
        await (await clinic.Client.PostAsync($"/api/v1/staff/{seat}/disable", null)).EnsureStatusAsync(204);
        (await Member(clinic.Client, seat)).GetProperty("fullName").GetString().Should().Be("موظف staff");
        await (await clinic.Client.PostAsync($"/api/v1/staff/{seat}/enable", null)).EnsureStatusAsync(204);
        (await Member(clinic.Client, seat)).GetProperty("userId").GetGuid().Should().Be(Guid.Empty);
        await (await clinic.Client.PostAsync($"/api/v1/staff/{seat}/disable", null)).EnsureStatusAsync(204);
        (await Member(clinic.Client, seat)).GetProperty("fullName").GetString().Should().Be("موظف staff");
    }

    // ------------------------------------------------------------------ QA-16

    [Fact]
    public async Task QA16_hidden_doctor_is_indistinguishable_from_a_missing_or_foreign_doctor()
    {
        var t = await factory.TwoBranchClinicAsync();
        var other = await factory.BookableClinicAsync();
        var mainOnly = await MainOnlyDoctorAsync(t);
        var (manager, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "clinic_manager", [t.SecondBranchId]);

        foreach (var doctorId in new[] { mainOnly, Guid.NewGuid(), other.DoctorId })
        {
            var res = await manager.PostAsJsonAsync("/api/v1/visits", new { patientId = t.PatientId, doctorId, branchId = t.SecondBranchId });
            res.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await res.ErrorCodeAsync()).Should().Be("DOCTOR_NOT_FOUND");
        }
        // Also with clinical content and with no branchId (the member's single branch is the default).
        (await (await manager.PostAsJsonAsync("/api/v1/visits", new { patientId = t.PatientId, doctorId = mainOnly })).ErrorCodeAsync())
            .Should().Be("DOCTOR_NOT_FOUND");
        (await (await t.OwnerClient.GetAsync($"/api/v1/visits?doctorId={mainOnly}")).ReadJsonAsync())
            .GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task QA16_member_of_both_branches_and_unscheduled_doctors_are_unaffected()
    {
        var t = await factory.TwoBranchClinicAsync();
        var mainOnly = await MainOnlyDoctorAsync(t);
        var (both, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "clinic_manager", [t.MainBranchId, t.SecondBranchId]);
        // The doctor is visible (not DOCTOR_NOT_FOUND); since GD-01 only that doctor may record the visit.
        (await (await both.PostAsJsonAsync("/api/v1/visits", new { patientId = t.PatientId, doctorId = mainOnly, branchId = t.MainBranchId }))
            .ErrorCodeAsync()).Should().Be("VISIT_NOT_TREATING_DOCTOR");

        // T-011: a doctor with no schedule at all stays visible to restricted members (so a branch manager can set them up).
        var (_, seat, _) = await factory.InviteAndAcceptAsync(t.Owner, "doctor");
        var unscheduled = (await t.OwnerClient.CreateDoctorAsync(seat, null)).GetProperty("id").GetGuid();
        var (restricted, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "clinic_manager", [t.SecondBranchId]);
        (await (await restricted.PostAsJsonAsync("/api/v1/visits", new { patientId = t.PatientId, doctorId = unscheduled, branchId = t.SecondBranchId }))
            .ErrorCodeAsync()).Should().Be("VISIT_NOT_TREATING_DOCTOR");
    }

    [Fact]
    public async Task QA18_fixed_restricted_member_cannot_issue_a_prescription_for_a_hidden_doctor_or_any_doctor()
    {
        // QA-18 (Low) — fixed by D8: a prescription needs a visit the caller can see, and the caller must be that visit's
        // doctor. A branch-restricted clinic_manager (no doctor profile) can no longer name a doctor hidden from their
        // branches — nor any other doctor.
        var t = await factory.TwoBranchClinicAsync();
        var (mainOnly, mainOnlyClient) = await MainOnlyDoctorWithSessionAsync(t);
        var (manager, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "clinic_manager", [t.SecondBranchId]);
        (await manager.GetAsync($"/api/v1/doctors/{mainOnly}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // No visit: rejected outright.
        (await (await manager.PostAsJsonAsync("/api/v1/prescriptions", new
        {
            patientId = t.PatientId, doctorId = mainOnly, items = new[] { new { medicationName = "Paracetamol" } },
        })).ErrorCodeAsync()).Should().Be("VALIDATION_FAILED");

        // The hidden doctor's visit in the main branch: not visible ⇒ not found.
        var mainVisit = (await (await mainOnlyClient.PostAsJsonAsync("/api/v1/visits", new { patientId = t.PatientId, doctorId = mainOnly, branchId = t.MainBranchId }))
            .DataAsync(201)).GetProperty("id").GetGuid();
        (await (await manager.PostAsJsonAsync("/api/v1/prescriptions", new { visitId = mainVisit, items = new[] { new { medicationName = "Paracetamol" } } }))
            .ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");

        // A visit the manager can see (own branch) but is not the doctor of: forbidden.
        var (unscheduledClient, seat, _) = await factory.InviteAndAcceptAsync(t.Owner, "doctor");
        var unscheduled = (await t.OwnerClient.CreateDoctorAsync(seat, null)).GetProperty("id").GetGuid();
        var visible = (await (await unscheduledClient.PostAsJsonAsync("/api/v1/visits", new { patientId = t.PatientId, doctorId = unscheduled, branchId = t.SecondBranchId }))
            .DataAsync(201)).GetProperty("id").GetGuid();
        var res = await manager.PostAsJsonAsync("/api/v1/prescriptions", new { visitId = visible, items = new[] { new { medicationName = "Paracetamol" } } });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.ErrorCodeAsync()).Should().Be("PRESCRIBER_NOT_TREATING_DOCTOR");
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Guid> MainOnlyDoctorAsync(QaScenarios.TwoBranchClinic t) => (await MainOnlyDoctorWithSessionAsync(t)).Id;

    /// <summary>A doctor working only in the main branch, with their own signed-in client (GD-01: only they record their visits).</summary>
    private async Task<(Guid Id, HttpClient Client)> MainOnlyDoctorWithSessionAsync(QaScenarios.TwoBranchClinic t)
    {
        var (client, seat, _) = await factory.InviteAndAcceptAsync(t.Owner, "doctor");
        var id = (await t.OwnerClient.CreateDoctorAsync(seat,
            new[] { new { branchId = t.MainBranchId, dayOfWeek = "Sunday", startTime = "08:00:00", endTime = "12:00:00" } })).GetProperty("id").GetGuid();
        return (id, client);
    }

    private async Task<ClinicSession> ProfessionalClinicAsync()
    {
        var owner = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(owner.TenantId, "professional");
        return owner;
    }

    private static async Task<Guid> InviteAsync(HttpClient inviter, string email) =>
        (await (await inviter.PostAsJsonAsync("/api/v1/staff", new { fullName = "x", email, roleIds = new[] { await inviter.RoleIdAsync("staff") } }))
            .DataAsync(201)).GetProperty("membershipId").GetGuid();

    private static async Task<JsonElement> Member(HttpClient client, Guid seat) => await (await client.GetAsync($"/api/v1/staff/{seat}")).DataAsync(200);

    private static async Task AssertMaskedAsync(HttpClient client, Guid seat, string email, string status)
    {
        var m = await Member(client, seat);
        m.GetProperty("status").GetString().Should().Be(status);
        m.GetProperty("fullName").GetString().Should().Be(email.ToLowerInvariant());
        m.GetProperty("userId").GetGuid().Should().Be(Guid.Empty);
        m.GetProperty("hasPassword").GetBoolean().Should().BeFalse();
        m.GetProperty("emailConfirmed").GetBoolean().Should().BeFalse();
        (m.TryGetProperty("lastLoginAtUtc", out var last) && last.ValueKind != JsonValueKind.Null).Should().BeFalse();
        (await (await client.GetAsync("/api/v1/staff?pageSize=100&search=" + Uri.EscapeDataString("د. مالك"))).DataAsync(200))
            .EnumerateArray().Should().OnlyContain(x => x.GetProperty("isOwner").GetBoolean());
        var row = (await (await client.GetAsync("/api/v1/staff?pageSize=100")).DataAsync(200)).EnumerateArray()
            .Single(x => x.GetProperty("membershipId").GetGuid() == seat);
        Shape(row, email).Should().Be(Shape(m, email));
    }

    /// <summary>The member with per-request values (seat id, row version, the email echo) blanked out.</summary>
    private static string Shape(JsonElement member, string email)
    {
        var lower = email.ToLowerInvariant();
        return string.Join("|", member.EnumerateObject()
            .Where(p => p.Name is not ("membershipId" or "rowVersion"))
            .Select(p => $"{p.Name}={(p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.ToLowerInvariant() == lower ? "<email>" : p.Value.GetRawText())}"));
    }
}
