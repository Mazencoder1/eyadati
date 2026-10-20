using System.Net.Http.Json;
using System.Text.Json;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicSaaS.IntegrationTests.Qa;

/// <summary>
/// A regression test for a CONFIRMED defect (see docs/security/T-002-audit.md). It asserts the required behaviour,
/// so it fails until the owning agent fixes the defect. It is skipped in the default run (so verify.ps1 stays a
/// usable merge gate) and executed when <c>CLINIC_RUN_KNOWN_DEFECTS=1</c> is set:
/// <code>$env:CLINIC_RUN_KNOWN_DEFECTS='1'; dotnet test --filter "Status=KnownDefect"</code>
/// When the defect is fixed, replace this attribute with <c>[Fact]</c> — never delete the test.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class KnownDefectFactAttribute : FactAttribute
{
    public const string EnvVar = "CLINIC_RUN_KNOWN_DEFECTS";

    public KnownDefectFactAttribute(string findingId)
    {
        FindingId = findingId;
        if (Environment.GetEnvironmentVariable(EnvVar) != "1")
            Skip = $"KnownDefect {findingId} (docs/security/T-002-audit.md): fails until fixed. Set {EnvVar}=1 to run.";
    }

    public string FindingId { get; }
}

internal static class QaScenarios
{
    /// <summary>A clinic on the professional plan with two branches. The owner is a doctor working
    /// 08:00–14:00 at the main branch and 15:00–22:00 at the second branch, every day; one patient.</summary>
    public sealed record TwoBranchClinic(Scenarios.BookableClinic C, Guid MainBranchId, Guid SecondBranchId)
    {
        public ClinicSession Owner => C.Owner;
        public HttpClient OwnerClient => C.Owner.Client;
        public Guid DoctorId => C.DoctorId;
        public Guid PatientId => C.PatientId;
    }

    private static readonly string[] AllDays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    public static async Task<TwoBranchClinic> TwoBranchClinicAsync(this ApiFactory factory)
    {
        var c = await factory.BookableClinicAsync();
        var res = await c.Owner.Client.PostAsJsonAsync("/api/v1/branches", new { name = "فرع الشمال", city = "الرياض" });
        await res.EnsureStatusAsync(201);
        var second = (await res.ReadJsonAsync()).GetProperty("data").GetProperty("id").GetGuid();

        var windows = AllDays.Select(d => new { branchId = c.BranchId, dayOfWeek = d, startTime = "08:00:00", endTime = "14:00:00" })
            .Concat(AllDays.Select(d => new { branchId = second, dayOfWeek = d, startTime = "15:00:00", endTime = "22:00:00" }))
            .ToArray();
        var rowVersion = await c.Owner.Client.RowVersionAsync($"/api/v1/doctors/{c.DoctorId}");
        await (await c.Owner.Client.PutAsJsonAsync($"/api/v1/doctors/{c.DoctorId}/schedule", new { windows, rowVersion })).EnsureStatusAsync(200);
        return new TwoBranchClinic(c, c.BranchId, second);
    }

    public static Task<HttpResponseMessage> BookAtAsync(this HttpClient client, TwoBranchClinic t, Guid branchId, DateTime startUtc, string source = "Reception") =>
        client.PostAsJsonAsync("/api/v1/appointments", new
        {
            patientId = t.PatientId, doctorId = t.DoctorId, branchId, startUtc = source == "WalkIn" ? (DateTime?)null : startUtc,
            durationMinutes = 15, source,
        });

    public static async Task<JsonElement> DataAsync(this HttpResponseMessage res, int expectedStatus)
    {
        await res.EnsureStatusAsync(expectedStatus);
        return (await res.ReadJsonAsync()).GetProperty("data");
    }

    /// <summary>Invites a new person with explicit role ids (custom roles included), accepts and signs in.</summary>
    public static async Task<(HttpClient Client, Guid MembershipId, string Email)> InviteWithRolesAsync(
        this ApiFactory factory, HttpClient inviter, Guid[] roleIds, Guid[]? branchIds = null, string prefix = "member")
    {
        var email = TestApi.UniqueEmail(prefix);
        var res = await inviter.PostAsJsonAsync("/api/v1/staff", new { fullName = "موظف " + prefix, email, roleIds, branchIds });
        var membershipId = (await res.DataAsync(201)).GetProperty("membershipId").GetGuid();
        var token = factory.Emails.TokenFor(email, "/accept-invitation");
        await (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token, newPassword = TestApi.Password }))
            .EnsureStatusAsync(204);
        return (await factory.LoginAsync(email), membershipId, email);
    }

    public static async Task<Guid> CreateRoleAsync(this HttpClient client, string name, params string[] permissions) =>
        (await (await client.PostAsJsonAsync("/api/v1/roles", new { name, permissions })).DataAsync(201)).GetProperty("id").GetGuid();

    /// <summary>A platform user created directly in the database (the bootstrap super admin is shared by other tests).</summary>
    public static async Task<string> CreatePlatformUserAsync(this ApiFactory factory, PlatformRole role)
    {
        var email = TestApi.UniqueEmail("platform");
        await factory.WithDbAsync(null, async db =>
        {
            var hasher = factory.Services.GetRequiredService<IPasswordHasher>();
            var user = new User { Email = email, NormalizedEmail = User.Normalize(email), FullName = "Platform QA", EmailConfirmed = true, PlatformRole = role };
            user.PasswordHash = hasher.Hash(user, TestApi.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        });
        return email;
    }
}
