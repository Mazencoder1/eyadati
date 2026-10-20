using System.Net;
using System.Net.Http.Json;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.IntegrationTests.Infrastructure;
using ClinicSaaS.IntegrationTests.Qa;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog.Core;
using Serilog.Events;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// Phase 3B: what a caller sees when the database or a lock is briefly unavailable (503 + Retry-After, no internals),
/// that liveness and readiness tell the truth without leaking anything, and that a failing email provider no longer turns
/// an already-committed request into a 500 — and is logged without the message, the link or the address.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ResilienceAndHealthTests(ApiFactory factory)
{
    private sealed class Sink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];
        public void Emit(LogEvent logEvent) { lock (_events) _events.Add(logEvent); }
        public string[] Text() { lock (_events) return _events.Select(e => e.Level + " " + e.RenderMessage() + " " + e.Exception + " " + (e.Properties.TryGetValue("EventId", out var id) ? id.ToString() : "")).ToArray(); }
    }

    private WebApplicationFactory<Program> WithFaultEndpoints(Sink sink) => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("Serilog:MinimumLevel:Default", "Verbose");
        b.ConfigureTestServices(s =>
        {
            s.AddSingleton<ILogEventSink>(sink);
            s.AddControllers().AddApplicationPart(typeof(ResilienceAndHealthTests).Assembly);
        });
    });

    // ------------------------------------------------------------------ 503

    [Theory]
    [InlineData("/__test/lock-timeout", "doctor:3f2a-secret-resource")]
    [InlineData("/__test/pool", "db-secret-host")]
    public async Task Transient_failures_answer_503_with_Retry_After_and_no_internal_detail(string url, string secretInTheException)
    {
        var sink = new Sink();
        using var host = WithFaultEndpoints(sink);

        var res = await host.CreateClient().PostAsJsonAsync(url, new { });

        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        res.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));
        (await res.ErrorCodeAsync()).Should().Be("TEMPORARILY_UNAVAILABLE");
        var body = await res.Content.ReadAsStringAsync();
        body.Should().NotContain(secretInTheException).And.NotContain("Timeout").And.NotContain("Exception").And.Contain("correlationId");
        sink.Text().Should().Contain(t => t.StartsWith("Warning") && t.Contains("temporarily unavailable") && t.Contains("DatabaseTemporarilyUnavailable"),
            "operators are told, at Warning not Error, under a stable event id (4106) that alerts match");
    }

    [Fact]
    public async Task The_message_is_localized_and_honest_that_the_last_action_may_or_may_not_have_gone_through()
    {
        using var host = WithFaultEndpoints(new Sink());
        var client = host.CreateClient();

        var ar = await (await client.PostAsJsonAsync("/__test/lock-timeout", new { })).ReadJsonAsync();
        ar.GetProperty("message").GetString().Should().Contain("حاول مجدداً");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        var en = await (await client.PostAsJsonAsync("/__test/lock-timeout", new { })).ReadJsonAsync();
        en.GetProperty("message").GetString().Should().Contain("Check whether your last action went through");
    }

    [Fact]
    public async Task An_unexpected_exception_is_still_a_plain_500_not_a_503()
    {
        using var host = WithFaultEndpoints(new Sink());
        var res = await host.CreateClient().PostAsJsonAsync("/__test/boom", new { });

        res.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        res.Headers.RetryAfter.Should().BeNull();
        (await res.ErrorCodeAsync()).Should().Be("INTERNAL_ERROR");
    }

    // ------------------------------------------------------------------ database down

    private WebApplicationFactory<Program> DatabaseDownHost() => factory.WithWebHostBuilder(b =>
    {
        // A database that does not exist: the server answers "cannot open database" (a transient error number).
        b.UseSetting("ConnectionStrings:Default", ApiFactory.TestConnectionString("ClinicSaaS_NoSuchDb_" + Guid.NewGuid().ToString("N")));
        b.UseSetting("Database:MigrateOnStartup", "false");
        b.UseSetting("Bootstrap:SuperAdminEmail", "");
        b.UseSetting("Bootstrap:SuperAdminPassword", "");
        b.UseSetting("Database:Resilience:DeltaMilliseconds", "5");
        b.UseSetting("Database:Resilience:MaxIntervalMilliseconds", "10");
    });

    [Fact]
    public async Task When_the_database_is_unreachable_requests_answer_503_without_any_sql_or_server_detail()
    {
        using var host = DatabaseDownHost();
        var client = host.CreateClient();

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "someone@clinic-test.sa", password = "Whatever-Passw0rd" });

        login.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        login.Headers.RetryAfter.Should().NotBeNull();
        (await login.ErrorCodeAsync()).Should().Be("TEMPORARILY_UNAVAILABLE");
        var body = await login.Content.ReadAsStringAsync();
        body.Should().NotContain("ClinicSaaS_NoSuchDb").And.NotContain("Cannot open database").And.NotContain("SqlException")
            .And.NotContain("Login failed").And.NotContain("(localdb)");
    }

    [Fact]
    public async Task Liveness_never_touches_the_database_and_readiness_reports_it_with_no_detail()
    {
        using var down = DatabaseDownHost();
        var client = down.CreateClient();

        var live = await client.GetAsync("/health/live");
        live.StatusCode.Should().Be(HttpStatusCode.OK, "the process is alive even though the database is not");
        (await live.Content.ReadAsStringAsync()).Should().Be("Healthy");

        var ready = await client.GetAsync("/health/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var text = await ready.Content.ReadAsStringAsync();
        text.Should().Be("Unhealthy", "no exception text, server name or database name");
        ready.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Readiness_is_healthy_when_the_database_answers()
    {
        var ready = await factory.CreateClient().GetAsync("/health/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ready.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }

    // ------------------------------------------------------------------ email provider failure

    private sealed class FailingEmailSender : IEmailSender
    {
        // A provider error that echoes the recipient, the key and the body (as real ones sometimes do).
        public Task SendAsync(EmailMessage message, CancellationToken ct = default) =>
            throw new HttpRequestException($"401 invalid api key sk-LIVE-SECRET-123 sending to {message.To}: {message.HtmlBody}");
    }

    [Fact]
    public async Task A_failing_email_provider_does_not_turn_committed_requests_into_errors_and_is_logged_safely()
    {
        var sink = new Sink();
        using var host = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Serilog:MinimumLevel:Default", "Verbose");
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<ILogEventSink>(sink);
                s.RemoveAll<IEmailSender>();
                s.AddSingleton<IEmailSender, FailingEmailSender>();
            });
        });
        var client = host.CreateClient();
        var email = TestApi.UniqueEmail("mailfail");

        // Registration commits the clinic and then tries to send the verification email.
        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            clinicName = "عيادة البريد", city = "الرياض", clinicPhone = "0501234567", ownerFullName = "د. بريد",
            email, password = TestApi.Password, acceptTerms = true,
        });
        await register.EnsureStatusAsync(201);
        // Forgot password answers the same whether or not anything could be sent.
        (await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        // The account really exists and works.
        await factory.LoginAsync(email);

        var text = string.Join("\n", sink.Text());
        text.Should().Contain("Email delivery failed for \"email-verification\"").And.Contain("Email delivery failed for \"password-reset\"");
        text.Should().Contain("*@\"clinic-test.sa\"").And.Contain("HttpRequestException");
        text.Should().NotContain("sk-LIVE-SECRET-123").And.NotContain(email).And.NotContain("token=").And.NotContain("href");
    }
}
