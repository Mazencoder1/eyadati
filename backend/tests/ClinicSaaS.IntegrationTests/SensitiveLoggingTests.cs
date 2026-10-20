using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using ClinicSaaS.IntegrationTests.Qa;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// Phase 3B: log output is checked, not assumed. The whole account lifecycle runs with our own code logging at the most
/// verbose level (framework categories stay at Warning, as production-like environments require) and every captured
/// event — message, exception text and properties — must be free of passwords, access/refresh tokens, one-time link
/// tokens, the signing key and patient identifiers.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SensitiveLoggingTests(ApiFactory factory)
{
    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];
        public void Emit(LogEvent logEvent) { lock (_events) _events.Add(logEvent); }

        public string[] AllText()
        {
            lock (_events)
                return _events.Select(e => e.RenderMessage() + " | " + e.Exception + " | "
                                           + string.Join(",", e.Properties.Select(p => p.Key + "=" + p.Value))).ToArray();
        }
    }

    [Fact]
    public async Task Nothing_sensitive_reaches_the_logs_across_the_account_lifecycle()
    {
        var sink = new CapturingSink();
        using var host = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Serilog:MinimumLevel:Default", "Verbose");
            b.ConfigureTestServices(s => s.AddSingleton<ILogEventSink>(sink));
        });
        var secrets = new List<string> { ApiFactory.JwtSigningKey };

        // --- register, sign in (web + native), a wrong password, refresh, change password
        var email = TestApi.UniqueEmail("logs");
        const string password = "Logs-Passw0rd-First!";
        const string newPassword = "Logs-Passw0rd-Second!";
        const string wrongPassword = "Logs-WrongPassw0rd!";
        secrets.AddRange([password, newPassword, wrongPassword]);

        var web = host.CreateClient();
        var register = await web.PostAsJsonAsync("/api/v1/auth/register", new
        {
            clinicName = "عيادة السجلات", city = "الرياض", clinicPhone = "0501234567", ownerFullName = "د. سجل",
            email, password, acceptTerms = true,
        });
        var access = (await register.DataAsync(201)).GetProperty("accessToken").GetString()!;
        secrets.Add(access);
        web.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);

        var native = host.CreateClient();
        native.DefaultRequestHeaders.Add("X-Client-Type", "mobile");
        var login = await (await native.PostAsJsonAsync("/api/v1/auth/login", new { email, password })).DataAsync(200);
        var refreshToken = login.GetProperty("refreshToken").GetString()!;
        secrets.Add(refreshToken);
        secrets.Add(login.GetProperty("accessToken").GetString()!);
        await native.PostAsJsonAsync("/api/v1/auth/login", new { email, password = wrongPassword }); // failed login
        var refreshed = await (await native.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken })).DataAsync(200);
        secrets.Add(refreshed.GetProperty("refreshToken").GetString()!);
        secrets.Add(refreshed.GetProperty("accessToken").GetString()!);

        // --- one-time links: verification, reset, invitation
        secrets.Add(factory.Emails.TokenFor(email, "/verify-email"));
        await web.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email });
        var resetToken = factory.Emails.TokenFor(email, "/reset-password");
        secrets.Add(resetToken);
        await web.PostAsJsonAsync("/api/v1/auth/reset-password", new { token = resetToken, newPassword });
        await web.PostAsJsonAsync("/api/v1/auth/reset-password", new { token = resetToken, newPassword }); // replay → rejected

        var owner = await host.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email, password = newPassword });
        var ownerClient = host.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await owner.DataAsync(200)).GetProperty("accessToken").GetString()!);
        await factory.UsePlanAsync((await (await ownerClient.GetAsync("/api/v1/auth/me")).DataAsync(200)).GetProperty("tenantId").GetGuid(), "professional");
        var invitee = TestApi.UniqueEmail("invitee");
        const string inviteePassword = "Logs-Invitee-Passw0rd!";
        secrets.Add(inviteePassword);
        var roles = await (await ownerClient.GetAsync("/api/v1/roles")).ReadJsonAsync();
        var nurse = roles.GetProperty("data").EnumerateArray().Single(r => r.TryGetProperty("systemKey", out var k) && k.GetString() == "nurse").GetProperty("id").GetGuid();
        await ownerClient.PostAsJsonAsync("/api/v1/staff", new { fullName = "ممرضة", email = invitee, roleIds = new[] { nurse } });
        var inviteToken = factory.Emails.TokenFor(invitee, "/accept-invitation");
        secrets.Add(inviteToken);
        await host.CreateClient().PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token = inviteToken, newPassword = inviteePassword });

        // --- patient data: identifiers travel in bodies AND query strings; none may be logged
        const string nationalId = "1087654321";
        const string mobile = "0551239876";
        secrets.AddRange([nationalId, mobile]);
        await ownerClient.PostAsJsonAsync("/api/v1/patients", new
        {
            fullName = "مريض سجل", mobileNumber = mobile, gender = "Male", identityType = "NationalId", identityNumber = nationalId,
        });
        await ownerClient.GetAsync($"/api/v1/patients/duplicates?mobile={mobile}&identityNumber={nationalId}");
        await ownerClient.GetAsync($"/api/v1/patients?search={nationalId}");

        // --- change password, logout
        await ownerClient.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = newPassword, newPassword = password });
        await native.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken });

        var text = sink.AllText();
        text.Length.Should().BeGreaterThan(0, "the capture must actually be receiving events or this test proves nothing");
        foreach (var secret in secrets.Where(s => !string.IsNullOrEmpty(s)).Distinct())
            text.Should().NotContain(t => t.Contains(secret, StringComparison.Ordinal), $"'{secret[..Math.Min(6, secret.Length)]}…' must never be logged");
    }

    [Fact]
    public async Task A_server_error_is_logged_without_the_request_body_and_returned_without_details()
    {
        var sink = new CapturingSink();
        using var host = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Serilog:MinimumLevel:Default", "Verbose");
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<ILogEventSink>(sink);
                s.AddControllers().AddApplicationPart(typeof(SensitiveLoggingTests).Assembly);
            });
        });
        const string secretInBody = "Body-Secret-Value-12345";

        var res = await host.CreateClient().PostAsJsonAsync("/__test/boom", new { password = secretInBody });

        res.StatusCode.Should().Be(System.Net.HttpStatusCode.InternalServerError);
        var raw = await res.Content.ReadAsStringAsync();
        raw.Should().NotContain("sa-password-leak").And.NotContain("Server=").And.NotContain("StackTrace").And.NotContain(secretInBody)
            .And.NotContain("Exception").And.NotContain(" at ");
        JsonDocument.Parse(raw).RootElement.GetProperty("code").GetString().Should().Be("INTERNAL_ERROR");
        sink.AllText().Should().NotContain(t => t.Contains(secretInBody), "request bodies are never logged");
    }
}

