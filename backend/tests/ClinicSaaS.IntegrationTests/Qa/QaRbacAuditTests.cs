using System.Net;
using System.Net.Http.Json;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests.Qa;

/// <summary>
/// T-002 RBAC / privilege-escalation audit. Escalation guards that hold are asserted (expected to pass). Behaviour
/// that is a design decision with a security cost is documented by <c>CurrentBehaviour_</c> tests (passing) and
/// listed in docs/security/T-002-audit.md — update them, never delete them, when the decision changes.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QaRbacAuditTests(ApiFactory factory)
{
    // ------------------------------------------------------------------ role editing

    [Fact]
    public async Task Role_editor_cannot_add_or_remove_permissions_they_do_not_hold_on_existing_roles()
    {
        var owner = await ProfessionalClinicAsync();
        var supervisorRole = await owner.Client.CreateRoleAsync("مشرف أدوار", "Roles.Manage", "Staff.View", "Patients.View");
        var clinicalRole = await owner.Client.CreateRoleAsync("سريري", "Patients.View", "Patients.Delete");
        var (supervisor, _, _) = await factory.InviteWithRolesAsync(owner.Client, [supervisorRole], prefix: "rolesupervisor");

        // Removing Patients.Delete (which the supervisor lacks) is a privileged change too.
        var strip = await supervisor.PutAsJsonAsync($"/api/v1/roles/{clinicalRole}", new
        {
            name = "سريري", permissions = new[] { "Patients.View" }, rowVersion = await RoleRowVersionAsync(supervisor, clinicalRole),
        });
        (await strip.ErrorCodeAsync()).Should().Be("PERMISSION_ESCALATION");

        // Adding MedicalRecords.View to a role, including the supervisor's OWN role.
        var add = await supervisor.PutAsJsonAsync($"/api/v1/roles/{supervisorRole}", new
        {
            name = "مشرف أدوار", permissions = new[] { "Roles.Manage", "Staff.View", "Patients.View", "MedicalRecords.View" },
            rowVersion = await RoleRowVersionAsync(supervisor, supervisorRole),
        });
        (await add.ErrorCodeAsync()).Should().Be("PERMISSION_ESCALATION");

        // Built-in doctor role: stripping its clinical permissions is refused as well.
        var doctorRole = await owner.Client.RoleIdAsync("doctor");
        var stripDoctor = await supervisor.PutAsJsonAsync($"/api/v1/roles/{doctorRole}", new
        {
            name = "طبيب", permissions = new[] { "Patients.View" }, rowVersion = await RoleRowVersionAsync(supervisor, doctorRole),
        });
        (await stripDoctor.ErrorCodeAsync()).Should().Be("PERMISSION_ESCALATION");

        var roles = await (await owner.Client.GetAsync("/api/v1/roles")).DataAsync(200);
        roles.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == clinicalRole).GetProperty("permissions")
            .EnumerateArray().Select(p => p.GetString()).Should().BeEquivalentTo("Patients.View", "Patients.Delete");
    }

    [Fact]
    public async Task Clinic_manager_without_roles_manage_cannot_create_edit_or_delete_roles()
    {
        var owner = await ProfessionalClinicAsync();
        var (manager, _, _) = await factory.InviteAndAcceptAsync(owner, "clinic_manager");
        var custom = await owner.Client.CreateRoleAsync("مخصص", "Patients.View");

        (await manager.PostAsJsonAsync("/api/v1/roles", new { name = "x", permissions = new[] { "Patients.View" } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.PutAsJsonAsync($"/api/v1/roles/{custom}", new { name = "x", permissions = Array.Empty<string>(), rowVersion = "AAAAAAAAAAA=" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.DeleteAsync($"/api/v1/roles/{custom}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.GetAsync("/api/v1/permissions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------------ staff management

    [Fact]
    public async Task Staff_manager_cannot_disable_or_strip_a_more_privileged_member_nor_change_self_or_owner()
    {
        var owner = await ProfessionalClinicAsync();
        var managerRole = await owner.Client.CreateRoleAsync("مدير موظفين", "Staff.View", "Staff.Manage", "Patients.View");
        var (manager, managerSeat, _) = await factory.InviteWithRolesAsync(owner.Client, [managerRole], prefix: "staffmanager");
        var (_, doctorSeat, _) = await factory.InviteAndAcceptAsync(owner, "doctor");
        var readOnly = await owner.Client.CreateRoleAsync("قراءة", "Patients.View");

        (await (await manager.PostAsync($"/api/v1/staff/{doctorSeat}/disable", null)).ErrorCodeAsync()).Should().Be("PERMISSION_ESCALATION");
        var strip = await manager.PutAsJsonAsync($"/api/v1/staff/{doctorSeat}", new
        {
            roleIds = new[] { readOnly }, rowVersion = await manager.RowVersionAsync($"/api/v1/staff/{doctorSeat}"),
        });
        (await strip.ErrorCodeAsync()).Should().Be("PERMISSION_ESCALATION");

        // Self: neither role changes nor disabling one's own seat.
        var self = await manager.PutAsJsonAsync($"/api/v1/staff/{managerSeat}", new
        {
            roleIds = new[] { managerRole, readOnly }, rowVersion = await manager.RowVersionAsync($"/api/v1/staff/{managerSeat}"),
        });
        (await self.ErrorCodeAsync()).Should().Be("CANNOT_CHANGE_SELF");
        (await (await manager.PostAsync($"/api/v1/staff/{managerSeat}/disable", null)).ErrorCodeAsync()).Should().Be("CANNOT_CHANGE_SELF");

        // Owner seat: not editable through staff management, even with the owner's own token.
        var ownerSeat = await owner.OwnerMembershipIdAsync();
        var editOwner = await owner.Client.PutAsJsonAsync($"/api/v1/staff/{ownerSeat}", new
        {
            roleIds = new[] { readOnly }, rowVersion = await owner.Client.RowVersionAsync($"/api/v1/staff/{ownerSeat}"),
        });
        (await editOwner.ErrorCodeAsync()).Should().Be("OWNER_PROTECTED");
        (await (await manager.PostAsync($"/api/v1/staff/{ownerSeat}/disable", null)).ErrorCodeAsync()).Should().Be("OWNER_PROTECTED");

        var doctor = await (await owner.Client.GetAsync($"/api/v1/staff/{doctorSeat}")).DataAsync(200);
        doctor.GetProperty("status").GetString().Should().Be("Active");
        doctor.GetProperty("roles").EnumerateArray().Single().GetProperty("id").GetGuid().Should().Be(await owner.Client.RoleIdAsync("doctor"));
    }

    [Fact]
    public async Task Staff_manager_cannot_change_branch_restriction_of_a_more_privileged_member()
    {
        // QA-04 (fixed in T-009): changing a member's branch restriction needs the same guard as disabling them — the
        // actor must hold every permission of the member's current roles (PERMISSION_ESCALATION otherwise).
        var owner = await ProfessionalClinicAsync();
        var second = (await (await owner.Client.PostAsJsonAsync("/api/v1/branches", new { name = "فرع ٢" })).DataAsync(201)).GetProperty("id").GetGuid();
        var managerRole = await owner.Client.CreateRoleAsync("مدير موظفين", "Staff.View", "Staff.Manage", "Patients.View");
        var (manager, _, _) = await factory.InviteWithRolesAsync(owner.Client, [managerRole], prefix: "staffmanager");
        var (_, doctorSeat, _) = await factory.InviteAndAcceptAsync(owner, "doctor");
        var doctorRole = await owner.Client.RoleIdAsync("doctor");

        var restrict = await manager.PutAsJsonAsync($"/api/v1/staff/{doctorSeat}", new
        {
            roleIds = new[] { doctorRole }, branchIds = new[] { second }, rowVersion = await manager.RowVersionAsync($"/api/v1/staff/{doctorSeat}"),
        });
        restrict.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await restrict.ErrorCodeAsync()).Should().Be("PERMISSION_ESCALATION");
        var doctor = await (await owner.Client.GetAsync($"/api/v1/staff/{doctorSeat}")).DataAsync(200);
        doctor.GetProperty("branchIds").GetArrayLength().Should().Be(0, "the doctor keeps access to every branch");
    }

    // ------------------------------------------------------------------ sessions: membership, suspension, switching

    [Fact]
    public async Task Login_or_switch_into_a_clinic_without_active_membership_or_while_suspended_is_refused()
    {
        var a = await factory.RegisterClinicAsync();
        var b = await ProfessionalClinicAsync();
        var stranger = await factory.RegisterClinicAsync();
        // T-010: an existing account joins B only after accepting B's invitation (setup only; assertions unchanged).
        await factory.AddExistingAccountAsync(b.Client, a.Email, await b.Client.RoleIdAsync("staff"));

        // Login naming a clinic the user is not a member of.
        var foreignLogin = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = a.Email, password = TestApi.Password, tenantId = stranger.TenantId });
        (await foreignLogin.ErrorCodeAsync()).Should().Be("NO_ACTIVE_MEMBERSHIP");

        // B suspended by the platform: neither login into B nor switching into B works; A is unaffected.
        var admin = await factory.LoginAsync(ApiFactory.SuperAdminEmail, ApiFactory.SuperAdminPassword);
        await (await admin.PostAsync($"/api/v1/platform/tenants/{b.TenantId}/suspend", null)).EnsureStatusAsync(204);
        try
        {
            var loginB = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = a.Email, password = TestApi.Password, tenantId = b.TenantId });
            (await loginB.ErrorCodeAsync()).Should().Be("TENANT_SUSPENDED");
            var inA = await LoginIntoAsync(a.Email, a.TenantId);
            (await (await inA.PostAsJsonAsync("/api/v1/auth/switch-tenant", new { tenantId = b.TenantId })).ErrorCodeAsync()).Should().Be("TENANT_SUSPENDED");
        }
        finally
        {
            await (await admin.PostAsync($"/api/v1/platform/tenants/{b.TenantId}/reactivate", null)).EnsureStatusAsync(204);
        }

        // Disabled membership in B: switching is refused. (B's owner signs in again: suspending B ended their old session,
        // and reactivating it does not bring that session back — GD-15.)
        var bOwner = await factory.LoginAsync(b.Email);
        var seat = (await (await bOwner.GetAsync("/api/v1/staff?pageSize=100")).DataAsync(200)).EnumerateArray()
            .Single(m => m.GetProperty("email").GetString() == a.Email).GetProperty("membershipId").GetGuid();
        await (await bOwner.PostAsync($"/api/v1/staff/{seat}/disable", null)).EnsureStatusAsync(204);
        var again = await LoginIntoAsync(a.Email, a.TenantId);
        (await (await again.PostAsJsonAsync("/api/v1/auth/switch-tenant", new { tenantId = b.TenantId })).ErrorCodeAsync()).Should().Be("NO_ACTIVE_MEMBERSHIP");
    }

    [Fact]
    public async Task Disabled_members_token_and_clinic_suspension_both_act_on_the_next_request()
    {
        // QA-05 / GD-02 (fixed): disabling a member is checked on every clinic request, like clinic suspension (Gate C),
        // so the member's already-issued access token stops working at once instead of at expiry.
        var owner = await ProfessionalClinicAsync();
        var (reception, seat, _) = await factory.InviteAndAcceptAsync(owner, "receptionist");
        await (await owner.Client.PostAsync($"/api/v1/staff/{seat}/disable", null)).EnsureStatusAsync(204);
        var refused = await reception.GetAsync("/api/v1/patients");
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await refused.ErrorCodeAsync()).Should().Be("NO_ACTIVE_MEMBERSHIP");

        var admin = await factory.LoginAsync(ApiFactory.SuperAdminEmail, ApiFactory.SuperAdminPassword);
        await (await admin.PostAsync($"/api/v1/platform/tenants/{owner.TenantId}/suspend", null)).EnsureStatusAsync(204);
        try
        {
            var res = await owner.Client.GetAsync("/api/v1/patients");
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "Gate C: suspension takes effect on the next request");
            (await res.ErrorCodeAsync()).Should().Be("TENANT_SUSPENDED");
        }
        finally
        {
            await (await admin.PostAsync($"/api/v1/platform/tenants/{owner.TenantId}/reactivate", null)).EnsureStatusAsync(204);
        }
    }

    // ------------------------------------------------------------------ platform vs clinic plane

    [Fact]
    public async Task Platform_admin_who_is_also_a_clinic_member_never_gets_one_token_for_both_planes()
    {
        // QA-03 (fixed in T-008): a clinic token never carries platform permissions and never reaches /platform.
        // A platform user signs in WITHOUT tenantId to get a platform session (no tenant, no clinic permissions),
        // and names the clinic (login tenantId or switch-tenant) to get a clinic session (no platform permissions).
        var adminEmail = await factory.CreatePlatformUserAsync(PlatformRole.SuperAdmin);
        var clinic = await ProfessionalClinicAsync();
        await factory.AddExistingAccountAsync(clinic.Client, adminEmail, await clinic.Client.RoleIdAsync("receptionist"));

        var login = await factory.LoginDataAsync(factory.CreateClient(), adminEmail);
        (login.TryGetProperty("tenant", out var tenant) && tenant.ValueKind != System.Text.Json.JsonValueKind.Null).Should().BeFalse("a platform user is not auto-entered into a clinic");
        var permissions = login.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        permissions.Should().Contain("Platform.Tenants.Manage").And.NotContain("Appointments.Create");
        var platformToken = factory.WithToken(login.GetProperty("accessToken").GetString()!);
        (await platformToken.GetAsync("/api/v1/platform/tenants")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await platformToken.GetAsync("/api/v1/patients")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var clinicLogin = await factory.LoginDataAsync(factory.CreateClient(), adminEmail, tenantId: clinic.TenantId);
        clinicLogin.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().Contain("Appointments.Create")
            .And.NotContain("Platform.Tenants.Manage");
        var token = factory.WithToken(clinicLogin.GetProperty("accessToken").GetString()!);
        (await token.GetAsync("/api/v1/platform/tenants")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "a clinic-bound token never reaches /platform");
        (await token.GetAsync("/api/v1/patients")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------ cross-tenant disclosure of global accounts

    [Fact]
    public async Task Adding_an_existing_account_discloses_nothing_and_does_not_change_its_login()
    {
        // QA-02 (fixed in T-010): adding an email creates a pending invitation, whether or not an account exists.
        // Until the account owner accepts, the clinic sees only the invited email (no real name, phone, confirmation
        // state, password state or last login), and the account has no active membership (its login is unchanged).
        var victim = await factory.RegisterClinicAsync();
        await factory.LoginAsync(victim.Email); // sets LastLoginAtUtc
        var prober = await ProfessionalClinicAsync();

        var added = await (await prober.Client.PostAsJsonAsync("/api/v1/staff", new
        {
            fullName = "اسم يكتبه المستكشف", email = victim.Email, roleIds = new[] { await prober.Client.RoleIdAsync("staff") },
        })).DataAsync(201);
        added.GetProperty("fullName").GetString().Should().NotBe("د. مالك", "the existing account's real name is never returned");
        added.GetProperty("status").GetString().Should().Be("Invited");

        var member = await (await prober.Client.GetAsync($"/api/v1/staff/{added.GetProperty("membershipId").GetGuid()}")).DataAsync(200);
        member.GetProperty("fullName").GetString().Should().NotBe("د. مالك");
        member.GetProperty("hasPassword").GetBoolean().Should().BeFalse("password state of a foreign account is not disclosed");
        (member.TryGetProperty("lastLoginAtUtc", out var lastLogin) && lastLogin.ValueKind != System.Text.Json.JsonValueKind.Null)
            .Should().BeFalse("last login time of a foreign account is not disclosed");

        // A fresh email is answered with exactly the same fields and values (except the email itself and the new seat id).
        var fresh = await (await prober.Client.PostAsJsonAsync("/api/v1/staff", new
        {
            fullName = "جديد", email = TestApi.UniqueEmail(), roleIds = new[] { await prober.Client.RoleIdAsync("staff") },
        })).DataAsync(201);
        fresh.GetProperty("hasPassword").GetBoolean().Should().BeFalse();
        fresh.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(added.EnumerateObject().Select(p => p.Name));

        var login = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = victim.Email, password = TestApi.Password });
        (await login.DataAsync(200)).GetProperty("tenant").GetProperty("id").GetGuid().Should().Be(victim.TenantId, "no membership before acceptance");
    }

    [Fact]
    public async Task CurrentBehaviour_lockout_response_reveals_that_an_email_is_registered()
    {
        // QA-06 (Low): unknown e-mails never lock, real accounts answer 423 ACCOUNT_LOCKED after 5 failures.
        var real = await factory.RegisterClinicAsync();
        var unknown = TestApi.UniqueEmail("nobody");
        HttpResponseMessage? lastReal = null, lastUnknown = null;
        for (var i = 0; i < 6; i++)
        {
            lastReal = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = real.Email, password = "Wrong-Passw0rd-1" });
            lastUnknown = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = unknown, password = "Wrong-Passw0rd-1" });
        }
        (await lastReal!.ErrorCodeAsync()).Should().Be("ACCOUNT_LOCKED");
        (await lastUnknown!.ErrorCodeAsync()).Should().Be("INVALID_CREDENTIALS", "CURRENT behaviour: the two cases are distinguishable");
    }

    // ------------------------------------------------------------------ helpers

    private async Task<ClinicSession> ProfessionalClinicAsync()
    {
        var owner = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(owner.TenantId, "professional");
        return owner;
    }

    private async Task<HttpClient> LoginIntoAsync(string email, Guid tenantId)
    {
        var data = await (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email, password = TestApi.Password, tenantId })).DataAsync(200);
        return factory.WithToken(data.GetProperty("accessToken").GetString()!);
    }

    private static async Task<string> RoleRowVersionAsync(HttpClient client, Guid roleId) =>
        (await (await client.GetAsync("/api/v1/roles")).DataAsync(200)).EnumerateArray()
        .Single(r => r.GetProperty("id").GetGuid() == roleId).GetProperty("rowVersion").GetString()!;
}
