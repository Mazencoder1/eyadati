using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.IntegrationTests.Infrastructure;
using ClinicSaaS.IntegrationTests.Qa;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// Phase 3C: the monitoring hooks that alert rules depend on (docs/operations/monitoring-and-alerting.md). Each hook has
/// a stable event id and named properties; the security trail is mirrored to the log with identifiers only, after the
/// audit row is saved, and clinical activity is never mirrored.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MonitoringEventsTests(ApiFactory factory)
{
    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];
        public void Emit(LogEvent logEvent) { lock (_events) _events.Add(logEvent); }
        public LogEvent[] All() { lock (_events) return [.. _events]; }
        public LogEvent[] WithEventId(int id) => All().Where(e => EventIdOf(e) == id).ToArray();
    }

    private static int? EventIdOf(LogEvent e) =>
        e.Properties.TryGetValue("EventId", out var v) && v is StructureValue s
        && s.Properties.FirstOrDefault(p => p.Name == "Id")?.Value is ScalarValue { Value: int id } ? id : null;

    private static string? Prop(LogEvent e, string name) =>
        e.Properties.TryGetValue(name, out var v) ? (v as ScalarValue)?.Value?.ToString() ?? v.ToString() : null;

    private static string Text(LogEvent e) =>
        e.RenderMessage() + " | " + e.Exception + " | " + string.Join(",", e.Properties.Select(p => p.Key + "=" + p.Value));

    private (WebApplicationFactory<Program> Host, CapturingSink Sink) Capture(params (string Key, string Value)[] settings)
    {
        var sink = new CapturingSink();
        var host = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Serilog:MinimumLevel:Default", "Information");
            foreach (var (key, value) in settings) b.UseSetting(key, value);
            b.ConfigureTestServices(s => s.AddSingleton<ILogEventSink>(sink));
        });
        return (host, sink);
    }

    [Fact]
    public async Task Security_audit_events_reach_the_log_with_identifiers_only_and_clinical_activity_does_not()
    {
        var (host, sink) = Capture();
        using var _ = host;
        var email = TestApi.UniqueEmail("monitor");
        var web = host.CreateClient();
        var register = await web.PostAsJsonAsync("/api/v1/auth/register", new
        {
            clinicName = "عيادة المراقبة", city = "الرياض", clinicPhone = "0501234567", ownerFullName = "د. مراقب",
            email, password = TestApi.Password, acceptTerms = true,
        });
        var access = (await register.DataAsync(201)).GetProperty("accessToken").GetString()!;
        web.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);

        // Clinical activity: audited in the table, never mirrored to the log.
        var patientId = (await web.CreatePatientAsync(TestApi.NewPatient(mobile: "0507770001"))).GetProperty("id").GetGuid();
        (await web.GetAsync($"/api/v1/patients/{patientId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Wrong passwords until the account locks (default threshold 5).
        var anon = host.CreateClient();
        for (var i = 0; i < 5; i++)
            await anon.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Not-The-Passw0rd!" });
        (await (await anon.PostAsJsonAsync("/api/v1/auth/login", new { email, password = TestApi.Password })).ErrorCodeAsync())
            .Should().Be("ACCOUNT_LOCKED");

        var events = sink.WithEventId(SecurityLogEvents.SecurityAudit.Id);
        var actions = events.Select(e => Prop(e, "AuditAction")).ToList();
        actions.Should().Contain(["tenant.registered", "auth.login.failed", "auth.account.locked"]);
        actions.Should().NotContain(a => a!.StartsWith("patient.") || a.StartsWith("visit.") || a.StartsWith("prescription."));

        var locked = events.Single(e => Prop(e, "AuditAction") == "auth.account.locked");
        locked.Level.Should().Be(LogEventLevel.Warning, "an account lockout is alert-worthy");
        long.Parse(Prop(locked, "AuditId")!).Should().BePositive("the row was saved, so its id links the log line to the audit table");
        Prop(locked, "AuditUserId").Should().NotBeNullOrEmpty();
        events.Where(e => Prop(e, "AuditAction") == "auth.login.failed").Should().OnlyContain(e => e.Level == LogEventLevel.Information);

        // Identifiers only: no audit details, client address, user agent, email, mobile number or patient id.
        foreach (var e in events)
        {
            e.Properties.Keys.Should().NotContain(["Details", "IpAddress", "UserAgent"]);
            Text(e).Should().NotContain(email).And.NotContain("0507770001").And.NotContain(patientId.ToString());
        }
    }

    [Fact]
    public async Task A_rate_limit_refusal_is_logged_with_policy_and_path_but_never_the_query_string()
    {
        // Endpoint policy ("auth").
        var (authHost, authSink) = Capture(("RateLimiting:AuthPerMinute", "2"));
        using (authHost)
        {
            var client = authHost.CreateClient();
            var statuses = new List<HttpStatusCode>();
            for (var i = 0; i < 4; i++)
                statuses.Add((await client.PostAsJsonAsync("/api/v1/auth/login", new { email = TestApi.UniqueEmail("rl"), password = "x" })).StatusCode);
            statuses.Should().Contain(HttpStatusCode.TooManyRequests);
        }
        authSink.WithEventId(SecurityLogEvents.RateLimited.Id).Should().NotBeEmpty()
            .And.OnlyContain(e => e.Level == LogEventLevel.Warning && Prop(e, "RateLimitPolicy") == "auth"
                                  && Prop(e, "RequestMethod") == "POST" && Prop(e, "RequestPath") == "/api/v1/auth/login");

        // Global per-user/IP budget on an endpoint without its own policy, with personal data in the query string.
        var (apiHost, apiSink) = Capture(("RateLimiting:ApiPerMinute", "2"));
        using (apiHost)
        {
            var client = apiHost.CreateClient();
            var statuses = new List<HttpStatusCode>();
            for (var i = 0; i < 4; i++) statuses.Add((await client.GetAsync("/api/v1/patients?search=0509990001")).StatusCode);
            statuses.Should().Contain(HttpStatusCode.TooManyRequests);
        }
        apiSink.WithEventId(SecurityLogEvents.RateLimited.Id).Should().NotBeEmpty()
            .And.OnlyContain(e => Prop(e, "RateLimitPolicy") == "global" && Prop(e, "RequestPath") == "/api/v1/patients");
        apiSink.All().Select(Text).Should().NotContain(t => t.Contains("0509990001"), "query strings can carry personal data");
    }

    [Fact]
    public async Task Bootstrap_secrets_left_in_the_environment_after_the_account_exists_are_reported_without_the_value()
    {
        // The shared factory configures Bootstrap:* and has already created the account, so this host starts with
        // secrets that are no longer needed.
        var (host, sink) = Capture();
        using var _ = host;
        (await host.CreateClient().GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);

        sink.WithEventId(SecurityLogEvents.BootstrapSecretStillConfigured.Id).Should().ContainSingle()
            .Which.Level.Should().Be(LogEventLevel.Warning);
        sink.All().Select(Text).Should().NotContain(t => t.Contains(ApiFactory.SuperAdminPassword));
    }
}
