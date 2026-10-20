using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Domain.Authorization;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.IntegrationTests.Infrastructure;
using ClinicSaaS.IntegrationTests.Qa;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using JwtOptions = ClinicSaaS.Infrastructure.Security.JwtOptions;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// Phase 3A, P3-D3/P3-D4 (GD-03, GD-15): an access token is only as good as its session. Every request re-checks that
/// the token's <c>sid</c> still names a live session (an unrevoked, unexpired refresh token of the same user and clinic),
/// so logout, password change, ending a session, suspension and refresh-token reuse take effect immediately — for clinic
/// and platform tokens alike — and reactivating a clinic does not bring revoked sessions back.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SessionRevocationTests(ApiFactory factory)
{
    private static async Task ShouldBeEndedAsync(HttpResponseMessage res, string because = "")
    {
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, because);
        (await res.ErrorCodeAsync()).Should().Be("UNAUTHORIZED", because);
    }

    // ------------------------------------------------------------------ logout / password change / end session

    [Fact]
    public async Task Native_logout_ends_the_access_token_on_every_endpoint_at_once()
    {
        var owner = await factory.RegisterClinicAsync();
        var s = await factory.NativeSignInAsync(owner.Email);
        await (await s.Client.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);

        (await s.Client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = s.RefreshToken })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await ShouldBeEndedAsync(await s.Client.GetAsync("/api/v1/patients"), "clinic data");
        await ShouldBeEndedAsync(await s.Client.PostAsJsonAsync("/api/v1/patients", TestApi.NewPatient(mobile: "0501110001")), "clinic write");
        await ShouldBeEndedAsync(await s.Client.GetAsync("/api/v1/auth/me"), "/auth/me");
        await ShouldBeEndedAsync(await s.Client.GetAsync("/api/v1/auth/sessions"), "/auth/sessions");
        await ShouldBeEndedAsync(await s.Client.PostAsJsonAsync("/api/v1/auth/switch-tenant", new { tenantId = owner.TenantId }), "/auth/switch-tenant");

        // The owner's other (web) session is a different session and is untouched.
        await (await owner.Client.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
    }

    [Fact]
    public async Task Web_logout_with_the_cookie_ends_the_access_token_at_once()
    {
        var owner = await factory.RegisterClinicAsync();
        var web = factory.CreateClient(); // keeps the httpOnly rt cookie
        var login = await (await web.PostAsJsonAsync("/api/v1/auth/login", new { email = owner.Email, password = TestApi.Password })).DataAsync(200);
        var bearer = factory.WithToken(login.GetProperty("accessToken").GetString()!);
        await (await bearer.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);

        (await web.PostAsync("/api/v1/auth/logout", JsonContent.Create(new { }))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await ShouldBeEndedAsync(await bearer.GetAsync("/api/v1/patients"));
        await ShouldBeEndedAsync(await bearer.GetAsync("/api/v1/auth/me"));
    }

    [Fact]
    public async Task Password_change_ends_every_session_including_the_one_that_changed_it_and_issues_a_new_one()
    {
        var owner = await factory.RegisterClinicAsync();
        var other = await factory.NativeSignInAsync(owner.Email);
        const string newPassword = "Phase3A-N3w-Passw0rd";

        var changed = await owner.Client.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = TestApi.Password, newPassword });
        var fresh = factory.WithToken((await changed.DataAsync(200)).GetProperty("accessToken").GetString()!);

        await ShouldBeEndedAsync(await owner.Client.GetAsync("/api/v1/patients"), "the token that made the change");
        await ShouldBeEndedAsync(await other.Client.GetAsync("/api/v1/patients"), "another device");
        (await other.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = other.RefreshToken })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await (await fresh.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
        await (await fresh.GetAsync("/api/v1/auth/me")).EnsureStatusAsync(200);
    }

    [Fact]
    public async Task Password_reset_from_an_emailed_link_ends_the_access_token_at_once()
    {
        var owner = await factory.RegisterClinicAsync();
        var anon = factory.CreateClient();
        await anon.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = owner.Email });
        var token = factory.Emails.TokenFor(owner.Email, "/reset-password");
        await (await anon.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = "Phase3A-Reset-Passw0rd" })).EnsureStatusAsync(204);

        await ShouldBeEndedAsync(await owner.Client.GetAsync("/api/v1/patients"));
    }

    [Fact]
    public async Task Ending_another_session_ends_only_that_sessions_access_token()
    {
        var owner = await factory.RegisterClinicAsync();
        var phone = await factory.NativeSignInAsync(owner.Email);
        var phoneSession = await phone.Client.CurrentSessionIdAsync();

        (await owner.Client.DeleteAsync($"/api/v1/auth/sessions/{phoneSession}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await ShouldBeEndedAsync(await phone.Client.GetAsync("/api/v1/patients"));
        await (await owner.Client.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
    }

    [Fact]
    public async Task A_user_cannot_end_someone_elses_session()
    {
        var a = await factory.RegisterClinicAsync();
        var b = await factory.RegisterClinicAsync();
        var bSession = await b.Client.CurrentSessionIdAsync();

        var attempt = await a.Client.DeleteAsync($"/api/v1/auth/sessions/{bSession}");
        attempt.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await attempt.ErrorCodeAsync()).Should().Be("SESSION_NOT_FOUND");
        await (await b.Client.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
    }

    [Fact]
    public async Task Switching_clinic_ends_the_previous_clinics_access_token_and_the_new_one_works()
    {
        var home = await factory.RegisterClinicAsync();
        var other = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(other.TenantId, "professional");
        await factory.AddExistingAccountAsync(other.Client, home.Email, await other.Client.RoleIdAsync("receptionist"));

        var switched = await (await home.Client.PostAsJsonAsync("/api/v1/auth/switch-tenant", new { tenantId = other.TenantId })).DataAsync(200);
        var into = factory.WithToken(switched.GetProperty("accessToken").GetString()!);

        await ShouldBeEndedAsync(await home.Client.GetAsync("/api/v1/patients"));
        (await (await into.GetAsync("/api/v1/auth/me")).DataAsync(200)).GetProperty("tenantId").GetGuid().Should().Be(other.TenantId);
    }

    // ------------------------------------------------------------------ rotation and reuse

    [Fact]
    public async Task Refresh_rotation_keeps_the_session_alive_and_replaying_a_rotated_token_kills_it_and_its_access_tokens()
    {
        var owner = await factory.RegisterClinicAsync();
        var s = await factory.NativeSignInAsync(owner.Email);

        var rotated = await (await s.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = s.RefreshToken })).DataAsync(200);
        var second = factory.WithToken(rotated.GetProperty("accessToken").GetString()!);
        // Same session (same sid): both the old and the new access token keep working while the family is live.
        await (await s.Client.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
        await (await second.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
        (await s.Client.CurrentSessionIdAsync()).Should().Be(await second.CurrentSessionIdAsync());

        // Replaying the first (already rotated) refresh token is theft: the whole session dies, access tokens included.
        (await s.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = s.RefreshToken })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await ShouldBeEndedAsync(await s.Client.GetAsync("/api/v1/patients"));
        await ShouldBeEndedAsync(await second.GetAsync("/api/v1/patients"));
    }

    // ------------------------------------------------------------------ suspension / reactivation (GD-15)

    [Fact]
    public async Task Suspension_refuses_every_session_of_the_clinic_and_reactivation_does_not_revive_them()
    {
        var clinic = await factory.RegisterClinicAsync();
        var other = await factory.RegisterClinicAsync();
        var phone = await factory.NativeSignInAsync(clinic.Email);
        var admin = await factory.LoginAsync(ApiFactory.SuperAdminEmail, ApiFactory.SuperAdminPassword);

        await (await admin.PostAsync($"/api/v1/platform/tenants/{clinic.TenantId}/suspend", null)).EnsureStatusAsync(204);
        foreach (var client in new[] { clinic.Client, phone.Client })
        {
            var res = await client.GetAsync("/api/v1/patients");
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await res.ErrorCodeAsync()).Should().Be("TENANT_SUSPENDED");
        }
        // Another clinic and the platform are not affected.
        await (await other.Client.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
        await (await admin.GetAsync("/api/v1/platform/tenants")).EnsureStatusAsync(200);

        await (await admin.PostAsync($"/api/v1/platform/tenants/{clinic.TenantId}/reactivate", null)).EnsureStatusAsync(204);

        // The sessions that existed during the suspension stay dead: tokens and refresh tokens.
        foreach (var client in new[] { clinic.Client, phone.Client })
        {
            await ShouldBeEndedAsync(await client.GetAsync("/api/v1/patients"), "reactivation must not revive old tokens");
            await ShouldBeEndedAsync(await client.GetAsync("/api/v1/auth/me"));
        }
        (await phone.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = phone.RefreshToken })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // A new sign-in after reactivation is a new session and works.
        var fresh = await factory.LoginAsync(clinic.Email);
        await (await fresh.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
        await (await other.Client.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
    }

    // ------------------------------------------------------------------ platform sessions

    [Fact]
    public async Task Platform_tokens_are_checked_too_logout_and_ending_a_session_refuse_them_at_once()
    {
        var email = await factory.CreatePlatformUserAsync(PlatformRole.SuperAdmin);
        var laptop = await factory.NativeSignInAsync(email);
        var tablet = await factory.NativeSignInAsync(email);
        await (await laptop.Client.GetAsync("/api/v1/platform/tenants")).EnsureStatusAsync(200);
        await (await tablet.Client.GetAsync("/api/v1/platform/tenants")).EnsureStatusAsync(200);

        // End the tablet from the laptop.
        var tabletSession = await tablet.Client.CurrentSessionIdAsync();
        (await laptop.Client.DeleteAsync($"/api/v1/auth/sessions/{tabletSession}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await ShouldBeEndedAsync(await tablet.Client.GetAsync("/api/v1/platform/tenants"));
        await (await laptop.Client.GetAsync("/api/v1/platform/tenants")).EnsureStatusAsync(200);

        // Logging out ends the laptop's token.
        (await laptop.Client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = laptop.RefreshToken })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await ShouldBeEndedAsync(await laptop.Client.GetAsync("/api/v1/platform/tenants"));
        await ShouldBeEndedAsync(await laptop.Client.PostAsync($"/api/v1/platform/tenants/{Guid.NewGuid()}/suspend", null));
    }

    // ------------------------------------------------------------------ isolation

    [Fact]
    public async Task A_session_is_bound_to_its_user_and_its_clinic_a_token_cannot_borrow_another_sessions_id()
    {
        var home = await factory.RegisterClinicAsync();
        var other = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(other.TenantId, "professional");
        var otherSeat = await factory.AddExistingAccountAsync(other.Client, home.Email, await other.Client.RoleIdAsync("receptionist"));
        var stranger = await factory.RegisterClinicAsync();

        var homeSession = await home.Client.CurrentSessionIdAsync();
        var strangerSession = await stranger.Client.CurrentSessionIdAsync();
        var tokens = factory.Services.GetRequiredService<ITokenService>();
        string[] perms = [Permissions.Patients.View];

        // Control: a token that matches its session (user, clinic, membership) is accepted.
        var homeSeat = await home.OwnerMembershipIdAsync();
        var genuine = tokens.CreateAccessToken(new TokenClaims(home.UserId, home.Email, "x", home.TenantId, homeSeat, PlatformRole.None, perms, [], homeSession));
        await (await factory.WithToken(genuine.Token).GetAsync("/api/v1/patients")).EnsureStatusAsync(200);

        // Same user and a real active seat in the other clinic, but the session belongs to the home clinic.
        var wrongClinic = tokens.CreateAccessToken(new TokenClaims(home.UserId, home.Email, "x", other.TenantId, otherSeat, PlatformRole.None, perms, [], homeSession));
        await ShouldBeEndedAsync(await factory.WithToken(wrongClinic.Token).GetAsync("/api/v1/patients"), "session of clinic A used for clinic B");

        // Someone else's session id with this user's identity.
        var borrowed = tokens.CreateAccessToken(new TokenClaims(home.UserId, home.Email, "x", home.TenantId, homeSeat, PlatformRole.None, perms, [], strangerSession));
        await ShouldBeEndedAsync(await factory.WithToken(borrowed.Token).GetAsync("/api/v1/patients"), "another user's session id");

        // A clinic session cannot be presented as a platform session either.
        var asPlatform = tokens.CreateAccessToken(new TokenClaims(home.UserId, home.Email, "x", null, null, PlatformRole.SuperAdmin, [.. Permissions.AllPlatform], [], homeSession));
        await ShouldBeEndedAsync(await factory.WithToken(asPlatform.Token).GetAsync("/api/v1/platform/tenants"), "clinic session presented without its clinic");

        // And the home clinic's own session is still fine after all of that.
        await (await home.Client.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
    }

    [Fact]
    public async Task A_validly_signed_token_without_a_session_id_is_refused()
    {
        var owner = await factory.RegisterClinicAsync();
        var seat = await owner.OwnerMembershipIdAsync();
        var jwt = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<JwtOptions>>().Value;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(jwt.GetKey(), SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity([
                new Claim("sub", owner.UserId.ToString()), new Claim("tid", owner.TenantId.ToString()), new Claim("mid", seat.ToString()),
                new Claim("prole", "None"), new Claim("perm", Permissions.Patients.View),
            ]),
        });

        await ShouldBeEndedAsync(await factory.WithToken(token).GetAsync("/api/v1/patients"));
    }

    // ------------------------------------------------------------------ anonymous endpoints

    [Fact]
    public async Task A_dead_session_token_is_treated_as_anonymous_on_public_endpoints_and_never_blocks_sign_in()
    {
        var owner = await factory.RegisterClinicAsync();
        var s = await factory.NativeSignInAsync(owner.Email);
        await s.Client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = s.RefreshToken });

        // The stale bearer is still attached to these requests.
        await (await s.Client.GetAsync("/api/v1/plans")).EnsureStatusAsync(200);
        await (await s.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = owner.Email, password = TestApi.Password })).EnsureStatusAsync(200);
        (await s.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = s.RefreshToken })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_dead_session_token_cannot_stand_in_as_the_signed_in_account_when_accepting_an_invitation()
    {
        var clinic = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(clinic.TenantId, "professional");
        var person = await factory.RegisterClinicAsync();
        await (await clinic.Client.PostAsJsonAsync("/api/v1/staff", new
        {
            fullName = "x", email = person.Email, roleIds = new[] { await clinic.Client.RoleIdAsync("nurse") },
        })).EnsureStatusAsync(201);
        var token = factory.Emails.TokenFor(person.Email, "/accept-invitation");

        var s = await factory.NativeSignInAsync(person.Email);
        await s.Client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = s.RefreshToken });

        // An existing account must be signed in to accept; a logged-out token is not "signed in".
        var denied = await s.Client.PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token });
        denied.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await denied.ErrorCodeAsync()).Should().Be("INVITATION_SIGN_IN_REQUIRED");

        // With a live session it works.
        var live = await factory.NativeSignInAsync(person.Email);
        await (await live.Client.PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token })).EnsureStatusAsync(204);
    }
}
