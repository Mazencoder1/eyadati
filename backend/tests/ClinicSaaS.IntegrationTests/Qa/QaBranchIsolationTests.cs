using System.Net;
using System.Net.Http.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests.Qa;

/// <summary>
/// T-002 branch-restriction audit (CLAUDE.md §6). A member restricted to the SECOND branch versus resources of the
/// MAIN branch. Appointments are required to be branch-restricted: those tests assert the restriction.
/// Finding F2 was decided and implemented in T-011: doctors and their working hours are branch-contextual, branch edits
/// are limited to the member's branches, services and patients stay clinic-wide (the former <c>CurrentBehaviour_</c>
/// tests were rewritten to assert that decision).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QaBranchIsolationTests(ApiFactory factory)
{
    // ------------------------------------------------------------------ appointments (required restriction)

    [Fact]
    public async Task Restricted_member_cannot_reschedule_or_transition_an_appointment_of_another_branch()
    {
        var t = await factory.TwoBranchClinicAsync();
        var appt = await (await t.OwnerClient.BookAtAsync(t, t.MainBranchId, Scenarios.RiyadhUtc(2, 10))).DataAsync(201);
        var url = $"/api/v1/appointments/{appt.GetProperty("id").GetGuid()}";
        var rowVersion = appt.GetProperty("rowVersion").GetString();
        var (reception, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "receptionist", [t.SecondBranchId]);

        (await reception.PutAsJsonAsync($"{url}/reschedule", new { startUtc = Scenarios.RiyadhUtc(2, 11), rowVersion }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await reception.PutAsJsonAsync($"{url}/reschedule", new { startUtc = Scenarios.RiyadhUtc(2, 16), branchId = t.SecondBranchId, rowVersion }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "pulling another branch's appointment into your own branch is still acting on it");
        foreach (var action in new[] { "confirm", "check-in", "complete", "cancel", "no-show" })
            (await reception.PostAsJsonAsync($"{url}/{action}", new { rowVersion, reason = "qa" }))
                .StatusCode.Should().Be(HttpStatusCode.NotFound, $"/{action} on another branch's appointment");

        var unchanged = await (await t.OwnerClient.GetAsync(url)).DataAsync(200);
        unchanged.GetProperty("status").GetString().Should().Be("Scheduled");
        unchanged.GetProperty("branchId").GetGuid().Should().Be(t.MainBranchId);
        unchanged.GetProperty("rowVersion").GetString().Should().Be(rowVersion);
    }

    [Fact]
    public async Task Restricted_member_list_filters_never_reveal_another_branchs_appointments()
    {
        var t = await factory.TwoBranchClinicAsync();
        await (await t.OwnerClient.BookAtAsync(t, t.MainBranchId, Scenarios.RiyadhUtc(2, 10))).EnsureStatusAsync(201);
        var own = await (await t.OwnerClient.BookAtAsync(t, t.SecondBranchId, Scenarios.RiyadhUtc(2, 16))).DataAsync(201);
        var (reception, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "receptionist", [t.SecondBranchId]);

        var date = Scenarios.LocalDateIso(2);
        foreach (var query in new[]
                 {
                     $"date={date}", $"date={date}&branchId={t.MainBranchId}", $"date={date}&doctorId={t.DoctorId}",
                     $"patientId={t.PatientId}", $"doctorId={t.DoctorId}", "pageSize=100",
                 })
        {
            var list = await (await reception.GetAsync($"/api/v1/appointments?{query}")).ReadJsonAsync();
            list.GetProperty("data").EnumerateArray().Where(a => a.GetProperty("branchId").GetGuid() != t.SecondBranchId)
                .Should().BeEmpty(query);
            list.GetProperty("meta").GetProperty("totalCount").GetInt32().Should().BeLessThanOrEqualTo(1, query);
        }
        var mine = await (await reception.GetAsync($"/api/v1/appointments?date={date}")).ReadJsonAsync();
        mine.GetProperty("data").EnumerateArray().Single().GetProperty("id").GetGuid().Should().Be(own.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Restricted_member_cannot_move_or_book_into_another_branch()
    {
        var t = await factory.TwoBranchClinicAsync();
        var (reception, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "receptionist", [t.SecondBranchId]);
        var own = await (await reception.BookAtAsync(t, t.SecondBranchId, Scenarios.RiyadhUtc(2, 16))).DataAsync(201);

        // Reschedule its own appointment into the main branch (inside the doctor's main-branch hours).
        var move = await reception.PutAsJsonAsync($"/api/v1/appointments/{own.GetProperty("id").GetGuid()}/reschedule", new
        {
            startUtc = Scenarios.RiyadhUtc(2, 10), branchId = t.MainBranchId, rowVersion = own.GetProperty("rowVersion").GetString(),
        });
        move.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await move.ErrorCodeAsync()).Should().Be("BRANCH_ACCESS_DENIED");

        // Walk-in and regular booking in the main branch.
        (await (await reception.BookAtAsync(t, t.MainBranchId, default, source: "WalkIn")).ErrorCodeAsync()).Should().Be("BRANCH_ACCESS_DENIED");
        (await (await reception.BookAtAsync(t, t.MainBranchId, Scenarios.RiyadhUtc(2, 11))).ErrorCodeAsync()).Should().Be("BRANCH_ACCESS_DENIED");
    }

    [Fact]
    public async Task Restricted_member_availability_shows_only_own_branch_slots()
    {
        var t = await factory.TwoBranchClinicAsync();
        var (reception, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "receptionist", [t.SecondBranchId]);
        var url = $"/api/v1/doctors/{t.DoctorId}/availability?date={Scenarios.LocalDateIso(2)}";

        var all = await (await reception.GetAsync(url)).DataAsync(200);
        all.GetArrayLength().Should().BeGreaterThan(0);
        all.EnumerateArray().Should().OnlyContain(s => s.GetProperty("branchId").GetGuid() == t.SecondBranchId);
        (await (await reception.GetAsync($"{url}&branchId={t.MainBranchId}")).DataAsync(200)).GetArrayLength().Should().Be(0);

        // The owner (unrestricted) does see both branches — the filter above is the restriction, not missing data.
        var owner = await (await t.OwnerClient.GetAsync(url)).DataAsync(200);
        owner.EnumerateArray().Select(s => s.GetProperty("branchId").GetGuid()).Distinct().Should().HaveCount(2);
    }

    // ------------------------------------------------------------------ F2: doctors / schedules (decided in T-011)

    [Fact]
    public async Task Restricted_member_cannot_list_or_read_doctors_working_only_in_another_branch()
    {
        // F2 (T-011): a branch-restricted member sees a doctor only if the doctor has a schedule window in one of the
        // member's branches; other doctors are excluded from lists and are "not found".
        var t = await factory.TwoBranchClinicAsync();
        var mainOnlyDoctor = await MainOnlyDoctorAsync(t);
        var (reception, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "receptionist", [t.SecondBranchId]);

        var list = await (await reception.GetAsync("/api/v1/doctors?pageSize=100")).DataAsync(200);
        list.EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).Should().NotContain(mainOnlyDoctor,
            "doctors are branch-filtered for restricted members");
        var filtered = await (await reception.GetAsync($"/api/v1/doctors?branchId={t.MainBranchId}")).DataAsync(200);
        filtered.EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).Should().NotContain(mainOnlyDoctor);

        var one = await reception.GetAsync($"/api/v1/doctors/{mainOnlyDoctor}");
        one.StatusCode.Should().Be(HttpStatusCode.NotFound, "the other branch's doctor and working hours are not disclosed");
        (await one.ErrorCodeAsync()).Should().Be("DOCTOR_NOT_FOUND");

        // …but booking with that doctor is still impossible from the restricted branch (appointments ARE restricted).
        var book = await reception.PostAsJsonAsync("/api/v1/appointments", new
        {
            patientId = t.PatientId, doctorId = mainOnlyDoctor, startUtc = Scenarios.RiyadhUtc(2, 10), durationMinutes = 15,
        });
        (await book.ErrorCodeAsync()).Should().Be("BRANCH_ACCESS_DENIED");
    }

    [Fact]
    public async Task Restricted_manager_cannot_replace_schedule_or_deactivate_a_doctor_of_another_branch()
    {
        // F2 (T-011): the other branch's doctor is "not found" for the restricted manager, for every write too.
        var t = await factory.TwoBranchClinicAsync();
        var mainOnlyDoctor = await MainOnlyDoctorAsync(t);
        var (manager, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "clinic_manager", [t.SecondBranchId]);

        // Wipe the main-branch working hours of a doctor the manager's branch has nothing to do with.
        var rv = await t.OwnerClient.RowVersionAsync($"/api/v1/doctors/{mainOnlyDoctor}");
        var schedule = await manager.PutAsJsonAsync($"/api/v1/doctors/{mainOnlyDoctor}/schedule", new { windows = Array.Empty<object>(), rowVersion = rv });
        schedule.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Move the doctor's hours into the manager's own branch instead.
        (await manager.PutAsJsonAsync($"/api/v1/doctors/{mainOnlyDoctor}/schedule", new
        {
            windows = new[] { new { branchId = t.SecondBranchId, dayOfWeek = "Sunday", startTime = "09:00:00", endTime = "10:00:00" } }, rowVersion = rv,
        })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Deactivate the doctor.
        var doctor = await (await t.OwnerClient.GetAsync($"/api/v1/doctors/{mainOnlyDoctor}")).DataAsync(200);
        var deactivate = await manager.PutAsJsonAsync($"/api/v1/doctors/{mainOnlyDoctor}", new
        {
            displayNameAr = doctor.GetProperty("displayNameAr").GetString(), specialty = doctor.GetProperty("specialty").GetString(),
            consultationFee = 0m, defaultSlotMinutes = 15, acceptsOnlineBooking = false, isActive = false,
            rowVersion = doctor.GetProperty("rowVersion").GetString(),
        });
        deactivate.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var unchanged = await (await t.OwnerClient.GetAsync($"/api/v1/doctors/{mainOnlyDoctor}")).DataAsync(200);
        unchanged.GetProperty("isActive").GetBoolean().Should().BeTrue();
        unchanged.GetProperty("schedule").EnumerateArray().Select(w => w.GetProperty("branchId").GetGuid()).Should().Equal(t.MainBranchId);
        unchanged.GetProperty("rowVersion").GetString().Should().Be(rv);
    }

    [Fact]
    public async Task Restricted_manager_can_edit_clinic_wide_services_and_patients_but_not_other_branches()
    {
        // F2 (T-011): services and patients stay clinic-wide by product decision (services have no branch column);
        // a branch-restricted manager may not modify a branch outside their set.
        var t = await factory.TwoBranchClinicAsync();
        var (manager, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "clinic_manager", [t.SecondBranchId]);

        // Services have no branch in the data model at all (CLAUDE.md §6 lists them as branch-scoped).
        var service = await (await t.OwnerClient.PostAsJsonAsync("/api/v1/services", new { nameAr = "كشف", durationMinutes = 15, price = 100m })).DataAsync(201);
        await (await manager.PutAsJsonAsync($"/api/v1/services/{service.GetProperty("id").GetGuid()}", new
        {
            nameAr = "كشف معدل", durationMinutes = 20, price = 1m, rowVersion = service.GetProperty("rowVersion").GetString(),
        })).EnsureStatusAsync(200);

        // A patient registered at the main branch is visible and editable from the second branch.
        var patient = await t.OwnerClient.CreatePatientAsync(new
        {
            fullName = "مريض الفرع الرئيسي", mobileNumber = "0551112233", gender = "Female", identityType = "None", registeredBranchId = t.MainBranchId,
        });
        await (await manager.GetAsync($"/api/v1/patients/{patient.GetProperty("id").GetGuid()}")).EnsureStatusAsync(200);

        // The main branch is still listed (names are needed for display) but cannot be edited by a manager restricted
        // to the second branch.
        var main = (await (await manager.GetAsync("/api/v1/branches")).DataAsync(200)).EnumerateArray()
            .Single(b => b.GetProperty("id").GetGuid() == t.MainBranchId);
        var edit = await manager.PutAsJsonAsync($"/api/v1/branches/{t.MainBranchId}", new
        {
            name = "الرئيسي - معدل", isActive = true, rowVersion = main.GetProperty("rowVersion").GetString(),
        });
        edit.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await edit.ErrorCodeAsync()).Should().Be("BRANCH_ACCESS_DENIED");
        (await (await t.OwnerClient.GetAsync("/api/v1/branches")).DataAsync(200)).EnumerateArray()
            .Single(b => b.GetProperty("id").GetGuid() == t.MainBranchId).GetProperty("name").GetString().Should().NotBe("الرئيسي - معدل");
    }

    // ------------------------------------------------------------------ QA-01: branch escalation through staff management (CONFIRMED)

    [Fact]
    public async Task KnownDefect_QA01_restricted_manager_cannot_invite_staff_with_wider_branch_access()
    {
        var t = await factory.TwoBranchClinicAsync();
        var (manager, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "clinic_manager", [t.SecondBranchId]);
        var receptionist = await t.OwnerClient.RoleIdAsync("receptionist");

        // No branchIds = access to EVERY branch. Today: 201, and the new account then reads main-branch appointments.
        var unrestricted = await manager.PostAsJsonAsync("/api/v1/staff", new { fullName = "x", email = TestApi.UniqueEmail(), roleIds = new[] { receptionist } });
        unrestricted.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a branch-restricted actor must not hand out access to branches it cannot access");

        var otherBranch = await manager.PostAsJsonAsync("/api/v1/staff", new
        {
            fullName = "y", email = TestApi.UniqueEmail(), roleIds = new[] { receptionist }, branchIds = new[] { t.MainBranchId },
        });
        otherBranch.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task KnownDefect_QA01_restricted_manager_cannot_widen_an_existing_members_branch_access()
    {
        var t = await factory.TwoBranchClinicAsync();
        var (manager, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "clinic_manager", [t.SecondBranchId]);
        var (_, receptionMembership, _) = await factory.InviteAndAcceptAsync(t.Owner, "receptionist", [t.SecondBranchId]);
        var receptionist = await t.OwnerClient.RoleIdAsync("receptionist");

        var rv = await manager.RowVersionAsync($"/api/v1/staff/{receptionMembership}");
        var widen = await manager.PutAsJsonAsync($"/api/v1/staff/{receptionMembership}", new
        {
            roleIds = new[] { receptionist }, branchIds = Array.Empty<Guid>(), rowVersion = rv,
        });
        widen.StatusCode.Should().Be(HttpStatusCode.Forbidden, "removing the restriction grants every branch, beyond the actor's own");

        var member = await (await t.OwnerClient.GetAsync($"/api/v1/staff/{receptionMembership}")).DataAsync(200);
        member.GetProperty("branchIds").EnumerateArray().Select(b => b.GetGuid()).Should().Equal(t.SecondBranchId);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A second doctor (an invited doctor-role member) working only at the main branch.</summary>
    private async Task<Guid> MainOnlyDoctorAsync(QaScenarios.TwoBranchClinic t)
    {
        var (_, membershipId, _) = await factory.InviteAndAcceptAsync(t.Owner, "doctor");
        var doctor = await t.OwnerClient.CreateDoctorAsync(membershipId,
            new[] { new { branchId = t.MainBranchId, dayOfWeek = "Sunday", startTime = "08:00:00", endTime = "12:00:00" } });
        return doctor.GetProperty("id").GetGuid();
    }
}
