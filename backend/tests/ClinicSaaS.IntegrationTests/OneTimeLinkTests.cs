using System.Net.Http.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using ClinicSaaS.IntegrationTests.Qa;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// Phase 3B: one-time tokens leave the application inside emailed links. The token travels in the URL fragment
/// (<c>#token=</c>), which a browser never sends to a server — so it cannot land in a reverse proxy's, a load balancer's or
/// the web server's access log. The API itself only ever receives the token in a POST body.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OneTimeLinkTests(ApiFactory factory)
{
    private static void ShouldCarryTheTokenInTheFragment(string link, string path)
    {
        var uri = new Uri(link);
        uri.AbsolutePath.Should().Be(path, "the path alone identifies the page");
        uri.Query.Should().BeEmpty("a query string would be sent to — and logged by — every server on the way");
        uri.Fragment.Should().MatchRegex("^#token=[A-Za-z0-9_-]{43}$", "a 256-bit token, base64url");
    }

    [Fact]
    public async Task Verification_reset_and_invitation_links_use_the_fragment_and_their_tokens_still_work()
    {
        var owner = await factory.RegisterClinicAsync();
        ShouldCarryTheTokenInTheFragment(factory.Emails.LinkFor(owner.Email, "/verify-email"), "/verify-email");
        var verify = factory.Emails.TokenFor(owner.Email, "/verify-email");
        await (await owner.Client.PostAsJsonAsync("/api/v1/auth/verify-email", new { token = verify })).EnsureStatusAsync(204);

        await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = owner.Email });
        ShouldCarryTheTokenInTheFragment(factory.Emails.LinkFor(owner.Email, "/reset-password"), "/reset-password");
        var reset = factory.Emails.TokenFor(owner.Email, "/reset-password");
        await (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password", new { token = reset, newPassword = "Links-New-Passw0rd!" })).EnsureStatusAsync(204);

        await factory.UsePlanAsync(owner.TenantId, "professional");
        var invitee = TestApi.UniqueEmail("link-invitee");
        await (await (await factory.LoginAsync(owner.Email, "Links-New-Passw0rd!")).PostAsJsonAsync("/api/v1/staff", new
        {
            fullName = "x", email = invitee, roleIds = new[] { await (await factory.LoginAsync(owner.Email, "Links-New-Passw0rd!")).RoleIdAsync("nurse") },
        })).EnsureStatusAsync(201);
        var link = factory.Emails.LinkFor(invitee, "/accept-invitation");
        var uri = new Uri(link);
        uri.AbsolutePath.Should().Be("/accept-invitation");
        uri.Query.Should().BeEmpty();
        uri.Fragment.Should().StartWith("#token=");
        var invitation = factory.Emails.TokenFor(invitee, "/accept-invitation");
        await (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token = invitation, newPassword = TestApi.Password })).EnsureStatusAsync(204);
    }

    [Fact]
    public async Task The_api_reads_the_token_only_from_the_request_body_never_from_the_url()
    {
        var owner = await factory.RegisterClinicAsync();
        await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = owner.Email });
        var token = factory.Emails.TokenFor(owner.Email, "/reset-password");

        // A token in the query string is not accepted by the API (so nothing here can ever need it in a URL).
        var viaQuery = await factory.CreateClient().PostAsJsonAsync($"/api/v1/auth/reset-password?token={Uri.EscapeDataString(token)}", new { newPassword = "Links-Other-Passw0rd!" });
        viaQuery.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        // … and the link is still unused afterwards.
        await (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = "Links-Other-Passw0rd!" })).EnsureStatusAsync(204);
    }
}
