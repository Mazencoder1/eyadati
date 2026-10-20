using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicSaaS.IntegrationTests.Qa;

/// <summary>
/// Gate D — independent QA of Phase 2. Adversarial checks that go beyond the feature tests: sweeps over every
/// endpoint the API exposes, forged bodies and route ids, platform authority, session edges. Tests named
/// <c>CurrentBehaviour_*</c> document a finding (see docs/security/GATE-D-qa.md) without changing the product.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QaGateDTests(ApiFactory factory)
{
    // ------------------------------------------------------------------ helpers

    private sealed record Route(string Method, string Template, string Url);

    /// <summary>Every routed API endpoint (from the app's own endpoint data sources), with route parameters filled in.</summary>
    private List<Route> AllApiRoutes()
    {
        var routes = new List<Route>();
        foreach (var source in factory.Services.GetServices<EndpointDataSource>())
        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
        {
            var template = "/" + endpoint.RoutePattern.RawText!.TrimStart('/');
            if (!template.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)) continue;
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            var url = System.Text.RegularExpressions.Regex.Replace(template, @"\{[^}]+\}", Guid.NewGuid().ToString());
            routes.AddRange(methods.Select(m => new Route(m, template, url)));
        }
        return routes.DistinctBy(r => (r.Method, r.Template)).ToList();
    }

    private static HttpRequestMessage Request(Route r) => new(new HttpMethod(r.Method), r.Url)
    {
        Content = r.Method is "GET" or "HEAD" or "DELETE" ? null : new StringContent("{}", Encoding.UTF8, "application/json"),
    };

    private static async Task<string?> CodeOf(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        try { return JsonDocument.Parse(text).RootElement.TryGetProperty("code", out var c) ? c.GetString() : null; }
        catch (JsonException) { return null; }
    }

    private Task EndPeriodAsync(Guid tenantId) =>
        factory.WithDbAsync(tenantId, async db =>
        {
            var sub = await db.Subscriptions.SingleAsync();
            sub.CurrentPeriodStartUtc = DateTime.UtcNow.AddDays(-15);
            sub.CurrentPeriodEndUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        });

    private Task SetSubscriptionAsync(Guid tenantId, string status, DateTime periodEnd) =>
        factory.WithDbAsync(tenantId, async db =>
        {
            var sub = await db.Subscriptions.SingleAsync();
            sub.Status = Enum.Parse<ClinicSaaS.Domain.Platform.SubscriptionStatus>(status);
            sub.CurrentPeriodStartUtc = periodEnd.AddDays(-30);
            sub.CurrentPeriodEndUtc = periodEnd;
            await db.SaveChangesAsync();
        });

    private async Task<int> CountAsync(Guid tenantId, Func<ClinicSaaS.Infrastructure.Persistence.AppDbContext, Task<int>> count)
    {
        var n = 0;
        await factory.WithDbAsync(tenantId, async db => n = await count(db));
        return n;
    }

    // ------------------------------------------------------------------ subscription enforcement: no bypass

    [Fact]
    public async Task Expired_clinic_every_write_endpoint_is_refused_and_every_read_still_answers_and_nothing_is_deleted()
    {
        var c = await factory.BookableClinicAsync();
        var visitId = await c.Owner.Client.VisitIdAsync(c);
        await c.Owner.Client.PrescriptionIdAsync(visitId);
        var before = (await CountAsync(c.Owner.TenantId, db => db.Patients.CountAsync()), await CountAsync(c.Owner.TenantId, db => db.Visits.CountAsync()),
            await CountAsync(c.Owner.TenantId, db => db.Prescriptions.CountAsync()), await CountAsync(c.Owner.TenantId, db => db.Appointments.CountAsync()));
        await EndPeriodAsync(c.Owner.TenantId);

        var routes = AllApiRoutes();
        routes.Should().HaveCountGreaterThan(60, "the sweep must see the whole API");
        var leaks = new List<string>();
        foreach (var r in routes.Where(r => !r.Template.StartsWith("/api/v1/auth", StringComparison.OrdinalIgnoreCase)))
        {
            var res = await c.Owner.Client.SendAsync(Request(r));
            var code = await CodeOf(res);
            if (r.Method is "GET" or "HEAD")
            {
                if (code == "SUBSCRIPTION_EXPIRED") leaks.Add($"read refused: {r.Method} {r.Template}");
            }
            else if (res.StatusCode != HttpStatusCode.Forbidden || code != "SUBSCRIPTION_EXPIRED")
            {
                leaks.Add($"write not refused: {r.Method} {r.Template} -> {(int)res.StatusCode} {code}");
            }
        }
        leaks.Should().BeEmpty();

        // Real data paths too (not only random ids): the clinic's own records stay readable, nothing was removed.
        await (await c.Owner.Client.GetAsync($"/api/v1/visits/{visitId}")).EnsureStatusAsync(200);
        await (await c.Owner.Client.GetAsync($"/api/v1/patients/{c.PatientId}/prescriptions")).EnsureStatusAsync(200);
        var after = (await CountAsync(c.Owner.TenantId, db => db.Patients.CountAsync()), await CountAsync(c.Owner.TenantId, db => db.Visits.CountAsync()),
            await CountAsync(c.Owner.TenantId, db => db.Prescriptions.CountAsync()), await CountAsync(c.Owner.TenantId, db => db.Appointments.CountAsync()));
        after.Should().Be(before);
    }

    [Fact]
    public async Task Suspended_clinic_every_non_auth_endpoint_is_refused_with_an_existing_token_and_the_platform_keeps_working()
    {
        var owner = await factory.RegisterClinicAsync();
        var admin = await factory.LoginAsync(ApiFactory.SuperAdminEmail, ApiFactory.SuperAdminPassword);
        await (await admin.PostAsync($"/api/v1/platform/tenants/{owner.TenantId}/suspend", null)).EnsureStatusAsync(204);
        try
        {
            var leaks = new List<string>();
            // /plans is the public catalog ([AllowAnonymous]): since Phase 3A the suspended clinic's revoked token is simply
            // treated as no token there (no identity, nothing clinic-specific), instead of being refused with a 403.
            foreach (var r in AllApiRoutes().Where(r => !r.Template.StartsWith("/api/v1/auth", StringComparison.OrdinalIgnoreCase)
                                                        && !r.Template.Equals("/api/v1/plans", StringComparison.OrdinalIgnoreCase)))
            {
                var res = await owner.Client.SendAsync(Request(r));
                if (res.StatusCode != HttpStatusCode.Forbidden || await CodeOf(res) != "TENANT_SUSPENDED")
                    leaks.Add($"{r.Method} {r.Template} -> {(int)res.StatusCode}");
            }
            leaks.Should().BeEmpty();

            // Sign-in and refresh are refused by the auth endpoints themselves.
            (await (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = owner.Email, password = TestApi.Password }))
                .ErrorCodeAsync()).Should().Be("TENANT_SUSPENDED");
            // Platform administration is not affected.
            await (await admin.GetAsync($"/api/v1/platform/tenants/{owner.TenantId}/subscription")).EnsureStatusAsync(200);
            await (await admin.GetAsync("/api/v1/platform/tenants")).EnsureStatusAsync(200);
        }
        finally
        {
            await (await admin.PostAsync($"/api/v1/platform/tenants/{owner.TenantId}/reactivate", null)).EnsureStatusAsync(204);
        }
        // GD-15 (fixed in Phase 3A): reactivation does not revive the sessions that suspension ended.
        var revived = await owner.Client.GetAsync("/api/v1/patients");
        revived.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await revived.ErrorCodeAsync()).Should().Be("UNAUTHORIZED");
        var fresh = await factory.LoginAsync(owner.Email);
        await (await fresh.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
    }

    [Fact]
    public async Task Expired_clinic_keeps_authentication_usable_login_refresh_change_password_logout()
    {
        var owner = await factory.RegisterClinicAsync();
        await EndPeriodAsync(owner.TenantId);

        var mobile = factory.CreateClient();
        mobile.DefaultRequestHeaders.Add("X-Client-Type", "mobile");
        var login = await (await mobile.PostAsJsonAsync("/api/v1/auth/login", new { email = owner.Email, password = TestApi.Password })).DataAsync(200);
        var refreshed = await (await mobile.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = login.GetProperty("refreshToken").GetString() }))
            .DataAsync(200);
        var client = factory.WithToken(refreshed.GetProperty("accessToken").GetString()!);
        (await (await client.GetAsync("/api/v1/auth/me")).DataAsync(200)).GetProperty("access").GetString().Should().Be("ReadOnly");
        // Changing the password works and returns a fresh session (the old refresh family is ended).
        await (await client.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = TestApi.Password, newPassword = "Gate-D-N3w-Passw0rd" }))
            .EnsureStatusAsync(200);
        (await mobile.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = refreshed.GetProperty("refreshToken").GetString() }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData("Trialing", 10, true)]
    [InlineData("Active", 10, true)]
    [InlineData("PastDue", 10, true)]
    [InlineData("Trialing", -1, false)]
    [InlineData("Active", -1, false)]
    [InlineData("PastDue", -1, false)]
    public async Task Access_follows_status_and_period_end(string status, int daysLeft, bool canWrite)
    {
        var owner = await factory.RegisterClinicAsync();
        await SetSubscriptionAsync(owner.TenantId, status, DateTime.UtcNow.AddDays(daysLeft));

        var write = await owner.Client.PostAsJsonAsync("/api/v1/patients", TestApi.NewPatient(mobile: "0507777000"));
        if (canWrite) await write.EnsureStatusAsync(201);
        else (await write.ErrorCodeAsync()).Should().Be("SUBSCRIPTION_EXPIRED");
        await (await owner.Client.GetAsync("/api/v1/patients")).EnsureStatusAsync(200);
        (await (await owner.Client.GetAsync("/api/v1/auth/me")).DataAsync(200)).GetProperty("access").GetString()
            .Should().Be(canWrite ? "Full" : "ReadOnly");
    }

    // ------------------------------------------------------------------ platform authority

    [Fact]
    public async Task Only_platform_billing_authority_can_read_or_change_a_subscription()
    {
        var a = await factory.BookableClinicAsync();
        var b = await factory.RegisterClinicAsync();
        var url = $"/api/v1/platform/tenants/{b.TenantId}/subscription";
        var admin = await factory.LoginAsync(ApiFactory.SuperAdminEmail, ApiFactory.SuperAdminPassword);
        var rv = (await (await admin.GetAsync(url)).DataAsync(200)).GetProperty("rowVersion").GetString();
        var body = new { planCode = "enterprise", status = "Active", billingCycle = "Yearly", currentPeriodEndUtc = DateTime.UtcNow.AddDays(300), rowVersion = rv };

        var (manager, _, _) = await factory.InviteAndAcceptAsync(a.Owner, "clinic_manager");
        var (reception, _, _) = await factory.InviteAndAcceptAsync(a.Owner, "receptionist");
        var support = await factory.LoginAsync(await factory.CreatePlatformUserAsync(PlatformRole.Support));
        var denied = new (string Who, HttpClient Client)[]
        {
            ("tenant A owner", a.Owner.Client), ("tenant A clinic manager", manager), ("tenant A receptionist", reception),
            ("tenant B's own owner", b.Client), ("platform Support", support), ("anonymous", factory.CreateClient()),
        };
        foreach (var (who, client) in denied)
        {
            (await client.GetAsync(url)).StatusCode.Should().BeOneOf([HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized], who);
            (await client.PutAsJsonAsync(url, body)).StatusCode.Should().BeOneOf([HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized], who);
            (await client.PostAsync($"/api/v1/platform/tenants/{b.TenantId}/suspend", null)).StatusCode
                .Should().BeOneOf([HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized], who);
        }
        (await (await b.Client.GetAsync("/api/v1/subscription")).DataAsync(200)).GetProperty("planCode").GetString().Should().Be("basic", "nothing changed");

        // Missing row version on an existing subscription is a validation error, not an overwrite.
        var noRv = await admin.PutAsJsonAsync(url, new { planCode = "enterprise", status = "Active", billingCycle = "Yearly", currentPeriodEndUtc = DateTime.UtcNow.AddDays(30) });
        noRv.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // Bad enum value and malformed date are 400s, not 500s.
        (await admin.PutAsJsonAsync(url, new { planCode = "basic", status = "Gold", billingCycle = "Monthly", currentPeriodEndUtc = DateTime.UtcNow.AddDays(30), rowVersion = rv }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync(url, new { planCode = "basic", status = "Active", billingCycle = "Weekly", currentPeriodEndUtc = DateTime.UtcNow.AddDays(30), rowVersion = rv }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsync(url, new StringContent("{\"planCode\":\"basic\",\"status\":\"Active\",\"billingCycle\":\"Monthly\",\"currentPeriodEndUtc\":\"not-a-date\"}", Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------------ sessions (documented trade-offs)

    // GD-02 (fixed): the disabled-member test moved to GateDFixPassTests (GD02_*).

    [Fact]
    public async Task Password_change_ends_refresh_and_the_already_issued_access_token_at_once()
    {
        // GD-03 (fixed in Phase 3A): the access token is checked against its live session on every request.
        // More cases (logout, ending a session, suspension, platform tokens, reuse): SessionRevocationTests.
        var owner = await factory.RegisterClinicAsync();
        var mobile = factory.CreateClient();
        mobile.DefaultRequestHeaders.Add("X-Client-Type", "mobile");
        var session = await (await mobile.PostAsJsonAsync("/api/v1/auth/login", new { email = owner.Email, password = TestApi.Password })).DataAsync(200);
        var access = factory.WithToken(session.GetProperty("accessToken").GetString()!);
        var refresh = session.GetProperty("refreshToken").GetString();

        await (await access.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = TestApi.Password, newPassword = "Gate-D-Rot4ted-Pw" })).EnsureStatusAsync(200);
        (await mobile.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = refresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await access.GetAsync("/api/v1/patients")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_pending_invitation_cannot_be_accepted_while_the_clinic_is_suspended_and_still_works_after_reactivation()
    {
        // GD-04 (fixed in Phase 3C): accepting used to activate the seat of a suspended clinic (/auth/* is exempt from
        // the access middleware). Now it is refused like sign-in, before anything changes: no password is set, the
        // link is not consumed and the seat stays pending, so the same link works once the clinic is reactivated.
        var owner = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(owner.TenantId, "professional");
        var email = TestApi.UniqueEmail("pending");
        await (await owner.Client.PostAsJsonAsync("/api/v1/staff", new { fullName = "x", email, roleIds = new[] { await owner.Client.RoleIdAsync("nurse") } }))
            .EnsureStatusAsync(201);
        var admin = await factory.LoginAsync(ApiFactory.SuperAdminEmail, ApiFactory.SuperAdminPassword);
        var token = factory.Emails.TokenFor(email, "/accept-invitation");
        await (await admin.PostAsync($"/api/v1/platform/tenants/{owner.TenantId}/suspend", null)).EnsureStatusAsync(204);
        try
        {
            var refused = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token, newPassword = TestApi.Password });
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await refused.ErrorCodeAsync()).Should().Be("TENANT_SUSPENDED");
            // No password was set by the refused attempt.
            (await (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email, password = TestApi.Password })).ErrorCodeAsync())
                .Should().Be("INVALID_CREDENTIALS");
        }
        finally
        {
            await (await admin.PostAsync($"/api/v1/platform/tenants/{owner.TenantId}/reactivate", null)).EnsureStatusAsync(204);
        }

        (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token, newPassword = TestApi.Password }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email, password = TestApi.Password }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------ clinical / prescriptions

    [Fact]
    public async Task Prescription_body_cannot_override_tenant_patient_doctor_or_branch()
    {
        var a = await factory.BookableClinicAsync();
        var b = await factory.BookableClinicAsync();
        var second = await a.Owner.AddBranchAsync();
        var visitId = await a.Owner.Client.VisitIdAsync(a);
        var otherPatient = (await a.Owner.Client.CreatePatientAsync(TestApi.NewPatient(mobile: "0506666000"))).GetProperty("id").GetGuid();

        var rx = await (await a.Owner.Client.PostAsJsonAsync("/api/v1/prescriptions", new
        {
            visitId, tenantId = b.Owner.TenantId, patientId = otherPatient, doctorId = b.DoctorId, branchId = second, issuedAtUtc = "2020-01-01T00:00:00Z",
            number = "RX-999999", items = ClinicalScenarios.RxItems(1),
        })).DataAsync(201);
        rx.GetProperty("patientId").GetGuid().Should().Be(a.PatientId);
        rx.GetProperty("doctorId").GetGuid().Should().Be(a.DoctorId);
        rx.GetProperty("number").GetString().Should().Be("RX-000001");
        rx.GetProperty("issuedAtUtc").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));
        await factory.WithDbAsync(a.Owner.TenantId, async db =>
        {
            var row = await db.Prescriptions.SingleAsync(p => p.Id == rx.GetProperty("id").GetGuid());
            row.TenantId.Should().Be(a.Owner.TenantId);
            row.VisitId.Should().Be(visitId);
        });
        (await (await b.Owner.Client.GetAsync($"/api/v1/patients/{b.PatientId}/prescriptions")).DataAsync(200)).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Receptionist_gets_no_clinical_content_through_any_direct_api_call()
    {
        var c = await factory.BookableClinicAsync();
        var visit = await c.Owner.Client.RecordVisitAsync(c, await c.Owner.Client.CheckedInAppointmentAsync(c), ClinicalScenarios.Clinical());
        var visitId = visit.GetProperty("id").GetGuid();
        var rxId = await c.Owner.Client.PrescriptionIdAsync(visitId);
        var (reception, _, _) = await factory.InviteAndAcceptAsync(c.Owner, "receptionist");

        foreach (var url in new[]
        {
            "/api/v1/visits", $"/api/v1/visits/{visitId}", $"/api/v1/patients/{c.PatientId}/visits", $"/api/v1/visits?patientId={c.PatientId}",
            $"/api/v1/prescriptions/{rxId}", $"/api/v1/patients/{c.PatientId}/prescriptions", $"/api/v1/patients/{c.PatientId}/attachments",
        })
            (await reception.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden, url);
        (await reception.PutAsJsonAsync($"/api/v1/visits/{visitId}", new { clinical = ClinicalScenarios.Clinical("x"), rowVersion = visit.GetProperty("rowVersion").GetString() }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reception.PostAsJsonAsync($"/api/v1/visits/{visitId}/complete", new { rowVersion = visit.GetProperty("rowVersion").GetString() }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reception.PutAsJsonAsync($"/api/v1/patients/{c.PatientId}/medical", new { allergies = "x", rowVersion = "AAAAAAAAAAA=" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The patient record itself is readable, without the medical block; the appointment carries no clinical content.
        var patient = await (await reception.GetAsync($"/api/v1/patients/{c.PatientId}")).DataAsync(200);
        patient.TryGetProperty("medical", out _).Should().BeFalse();
        var json = await (await reception.GetAsync($"/api/v1/appointments?patientId={c.PatientId}")).Content.ReadAsStringAsync();
        json.Should().NotContain("التهاب البلعوم الحاد").And.NotContain("Paracetamol");
    }

    // GD-01 (fixed): the clinical-authorship test moved to GateDFixPassTests (GD01_*).

    [Fact]
    public async Task CurrentBehaviour_legacy_prescription_without_visit_is_clinic_wide_but_still_own_doctor_scoped()
    {
        // GD-07: rows issued before D8 have no visit, so no branch: branch-restricted readers see them; the own-doctor
        // rule still hides them from other doctors. Only dev/test data can contain such rows today.
        var t = await factory.TwoBranchClinicAsync();
        var legacy = Guid.NewGuid();
        await factory.WithDbAsync(t.Owner.TenantId, async db =>
        {
            db.Prescriptions.Add(new Prescription
            {
                Id = legacy, TenantId = t.Owner.TenantId, Number = "RX-LEGACY", PatientId = t.PatientId, DoctorId = t.DoctorId,
                IssuedAtUtc = DateTime.UtcNow, Items = [new PrescriptionItem { TenantId = t.Owner.TenantId, LineNumber = 1, MedicationName = "Legacy" }],
            });
            await db.SaveChangesAsync();
        });
        var (nurseOtherBranch, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "nurse", [t.SecondBranchId]);
        (await nurseOtherBranch.GetAsync($"/api/v1/prescriptions/{legacy}")).StatusCode.Should().Be(HttpStatusCode.OK, "CURRENT behaviour: no branch to restrict on");

        var (otherDoctor, seat, _) = await factory.InviteAndAcceptAsync(t.Owner, "doctor");
        await t.OwnerClient.CreateDoctorAsync(seat, Scenarios.Week(t.MainBranchId));
        (await otherDoctor.GetAsync($"/api/v1/prescriptions/{legacy}")).StatusCode.Should().Be(HttpStatusCode.NotFound, "own-doctor rule still applies");
    }

    [Fact]
    public async Task Doctor_cannot_change_a_completed_visit_nor_reach_records_by_swapping_route_ids()
    {
        var c = await factory.BookableClinicAsync();
        var visit = await c.Owner.Client.RecordVisitAsync(c, await c.Owner.Client.CheckedInAppointmentAsync(c), ClinicalScenarios.Clinical());
        var id = visit.GetProperty("id").GetGuid();
        var done = await (await c.Owner.Client.PostAsJsonAsync($"/api/v1/visits/{id}/complete", new { rowVersion = visit.GetProperty("rowVersion").GetString() })).DataAsync(200);

        (await (await c.Owner.Client.PutAsJsonAsync($"/api/v1/visits/{id}", new { clinical = ClinicalScenarios.Clinical("changed"), rowVersion = done.GetProperty("rowVersion").GetString() }))
            .ErrorCodeAsync()).Should().Be("VISIT_COMPLETED");
        (await (await c.Owner.Client.PutAsJsonAsync($"/api/v1/visits/{id}", new { followUpNotes = "x", rowVersion = done.GetProperty("rowVersion").GetString() }))
            .ErrorCodeAsync()).Should().Be("VISIT_COMPLETED");
        (await (await c.Owner.Client.PostAsJsonAsync($"/api/v1/visits/{id}/complete", new { rowVersion = done.GetProperty("rowVersion").GetString() }))
            .ErrorCodeAsync()).Should().Be("INVALID_STATUS_TRANSITION");

        // Another tenant's doctor using this visit id in every route that takes one.
        var other = await factory.BookableClinicAsync();
        foreach (var res in new[]
        {
            await other.Owner.Client.GetAsync($"/api/v1/visits/{id}"),
            await other.Owner.Client.PutAsJsonAsync($"/api/v1/visits/{id}", new { followUpNotes = "x", rowVersion = done.GetProperty("rowVersion").GetString() }),
            await other.Owner.Client.PostAsJsonAsync($"/api/v1/visits/{id}/complete", new { rowVersion = done.GetProperty("rowVersion").GetString() }),
            await other.Owner.Client.IssuePrescriptionAsync(id),
            await other.Owner.Client.GetAsync($"/api/v1/visits?appointmentId={done.GetProperty("appointmentId").GetGuid()}"),
        })
            res.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.OK);
        (await (await other.Owner.Client.GetAsync($"/api/v1/visits?appointmentId={done.GetProperty("appointmentId").GetGuid()}")).DataAsync(200))
            .GetArrayLength().Should().Be(0);
        (await other.Owner.Client.GetAsync($"/api/v1/visits/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Forged_or_tampered_tokens_cannot_reach_another_clinic()
    {
        var a = await factory.RegisterClinicAsync();
        var b = await factory.RegisterClinicAsync();
        // Swap the tenant claim of A's token for B's (signature no longer matches).
        var parts = a.AccessToken.Split('.');
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Base64UrlDecode(parts[1]))!;
        var forged = payload.ToDictionary(k => k.Key, k => (object)k.Value);
        forged["tid"] = b.TenantId.ToString();
        var token = $"{parts[0]}.{Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(forged))}.{parts[2]}";
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await client.GetAsync("/api/v1/patients")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // alg=none
        var none = $"{Base64UrlEncode("{\"alg\":\"none\",\"typ\":\"JWT\"}"u8.ToArray())}.{parts[1]}.";
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", none);
        (await client.GetAsync("/api/v1/patients")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
