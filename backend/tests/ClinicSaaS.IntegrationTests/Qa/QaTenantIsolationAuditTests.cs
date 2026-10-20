using System.Net;
using System.Net.Http.Json;
using ClinicSaaS.Domain.Authorization;
using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.Infrastructure.Persistence;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.IntegrationTests.Qa;

/// <summary>
/// T-002 tenant-isolation / IDOR / BOLA audit: every id-taking endpoint not already covered by TenantIsolationTests,
/// AppointmentTests or StaffAndRolesTests, plus body-supplied foreign ids. Also the independent review of T-001
/// (TenantId concurrency token): join tables, the synchronous save path, missing rows and soft-deleted rows.
/// All of these are expected to PASS — they prove the isolation claims.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QaTenantIsolationAuditTests(ApiFactory factory)
{
    // ------------------------------------------------------------------ API: path ids of another tenant

    [Fact]
    public async Task Tenant_B_cannot_read_or_change_tenant_A_doctor_profile_schedule_or_availability()
    {
        var a = await factory.BookableClinicAsync();
        var b = await factory.BookableClinicAsync();
        var url = $"/api/v1/doctors/{a.DoctorId}";
        var doctorOfA = await (await a.Owner.Client.GetAsync(url)).DataAsync(200);
        var rowVersion = doctorOfA.GetProperty("rowVersion").GetString();

        await ExpectNotFound(await b.Owner.Client.GetAsync(url), "DOCTOR_NOT_FOUND");
        await ExpectNotFound(await b.Owner.Client.PutAsJsonAsync(url, new
        {
            displayNameAr = "مخترق", specialty = "x", consultationFee = 0m, defaultSlotMinutes = 15, acceptsOnlineBooking = false, isActive = false, rowVersion,
        }), "DOCTOR_NOT_FOUND");
        await ExpectNotFound(await b.Owner.Client.PutAsJsonAsync($"{url}/schedule", new { windows = Array.Empty<object>(), rowVersion }), "DOCTOR_NOT_FOUND");
        await ExpectNotFound(await b.Owner.Client.GetAsync($"{url}/availability?date={Scenarios.LocalDateIso(2)}&branchId={b.BranchId}"), "DOCTOR_NOT_FOUND");

        var list = await (await b.Owner.Client.GetAsync($"/api/v1/doctors?branchId={a.BranchId}")).DataAsync(200);
        list.GetArrayLength().Should().Be(0);

        var after = await (await a.Owner.Client.GetAsync(url)).DataAsync(200);
        after.GetProperty("rowVersion").GetString().Should().Be(rowVersion);
        after.GetProperty("schedule").GetArrayLength().Should().Be(7);
    }

    [Fact]
    public async Task Tenant_B_cannot_change_tenant_A_services_branches_or_roles()
    {
        var a = await factory.BookableClinicAsync();
        var b = await factory.RegisterClinicAsync();
        var service = await (await a.Owner.Client.PostAsJsonAsync("/api/v1/services", new { nameAr = "تنظيف", durationMinutes = 30, price = 150m })).DataAsync(201);
        var serviceUrl = $"/api/v1/services/{service.GetProperty("id").GetGuid()}";
        var role = await a.Owner.Client.CreateRoleAsync("دور أ", "Patients.View");
        var roleRv = await RoleRowVersionAsync(a.Owner.Client, role);
        var branchRv = (await (await a.Owner.Client.GetAsync("/api/v1/branches")).DataAsync(200)).EnumerateArray().Single().GetProperty("rowVersion").GetString();

        await ExpectNotFound(await b.Client.PutAsJsonAsync(serviceUrl, new
        {
            nameAr = "x", durationMinutes = 5, price = 0m, rowVersion = service.GetProperty("rowVersion").GetString(),
        }), "SERVICE_NOT_FOUND");
        await ExpectNotFound(await b.Client.DeleteAsync(serviceUrl), "SERVICE_NOT_FOUND");
        await ExpectNotFound(await b.Client.PutAsJsonAsync($"/api/v1/branches/{a.BranchId}", new { name = "x", isActive = false, rowVersion = branchRv }), "BRANCH_NOT_FOUND");
        await ExpectNotFound(await b.Client.PutAsJsonAsync($"/api/v1/roles/{role}", new { name = "x", permissions = Array.Empty<string>(), rowVersion = roleRv }), "ROLE_NOT_FOUND");
        await ExpectNotFound(await b.Client.DeleteAsync($"/api/v1/roles/{role}"), "ROLE_NOT_FOUND");

        // B's lists never include A's rows.
        (await (await b.Client.GetAsync("/api/v1/services")).DataAsync(200)).GetArrayLength().Should().Be(0);
        (await (await b.Client.GetAsync("/api/v1/roles")).DataAsync(200)).EnumerateArray()
            .Select(r => r.GetProperty("id").GetGuid()).Should().NotContain(role);
        (await (await b.Client.GetAsync("/api/v1/branches")).DataAsync(200)).EnumerateArray()
            .Select(x => x.GetProperty("id").GetGuid()).Should().NotContain(a.BranchId);

        // A's rows are untouched.
        (await (await a.Owner.Client.GetAsync("/api/v1/services")).DataAsync(200)).EnumerateArray().Single().GetProperty("nameAr").GetString().Should().Be("تنظيف");
        (await RoleRowVersionAsync(a.Owner.Client, role)).Should().Be(roleRv);
    }

    [Fact]
    public async Task Tenant_B_cannot_read_or_manage_tenant_A_staff_and_cannot_assign_A_roles_or_branches()
    {
        var a = await factory.RegisterClinicAsync();
        var b = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(a.TenantId, "professional");
        await factory.UsePlanAsync(b.TenantId, "professional");
        var (_, memberOfA, _) = await factory.InviteAndAcceptAsync(a, "receptionist");
        var (_, memberOfB, _) = await factory.InviteAndAcceptAsync(b, "receptionist");
        var url = $"/api/v1/staff/{memberOfA}";
        var rowVersion = await a.Client.RowVersionAsync(url);
        var receptionistOfB = await b.Client.RoleIdAsync("receptionist");

        await ExpectNotFound(await b.Client.GetAsync(url), "STAFF_NOT_FOUND");
        await ExpectNotFound(await b.Client.PutAsJsonAsync(url, new { roleIds = new[] { receptionistOfB }, rowVersion }), "STAFF_NOT_FOUND");
        foreach (var action in new[] { "disable", "enable", "resend-invitation" })
            await ExpectNotFound(await b.Client.PostAsync($"{url}/{action}", null), "STAFF_NOT_FOUND");
        await ExpectNotFound(await b.Client.GetAsync($"/api/v1/staff/{await a.OwnerMembershipIdAsync()}"), "STAFF_NOT_FOUND");

        // B edits its own member but smuggles A's role / A's branch into the body.
        var rvB = await b.Client.RowVersionAsync($"/api/v1/staff/{memberOfB}");
        var foreignRole = await b.Client.PutAsJsonAsync($"/api/v1/staff/{memberOfB}", new
        {
            roleIds = new[] { receptionistOfB, await a.Client.RoleIdAsync("tenant_owner") }, rowVersion = rvB,
        });
        await ExpectFieldError(foreignRole, "roleIds");
        var foreignBranch = await b.Client.PutAsJsonAsync($"/api/v1/staff/{memberOfB}", new
        {
            roleIds = new[] { receptionistOfB }, branchIds = new[] { await a.MainBranchIdAsync() }, rowVersion = rvB,
        });
        await ExpectFieldError(foreignBranch, "branchIds");
        var inviteWithForeignBranch = await b.Client.PostAsJsonAsync("/api/v1/staff", new
        {
            fullName = "x", email = TestApi.UniqueEmail(), roleIds = new[] { receptionistOfB }, branchIds = new[] { await a.MainBranchIdAsync() },
        });
        await ExpectFieldError(inviteWithForeignBranch, "branchIds");
        // Control: the same edit with B's own branch is accepted, so the 400s above are about the foreign ids.
        await (await b.Client.PutAsJsonAsync($"/api/v1/staff/{memberOfB}", new
        {
            roleIds = new[] { receptionistOfB }, branchIds = new[] { await b.MainBranchIdAsync() }, rowVersion = rvB,
        })).EnsureStatusAsync(200);

        // A's member is still active with its original row version.
        var member = await (await a.Client.GetAsync(url)).DataAsync(200);
        member.GetProperty("status").GetString().Should().Be("Active");
        member.GetProperty("rowVersion").GetString().Should().Be(rowVersion);
    }

    [Fact]
    public async Task Tenant_B_cannot_drive_tenant_A_appointment_through_any_transition_or_filter_on_A_ids()
    {
        var a = await factory.BookableClinicAsync();
        var b = await factory.BookableClinicAsync();
        var appt = await (await a.Owner.Client.BookAsync(a, Scenarios.RiyadhUtc(2, 10))).DataAsync(201);
        var url = $"/api/v1/appointments/{appt.GetProperty("id").GetGuid()}";
        var rowVersion = appt.GetProperty("rowVersion").GetString();

        await ExpectNotFound(await b.Owner.Client.PutAsJsonAsync($"{url}/reschedule", new
        {
            startUtc = Scenarios.RiyadhUtc(2, 12), doctorId = b.DoctorId, branchId = b.BranchId, rowVersion,
        }), "APPOINTMENT_NOT_FOUND");
        foreach (var action in new[] { "confirm", "check-in", "complete", "no-show" })
            await ExpectNotFound(await b.Owner.Client.PostAsJsonAsync($"{url}/{action}", new { rowVersion, reason = "x" }), "APPOINTMENT_NOT_FOUND");

        foreach (var query in new[] { $"doctorId={a.DoctorId}", $"patientId={a.PatientId}", $"branchId={a.BranchId}", "pageSize=100" })
        {
            var list = await (await b.Owner.Client.GetAsync($"/api/v1/appointments?{query}")).ReadJsonAsync();
            list.GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(0, query);
        }

        var after = await (await a.Owner.Client.GetAsync(url)).DataAsync(200);
        after.GetProperty("status").GetString().Should().Be("Scheduled");
        after.GetProperty("rowVersion").GetString().Should().Be(rowVersion);
    }

    // ------------------------------------------------------------------ API: foreign ids inside request bodies

    [Fact]
    public async Task Tenant_B_cannot_reference_tenant_A_membership_branch_service_or_doctor_in_its_own_writes()
    {
        var a = await factory.BookableClinicAsync();
        var b = await factory.BookableClinicAsync();
        var serviceOfA = (await (await a.Owner.Client.PostAsJsonAsync("/api/v1/services", new { nameAr = "أشعة", durationMinutes = 15, price = 50m })).DataAsync(201))
            .GetProperty("id").GetGuid();

        // Doctor profile on A's membership.
        var doctorOnA = await b.Owner.Client.PostAsJsonAsync("/api/v1/doctors", new
        {
            membershipId = await a.Owner.OwnerMembershipIdAsync(), displayNameAr = "x", specialty = "x", consultationFee = 0m, defaultSlotMinutes = 15,
        });
        await ExpectFieldError(doctorOnA, "membershipId");

        // Working hours of B's doctor at A's branch.
        var rv = await b.Owner.Client.RowVersionAsync($"/api/v1/doctors/{b.DoctorId}");
        await ExpectFieldError(await b.Owner.Client.PutAsJsonAsync($"/api/v1/doctors/{b.DoctorId}/schedule", new { windows = Scenarios.Week(a.BranchId), rowVersion = rv }),
            "windows");

        // Bookings with A's service / A's branch.
        var withService = await b.Owner.Client.PostAsJsonAsync("/api/v1/appointments", new
        {
            patientId = b.PatientId, doctorId = b.DoctorId, branchId = b.BranchId, serviceId = serviceOfA, startUtc = Scenarios.RiyadhUtc(2, 10),
        });
        await ExpectNotFound(withService, "SERVICE_NOT_FOUND");
        var withBranch = await b.Owner.Client.PostAsJsonAsync("/api/v1/appointments", new
        {
            patientId = b.PatientId, doctorId = b.DoctorId, branchId = a.BranchId, startUtc = Scenarios.RiyadhUtc(2, 10),
        });
        await ExpectNotFound(withBranch, "BRANCH_NOT_FOUND");
        await ExpectNotFound(await b.Owner.Client.GetAsync($"/api/v1/doctors/{b.DoctorId}/availability?date={Scenarios.LocalDateIso(2)}&serviceId={serviceOfA}"),
            "SERVICE_NOT_FOUND");

        // Rescheduling B's own appointment onto A's doctor / into A's branch.
        var own = await (await b.Owner.Client.BookAsync(b, Scenarios.RiyadhUtc(2, 11))).DataAsync(201);
        var ownUrl = $"/api/v1/appointments/{own.GetProperty("id").GetGuid()}/reschedule";
        var ownRv = own.GetProperty("rowVersion").GetString();
        await ExpectNotFound(await b.Owner.Client.PutAsJsonAsync(ownUrl, new { startUtc = Scenarios.RiyadhUtc(2, 12), doctorId = a.DoctorId, rowVersion = ownRv }),
            "DOCTOR_NOT_FOUND");
        await ExpectNotFound(await b.Owner.Client.PutAsJsonAsync(ownUrl, new { startUtc = Scenarios.RiyadhUtc(2, 12), branchId = a.BranchId, rowVersion = ownRv }),
            "BRANCH_NOT_FOUND");

        // Patient update pointing at A's branch (create is covered by TenantIsolationTests).
        var patient = await (await b.Owner.Client.GetAsync($"/api/v1/patients/{b.PatientId}")).DataAsync(200);
        object PatientUpdate(Guid branchId) => new
        {
            fullName = "محمد عبدالله", mobileNumber = "0551234567", gender = "Male", identityType = "None",
            registeredBranchId = branchId, rowVersion = patient.GetProperty("rowVersion").GetString(),
        };
        await ExpectFieldError(await b.Owner.Client.PutAsJsonAsync($"/api/v1/patients/{b.PatientId}", PatientUpdate(a.BranchId)), "registeredBranchId");
        await (await b.Owner.Client.PutAsJsonAsync($"/api/v1/patients/{b.PatientId}", PatientUpdate(b.BranchId))).EnsureStatusAsync(200);
    }

    // ------------------------------------------------------------------ T-001 review: data layer

    [Fact]
    public async Task T001_join_tables_cannot_be_forged_or_cross_linked_between_tenants()
    {
        var a = await factory.BookableClinicAsync();
        var b = await factory.BookableClinicAsync();
        var ta = a.Owner.TenantId;
        var tb = b.Owner.TenantId;
        Guid ownerRoleA = default, ownerMembershipA = default, scheduleA = default, ownerRoleB = default, ownerMembershipB = default;
        await factory.WithDbAsync(ta, async db =>
        {
            ownerRoleA = (await db.Roles.SingleAsync(r => r.SystemKey == SystemRoles.TenantOwner)).Id;
            ownerMembershipA = (await db.TenantMemberships.SingleAsync(m => m.IsOwner)).Id;
            scheduleA = (await db.DoctorSchedules.FirstAsync()).Id;
        });
        await factory.WithDbAsync(tb, async db =>
        {
            ownerRoleB = (await db.Roles.SingleAsync(r => r.SystemKey == SystemRoles.TenantOwner)).Id;
            ownerMembershipB = (await db.TenantMemberships.SingleAsync(m => m.IsOwner)).Id;
        });

        // Forged deletes/updates of A's join rows (composite keys, no rowversion) affect 0 rows and are reported.
        await ExpectIsolationViolation(tb, db => db.Set<RolePermission>().Remove(
            new RolePermission { TenantId = tb, RoleId = ownerRoleA, Permission = Permissions.Patients.View }));
        await ExpectIsolationViolation(tb, db => db.Set<MembershipRole>().Remove(
            new MembershipRole { TenantId = tb, MembershipId = ownerMembershipA, RoleId = ownerRoleA }));
        await ExpectIsolationViolation(tb, db => db.DoctorSchedules.Update(new DoctorSchedule
        {
            Id = scheduleA, TenantId = tb, DoctorId = b.DoctorId, BranchId = b.BranchId, DayOfWeek = DayOfWeek.Sunday,
            StartTime = new TimeOnly(0, 0), EndTime = new TimeOnly(23, 0),
        }));

        // Cross-links: B's membership + A's role, A's membership + B's role, B's membership + A's branch → composite FKs.
        await ExpectForeignKeyViolation(tb, db => db.Set<MembershipRole>().Add(new MembershipRole { MembershipId = ownerMembershipB, RoleId = ownerRoleA }));
        await ExpectForeignKeyViolation(tb, db => db.Set<MembershipRole>().Add(new MembershipRole { MembershipId = ownerMembershipA, RoleId = ownerRoleB }));
        await ExpectForeignKeyViolation(tb, db => db.Set<MembershipBranch>().Add(new MembershipBranch { MembershipId = ownerMembershipB, BranchId = a.BranchId }));
        await ExpectForeignKeyViolation(tb, db => db.Set<RolePermission>().Add(new RolePermission { RoleId = ownerRoleA, Permission = "Qa.NotYetGranted" }));

        // A's owner role still carries every tenant permission and is still assigned.
        await factory.WithDbAsync(ta, async db =>
        {
            (await db.Set<RolePermission>().CountAsync(p => p.RoleId == ownerRoleA)).Should().Be(Permissions.AllTenant.Count);
            (await db.Set<MembershipRole>().AnyAsync(r => r.MembershipId == ownerMembershipA && r.RoleId == ownerRoleA)).Should().BeTrue();
            (await db.DoctorSchedules.SingleAsync(s => s.Id == scheduleA)).DoctorId.Should().Be(a.DoctorId);
        });
    }

    [Fact]
    public async Task T001_synchronous_SaveChanges_maps_a_forged_cross_tenant_write_to_an_isolation_violation()
    {
        var a = await factory.BookableClinicAsync();
        var b = await factory.RegisterClinicAsync();
        await factory.WithDbAsync(b.TenantId, db =>
        {
            db.Patients.Update(new Patient
            {
                Id = a.PatientId, TenantId = b.TenantId, MedicalRecordNumber = "P-999999", FullName = "forged", MobileNumber = "+966500000000",
                RowVersion = new byte[8],
            });
            var save = () => db.SaveChanges();
            save.Should().Throw<TenantIsolationViolationException>();
            return Task.CompletedTask;
        });
        await factory.WithDbAsync(a.Owner.TenantId, async db => (await db.Patients.SingleAsync()).FullName.Should().Be("محمد عبدالله"));
    }

    [Fact]
    public async Task T001_forged_write_to_a_missing_row_stays_a_concurrency_conflict_and_soft_deleted_foreign_rows_stay_protected()
    {
        var a = await factory.BookableClinicAsync();
        var b = await factory.RegisterClinicAsync();

        // A row that exists nowhere: indistinguishable from a concurrent delete → plain concurrency conflict (409), not 404.
        await factory.WithDbAsync(b.TenantId, async db =>
        {
            db.Patients.Update(new Patient { Id = Guid.NewGuid(), TenantId = b.TenantId, MedicalRecordNumber = "x", FullName = "x", MobileNumber = "+966500000000" });
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<DbUpdateConcurrencyException>();
        });

        // A's patient soft-deleted by A, then targeted by a forged write from B: still an isolation violation.
        await (await a.Owner.Client.DeleteAsync($"/api/v1/patients/{a.PatientId}")).EnsureStatusAsync(204);
        await factory.WithDbAsync(b.TenantId, async db =>
        {
            db.Patients.Update(new Patient
            {
                Id = a.PatientId, TenantId = b.TenantId, MedicalRecordNumber = "x", FullName = "resurrected", MobileNumber = "+966500000000", IsDeleted = false,
            });
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<TenantIsolationViolationException>();
        });
        await factory.WithDbAsync(a.Owner.TenantId, async db =>
        {
            var row = await db.Patients.IgnoreQueryFilters([Application.Common.QueryFilters.SoftDelete]).SingleAsync(p => p.Id == a.PatientId);
            row.IsDeleted.Should().BeTrue();
            row.FullName.Should().Be("محمد عبدالله");
        });
    }

    // ------------------------------------------------------------------ helpers

    private static async Task ExpectNotFound(HttpResponseMessage res, string code)
    {
        if (res.StatusCode != HttpStatusCode.NotFound)
            throw new Xunit.Sdk.XunitException($"{res.RequestMessage?.Method} {res.RequestMessage?.RequestUri}: expected 404 {code}, got {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        (await res.ErrorCodeAsync()).Should().Be(code);
    }

    private static async Task ExpectFieldError(HttpResponseMessage res, string field)
    {
        if (res.StatusCode != HttpStatusCode.BadRequest)
            throw new Xunit.Sdk.XunitException($"{res.RequestMessage?.Method} {res.RequestMessage?.RequestUri}: expected 400 on '{field}', got {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        var errors = (await res.ReadJsonAsync()).GetProperty("errors");
        errors.TryGetProperty(field, out _).Should().BeTrue($"the 400 must be about '{field}', got {errors}");
    }

    private static async Task<string> RoleRowVersionAsync(HttpClient client, Guid roleId) =>
        (await (await client.GetAsync("/api/v1/roles")).DataAsync(200)).EnumerateArray()
        .Single(r => r.GetProperty("id").GetGuid() == roleId).GetProperty("rowVersion").GetString()!;

    private Task ExpectIsolationViolation(Guid actingTenant, Action<AppDbContext> forge) =>
        factory.WithDbAsync(actingTenant, async db =>
        {
            forge(db);
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<TenantIsolationViolationException>();
        });

    private Task ExpectForeignKeyViolation(Guid actingTenant, Action<AppDbContext> forge) =>
        factory.WithDbAsync(actingTenant, async db =>
        {
            forge(db);
            var save = () => db.SaveChangesAsync();
            (await save.Should().ThrowAsync<DbUpdateException>()).Which.InnerException.Should().BeOfType<SqlException>()
                .Which.Number.Should().Be(547);
        });
}
