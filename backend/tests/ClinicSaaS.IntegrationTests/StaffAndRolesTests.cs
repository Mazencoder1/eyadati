using System.Net;
using System.Net.Http.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class StaffAndRolesTests(ApiFactory factory)
{
    [Fact]
    public async Task Invited_staff_set_a_password_from_the_link_and_get_their_role_permissions()
    {
        var owner = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(owner.TenantId, "professional");
        var (reception, membershipId, email) = await factory.InviteAndAcceptAsync(owner, "receptionist");

        var me = await (await reception.GetAsync("/api/v1/auth/me")).ReadJsonAsync();
        var perms = me.GetProperty("data").GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        perms.Should().Contain("Appointments.Create").And.NotContain("MedicalRecords.View");

        var member = await (await owner.Client.GetAsync($"/api/v1/staff/{membershipId}")).ReadJsonAsync();
        member.GetProperty("data").GetProperty("emailConfirmed").GetBoolean().Should().BeTrue();
        member.GetProperty("data").GetProperty("hasPassword").GetBoolean().Should().BeTrue();

        // The invitation link is single-use.
        var reuse = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/accept-invitation",
            new { token = factory.Emails.TokenFor(email, "/accept-invitation"), newPassword = "An0ther-Passw0rd" });
        (await reuse.ErrorCodeAsync()).Should().Be("INVALID_OR_EXPIRED_TOKEN");
    }

    [Fact]
    public async Task Free_plan_user_limit_blocks_invitations()
    {
        var owner = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(owner.TenantId, "free"); // free plan: 1 user (the owner)
        var res = await owner.Client.PostAsJsonAsync("/api/v1/staff", new
        {
            fullName = "x", email = TestApi.UniqueEmail(), roleIds = new[] { await owner.Client.RoleIdAsync("receptionist") },
        });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.ErrorCodeAsync()).Should().Be("USAGE_LIMIT_REACHED");
    }

    [Fact]
    public async Task Owner_role_cannot_be_handed_out_and_owner_seat_cannot_be_changed()
    {
        var owner = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(owner.TenantId, "professional");

        var asOwner = await owner.Client.PostAsJsonAsync("/api/v1/staff", new
        {
            fullName = "x", email = TestApi.UniqueEmail(), roleIds = new[] { await owner.Client.RoleIdAsync("tenant_owner") },
        });
        (await asOwner.ErrorCodeAsync()).Should().Be("PERMISSION_ESCALATION");

        var ownerSeat = await owner.OwnerMembershipIdAsync();
        (await (await owner.Client.PostAsync($"/api/v1/staff/{ownerSeat}/disable", null)).ErrorCodeAsync()).Should().Be("OWNER_PROTECTED");

        var ownerRole = await owner.Client.RoleIdAsync("tenant_owner");
        var editOwnerRole = await owner.Client.PutAsJsonAsync($"/api/v1/roles/{ownerRole}", new { name = "x", permissions = Array.Empty<string>(), rowVersion = "AAAAAAAAAAA=" });
        (await editOwnerRole.ErrorCodeAsync()).Should().Be("SYSTEM_ROLE_PROTECTED");
    }

    [Fact]
    public async Task Nobody_can_grant_permissions_they_do_not_hold()
    {
        var owner = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(owner.TenantId, "professional");

        // A custom "supervisor" role that may manage roles and staff but has no clinical access.
        var create = await owner.Client.PostAsJsonAsync("/api/v1/roles", new
        {
            name = "مشرف", permissions = new[] { "Roles.Manage", "Staff.View", "Staff.Manage", "Patients.View" },
        });
        await create.EnsureStatusAsync(201);
        var supervisorRole = (await create.ReadJsonAsync()).GetProperty("data").GetProperty("id").GetGuid();

        var email = TestApi.UniqueEmail("supervisor");
        await (await owner.Client.PostAsJsonAsync("/api/v1/staff", new { fullName = "مشرف", email, roleIds = new[] { supervisorRole } })).EnsureStatusAsync(201);
        await (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/accept-invitation",
            new { token = factory.Emails.TokenFor(email, "/accept-invitation"), newPassword = TestApi.Password })).EnsureStatusAsync(204);
        var supervisor = await factory.LoginAsync(email);

        var escalate = await supervisor.PostAsJsonAsync("/api/v1/roles", new { name = "صلاحيات كاملة", permissions = new[] { "Patients.Delete" } });
        (await escalate.ErrorCodeAsync()).Should().Be("PERMISSION_ESCALATION");

        // …nor hand out a role that carries more than they have (doctor role has clinical permissions).
        var assignDoctor = await supervisor.PostAsJsonAsync("/api/v1/staff", new
        {
            fullName = "x", email = TestApi.UniqueEmail(), roleIds = new[] { await owner.Client.RoleIdAsync("doctor") },
        });
        (await assignDoctor.ErrorCodeAsync()).Should().Be("PERMISSION_ESCALATION");

        // Within their own permissions it works.
        await (await supervisor.PostAsJsonAsync("/api/v1/roles", new { name = "قراءة فقط", permissions = new[] { "Patients.View" } })).EnsureStatusAsync(201);
    }

    [Fact]
    public async Task Roles_in_use_cannot_be_deleted_and_names_are_unique()
    {
        var owner = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(owner.TenantId, "professional");
        var role = (await (await owner.Client.PostAsJsonAsync("/api/v1/roles", new { name = "فني أشعة", permissions = new[] { "Patients.View" } })).ReadJsonAsync())
            .GetProperty("data").GetProperty("id").GetGuid();

        (await (await owner.Client.PostAsJsonAsync("/api/v1/roles", new { name = "فني أشعة", permissions = Array.Empty<string>() })).ErrorCodeAsync())
            .Should().Be("ROLE_NAME_TAKEN");

        await (await owner.Client.PostAsJsonAsync("/api/v1/staff", new { fullName = "x", email = TestApi.UniqueEmail(), roleIds = new[] { role } })).EnsureStatusAsync(201);
        (await (await owner.Client.DeleteAsync($"/api/v1/roles/{role}")).ErrorCodeAsync()).Should().Be("ROLE_IN_USE");

        var builtIn = await owner.Client.RoleIdAsync("staff");
        (await (await owner.Client.DeleteAsync($"/api/v1/roles/{builtIn}")).ErrorCodeAsync()).Should().Be("SYSTEM_ROLE_PROTECTED");
    }

    [Fact]
    public async Task Disabling_staff_ends_their_access_immediately_at_next_refresh_and_login()
    {
        var owner = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(owner.TenantId, "professional");
        var (_, membershipId, email) = await factory.InviteAndAcceptAsync(owner, "nurse");

        var mobile = factory.CreateClient();
        mobile.DefaultRequestHeaders.Add("X-Client-Type", "mobile");
        var refresh = (await (await mobile.PostAsJsonAsync("/api/v1/auth/login", new { email, password = TestApi.Password })).ReadJsonAsync())
            .GetProperty("data").GetProperty("refreshToken").GetString();

        await (await owner.Client.PostAsync($"/api/v1/staff/{membershipId}/disable", null)).EnsureStatusAsync(204);

        (await mobile.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = refresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var login = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email, password = TestApi.Password });
        (await login.ErrorCodeAsync()).Should().Be("NO_ACTIVE_MEMBERSHIP");
    }

    [Fact]
    public async Task Foreign_role_ids_are_rejected()
    {
        var a = await factory.RegisterClinicAsync();
        var b = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(a.TenantId, "professional");
        var res = await a.Client.PostAsJsonAsync("/api/v1/staff", new
        {
            fullName = "x", email = TestApi.UniqueEmail(), roleIds = new[] { await b.Client.RoleIdAsync("receptionist") },
        });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task One_person_in_two_clinics_chooses_at_login_and_can_switch()
    {
        var a = await factory.RegisterClinicAsync();
        var b = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(b.TenantId, "professional");

        // Clinic B invites clinic A's owner as a doctor-role member: no new account; A's owner signs in and accepts
        // (T-010). Only after acceptance does B see the account's real profile.
        var seat = await factory.AddExistingAccountAsync(b.Client, a.Email, await b.Client.RoleIdAsync("doctor"));
        (await (await b.Client.GetAsync($"/api/v1/staff/{seat}")).ReadJsonAsync()).GetProperty("data").GetProperty("fullName").GetString().Should().Be("د. مالك");

        var ambiguous = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = a.Email, password = TestApi.Password });
        ambiguous.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var choice = await ambiguous.ReadJsonAsync();
        choice.GetProperty("code").GetString().Should().Be("TENANT_SELECTION_REQUIRED");
        choice.GetProperty("details").GetArrayLength().Should().Be(2);

        var inA = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = a.Email, password = TestApi.Password, tenantId = a.TenantId });
        var token = (await inA.ReadJsonAsync()).GetProperty("data").GetProperty("accessToken").GetString()!;
        var client = factory.WithToken(token);

        var switched = await client.PostAsJsonAsync("/api/v1/auth/switch-tenant", new { tenantId = b.TenantId });
        await switched.EnsureStatusAsync(200);
        var session = (await switched.ReadJsonAsync()).GetProperty("data");
        session.GetProperty("tenant").GetProperty("id").GetGuid().Should().Be(b.TenantId);
        session.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().NotContain("Staff.Manage");

        // Switching into a clinic you don't belong to fails. (Switching ended the first session, so use the new one.)
        var foreign = await factory.RegisterClinicAsync();
        var inB = factory.WithToken(session.GetProperty("accessToken").GetString()!);
        (await (await inB.PostAsJsonAsync("/api/v1/auth/switch-tenant", new { tenantId = foreign.TenantId })).ErrorCodeAsync())
            .Should().Be("NO_ACTIVE_MEMBERSHIP");
    }
}
