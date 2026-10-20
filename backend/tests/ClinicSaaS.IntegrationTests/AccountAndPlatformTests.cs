using System.Net;
using System.Net.Http.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class AccountAndPlatformTests(ApiFactory factory)
{
    [Fact]
    public async Task Change_password_requires_the_current_one_and_ends_other_sessions()
    {
        var owner = await factory.RegisterClinicAsync();
        var other = factory.CreateClient();
        other.DefaultRequestHeaders.Add("X-Client-Type", "mobile");
        var otherRefresh = (await (await other.PostAsJsonAsync("/api/v1/auth/login", new { email = owner.Email, password = TestApi.Password })).ReadJsonAsync())
            .GetProperty("data").GetProperty("refreshToken").GetString();

        var wrong = await owner.Client.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = "Wrong-Passw0rd", newPassword = "N3w-Passw0rd-1" });
        (await wrong.ErrorCodeAsync()).Should().Be("INVALID_CREDENTIALS");

        var ok = await owner.Client.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = TestApi.Password, newPassword = "N3w-Passw0rd-1" });
        await ok.EnsureStatusAsync(200);
        (await ok.ReadJsonAsync()).GetProperty("data").GetProperty("accessToken").GetString().Should().NotBeNullOrEmpty();

        (await other.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = otherRefresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await factory.LoginAsync(owner.Email, "N3w-Passw0rd-1");
    }

    [Fact]
    public async Task Sessions_can_be_listed_and_revoked_but_only_your_own()
    {
        var a = await factory.RegisterClinicAsync();
        var phone = factory.CreateClient();
        phone.DefaultRequestHeaders.Add("X-Client-Type", "mobile");
        var phoneLogin = (await (await phone.PostAsJsonAsync("/api/v1/auth/login", new { email = a.Email, password = TestApi.Password })).ReadJsonAsync()).GetProperty("data");

        var sessions = (await (await a.Client.GetAsync("/api/v1/auth/sessions")).ReadJsonAsync()).GetProperty("data").EnumerateArray().ToList();
        sessions.Should().HaveCount(2);
        sessions.Should().ContainSingle(s => s.GetProperty("isCurrent").GetBoolean());
        var phoneSession = sessions.Single(s => !s.GetProperty("isCurrent").GetBoolean()).GetProperty("sessionId").GetGuid();

        var b = await factory.RegisterClinicAsync();
        (await b.Client.DeleteAsync($"/api/v1/auth/sessions/{phoneSession}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        await (await a.Client.DeleteAsync($"/api/v1/auth/sessions/{phoneSession}")).EnsureStatusAsync(204);
        (await phone.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = phoneLogin.GetProperty("refreshToken").GetString() }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Super_admin_manages_clinics_and_suspension_locks_the_clinic_out()
    {
        var clinic = await factory.RegisterClinicAsync();
        var mobile = factory.CreateClient();
        mobile.DefaultRequestHeaders.Add("X-Client-Type", "mobile");
        var refresh = (await (await mobile.PostAsJsonAsync("/api/v1/auth/login", new { email = clinic.Email, password = TestApi.Password })).ReadJsonAsync())
            .GetProperty("data").GetProperty("refreshToken").GetString();

        var admin = await factory.LoginAsync(ApiFactory.SuperAdminEmail, ApiFactory.SuperAdminPassword);
        var list = await (await admin.GetAsync("/api/v1/platform/tenants?pageSize=100")).ReadJsonAsync();
        var row = list.GetProperty("data").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == clinic.TenantId);
        row.GetProperty("planCode").GetString().Should().Be("basic");
        row.GetProperty("activeMembers").GetInt32().Should().Be(1);

        await (await admin.PostAsync($"/api/v1/platform/tenants/{clinic.TenantId}/suspend", null)).EnsureStatusAsync(204);
        // Gate C: immediate — the clinic's still-valid access token is refused on the very next request.
        (await (await clinic.Client.GetAsync("/api/v1/patients")).ErrorCodeAsync()).Should().Be("TENANT_SUSPENDED");
        (await mobile.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = refresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = clinic.Email, password = TestApi.Password })).ErrorCodeAsync())
            .Should().Be("TENANT_SUSPENDED");

        await (await admin.PostAsync($"/api/v1/platform/tenants/{clinic.TenantId}/reactivate", null)).EnsureStatusAsync(204);
        // GD-15 (Phase 3A): reactivation does not revive the sessions suspension ended; signing in again does.
        (await clinic.Client.GetAsync("/api/v1/patients")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var again = await factory.LoginAsync(clinic.Email);
        await (await again.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
    }

    [Fact]
    public async Task Platform_and_tenant_permissions_never_cross()
    {
        var clinic = await factory.RegisterClinicAsync();
        (await clinic.Client.GetAsync("/api/v1/platform/tenants")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The super admin has no clinic session, so clinic data stays out of reach.
        var admin = await factory.LoginAsync(ApiFactory.SuperAdminEmail, ApiFactory.SuperAdminPassword);
        (await admin.GetAsync("/api/v1/patients")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.GetAsync("/api/v1/settings")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
