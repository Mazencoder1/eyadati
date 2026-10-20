using System.Net;
using System.Net.Http.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class OrganizationTests(ApiFactory factory)
{
    [Fact]
    public async Task Clinic_defaults_are_saudi_and_settings_validate_vat_and_concurrency()
    {
        var owner = await factory.RegisterClinicAsync();
        var settings = (await (await owner.Client.GetAsync("/api/v1/settings")).ReadJsonAsync()).GetProperty("data");
        settings.GetProperty("timeZone").GetString().Should().Be("Asia/Riyadh");
        settings.GetProperty("currency").GetString().Should().Be("SAR");
        settings.GetProperty("locale").GetString().Should().Be("ar");
        var version = settings.GetProperty("rowVersion").GetString();

        object Body(string vat, string? rv) => new
        {
            name = "عيادة الشفاء", vatNumber = vat, commercialRegistration = "1010123456", timeZone = "Asia/Riyadh",
            locale = "ar", defaultAppointmentMinutes = 20, rowVersion = rv,
        };

        var badVat = await owner.Client.PutAsJsonAsync("/api/v1/settings", Body("123", version));
        (await badVat.ReadJsonAsync()).GetProperty("errors").GetProperty("vatNumber")[0].GetString().Should().Contain("الرقم الضريبي");

        await (await owner.Client.PutAsJsonAsync("/api/v1/settings", Body("310123456700003", version))).EnsureStatusAsync(200);
        (await (await owner.Client.PutAsJsonAsync("/api/v1/settings", Body("310123456700003", version))).ErrorCodeAsync())
            .Should().Be("CONCURRENCY_CONFLICT");
    }

    [Fact]
    public async Task Settings_are_readable_by_members_but_only_managers_change_them()
    {
        var owner = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(owner.TenantId, "professional");
        var (reception, _, _) = await factory.InviteAndAcceptAsync(owner, "receptionist");

        (await reception.GetAsync("/api/v1/settings")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reception.PutAsJsonAsync("/api/v1/settings", new { name = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Extra_branches_need_a_plan_with_multiple_branches()
    {
        var owner = await factory.RegisterClinicAsync(); // free: 1 branch
        var blocked = await owner.Client.PostAsJsonAsync("/api/v1/branches", new { name = "فرع جدة", city = "جدة" });
        (await blocked.ErrorCodeAsync()).Should().Be("USAGE_LIMIT_REACHED");

        await factory.UsePlanAsync(owner.TenantId, "professional");
        await (await owner.Client.PostAsJsonAsync("/api/v1/branches", new { name = "فرع جدة", city = "جدة" })).EnsureStatusAsync(201);

        var main = await owner.MainBranchIdAsync();
        var rv = (await (await owner.Client.GetAsync("/api/v1/branches")).ReadJsonAsync()).GetProperty("data").EnumerateArray()
            .Single(b => b.GetProperty("id").GetGuid() == main).GetProperty("rowVersion").GetString();
        var deactivateMain = await owner.Client.PutAsJsonAsync($"/api/v1/branches/{main}", new { name = "الرئيسي", isActive = false, rowVersion = rv });
        (await deactivateMain.ErrorCodeAsync()).Should().Be("MAIN_BRANCH_PROTECTED");
    }

    [Fact]
    public async Task Doctor_schedule_cannot_overlap_and_doctor_count_follows_the_plan()
    {
        var owner = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(owner.TenantId, "free"); // free: 1 doctor
        var branch = await owner.MainBranchIdAsync();
        var overlapping = new[]
        {
            new { branchId = branch, dayOfWeek = "Sunday", startTime = "09:00:00", endTime = "13:00:00" },
            new { branchId = branch, dayOfWeek = "Sunday", startTime = "12:00:00", endTime = "16:00:00" },
        };
        var res = await owner.Client.PostAsJsonAsync("/api/v1/doctors", new
        {
            membershipId = await owner.OwnerMembershipIdAsync(), displayNameAr = "د. أحمد", specialty = "أسنان",
            consultationFee = 150m, defaultSlotMinutes = 20, acceptsOnlineBooking = true, schedule = overlapping,
        });
        (await res.ErrorCodeAsync()).Should().Be("SCHEDULE_OVERLAP");

        // Split shifts (morning + evening) are fine — common in Saudi clinics.
        var doctor = await owner.Client.CreateDoctorAsync(await owner.OwnerMembershipIdAsync(), new[]
        {
            new { branchId = branch, dayOfWeek = "Sunday", startTime = "09:00:00", endTime = "13:00:00" },
            new { branchId = branch, dayOfWeek = "Sunday", startTime = "16:00:00", endTime = "21:00:00" },
        });
        doctor.GetProperty("schedule").GetArrayLength().Should().Be(2);

        (await (await owner.Client.PostAsJsonAsync("/api/v1/doctors", new
        {
            membershipId = await owner.OwnerMembershipIdAsync(), displayNameAr = "x", specialty = "x", consultationFee = 0m, defaultSlotMinutes = 15, acceptsOnlineBooking = false,
        })).ErrorCodeAsync()).Should().Be("USAGE_LIMIT_REACHED");
    }

    [Fact]
    public async Task Services_catalog_crud_with_soft_delete()
    {
        var owner = await factory.RegisterClinicAsync();
        var created = await owner.Client.PostAsJsonAsync("/api/v1/services", new { nameAr = "تنظيف أسنان", durationMinutes = 30, price = 250m });
        await created.EnsureStatusAsync(201);
        var id = (await created.ReadJsonAsync()).GetProperty("data").GetProperty("id").GetGuid();

        (await owner.Client.PostAsJsonAsync("/api/v1/services", new { nameAr = "x", durationMinutes = 2, price = -1m })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await (await owner.Client.DeleteAsync($"/api/v1/services/{id}")).EnsureStatusAsync(204);
        var list = await (await owner.Client.GetAsync("/api/v1/services")).ReadJsonAsync();
        list.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Subscription_view_shows_plan_features_and_usage()
    {
        var owner = await factory.RegisterClinicAsync();
        await owner.Client.CreatePatientAsync();
        var sub = (await (await owner.Client.GetAsync("/api/v1/subscription")).ReadJsonAsync()).GetProperty("data");
        // D17 / Gate C: a new clinic starts on a 14-day trial of "basic".
        sub.GetProperty("planCode").GetString().Should().Be("basic");
        sub.GetProperty("status").GetString().Should().Be("Trialing");
        sub.GetProperty("trialEndsAtUtc").GetDateTime().Should().BeCloseTo(DateTime.UtcNow.AddDays(14), TimeSpan.FromMinutes(5));
        sub.GetProperty("hasFullAccess").GetBoolean().Should().BeTrue();
        // Only features that exist are shown.
        sub.GetProperty("features").EnumerateObject().Select(f => f.Name).Should().Equal("multi_branch");
        sub.GetProperty("features").GetProperty("multi_branch").GetBoolean().Should().BeFalse();
        var patients = sub.GetProperty("usage").EnumerateArray().Single(u => u.GetProperty("limitKey").GetString() == "max_patients");
        patients.GetProperty("used").GetInt32().Should().Be(1);
        patients.GetProperty("max").GetInt32().Should().Be(3000);
    }

    [Fact]
    public async Task Audit_log_is_available_on_every_plan_and_only_shows_own_clinic()
    {
        // D13: audit visibility is a core capability, gated by the AuditLogs.View permission only — even on "free".
        var owner = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(owner.TenantId, "free");
        await owner.Client.CreatePatientAsync();
        var other = await factory.RegisterClinicAsync();
        await other.Client.CreatePatientAsync();

        var logs = await (await owner.Client.GetAsync("/api/v1/audit-logs?action=patient.")).ReadJsonAsync();
        var items = logs.GetProperty("data").EnumerateArray().ToList();
        items.Should().ContainSingle(l => l.GetProperty("action").GetString() == "patient.created");
        items.Should().OnlyContain(l => l.GetProperty("userId").GetGuid() == owner.UserId);
        items[0].GetProperty("userName").GetString().Should().Be("د. مالك");
    }
}
