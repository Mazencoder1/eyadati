using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClinicSaaS.Infrastructure.Persistence;
using ClinicSaaS.IntegrationTests.Infrastructure;
using ClinicSaaS.IntegrationTests.Qa;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// Phase 3B: (1) the client address is only taken from forwarded headers when the TCP peer is a configured proxy;
/// (2) the two cookie-authenticated endpoints refuse browser requests from origins that are not the web app;
/// (3) JWT signing-key rotation with an overlap window.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ProxyOriginAndKeyTests(ApiFactory factory)
{
    // ------------------------------------------------------------------ client address

    /// <summary>The test server has no real socket: this lets a test choose the TCP peer address per request.</summary>
    private sealed class FakePeerStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nextMiddleware) =>
            {
                if (ctx.Request.Headers.TryGetValue("X-Test-Peer", out var peer)) ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
                return nextMiddleware();
            });
            next(app);
        };
    }

    private WebApplicationFactory<Program> WithForwardedHeaders(params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings) b.UseSetting(key, value);
            b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter, FakePeerStartupFilter>());
        });

    /// <summary>Signs in through the given host with a chosen TCP peer and forwarded header; returns the address the API recorded.</summary>
    private async Task<string?> RecordedAddressAsync(WebApplicationFactory<Program> host, string peer, string? forwardedFor)
    {
        var owner = await factory.RegisterClinicAsync();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Peer", peer);
        if (forwardedFor is not null) client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        await (await client.PostAsJsonAsync("/api/v1/auth/login", new { email = owner.Email, password = TestApi.Password })).EnsureStatusAsync(200);

        string? ip = null;
        await factory.WithDbAsync(null, async db =>
        {
            ip = await db.RefreshTokens.Where(t => t.UserId == owner.UserId).OrderByDescending(t => t.CreatedAtUtc).Select(t => t.CreatedByIp).FirstAsync();
        });
        return ip;
    }

    [Fact]
    public async Task A_client_cannot_choose_its_address_by_sending_a_forwarded_header_directly()
    {
        using var host = WithForwardedHeaders(("ForwardedHeaders:KnownProxies:0", "10.0.0.5"));
        (await RecordedAddressAsync(host, peer: "203.0.113.9", forwardedFor: "198.51.100.1"))
            .Should().Be("203.0.113.9", "the peer is not a trusted proxy, so its X-Forwarded-For is ignored");
    }

    [Fact]
    public async Task A_trusted_proxy_tells_the_api_the_real_client_address()
    {
        using var host = WithForwardedHeaders(("ForwardedHeaders:KnownProxies:0", "10.0.0.5"));
        (await RecordedAddressAsync(host, peer: "10.0.0.5", forwardedFor: "198.51.100.77")).Should().Be("198.51.100.77");
    }

    [Fact]
    public async Task Addresses_a_client_prepends_to_the_header_are_ignored_only_the_trusted_proxys_entry_counts()
    {
        using var host = WithForwardedHeaders(("ForwardedHeaders:KnownProxies:0", "10.0.0.5"));
        // The client sent "6.6.6.6"; the proxy appended the address it actually saw.
        (await RecordedAddressAsync(host, peer: "10.0.0.5", forwardedFor: "6.6.6.6, 198.51.100.77")).Should().Be("198.51.100.77");
    }

    [Fact]
    public async Task A_proxy_range_trusts_every_address_in_it_and_nothing_outside()
    {
        using var host = WithForwardedHeaders(("ForwardedHeaders:KnownNetworks:0", "10.1.0.0/24"));
        (await RecordedAddressAsync(host, peer: "10.1.0.17", forwardedFor: "198.51.100.5")).Should().Be("198.51.100.5");
        (await RecordedAddressAsync(host, peer: "10.2.0.17", forwardedFor: "198.51.100.6")).Should().Be("10.2.0.17");
    }

    [Fact]
    public async Task A_direct_deployment_ignores_forwarded_headers_entirely()
    {
        using var host = WithForwardedHeaders(("ForwardedHeaders:NoProxy", "true"));
        (await RecordedAddressAsync(host, peer: "203.0.113.50", forwardedFor: "198.51.100.1")).Should().Be("203.0.113.50");
    }

    [Fact]
    public async Task Rate_limiting_follows_the_resolved_address_so_a_spoofed_header_cannot_dodge_it()
    {
        // Same TCP peer, a different spoofed X-Forwarded-For on every request: still ONE budget (the peer's).
        using var host = WithForwardedHeaders(
            ("ForwardedHeaders:KnownProxies:0", "10.0.0.5"), ("RateLimiting:SensitivePer15Minutes", "3"));
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Peer", "203.0.113.200");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/forgot-password") { Content = JsonContent.Create(new { email = $"nobody{i}@clinic-test.sa" }) };
            req.Headers.Add("X-Forwarded-For", $"198.51.100.{i + 1}");
            statuses.Add((await client.SendAsync(req)).StatusCode);
        }
        statuses.Count(s => s == HttpStatusCode.TooManyRequests).Should().BeGreaterThanOrEqualTo(2, "the budget is per real address, not per header value");
    }

    // ------------------------------------------------------------------ CSRF: trusted origin for cookie endpoints

    private WebApplicationFactory<Program> WithWebOrigin() =>
        factory.WithWebHostBuilder(b => b.UseSetting("Cors:AllowedOrigins:0", "https://app.example.test"));

    private static HttpRequestMessage Post(string url, string? origin)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { }) };
        if (origin is not null) req.Headers.TryAddWithoutValidation("Origin", origin);
        return req;
    }

    [Theory]
    [InlineData("/api/v1/auth/refresh")]
    [InlineData("/api/v1/auth/logout")]
    public async Task Cookie_endpoints_refuse_a_browser_request_from_any_origin_but_the_web_app(string url)
    {
        using var host = WithWebOrigin();
        var client = host.CreateClient();

        foreach (var evil in new[] { "https://evil.example.test", "https://app.example.test.evil.test", "http://app.example.test", "null" })
        {
            var res = await client.SendAsync(Post(url, evil));
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden, evil);
            (await res.ErrorCodeAsync()).Should().Be("FORBIDDEN", evil);
        }
    }

    [Fact]
    public async Task Cookie_endpoints_still_work_from_the_web_app_origin_and_without_an_origin()
    {
        using var host = WithWebOrigin();
        var owner = await factory.RegisterClinicAsync();
        var web = host.CreateClient();
        await (await web.PostAsJsonAsync("/api/v1/auth/login", new { email = owner.Email, password = TestApi.Password })).EnsureStatusAsync(200);

        await (await web.SendAsync(Post("/api/v1/auth/refresh", "https://app.example.test"))).EnsureStatusAsync(200);
        await (await web.SendAsync(Post("/api/v1/auth/refresh", "https://APP.example.test/"))).EnsureStatusAsync(200);
        await (await web.SendAsync(Post("/api/v1/auth/refresh", null))).EnsureStatusAsync(200);
        (await web.SendAsync(Post("/api/v1/auth/logout", "https://app.example.test"))).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ------------------------------------------------------------------ signing-key rotation

    private const string NewKey = "rotation-test-new-key-9f8e7d6c5b4a39281706f5e4d3c2b1a0";

    [Fact]
    public async Task Rotating_the_signing_key_with_an_overlap_keeps_old_tokens_working_and_signs_new_ones_with_the_new_key()
    {
        var owner = await factory.RegisterClinicAsync();
        var oldToken = (await factory.NativeSignInAsync(owner.Email)).AccessToken; // signed with the test host's current key

        using var rotated = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Jwt:SigningKey", NewKey);
            b.UseSetting("Jwt:PreviousSigningKeys:0", ApiFactory.JwtSigningKey);
        });
        using var afterOverlap = factory.WithWebHostBuilder(b => b.UseSetting("Jwt:SigningKey", NewKey));

        HttpClient Bearer(WebApplicationFactory<Program> host, string token)
        {
            var c = host.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return c;
        }

        // During the overlap the old token is still accepted by the rotated host …
        await (await Bearer(rotated, oldToken).GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
        // … a token it issues now is signed with the NEW key only: the old-key host refuses it, the new-key host accepts it …
        var login = await rotated.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = owner.Email, password = TestApi.Password });
        var newToken = (await login.DataAsync(200)).GetProperty("accessToken").GetString()!;
        (await Bearer(factory.WithWebHostBuilder(_ => { }), newToken).GetAsync("/api/v1/patients")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await (await Bearer(afterOverlap, newToken).GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
        // … and once the overlap entry is removed, the old token is refused.
        (await Bearer(afterOverlap, oldToken).GetAsync("/api/v1/patients")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Only_HS256_is_accepted_the_right_key_with_another_algorithm_is_refused()
    {
        var owner = await factory.RegisterClinicAsync();
        var real = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().ReadJsonWebToken(owner.AccessToken);
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(ApiFactory.JwtSigningKey));

        string Resign(string algorithm) => new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = real.Issuer, Audience = real.Audiences.Single(), Expires = real.ValidTo,
            Subject = new System.Security.Claims.ClaimsIdentity(real.Claims.Where(c => c.Type is not ("iss" or "aud" or "exp" or "nbf" or "iat"))),
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(key, algorithm),
        });

        // Control: the same claims, the same key, the accepted algorithm.
        await (await factory.WithToken(Resign("HS256")).GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
        // Only the algorithm differs.
        foreach (var other in new[] { "HS384" }) // (the test key is too short for HS512 to be created at all)
            (await factory.WithToken(Resign(other)).GetAsync("/api/v1/patients")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, other);
    }

    [Fact]
    public async Task A_key_that_is_not_configured_never_validates_a_token()
    {
        var owner = await factory.RegisterClinicAsync();
        var token = (await factory.NativeSignInAsync(owner.Email)).AccessToken;
        using var other = factory.WithWebHostBuilder(b => b.UseSetting("Jwt:SigningKey", NewKey));
        var c = other.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await c.GetAsync("/api/v1/patients")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
