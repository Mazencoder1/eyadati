using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Domain.Authorization;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.IntegrationTests.Infrastructure;
using ClinicSaaS.IntegrationTests.Qa;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// T-008 (QA-03): the platform plane and the clinic plane never share a token. A clinic session (token with a
/// <c>tid</c>) never carries platform permissions, and platform endpoints require a platform session (no tenant),
/// whatever the token says. A platform user enters the platform plane by signing in WITHOUT a <c>tenantId</c>.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PlatformSessionTests(ApiFactory factory)
{
    [Fact]
    public async Task Platform_user_with_one_clinic_membership_gets_a_platform_session_by_default_and_a_clinic_session_only_on_request()
    {
        var (adminEmail, clinic) = await PlatformUserWithOneMembershipAsync();

        // Login without tenantId: platform session, even though exactly one clinic membership exists.
        var platform = await LoginDataAsync(adminEmail, tenantId: null);
        NoTenant(platform).Should().BeTrue();
        Permissions(platform).Should().Contain("Platform.Tenants.Manage").And.NotContain("Appointments.Create");
        var platformClient = factory.WithToken(platform.GetProperty("accessToken").GetString()!);
        (await platformClient.GetAsync("/api/v1/platform/tenants")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await platformClient.GetAsync("/api/v1/patients")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Login naming the clinic: clinic session with clinic permissions only.
        var inClinic = await LoginDataAsync(adminEmail, clinic.TenantId);
        inClinic.GetProperty("tenant").GetProperty("id").GetGuid().Should().Be(clinic.TenantId);
        Permissions(inClinic).Should().Contain("Appointments.Create").And.NotContain(p => p.StartsWith("Platform."));
        var clinicClient = factory.WithToken(inClinic.GetProperty("accessToken").GetString()!);
        (await clinicClient.GetAsync("/api/v1/platform/tenants")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await clinicClient.PostAsync($"/api/v1/platform/tenants/{clinic.TenantId}/suspend", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await clinicClient.GetAsync("/api/v1/patients")).StatusCode.Should().Be(HttpStatusCode.OK);
        var me = await (await clinicClient.GetAsync("/api/v1/auth/me")).DataAsync(200);
        me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().NotContain(p => p!.StartsWith("Platform."));
    }

    [Fact]
    public async Task Switching_into_a_clinic_and_refreshing_never_bring_platform_permissions_into_a_clinic_token()
    {
        var (adminEmail, clinic) = await PlatformUserWithOneMembershipAsync();

        // Platform session (mobile, so the refresh token is in the body) → switch into the clinic.
        var mobile = factory.CreateClient();
        mobile.DefaultRequestHeaders.Add("X-Client-Type", "mobile");
        var platform = await factory.LoginDataAsync(mobile, adminEmail);
        var platformRefresh = platform.GetProperty("refreshToken").GetString();

        var switched = await (await factory.WithToken(platform.GetProperty("accessToken").GetString()!)
            .PostAsJsonAsync("/api/v1/auth/switch-tenant", new { tenantId = clinic.TenantId })).DataAsync(200);
        Permissions(switched).Should().NotContain(p => p.StartsWith("Platform.")).And.Contain("Appointments.Create");
        (await factory.WithToken(switched.GetProperty("accessToken").GetString()!).GetAsync("/api/v1/platform/tenants"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The switch revoked the platform session's refresh family.
        (await mobile.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = platformRefresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Refreshing a clinic session keeps it a clinic session.
        var clinicLogin = await factory.LoginDataAsync(mobile, adminEmail, tenantId: clinic.TenantId);
        var refreshed = await (await mobile.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = clinicLogin.GetProperty("refreshToken").GetString() })).DataAsync(200);
        refreshed.GetProperty("tenant").GetProperty("id").GetGuid().Should().Be(clinic.TenantId);
        Permissions(refreshed).Should().NotContain(p => p.StartsWith("Platform."));
        (await factory.WithToken(refreshed.GetProperty("accessToken").GetString()!).GetAsync("/api/v1/platform/tenants"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Refreshing a platform session keeps it a platform session.
        var platformAgain = await factory.LoginDataAsync(mobile, adminEmail);
        var refreshedPlatform = await (await mobile.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = platformAgain.GetProperty("refreshToken").GetString() })).DataAsync(200);
        NoTenant(refreshedPlatform).Should().BeTrue();
        (await factory.WithToken(refreshedPlatform.GetProperty("accessToken").GetString()!).GetAsync("/api/v1/platform/tenants"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Platform_authorization_refuses_a_token_bound_to_a_clinic_even_if_it_carries_platform_permissions()
    {
        // Defence in depth: even a validly signed token that (by a future token-builder bug) carries platform
        // permissions together with a tid must not reach /platform.
        var clinic = await factory.RegisterClinicAsync();
        var membershipId = await clinic.OwnerMembershipIdAsync(); // a real, active seat (GD-02 checks it)
        var sessionId = await clinic.Client.CurrentSessionIdAsync(); // a real, live session (GD-03 checks it)
        var tokens = factory.Services.GetRequiredService<ITokenService>();
        var forged = tokens.CreateAccessToken(new TokenClaims(
            clinic.UserId, clinic.Email, "x", clinic.TenantId, membershipId, PlatformRole.SuperAdmin,
            [.. Domain.Authorization.Permissions.AllPlatform, Domain.Authorization.Permissions.Patients.View], [], sessionId));

        var client = factory.WithToken(forged.Token);
        (await client.GetAsync("/api/v1/platform/tenants")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync($"/api/v1/platform/tenants/{clinic.TenantId}/suspend", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/v1/patients")).StatusCode.Should().Be(HttpStatusCode.OK, "the clinic permission itself still works");
    }

    [Fact]
    public async Task Ordinary_users_still_enter_their_only_clinic_automatically()
    {
        var owner = await factory.RegisterClinicAsync();
        var login = await LoginDataAsync(owner.Email, tenantId: null);
        login.GetProperty("tenant").GetProperty("id").GetGuid().Should().Be(owner.TenantId);
        Permissions(login).Should().NotContain(p => p.StartsWith("Platform."));
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(string AdminEmail, ClinicSession Clinic)> PlatformUserWithOneMembershipAsync()
    {
        var adminEmail = await factory.CreatePlatformUserAsync(PlatformRole.SuperAdmin);
        var clinic = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(clinic.TenantId, "professional");
        await factory.AddExistingAccountAsync(clinic.Client, adminEmail, await clinic.Client.RoleIdAsync("receptionist"));
        return (adminEmail, clinic);
    }

    private async Task<JsonElement> LoginDataAsync(string email, Guid? tenantId) =>
        await factory.LoginDataAsync(factory.CreateClient(), email, tenantId: tenantId);

    /// <summary>Null properties are omitted from responses, so "no tenant" = absent or null.</summary>
    private static bool NoTenant(JsonElement session) =>
        !session.TryGetProperty("tenant", out var t) || t.ValueKind == JsonValueKind.Null;

    private static List<string> Permissions(JsonElement session) =>
        session.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToList();
}
