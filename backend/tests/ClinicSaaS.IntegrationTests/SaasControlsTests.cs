using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.IntegrationTests.Infrastructure;
using ClinicSaaS.IntegrationTests.Qa;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// Gate C SaaS controls (manual billing, D1): platform admin changes a clinic's plan / status / billing period;
/// an ended period makes the clinic read-only; suspension acts on the very next request. No schema involved.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SaasControlsTests(ApiFactory factory)
{
    private static object Body(string plan, string status, DateTime periodEnd, string? rowVersion, string cycle = "Monthly") =>
        new { planCode = plan, status, billingCycle = cycle, currentPeriodEndUtc = periodEnd, rowVersion };

    [Fact]
    public async Task Platform_admin_changes_plan_status_and_billing_period_and_the_change_is_audited()
    {
        var clinic = await factory.RegisterClinicAsync();
        var admin = await factory.LoginAsync(ApiFactory.SuperAdminEmail, ApiFactory.SuperAdminPassword);
        var url = $"/api/v1/platform/tenants/{clinic.TenantId}/subscription";

        var current = await (await admin.GetAsync(url)).DataAsync(200);
        current.GetProperty("planCode").GetString().Should().Be("basic");
        current.GetProperty("status").GetString().Should().Be("Trialing");
        current.GetProperty("hasFullAccess").GetBoolean().Should().BeTrue();

        var end = DateTime.UtcNow.AddDays(365);
        var updated = await (await admin.PutAsJsonAsync(url, Body("professional", "Active", end, current.GetProperty("rowVersion").GetString(), "Yearly")))
            .DataAsync(200);
        updated.GetProperty("planCode").GetString().Should().Be("professional");
        updated.GetProperty("status").GetString().Should().Be("Active");
        updated.GetProperty("billingCycle").GetString().Should().Be("Yearly");
        updated.GetProperty("currentPeriodEndUtc").GetDateTime().Should().BeCloseTo(end, TimeSpan.FromSeconds(1));
        updated.TryGetProperty("trialEndsAtUtc", out _).Should().BeFalse();

        // The clinic sees it at once (plan read per request): professional features and limits apply.
        var view = await (await clinic.Client.GetAsync("/api/v1/subscription")).DataAsync(200);
        view.GetProperty("planCode").GetString().Should().Be("professional");
        view.GetProperty("features").GetProperty("multi_branch").GetBoolean().Should().BeTrue();
        await (await clinic.Client.PostAsJsonAsync("/api/v1/branches", new { name = "فرع جديد", city = "جدة" })).EnsureStatusAsync(201);

        // A stale row version is a conflict, not a silent overwrite.
        (await (await admin.PutAsJsonAsync(url, Body("basic", "Active", end, current.GetProperty("rowVersion").GetString())))
            .ErrorCodeAsync()).Should().Be("CONCURRENCY_CONFLICT");

        await factory.WithDbAsync(null, async db =>
        {
            var row = await db.AuditLogs.SingleAsync(a => a.TenantId == clinic.TenantId && a.Action == "subscription.changed");
            row.Details.Should().Contain("\"basic\"").And.Contain("\"professional\"").And.Contain("Trialing").And.Contain("Active");
        });
    }

    [Fact]
    public async Task Subscription_changes_are_validated_and_need_platform_billing_permissions()
    {
        var clinic = await factory.RegisterClinicAsync();
        var admin = await factory.LoginAsync(ApiFactory.SuperAdminEmail, ApiFactory.SuperAdminPassword);
        var url = $"/api/v1/platform/tenants/{clinic.TenantId}/subscription";
        var rv = (await (await admin.GetAsync(url)).DataAsync(200)).GetProperty("rowVersion").GetString();

        (await admin.PutAsJsonAsync(url, Body("basic", "Expired", DateTime.UtcNow.AddDays(30), rv))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync(url, Body("basic", "Canceled", DateTime.UtcNow.AddDays(30), rv))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync(url, Body("basic", "Active", DateTime.UtcNow.AddDays(-1), rv))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync(url, Body("basic", "Active", DateTime.UtcNow.AddYears(5), rv))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await (await admin.PutAsJsonAsync(url, Body("gold", "Active", DateTime.UtcNow.AddDays(30), rv))).ErrorCodeAsync()).Should().Be("PLAN_NOT_FOUND");
        (await (await admin.PutAsJsonAsync($"/api/v1/platform/tenants/{Guid.NewGuid()}/subscription", Body("basic", "Active", DateTime.UtcNow.AddDays(30), rv)))
            .ErrorCodeAsync()).Should().Be("TENANT_NOT_FOUND");

        // Clinic owner (all clinic permissions) cannot reach it; platform Support (no billing permission) cannot either.
        (await clinic.Client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await clinic.Client.PutAsJsonAsync(url, Body("enterprise", "Active", DateTime.UtcNow.AddDays(30), rv))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var support = await factory.LoginAsync(await factory.CreatePlatformUserAsync(PlatformRole.Support));
        (await support.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await support.PutAsJsonAsync(url, Body("enterprise", "Active", DateTime.UtcNow.AddDays(30), rv))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Billing admin may.
        var billing = await factory.LoginAsync(await factory.CreatePlatformUserAsync(PlatformRole.BillingAdmin));
        await (await billing.PutAsJsonAsync(url, Body("professional", "PastDue", DateTime.UtcNow.AddDays(10), rv))).EnsureStatusAsync(200);
        var after = await (await clinic.Client.GetAsync("/api/v1/subscription")).DataAsync(200);
        after.GetProperty("planCode").GetString().Should().Be("professional");
        after.GetProperty("hasFullAccess").GetBoolean().Should().BeTrue("PastDue keeps access until the period ends");
    }

    [Fact]
    public async Task Ended_period_makes_the_clinic_read_only_until_the_platform_extends_it()
    {
        var owner = await factory.RegisterClinicAsync();
        var patientId = (await owner.Client.CreatePatientAsync()).GetProperty("id").GetGuid();
        await EndPeriodAsync(owner.TenantId); // the 14-day trial ran out

        // Reads, printing data, sign-in and session management keep working.
        await (await owner.Client.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
        await (await owner.Client.GetAsync($"/api/v1/patients/{patientId}")).EnsureStatusAsync(200);
        var me = await (await owner.Client.GetAsync("/api/v1/auth/me")).DataAsync(200);
        me.GetProperty("access").GetString().Should().Be("ReadOnly");
        (await (await owner.Client.GetAsync("/api/v1/subscription")).DataAsync(200)).GetProperty("hasFullAccess").GetBoolean().Should().BeFalse();
        await factory.LoginAsync(owner.Email);

        // Every write is refused with one clear code, whatever the endpoint.
        foreach (var write in new Func<Task<HttpResponseMessage>>[]
        {
            () => owner.Client.PostAsJsonAsync("/api/v1/patients", TestApi.NewPatient(mobile: "0507654321")),
            () => owner.Client.PutAsJsonAsync($"/api/v1/patients/{patientId}/medical", new { allergies = "x", rowVersion = "AAAAAAAAAAA=" }),
            () => owner.Client.DeleteAsync($"/api/v1/patients/{patientId}"),
            () => owner.Client.PostAsJsonAsync("/api/v1/branches", new { name = "x" }),
            () => owner.Client.PostAsJsonAsync("/api/v1/visits", new { patientId, doctorId = Guid.NewGuid() }),
        })
        {
            var res = await write();
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await res.ErrorCodeAsync()).Should().Be("SUBSCRIPTION_EXPIRED");
        }
        await (await owner.Client.GetAsync($"/api/v1/patients/{patientId}")).EnsureStatusAsync(200); // nothing was deleted

        // Manual renewal: the same session can write again immediately.
        var admin = await factory.LoginAsync(ApiFactory.SuperAdminEmail, ApiFactory.SuperAdminPassword);
        var url = $"/api/v1/platform/tenants/{owner.TenantId}/subscription";
        var rv = (await (await admin.GetAsync(url)).DataAsync(200)).GetProperty("rowVersion").GetString();
        await (await admin.PutAsJsonAsync(url, Body("basic", "Active", DateTime.UtcNow.AddMonths(1), rv))).EnsureStatusAsync(200);
        (await (await owner.Client.GetAsync("/api/v1/auth/me")).DataAsync(200)).GetProperty("access").GetString().Should().Be("Full");
        await (await owner.Client.PostAsJsonAsync("/api/v1/patients", TestApi.NewPatient(mobile: "0507654321"))).EnsureStatusAsync(201);
    }

    [Fact]
    public async Task Suspension_blocks_every_clinic_request_at_once_except_signing_out_and_reactivation_restores_it()
    {
        var owner = await factory.RegisterClinicAsync();
        var other = await factory.RegisterClinicAsync();
        var admin = await factory.LoginAsync(ApiFactory.SuperAdminEmail, ApiFactory.SuperAdminPassword);
        await (await admin.PostAsync($"/api/v1/platform/tenants/{owner.TenantId}/suspend", null)).EnsureStatusAsync(204);
        try
        {
            foreach (var res in new[]
            {
                await owner.Client.GetAsync("/api/v1/patients"),
                await owner.Client.GetAsync("/api/v1/settings"),
                await owner.Client.PostAsJsonAsync("/api/v1/patients", TestApi.NewPatient()),
            })
            {
                res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await res.ErrorCodeAsync()).Should().Be("TENANT_SUSPENDED");
            }
            // /auth/me is a clinic endpoint: a suspended clinic gets the same 403 (the UI shows its one "suspended" screen).
            var me = await owner.Client.GetAsync("/api/v1/auth/me");
            me.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await me.ErrorCodeAsync()).Should().Be("TENANT_SUSPENDED");
            (await owner.Client.PostAsJsonAsync("/api/v1/auth/logout", new { })).StatusCode.Should().Be(HttpStatusCode.NoContent);

            // Other clinics and the platform plane are unaffected.
            await (await other.Client.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
            await (await admin.GetAsync("/api/v1/platform/tenants")).EnsureStatusAsync(200);
        }
        finally
        {
            await (await admin.PostAsync($"/api/v1/platform/tenants/{owner.TenantId}/reactivate", null)).EnsureStatusAsync(204);
        }
        var again = await factory.LoginAsync(owner.Email);
        await (await again.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
    }

    [Fact]
    public async Task Downgrade_keeps_existing_data_and_only_gates_new_resources()
    {
        var owner = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(owner.TenantId, "professional");
        var second = (await (await owner.Client.PostAsJsonAsync("/api/v1/branches", new { name = "فرع الشمال" })).DataAsync(201)).GetProperty("id").GetGuid();

        var admin = await factory.LoginAsync(ApiFactory.SuperAdminEmail, ApiFactory.SuperAdminPassword);
        var url = $"/api/v1/platform/tenants/{owner.TenantId}/subscription";
        var rv = (await (await admin.GetAsync(url)).DataAsync(200)).GetProperty("rowVersion").GetString();
        await (await admin.PutAsJsonAsync(url, Body("basic", "Active", DateTime.UtcNow.AddMonths(1), rv))).EnsureStatusAsync(200);

        var branches = await (await owner.Client.GetAsync("/api/v1/branches")).DataAsync(200);
        branches.EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).Should().Contain(second);
        var third = await owner.Client.PostAsJsonAsync("/api/v1/branches", new { name = "فرع ثالث" });
        third.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await third.ErrorCodeAsync()).Should().BeOneOf("USAGE_LIMIT_REACHED", "FEATURE_NOT_AVAILABLE");
    }

    /// <summary>Simulates the passage of time: the current period ended a minute ago.</summary>
    private Task EndPeriodAsync(Guid tenantId) =>
        factory.WithDbAsync(tenantId, async db =>
        {
            var sub = await db.Subscriptions.SingleAsync();
            sub.CurrentPeriodStartUtc = DateTime.UtcNow.AddDays(-15);
            sub.CurrentPeriodEndUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        });
}
