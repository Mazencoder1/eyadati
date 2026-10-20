using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ClinicSaaS.IntegrationTests.Infrastructure;

/// <summary>Higher-level setups: a clinic with a doctor and working hours, invited staff, Riyadh-local times.</summary>
public static class Scenarios
{
    public static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
    private static readonly string[] AllDays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    public sealed record BookableClinic(ClinicSession Owner, Guid BranchId, Guid DoctorId, Guid PatientId);

    /// <summary>Clinic-local calendar date <paramref name="daysAhead"/> days from today.</summary>
    public static DateOnly LocalDate(int daysAhead) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Riyadh)).AddDays(daysAhead);

    /// <summary>ISO yyyy-MM-dd in the Gregorian calendar, whatever the machine culture (ar-SA would print Hijri).</summary>
    public static string LocalDateIso(int daysAhead) => LocalDate(daysAhead).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>UTC instant of a Riyadh wall-clock time <paramref name="daysAhead"/> days from today.</summary>
    public static DateTime RiyadhUtc(int daysAhead, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(LocalDate(daysAhead).ToDateTime(new TimeOnly(hour, minute)), Riyadh);

    public static async Task<Guid> MainBranchIdAsync(this ClinicSession s)
    {
        var body = await (await s.Client.GetAsync("/api/v1/branches")).ReadJsonAsync();
        return body.GetProperty("data").EnumerateArray().Single(b => b.GetProperty("isMain").GetBoolean()).GetProperty("id").GetGuid();
    }

    public static async Task<Guid> OwnerMembershipIdAsync(this ClinicSession s)
    {
        var body = await (await s.Client.GetAsync("/api/v1/staff")).ReadJsonAsync();
        return body.GetProperty("data").EnumerateArray().Single(m => m.GetProperty("isOwner").GetBoolean()).GetProperty("membershipId").GetGuid();
    }

    public static async Task<Guid> RoleIdAsync(this HttpClient client, string systemKey)
    {
        var body = await (await client.GetAsync("/api/v1/roles")).ReadJsonAsync();
        return body.GetProperty("data").EnumerateArray()
            .Single(r => r.TryGetProperty("systemKey", out var k) && k.GetString() == systemKey).GetProperty("id").GetGuid();
    }

    public static object Week(Guid branchId, string from = "08:00:00", string to = "22:00:00") =>
        AllDays.Select(d => new { branchId, dayOfWeek = d, startTime = from, endTime = to }).ToArray();

    public static async Task<JsonElement> CreateDoctorAsync(this HttpClient client, Guid membershipId, object? schedule, int slotMinutes = 15)
    {
        var res = await client.PostAsJsonAsync("/api/v1/doctors", new
        {
            membershipId, displayNameAr = "د. سارة القحطاني", specialty = "طب الأسرة", consultationFee = 200m,
            defaultSlotMinutes = slotMinutes, acceptsOnlineBooking = true, schedule,
        });
        await res.EnsureStatusAsync(201);
        return (await res.ReadJsonAsync()).GetProperty("data");
    }

    /// <summary>A clinic on the given plan whose owner is also the doctor, working 08:00–22:00 every day, with one patient.</summary>
    public static async Task<BookableClinic> BookableClinicAsync(this ApiFactory factory, string plan = "professional")
    {
        var owner = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(owner.TenantId, plan);
        var branch = await owner.MainBranchIdAsync();
        var doctor = await owner.Client.CreateDoctorAsync(await owner.OwnerMembershipIdAsync(), Week(branch));
        var patient = await owner.Client.CreatePatientAsync();
        return new BookableClinic(owner, branch, doctor.GetProperty("id").GetGuid(), patient.GetProperty("id").GetGuid());
    }

    public static Task<HttpResponseMessage> BookAsync(this HttpClient client, BookableClinic c, DateTime startUtc, int? minutes = 15, string source = "Reception") =>
        client.PostAsJsonAsync("/api/v1/appointments", new
        {
            patientId = c.PatientId, doctorId = c.DoctorId, branchId = c.BranchId, startUtc, durationMinutes = minutes, source,
        });

    /// <summary>Invites a new person with a system role, accepts the emailed invitation and returns a signed-in client.</summary>
    public static async Task<(HttpClient Client, Guid MembershipId, string Email)> InviteAndAcceptAsync(
        this ApiFactory factory, ClinicSession owner, string roleKey, Guid[]? branchIds = null)
    {
        var email = TestApi.UniqueEmail(roleKey);
        var res = await owner.Client.PostAsJsonAsync("/api/v1/staff", new
        {
            fullName = "موظف " + roleKey, email, roleIds = new[] { await owner.Client.RoleIdAsync(roleKey) }, branchIds,
        });
        await res.EnsureStatusAsync(201);
        var membershipId = (await res.ReadJsonAsync()).GetProperty("data").GetProperty("membershipId").GetGuid();

        var token = factory.Emails.TokenFor(email, "/accept-invitation");
        await (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token, newPassword = TestApi.Password }))
            .EnsureStatusAsync(204);
        return (await factory.LoginAsync(email), membershipId, email);
    }

    /// <summary>
    /// Gives an EXISTING account (with a password) a seat in the inviter's clinic: invites it, then the account owner
    /// signs in and accepts the emailed invitation (T-010). Returns the membership id. The account must be able to
    /// sign in without naming a clinic (one membership, or a platform user).
    /// </summary>
    public static async Task<Guid> AddExistingAccountAsync(this ApiFactory factory, HttpClient inviter, string email, Guid roleId, Guid[]? branchIds = null)
    {
        var res = await inviter.PostAsJsonAsync("/api/v1/staff", new { fullName = "موظف", email, roleIds = new[] { roleId }, branchIds });
        await res.EnsureStatusAsync(201);
        var membershipId = (await res.ReadJsonAsync()).GetProperty("data").GetProperty("membershipId").GetGuid();

        var token = factory.Emails.TokenFor(email, "/accept-invitation");
        var account = await factory.LoginAsync(email);
        await (await account.PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token })).EnsureStatusAsync(204);
        return membershipId;
    }

    public static HttpClient WithToken(this ApiFactory factory, string accessToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>A sign-in as a native app (refresh token in the body): the bearer client plus both tokens.</summary>
    public sealed record NativeSession(HttpClient Client, string AccessToken, string RefreshToken, JsonElement Data);

    public static async Task<NativeSession> NativeSignInAsync(
        this ApiFactory factory, string email, string password = TestApi.Password, Guid? tenantId = null)
    {
        var native = factory.CreateClient();
        native.DefaultRequestHeaders.Add("X-Client-Type", "mobile");
        var data = await factory.LoginDataAsync(native, email, password, tenantId); // answers MFA for platform accounts
        var access = data.GetProperty("accessToken").GetString()!;
        // The same client keeps announcing itself as native, so body refresh tokens are accepted on later calls.
        native.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return new NativeSession(native, access, data.GetProperty("refreshToken").GetString()!, data);
    }

    /// <summary>The session id (the access token's <c>sid</c>) of the signed-in client, as GET /auth/sessions reports it.</summary>
    public static async Task<Guid> CurrentSessionIdAsync(this HttpClient client)
    {
        var body = await (await client.GetAsync("/api/v1/auth/sessions")).ReadJsonAsync();
        return body.GetProperty("data").EnumerateArray().Single(s => s.GetProperty("isCurrent").GetBoolean()).GetProperty("sessionId").GetGuid();
    }

    public static async Task<string> RowVersionAsync(this HttpClient client, string url) =>
        (await (await client.GetAsync(url)).ReadJsonAsync()).GetProperty("data").GetProperty("rowVersion").GetString()!;
}
