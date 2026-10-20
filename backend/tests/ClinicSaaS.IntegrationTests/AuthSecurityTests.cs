using System.Net;
using System.Net.Http.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class AuthSecurityTests(ApiFactory factory)
{
    [Fact]
    public async Task Registration_creates_a_working_session_with_owner_permissions_and_secure_cookie()
    {
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            clinicName = "مجمع النخبة الطبي", city = "جدة", clinicPhone = "0561234567", ownerFullName = "سارة",
            email = TestApi.UniqueEmail(), password = TestApi.Password, acceptTerms = true,
        });
        await res.EnsureStatusAsync(201);

        var cookie = res.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("rt="));
        cookie.Should().ContainAll("httponly", "secure", "samesite=strict", "path=/api/v1/auth");

        var data = (await res.ReadJsonAsync()).GetProperty("data");
        data.TryGetProperty("refreshToken", out _).Should().BeFalse("web clients never see the refresh token");
        data.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().Contain("Patients.Delete");
        data.GetProperty("tenant").GetProperty("slug").GetString().Should().MatchRegex("^clinic-[a-z0-9]{6}$");
    }

    [Fact]
    public async Task Duplicate_email_registration_is_rejected()
    {
        var a = await factory.RegisterClinicAsync();
        var res = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new
        {
            clinicName = "x", city = "x", clinicPhone = "0501234567", ownerFullName = "x",
            email = a.Email.ToUpperInvariant(), password = TestApi.Password, acceptTerms = true,
        });
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.ErrorCodeAsync()).Should().Be("EMAIL_TAKEN");
    }

    [Fact]
    public async Task Validation_errors_are_per_field_and_in_arabic_by_default()
    {
        var res = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new
        {
            clinicName = "", city = "الرياض", clinicPhone = "0111234567", ownerFullName = "x",
            email = "not-an-email", password = "short", acceptTerms = false,
        });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.ReadJsonAsync();
        body.GetProperty("code").GetString().Should().Be("VALIDATION_FAILED");
        var errors = body.GetProperty("errors");
        errors.GetProperty("clinicPhone")[0].GetString().Should().Contain("رقم الجوال");
        errors.GetProperty("email").GetArrayLength().Should().BeGreaterThan(0);
        errors.GetProperty("password").GetArrayLength().Should().BeGreaterThan(0);
        errors.GetProperty("clinicName").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Unknown_email_and_wrong_password_look_identical()
    {
        var a = await factory.RegisterClinicAsync();
        var client = factory.CreateClient();
        var wrongPassword = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = a.Email, password = "WrongPassw0rd" });
        var unknownEmail = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = TestApi.UniqueEmail(), password = "WrongPassw0rd" });

        wrongPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknownEmail.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await wrongPassword.ErrorCodeAsync()).Should().Be("INVALID_CREDENTIALS");
        (await unknownEmail.ErrorCodeAsync()).Should().Be("INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task Account_locks_after_five_failures_even_for_the_right_password()
    {
        var a = await factory.RegisterClinicAsync();
        var client = factory.CreateClient();
        for (var i = 0; i < 5; i++)
            await client.PostAsJsonAsync("/api/v1/auth/login", new { email = a.Email, password = "WrongPassw0rd" });

        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = a.Email, password = TestApi.Password });
        res.StatusCode.Should().Be((HttpStatusCode)423);
        (await res.ErrorCodeAsync()).Should().Be("ACCOUNT_LOCKED");
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_replay_revokes_the_whole_session()
    {
        var a = await factory.RegisterClinicAsync();
        var mobile = factory.CreateClient();
        mobile.DefaultRequestHeaders.Add("X-Client-Type", "mobile");

        var login = await (await mobile.PostAsJsonAsync("/api/v1/auth/login", new { email = a.Email, password = TestApi.Password })).ReadJsonAsync();
        var first = login.GetProperty("data").GetProperty("refreshToken").GetString();

        var rotated = await mobile.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first });
        await rotated.EnsureStatusAsync(200);
        var second = (await rotated.ReadJsonAsync()).GetProperty("data").GetProperty("refreshToken").GetString();
        second.Should().NotBe(first);

        // Replay of the old token = theft signal: rejected, and the legitimate new token dies with it.
        (await mobile.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await mobile.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = second })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Web_refresh_works_from_the_cookie_and_logout_invalidates_it()
    {
        var a = await factory.RegisterClinicAsync();
        var web = factory.CreateClient(); // cookie container persists the rt cookie
        await (await web.PostAsJsonAsync("/api/v1/auth/login", new { email = a.Email, password = TestApi.Password })).EnsureStatusAsync(200);

        await (await web.PostAsync("/api/v1/auth/refresh", JsonContent.Create(new { }))).EnsureStatusAsync(200);
        await (await web.PostAsync("/api/v1/auth/logout", JsonContent.Create(new { }))).EnsureStatusAsync(204);
        (await web.PostAsync("/api/v1/auth/refresh", JsonContent.Create(new { }))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Password_reset_is_single_use_and_ends_existing_sessions()
    {
        var a = await factory.RegisterClinicAsync();
        var mobile = factory.CreateClient();
        mobile.DefaultRequestHeaders.Add("X-Client-Type", "mobile");
        var session = await (await mobile.PostAsJsonAsync("/api/v1/auth/login", new { email = a.Email, password = TestApi.Password })).ReadJsonAsync();
        var refresh = session.GetProperty("data").GetProperty("refreshToken").GetString();

        var anon = factory.CreateClient();
        (await anon.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = a.Email })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        // Unknown emails get the exact same response (no account enumeration).
        (await anon.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = TestApi.UniqueEmail() })).StatusCode.Should().Be(HttpStatusCode.Accepted);

        var token = factory.Emails.TokenFor(a.Email, "/reset-password");
        const string newPassword = "N3wPassw0rd-2026";
        await (await anon.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword })).EnsureStatusAsync(204);

        var reuse = await anon.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = "Another1Passw0rd" });
        (await reuse.ErrorCodeAsync()).Should().Be("INVALID_OR_EXPIRED_TOKEN");

        (await mobile.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = refresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await factory.LoginAsync(a.Email, newPassword);
    }

    [Fact]
    public async Task Email_verification_flow()
    {
        var email = TestApi.UniqueEmail();
        await factory.RegisterClinicAsync(email);
        var token = factory.Emails.TokenFor(email, "/verify-email");

        var anon = factory.CreateClient();
        await (await anon.PostAsJsonAsync("/api/v1/auth/verify-email", new { token })).EnsureStatusAsync(204);
        (await anon.PostAsJsonAsync("/api/v1/auth/verify-email", new { token })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var login = await (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email, password = TestApi.Password })).ReadJsonAsync();
        login.GetProperty("data").GetProperty("user").GetProperty("emailConfirmed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_correlation_id()
    {
        var res = await factory.CreateClient().GetAsync("/api/v1/patients");
        res.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        res.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
        res.Headers.GetValues("X-Correlation-Id").Single().Should().NotBeNullOrEmpty();
    }
}
