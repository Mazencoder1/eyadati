using System.Net;
using System.Net.Http.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using ClinicSaaS.IntegrationTests.Qa;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// Phase 3A, XSS / refresh-token finding (P3-SEC-08): script running in the web app must never be able to obtain a
/// long-lived refresh token. Browsers get it only as an httpOnly cookie; the "native app" mode (refresh token in the body)
/// is refused for anything that looks like a browser, before any session or token is touched, and CORS no longer even
/// allows the header that selects it. Legitimate native clients are unaffected.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RefreshTokenExposureTests(ApiFactory factory)
{
    private const string BrowserOrigin = "https://app.example.test";

    /// <summary>What a browser adds to a request on its own (page script cannot set or remove any of these).</summary>
    public static TheoryData<string, string> BrowserHeaders => new()
    {
        { "Origin", BrowserOrigin },
        { "Sec-Fetch-Site", "same-origin" },
        { "Sec-Fetch-Mode", "cors" },
        { "Sec-Fetch-Dest", "empty" },
    };

    private static HttpRequestMessage Post(string url, object body, string? bearer = null, params (string Name, string Value)[] headers)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        foreach (var (name, value) in headers) req.Headers.TryAddWithoutValidation(name, value);
        if (bearer is not null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    // ------------------------------------------------------------------ 1 + 3: the normal browser flow

    [Fact]
    public async Task Browser_login_and_refresh_deliver_the_refresh_token_only_as_an_httponly_cookie_never_in_the_body()
    {
        var owner = await factory.RegisterClinicAsync();
        var web = factory.CreateClient();

        foreach (var (url, body) in new (string, object)[]
                 {
                     ("/api/v1/auth/login", new { email = owner.Email, password = TestApi.Password }),
                     ("/api/v1/auth/refresh", new { }),
                 })
        {
            var res = await web.PostAsJsonAsync(url, body);
            await res.EnsureStatusAsync(200);

            var raw = await res.Content.ReadAsStringAsync();
            raw.Should().NotContainEquivalentOf("refreshToken", $"{url}: a browser response must not carry a refresh token");
            var cookie = res.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("rt="));
            cookie.Should().ContainAll("httponly", "secure", "samesite=strict", "path=/api/v1/auth");
        }
    }

    [Fact]
    public async Task Browser_requests_that_announce_themselves_with_the_browser_headers_still_get_the_cookie_flow()
    {
        var owner = await factory.RegisterClinicAsync();
        var web = factory.CreateClient();
        var req = Post("/api/v1/auth/login", new { email = owner.Email, password = TestApi.Password }, null, ("Origin", BrowserOrigin), ("Sec-Fetch-Mode", "cors"));

        var res = await web.SendAsync(req);
        await res.EnsureStatusAsync(200);
        (await res.Content.ReadAsStringAsync()).Should().NotContainEquivalentOf("refreshToken");
        res.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("rt=") && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ 2: a browser cannot switch into native mode

    [Theory]
    [MemberData(nameof(BrowserHeaders))]
    public async Task A_browser_asking_for_native_mode_is_refused_on_refresh_without_touching_the_token(string header, string value)
    {
        var owner = await factory.RegisterClinicAsync();
        var native = await factory.NativeSignInAsync(owner.Email); // the genuine app's session

        // "XSS": script on the web origin presents a refresh token and asks for it back in the body.
        var attack = factory.CreateClient();
        var res = await attack.SendAsync(Post("/api/v1/auth/refresh", new { refreshToken = native.RefreshToken }, null,
            ("X-Client-Type", "mobile"), (header, value)));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.ErrorCodeAsync()).Should().Be("CLIENT_TYPE_NOT_ALLOWED");
        (await res.Content.ReadAsStringAsync()).Should().NotContainEquivalentOf("refreshToken").And.NotContain(native.RefreshToken);
        res.Headers.Contains("Set-Cookie").Should().BeFalse();

        // Nothing happened to the session: the refresh token was not rotated, revoked or flagged as reused.
        var legit = await (await native.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = native.RefreshToken })).DataAsync(200);
        legit.GetProperty("refreshToken").GetString().Should().NotBe(native.RefreshToken);
    }

    [Theory]
    [MemberData(nameof(BrowserHeaders))]
    public async Task A_browser_asking_for_native_mode_is_refused_on_login_register_and_every_other_session_issuing_call(string header, string value)
    {
        var owner = await factory.RegisterClinicAsync();
        var other = await factory.RegisterClinicAsync();
        var client = factory.CreateClient();
        (string Name, string Value)[] h = [("X-Client-Type", "MOBILE"), (header, value)]; // value is case-insensitive

        var requests = new[]
        {
            Post("/api/v1/auth/login", new { email = owner.Email, password = TestApi.Password }, null, h),
            Post("/api/v1/auth/register", new
            {
                clinicName = "عيادة", city = "الرياض", clinicPhone = "0501234567", ownerFullName = "x",
                email = TestApi.UniqueEmail(), password = TestApi.Password, acceptTerms = true,
            }, null, h),
            Post("/api/v1/auth/change-password", new { currentPassword = TestApi.Password, newPassword = "Phase3A-Other-Passw0rd" }, owner.AccessToken, h),
            Post("/api/v1/auth/switch-tenant", new { tenantId = other.TenantId }, owner.AccessToken, h),
        };

        foreach (var req in requests)
        {
            var res = await client.SendAsync(req);
            var what = $"{req.Method} {req.RequestUri}";
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden, what);
            (await res.ErrorCodeAsync()).Should().Be("CLIENT_TYPE_NOT_ALLOWED", what);
            (await res.Content.ReadAsStringAsync()).Should().NotContainEquivalentOf("refreshToken", what);
            res.Headers.Contains("Set-Cookie").Should().BeFalse(what);
        }

        // Refused means refused before acting: the password did not change and the session is still the same one.
        await (await owner.Client.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
        await factory.LoginAsync(owner.Email, TestApi.Password);
    }

    [Fact]
    public async Task A_browser_cannot_use_the_body_to_present_a_refresh_token_in_web_mode_either()
    {
        var owner = await factory.RegisterClinicAsync();
        var native = await factory.NativeSignInAsync(owner.Email);

        // No cookie, no native header: a refresh token in the body is not a credential for a web client.
        var web = factory.CreateClient();
        (await web.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = native.RefreshToken })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // … and a body token cannot be used to log a session out in web mode.
        (await web.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = native.RefreshToken })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await (await native.Client.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
        await (await native.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = native.RefreshToken })).EnsureStatusAsync(200);
    }

    [Fact]
    public async Task Cors_does_not_allow_the_native_mode_header_so_cross_origin_browser_calls_fail_at_the_preflight()
    {
        using var cors = factory.WithWebHostBuilder(b => b.UseSetting("Cors:AllowedOrigins:0", BrowserOrigin));
        cors.ClientOptions.BaseAddress = new Uri("https://localhost");
        var client = cors.CreateClient();

        async Task<HttpResponseMessage> Preflight(string requestHeaders)
        {
            var req = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/refresh");
            req.Headers.Add("Origin", BrowserOrigin);
            req.Headers.Add("Access-Control-Request-Method", "POST");
            req.Headers.Add("Access-Control-Request-Headers", requestHeaders);
            return await client.SendAsync(req);
        }

        // Control: the headers the web app really sends are allowed for the configured origin.
        var allowed = await Preflight("content-type,authorization,accept-language");
        allowed.Headers.TryGetValues("Access-Control-Allow-Origin", out var origin).Should().BeTrue("the policy is active in this test");
        origin.Should().Contain(BrowserOrigin);

        // The server answers with the headers it allows; X-Client-Type is not among them, so the browser fails the
        // preflight and never sends the real request.
        var native = await Preflight("content-type,x-client-type");
        native.Headers.TryGetValues("Access-Control-Allow-Headers", out var allowedHeaders).Should().BeTrue();
        string.Join(",", allowedHeaders!).Should().ContainEquivalentOf("content-type").And.NotContainEquivalentOf("x-client-type");
    }

    // ------------------------------------------------------------------ 4: native apps keep working

    [Fact]
    public async Task Native_clients_still_receive_the_refresh_token_in_the_body_and_rotate_it_without_any_cookie()
    {
        var owner = await factory.RegisterClinicAsync();
        var native = factory.CreateClient();
        native.DefaultRequestHeaders.Add("X-Client-Type", "mobile");

        var login = await native.PostAsJsonAsync("/api/v1/auth/login", new { email = owner.Email, password = TestApi.Password });
        var loginData = await login.DataAsync(200);
        login.Headers.Contains("Set-Cookie").Should().BeFalse();
        var first = loginData.GetProperty("refreshToken").GetString();
        first.Should().NotBeNullOrEmpty();

        var refreshed = await native.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first });
        var second = (await refreshed.DataAsync(200)).GetProperty("refreshToken").GetString();
        second.Should().NotBe(first);
        refreshed.Headers.Contains("Set-Cookie").Should().BeFalse();

        (await native.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = second })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await native.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = second })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------------ 5: isolation stays intact

    [Fact]
    public async Task A_refresh_token_only_ever_renews_its_own_session_and_clinic()
    {
        var a = await factory.RegisterClinicAsync();
        var b = await factory.RegisterClinicAsync();
        var sa = await factory.NativeSignInAsync(a.Email);
        var sb = await factory.NativeSignInAsync(b.Email);

        var renewed = await (await sa.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = sa.RefreshToken })).DataAsync(200);
        renewed.GetProperty("tenant").GetProperty("id").GetGuid().Should().Be(a.TenantId);
        renewed.GetProperty("user").GetProperty("email").GetString().Should().BeEquivalentTo(a.Email);

        // Clinic A's refreshed token reaches only clinic A's data.
        var aData = factory.WithToken(renewed.GetProperty("accessToken").GetString()!);
        await (await aData.PostAsJsonAsync("/api/v1/patients", TestApi.NewPatient(mobile: "0502220001"))).EnsureStatusAsync(201);
        var bList = await (await sb.Client.GetAsync("/api/v1/patients")).ReadJsonAsync();
        bList.GetProperty("data").GetArrayLength().Should().Be(0, "clinic B must not see clinic A's patient");
    }
}
