using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicSaaS.IntegrationTests.Qa;

/// <summary>
/// Gate D approved fix pass — regression tests for GD-02 (a disabled membership is refused on its already-issued
/// access token) and GD-01 (clinical authorship belongs to the visit's own doctor; ViewAllDoctors is read-only).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class GateDFixPassTests(ApiFactory factory)
{
    // ================================================================== GD-02 — disabled membership

    /// <summary>Signs in to one named clinic as a "mobile" client, so the refresh token is returned in the body.</summary>
    private async Task<(HttpClient Client, string RefreshToken)> ClinicSessionAsync(string email, Guid tenantId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Type", "mobile");
        var data = await (await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = TestApi.Password, tenantId })).DataAsync(200);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.GetProperty("accessToken").GetString());
        return (client, data.GetProperty("refreshToken").GetString()!);
    }

    private static async Task ShouldBeRefusedAsync(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await res.ErrorCodeAsync()).Should().Be("NO_ACTIVE_MEMBERSHIP");
    }

    /// <summary>A clinic with a doctor seat held by a person who also owns another clinic (so they can still sign in elsewhere).</summary>
    private async Task<(Scenarios.BookableClinic C, ClinicSession Person, Guid Seat, HttpClient Token, string Refresh)> DoctorSeatAsync()
    {
        var c = await factory.BookableClinicAsync();
        var person = await factory.RegisterClinicAsync();
        var seat = await factory.AddExistingAccountAsync(c.Owner.Client, person.Email, await c.Owner.Client.RoleIdAsync("doctor"));
        await c.Owner.Client.CreateDoctorAsync(seat, Scenarios.Week(c.BranchId));
        var (token, refresh) = await ClinicSessionAsync(person.Email, c.Owner.TenantId);
        return (c, person, seat, token, refresh);
    }

    [Fact]
    public async Task GD02_1_active_membership_with_a_valid_token_is_allowed()
    {
        var s = await DoctorSeatAsync();
        await (await s.Token.GetAsync($"/api/v1/patients/{s.C.PatientId}")).EnsureStatusAsync(200);
        await (await s.Token.GetAsync($"/api/v1/patients/{s.C.PatientId}/visits")).EnsureStatusAsync(200);
        await (await s.Token.PostAsJsonAsync("/api/v1/patients", TestApi.NewPatient(mobile: "0508888101"))).EnsureStatusAsync(201);
        (await (await s.Token.GetAsync("/api/v1/auth/me")).DataAsync(200)).GetProperty("membershipId").GetGuid().Should().Be(s.Seat);
    }

    [Fact]
    public async Task GD02_2_disabling_refuses_the_same_access_token_immediately()
    {
        var s = await DoctorSeatAsync();
        await (await s.C.Owner.Client.PostAsync($"/api/v1/staff/{s.Seat}/disable", null)).EnsureStatusAsync(204);

        // Same, unexpired access token: reads (incl. medical), writes and /auth/me are all refused at once.
        await ShouldBeRefusedAsync(await s.Token.GetAsync($"/api/v1/patients/{s.C.PatientId}"));
        await ShouldBeRefusedAsync(await s.Token.GetAsync($"/api/v1/patients/{s.C.PatientId}/visits"));
        await ShouldBeRefusedAsync(await s.Token.PostAsJsonAsync("/api/v1/patients", TestApi.NewPatient(mobile: "0508888102")));
        await ShouldBeRefusedAsync(await s.Token.PostAsJsonAsync("/api/v1/visits", new { patientId = s.C.PatientId, doctorId = s.C.DoctorId }));
        await ShouldBeRefusedAsync(await s.Token.GetAsync("/api/v1/auth/me"));

        // The refresh token was revoked by the disable (unchanged rotation/reuse rules) …
        (await s.Token.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = s.Refresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // … and disabling the seat ended that session (GD-03), so the old token is dead on /auth/* too. The person is not
        // locked out of anything else: they sign in again and reach their other clinic.
        var ended = await s.Token.GetAsync("/api/v1/auth/tenants");
        ended.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ended.ErrorCodeAsync()).Should().Be("UNAUTHORIZED");
        var (ownClinic, _) = await ClinicSessionAsync(s.Person.Email, s.Person.TenantId);
        await (await ownClinic.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
    }

    [Fact]
    public async Task GD02_3_re_enabling_follows_the_existing_reactivation_policy()
    {
        var s = await DoctorSeatAsync();
        await (await s.C.Owner.Client.PostAsync($"/api/v1/staff/{s.Seat}/disable", null)).EnsureStatusAsync(204);
        await (await s.C.Owner.Client.PostAsync($"/api/v1/staff/{s.Seat}/enable", null)).EnsureStatusAsync(204);

        // QA-02 policy: re-enabling makes the seat a new pending invitation — the old token stays refused and the
        // clinic cannot be entered until the person accepts again.
        (await (await s.C.Owner.Client.GetAsync($"/api/v1/staff/{s.Seat}")).DataAsync(200)).GetProperty("status").GetString().Should().Be("Invited");
        await ShouldBeRefusedAsync(await s.Token.GetAsync($"/api/v1/patients/{s.C.PatientId}"));
        (await (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = s.Person.Email, password = TestApi.Password, tenantId = s.C.Owner.TenantId }))
            .ErrorCodeAsync()).Should().Be("NO_ACTIVE_MEMBERSHIP");

        // After accepting, a new session works again.
        var token = factory.Emails.TokenFor(s.Person.Email, "/accept-invitation");
        await (await s.Person.Client.PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token })).EnsureStatusAsync(204);
        var (fresh, _) = await ClinicSessionAsync(s.Person.Email, s.C.Owner.TenantId);
        await (await fresh.GetAsync($"/api/v1/patients/{s.C.PatientId}")).EnsureStatusAsync(200);
    }

    [Fact]
    public async Task GD02_4_cross_tenant_behaviour_is_unchanged()
    {
        var s = await DoctorSeatAsync();
        var other = await factory.BookableClinicAsync();

        // A valid clinic token still cannot reach another clinic's rows (tenant filter → 404).
        (await s.Token.GetAsync($"/api/v1/patients/{other.PatientId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Disabling the person's seat in one clinic does not touch their session in their own clinic.
        var (ownClinic, _) = await ClinicSessionAsync(s.Person.Email, s.Person.TenantId);
        await (await s.C.Owner.Client.PostAsync($"/api/v1/staff/{s.Seat}/disable", null)).EnsureStatusAsync(204);
        await (await ownClinic.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);

        // A token pairing a clinic with a membership of a DIFFERENT clinic is refused (the seat must belong to the tid).
        var tokens = factory.Services.GetRequiredService<ITokenService>();
        var mismatched = tokens.CreateAccessToken(new TokenClaims(
            other.Owner.UserId, other.Owner.Email, "x", other.Owner.TenantId, await s.C.Owner.OwnerMembershipIdAsync(), PlatformRole.None,
            [Domain.Authorization.Permissions.Patients.View], [], Guid.NewGuid()));
        await ShouldBeRefusedAsync(await factory.WithToken(mismatched.Token).GetAsync("/api/v1/patients"));
    }

    [Fact]
    public async Task GD02_5_platform_administration_is_not_blocked()
    {
        // A platform user whose clinic seat is disabled keeps working on the platform plane (no clinic in that token).
        var adminEmail = await factory.CreatePlatformUserAsync(PlatformRole.SuperAdmin);
        var c = await factory.BookableClinicAsync();
        var seat = await factory.AddExistingAccountAsync(c.Owner.Client, adminEmail, await c.Owner.Client.RoleIdAsync("receptionist"));
        await (await c.Owner.Client.PostAsync($"/api/v1/staff/{seat}/disable", null)).EnsureStatusAsync(204);

        var platform = await factory.LoginAsync(adminEmail);
        await (await platform.GetAsync("/api/v1/platform/tenants")).EnsureStatusAsync(200);
        await (await platform.GetAsync($"/api/v1/platform/tenants/{c.Owner.TenantId}/subscription")).EnsureStatusAsync(200);
        await (await platform.GetAsync("/api/v1/auth/me")).EnsureStatusAsync(200);

        var root = await factory.LoginAsync(ApiFactory.SuperAdminEmail, ApiFactory.SuperAdminPassword);
        await (await root.GetAsync("/api/v1/platform/tenants")).EnsureStatusAsync(200);
    }

    // ================================================================== GD-01 — clinical authorship

    private sealed record Authorship(
        Scenarios.BookableClinic C, Guid AppointmentId, JsonElement Visit, HttpClient Senior, HttpClient Manager, HttpClient PlainDoctor);

    /// <summary>
    /// The clinic owner is the treating doctor (Scenarios.BookableClinic). Around them: a senior doctor holding
    /// MedicalRecords.ViewAllDoctors (with their own doctor profile), a clinic manager, and a plain doctor.
    /// </summary>
    private async Task<Authorship> AuthorshipAsync()
    {
        var c = await factory.BookableClinicAsync();
        var appointmentId = await c.Owner.Client.CheckedInAppointmentAsync(c);
        var visit = await c.Owner.Client.RecordVisitAsync(c, appointmentId, ClinicalScenarios.Clinical());

        var seniorRole = await c.Owner.Client.CreateRoleAsync("طبيب مشرف",
            "Patients.View", "MedicalRecords.View", "MedicalRecords.Edit", "MedicalRecords.ViewAllDoctors", "Doctors.View",
            "Visits.View", "Visits.Create", "Visits.Edit", "Prescriptions.View", "Prescriptions.Create");
        var (senior, seniorSeat, _) = await factory.InviteWithRolesAsync(c.Owner.Client, [seniorRole], prefix: "senior");
        await c.Owner.Client.CreateDoctorAsync(seniorSeat, Scenarios.Week(c.BranchId));
        var (manager, _, _) = await factory.InviteAndAcceptAsync(c.Owner, "clinic_manager");
        var (plain, plainSeat, _) = await factory.InviteAndAcceptAsync(c.Owner, "doctor");
        await c.Owner.Client.CreateDoctorAsync(plainSeat, Scenarios.Week(c.BranchId));
        return new Authorship(c, appointmentId, visit, senior, manager, plain);
    }

    private static Guid Id(JsonElement visit) => visit.GetProperty("id").GetGuid();
    private static string? Rv(JsonElement visit) => visit.GetProperty("rowVersion").GetString();

    private static Task<HttpResponseMessage> EditAsync(HttpClient client, JsonElement visit, string diagnosis) =>
        client.PutAsJsonAsync($"/api/v1/visits/{Id(visit)}", new { clinical = ClinicalScenarios.Clinical(diagnosis), rowVersion = Rv(visit) });

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient client, JsonElement visit) =>
        client.PostAsJsonAsync($"/api/v1/visits/{Id(visit)}/complete", new { rowVersion = Rv(visit) });

    private static async Task ShouldBeNotTreatingAsync(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.ErrorCodeAsync()).Should().Be("VISIT_NOT_TREATING_DOCTOR");
    }

    private async Task<JsonElement> ReadAsync(Scenarios.BookableClinic c, JsonElement visit) =>
        await (await c.Owner.Client.GetAsync($"/api/v1/visits/{Id(visit)}")).DataAsync(200);

    [Fact]
    public async Task GD01_1_treating_doctor_edits_own_visit()
    {
        var a = await AuthorshipAsync();
        var edited = await (await EditAsync(a.C.Owner.Client, a.Visit, "تشخيص الطبيب المعالج")).DataAsync(200);
        edited.GetProperty("clinical").GetProperty("diagnosis").GetString().Should().Be("تشخيص الطبيب المعالج");
    }

    [Fact]
    public async Task GD01_2_treating_doctor_completes_own_visit()
    {
        var a = await AuthorshipAsync();
        (await (await CompleteAsync(a.C.Owner.Client, a.Visit)).DataAsync(200)).GetProperty("status").GetString().Should().Be("Completed");
        (await (await a.C.Owner.Client.GetAsync($"/api/v1/appointments/{a.AppointmentId}")).DataAsync(200))
            .GetProperty("status").GetString().Should().Be("Completed");
    }

    [Fact]
    public async Task GD01_3_ViewAllDoctors_doctor_cannot_record_or_edit_another_doctors_visit()
    {
        var a = await AuthorshipAsync();
        await ShouldBeNotTreatingAsync(await EditAsync(a.Senior, a.Visit, "كتبه طبيب آخر"));
        await ShouldBeNotTreatingAsync(await a.Senior.PutAsJsonAsync($"/api/v1/visits/{Id(a.Visit)}", new { followUpNotes = "x", rowVersion = Rv(a.Visit) }));
        await ShouldBeNotTreatingAsync(await a.Senior.PostAsJsonAsync("/api/v1/visits", new
        {
            patientId = a.C.PatientId, doctorId = a.C.DoctorId, clinical = ClinicalScenarios.Clinical("زيارة باسم زميل"),
        }));
        (await ReadAsync(a.C, a.Visit)).GetProperty("clinical").GetProperty("diagnosis").GetString().Should().Be("التهاب البلعوم الحاد");
    }

    [Fact]
    public async Task GD01_4_ViewAllDoctors_doctor_cannot_complete_another_doctors_visit()
    {
        var a = await AuthorshipAsync();
        await ShouldBeNotTreatingAsync(await CompleteAsync(a.Senior, a.Visit));
        (await ReadAsync(a.C, a.Visit)).GetProperty("status").GetString().Should().Be("Open");
    }

    [Fact]
    public async Task GD01_5_clinic_manager_cannot_record_or_edit()
    {
        var a = await AuthorshipAsync();
        await ShouldBeNotTreatingAsync(await EditAsync(a.Manager, a.Visit, "كتبه المدير"));
        await ShouldBeNotTreatingAsync(await a.Manager.PostAsJsonAsync("/api/v1/visits", new
        {
            patientId = a.C.PatientId, doctorId = a.C.DoctorId, clinical = ClinicalScenarios.Clinical("تشخيص كتبه المدير"),
        }));
        (await ReadAsync(a.C, a.Visit)).GetProperty("clinical").GetProperty("diagnosis").GetString().Should().Be("التهاب البلعوم الحاد");
    }

    [Fact]
    public async Task GD01_6_clinic_manager_cannot_complete()
    {
        var a = await AuthorshipAsync();
        await ShouldBeNotTreatingAsync(await CompleteAsync(a.Manager, a.Visit));
        (await ReadAsync(a.C, a.Visit)).GetProperty("status").GetString().Should().Be("Open");
        (await (await a.C.Owner.Client.GetAsync($"/api/v1/appointments/{a.AppointmentId}")).DataAsync(200))
            .GetProperty("status").GetString().Should().Be("CheckedIn");
    }

    [Fact]
    public async Task GD01_7_read_access_is_unchanged()
    {
        var a = await AuthorshipAsync();
        foreach (var reader in new[] { a.Senior, a.Manager })
        {
            var visit = await (await reader.GetAsync($"/api/v1/visits/{Id(a.Visit)}")).DataAsync(200);
            visit.GetProperty("clinical").GetProperty("diagnosis").GetString().Should().Be("التهاب البلعوم الحاد");
            var list = await (await reader.GetAsync($"/api/v1/patients/{a.C.PatientId}/visits")).ReadJsonAsync();
            list.GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(1);
        }
        // A doctor without ViewAllDoctors still does not see a colleague's visit at all.
        (await a.PlainDoctor.GetAsync($"/api/v1/visits/{Id(a.Visit)}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GD01_8_prescription_authorization_is_unchanged()
    {
        var a = await AuthorshipAsync();
        foreach (var other in new[] { a.Senior, a.Manager })
        {
            var res = await other.IssuePrescriptionAsync(Id(a.Visit));
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await res.ErrorCodeAsync()).Should().Be("PRESCRIBER_NOT_TREATING_DOCTOR");
        }
        (await a.PlainDoctor.IssuePrescriptionAsync(Id(a.Visit))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await (await a.C.Owner.Client.IssuePrescriptionAsync(Id(a.Visit))).EnsureStatusAsync(201);
    }

    [Fact]
    public async Task GD01_9_cross_tenant_and_branch_isolation_are_unchanged()
    {
        var a = await AuthorshipAsync();

        // Another clinic's owner is a treating doctor too — of their own clinic only: "not found", never 403.
        var other = await factory.BookableClinicAsync();
        foreach (var res in new[]
                 {
                     await EditAsync(other.Owner.Client, a.Visit, "x"), await CompleteAsync(other.Owner.Client, a.Visit),
                     await other.Owner.Client.GetAsync($"/api/v1/visits/{Id(a.Visit)}"),
                 })
        {
            res.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await res.ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");
        }
        (await other.Owner.Client.PostAsJsonAsync("/api/v1/visits", new { patientId = a.C.PatientId, doctorId = other.DoctorId }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A member restricted to another branch does not see the main-branch visit: 404 before any authorship check.
        var second = await a.C.Owner.AddBranchAsync();
        var (restricted, _, _) = await factory.InviteAndAcceptAsync(a.C.Owner, "clinic_manager", [second]);
        (await restricted.GetAsync($"/api/v1/visits/{Id(a.Visit)}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await (await EditAsync(restricted, a.Visit, "x")).ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");
        (await (await CompleteAsync(restricted, a.Visit)).ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");
    }
}