/// <summary>Test-only endpoint that fails the way an unexpected bug or a bad connection string would.</summary>
[Microsoft.AspNetCore.Mvc.ApiController]
[Microsoft.AspNetCore.Mvc.Route("__test")]
[Microsoft.AspNetCore.Authorization.AllowAnonymous]
public sealed class TestFaultController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [Microsoft.AspNetCore.Mvc.HttpPost("boom")]
    public Microsoft.AspNetCore.Mvc.IActionResult Boom() =>
        throw new InvalidOperationException("Cannot connect: Server=prod-db;User ID=sa;Password=sa-password-leak at C:/inetpub/secret/path.cs");

    /// <summary>What AppDbContext.AcquireLockAsync throws when another request holds the booking lock for 10 s.</summary>
    [Microsoft.AspNetCore.Mvc.HttpPost("lock-timeout")]
    public Microsoft.AspNetCore.Mvc.IActionResult LockTimeout() => throw new TimeoutException("Could not acquire lock 'doctor:3f2a-secret-resource'.");

    /// <summary>What ADO.NET throws when every pooled connection is busy.</summary>
    [Microsoft.AspNetCore.Mvc.HttpPost("pool")]
    public Microsoft.AspNetCore.Mvc.IActionResult Pool() =>
        throw new InvalidOperationException("Timeout expired.  The timeout period elapsed prior to obtaining a connection from the pool.  Server=db-secret-host");
}
