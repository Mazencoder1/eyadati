using System.Net;
using System.Net.Http.Json;
using ClinicSaaS.Domain.Platform;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class PatientRulesTests(ApiFactory factory)
{
    [Fact]
    public async Task Mobile_is_normalized_to_e164()
    {
        var a = await factory.RegisterClinicAsync();
        var p = await a.Client.CreatePatientAsync(TestApi.NewPatient(mobile: "٠٥٥ ٤٤٤ ٣٣٢٢"));
        p.GetProperty("mobileNumber").GetString().Should().Be("+966554443322");
    }

    [Fact]
    public async Task Medical_record_numbers_are_unique_and_gap_free_under_concurrency()
    {
        var a = await factory.RegisterClinicAsync();
        var tasks = Enumerable.Range(0, 12).Select(_ => a.Client.PostAsJsonAsync("/api/v1/patients", TestApi.NewPatient()));
        var responses = await Task.WhenAll(tasks);
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created);

        var mrns = new List<string>();
        foreach (var r in responses) mrns.Add((await r.ReadJsonAsync()).GetProperty("data").GetProperty("medicalRecordNumber").GetString()!);
        mrns.Order().Should().Equal(Enumerable.Range(1, 12).Select(i => $"P-{i:D6}"));
    }

    [Fact]
    public async Task Duplicate_national_id_is_rejected_but_shared_family_mobile_is_allowed()
    {
        var a = await factory.RegisterClinicAsync();
        await a.Client.CreatePatientAsync(TestApi.NewPatient(identityNumber: "1000000008", mobile: "0501112222"));

        var sameId = await a.Client.PostAsJsonAsync("/api/v1/patients", TestApi.NewPatient(identityNumber: "1000000008", mobile: "0503334444"));
        sameId.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await sameId.ErrorCodeAsync()).Should().Be("DUPLICATE_PATIENT_IDENTITY");

        await a.Client.CreatePatientAsync(TestApi.NewPatient(mobile: "0501112222")); // same phone, different person
        var dupes = await (await a.Client.GetAsync("/api/v1/patients/duplicates?mobile=0501112222")).ReadJsonAsync();
        dupes.GetProperty("data").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Invalid_national_id_check_digit_is_rejected()
    {
        var a = await factory.RegisterClinicAsync();
        var res = await a.Client.PostAsJsonAsync("/api/v1/patients", TestApi.NewPatient(identityNumber: "1000000001"));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.ReadJsonAsync()).GetProperty("errors").TryGetProperty("identityNumber", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_edits_are_detected_instead_of_silently_overwritten()
    {
        var a = await factory.RegisterClinicAsync();
        var p = await a.Client.CreatePatientAsync();
        var id = p.GetProperty("id").GetGuid();
        var version = p.GetProperty("rowVersion").GetString();

        object Edit(string name) => new { fullName = name, mobileNumber = "0551234567", gender = "Male", identityType = "None", rowVersion = version };

        await (await a.Client.PutAsJsonAsync($"/api/v1/patients/{id}", Edit("تعديل الموظف أ"))).EnsureStatusAsync(200);
        var stale = await a.Client.PutAsJsonAsync($"/api/v1/patients/{id}", Edit("تعديل الموظف ب"));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stale.ErrorCodeAsync()).Should().Be("CONCURRENCY_CONFLICT");

        var current = await (await a.Client.GetAsync($"/api/v1/patients/{id}")).ReadJsonAsync();
        current.GetProperty("data").GetProperty("fullName").GetString().Should().Be("تعديل الموظف أ");
    }

    [Fact]
    public async Task Soft_deleted_patients_disappear_but_remain_in_the_database()
    {
        var a = await factory.RegisterClinicAsync();
        var id = (await a.Client.CreatePatientAsync()).GetProperty("id").GetGuid();
        await (await a.Client.DeleteAsync($"/api/v1/patients/{id}")).EnsureStatusAsync(204);

        (await a.Client.GetAsync($"/api/v1/patients/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await factory.WithDbAsync(a.TenantId, async db =>
        {
            var row = await db.Patients.IgnoreQueryFilters(["SoftDelete"]).SingleAsync(x => x.Id == id);
            row.IsDeleted.Should().BeTrue();
            row.DeletedByUserId.Should().Be(a.UserId);
        });
    }

    [Fact]
    public async Task Plan_limit_blocks_new_patients_with_upgrade_code()
    {
        var a = await factory.RegisterClinicAsync();
        // Move this tenant to a tiny private plan (other tests keep the shared seed plans).
        await factory.WithDbAsync(a.TenantId, async db =>
        {
            var tiny = new Plan { Code = "tiny-" + Guid.NewGuid().ToString("N")[..8], NameAr = "صغيرة", NameEn = "Tiny", IsPublic = false };
            tiny.Limits.Add(new PlanLimit { LimitKey = LimitKeys.MaxPatients, MaxValue = 1 });
            db.Plans.Add(tiny);
            var sub = await db.Subscriptions.SingleAsync();
            sub.PlanId = tiny.Id;
            await db.SaveChangesAsync();
        });

        await a.Client.CreatePatientAsync();
        var res = await a.Client.PostAsJsonAsync("/api/v1/patients", TestApi.NewPatient());
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.ReadJsonAsync();
        body.GetProperty("code").GetString().Should().Be("USAGE_LIMIT_REACHED");
        body.GetProperty("details").GetProperty("limitKey").GetString().Should().Be(LimitKeys.MaxPatients);
        body.GetProperty("message").GetString().Should().Contain("ترقية");
    }

    [Fact]
    public async Task Paging_is_bounded_and_sort_is_whitelisted()
    {
        var a = await factory.RegisterClinicAsync();
        for (var i = 0; i < 3; i++) await a.Client.CreatePatientAsync();

        var page = await (await a.Client.GetAsync("/api/v1/patients?pageSize=100000&sort=PasswordHash")).ReadJsonAsync();
        page.GetProperty("meta").GetProperty("pageSize").GetInt32().Should().Be(100);
        page.GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(3);
    }
}

[Collection(ApiCollection.Name)]
public sealed class PlatformEndpointsTests(ApiFactory factory)
{
    [Fact]
    public async Task Public_plans_are_listed_from_the_database_and_advertise_only_what_exists()
    {
        var body = await (await factory.CreateClient().GetAsync("/api/v1/plans")).ReadJsonAsync();
        var plans = body.GetProperty("data").EnumerateArray().ToList();
        plans.Select(p => p.GetProperty("code").GetString()).Should().Equal("basic", "professional", "enterprise"); // "free" hidden by config
        foreach (var p in plans)
        {
            // Plan data still holds planned features (invoicing, SMS, reports…); the catalog shows only real ones.
            p.GetProperty("features").EnumerateArray().Select(f => f.GetString()).Should().BeSubsetOf(["multi_branch"]);
            p.GetProperty("limits").EnumerateObject().Select(l => l.Name).Should()
                .BeSubsetOf(["max_users", "max_doctors", "max_patients", "max_appointments_month", "max_branches"]);
        }
        plans.Single(p => p.GetProperty("code").GetString() == "professional").GetProperty("features").EnumerateArray()
            .Select(f => f.GetString()).Should().Equal("multi_branch");
    }

    [Fact]
    public async Task Health_endpoints_respond()
    {
        var client = factory.CreateClient();
        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
