using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClinicSaaS.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real API against a throw-away SQL Server LocalDB database (real constraints, filtered indexes,
/// rowversion — no in-memory fakes), migrated on start and dropped on dispose.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _database = $"ClinicSaaS_Test_{Guid.NewGuid():N}";
    public const string SuperAdminEmail = "root@platform.test";
    public const string SuperAdminPassword = "Platform-Admin-Passw0rd!";
    /// <summary>The JWT key the test host signs with (test-only; production-like environments reject it).</summary>
    public const string JwtSigningKey = "integration-tests-only-signing-key-0123456789-abcdefghijklmnop";
    public CapturingEmailSender Emails { get; } = new();
    /// <summary>
    /// The authenticator secrets of the platform accounts this test run enrolled (by email), so later sign-ins can
    /// answer the MFA challenge. They come from the real enrolment flow; nothing is seeded into the database.
    /// </summary>
    public ConcurrentDictionary<string, byte[]> MfaSecrets { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Throw-away attachment storage for this test run (deleted on dispose).</summary>
    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), $"clinicsaas-test-files-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", TestConnectionString(_database));
        builder.UseSetting("Jwt:SigningKey", JwtSigningKey);
        builder.UseSetting("RateLimiting:AuthPerMinute", "100000");
        builder.UseSetting("RateLimiting:RefreshPerMinute", "100000");
        builder.UseSetting("RateLimiting:SensitivePer15Minutes", "100000");
        builder.UseSetting("RateLimiting:ApiPerMinute", "100000");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Bootstrap:SuperAdminEmail", SuperAdminEmail);
        builder.UseSetting("Bootstrap:SuperAdminPassword", SuperAdminPassword);
        builder.UseSetting("FileStorage:Local:Root", StorageRoot);
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IEmailSender>();
            s.AddSingleton<IEmailSender>(Emails);
        });
    }

    /// <summary>
    /// Connection string for a throw-away test database. LocalDB by default; CLINICSAAS_TEST_SQL_SERVER points the
    /// tests at another local SQL Server (e.g. a Linux container:
    /// "Server=localhost,1433;User ID=sa;Password=...;TrustServerCertificate=True").
    /// </summary>
    public static string TestConnectionString(string database)
    {
        var server = Environment.GetEnvironmentVariable("CLINICSAAS_TEST_SQL_SERVER") is { Length: > 0 } custom
            ? custom
            : "Server=(localdb)\\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True";
        return $"{server.TrimEnd(';')};Database={database}";
    }

    public async Task InitializeAsync()
    {
        ClientOptions.BaseAddress = new Uri("https://localhost");
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        using (var scope = Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureDeletedAsync();
        if (Directory.Exists(StorageRoot)) Directory.Delete(StorageRoot, recursive: true);
        await base.DisposeAsync();
    }

    /// <summary>Direct database access with an explicit tenant scope (or none), bypassing HTTP.</summary>
    public async Task WithDbAsync(Guid? tenantId, Func<AppDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        using var _ = tenantId is { } t ? tenant.UseTenant(t) : null;
        await action(db);
    }
}

public sealed class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        _sent.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <summary>The full link (href) in the latest email to <paramref name="to"/> that points at <paramref name="path"/>.</summary>
    public string LinkFor(string to, string path)
    {
        var mail = _sent.Last(m => m.To.Equals(to, StringComparison.OrdinalIgnoreCase) && m.HtmlBody.Contains(path));
        return System.Net.WebUtility.HtmlDecode(Regex.Match(mail.HtmlBody, @"href=""([^""]*" + Regex.Escape(path) + @"[^""]*)""").Groups[1].Value);
    }

    /// <summary>Extracts the one-time token from the latest email to <paramref name="to"/> containing <paramref name="path"/>.</summary>
    public string TokenFor(string to, string path)
    {
        var mail = _sent.Last(m => m.To.Equals(to, StringComparison.OrdinalIgnoreCase) && m.HtmlBody.Contains(path));
        return Uri.UnescapeDataString(Regex.Match(mail.HtmlBody, @"token=([^""&]+)").Groups[1].Value);
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

/// <summary>A registered clinic plus an authenticated client for its owner.</summary>
public sealed record ClinicSession(HttpClient Client, Guid TenantId, Guid UserId, string Email, string AccessToken);

public static class TestApi
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string Password = "Str0ngPassw0rd!";

    public static string UniqueEmail(string prefix = "owner") => $"{prefix}.{Guid.NewGuid():N}@clinic-test.sa";

    public static async Task<ClinicSession> RegisterClinicAsync(this ApiFactory factory, string? email = null)
    {
        email ??= UniqueEmail();
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            clinicName = "عيادة الاختبار",
            city = "الرياض",
            clinicPhone = "0501234567",
            ownerFullName = "د. مالك",
            email,
            password = Password,
            acceptTerms = true,
        });
        await res.EnsureStatusAsync(201);
        var body = await res.ReadJsonAsync();
        var data = body.GetProperty("data");
        var token = data.GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new ClinicSession(client, data.GetProperty("tenant").GetProperty("id").GetGuid(),
            data.GetProperty("user").GetProperty("id").GetGuid(), email, token);
    }

    public static async Task<HttpClient> LoginAsync(this ApiFactory factory, string email, string password = Password)
    {
        var client = factory.CreateClient();
        var data = await factory.LoginDataAsync(client, email, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.GetProperty("accessToken").GetString());
        return client;
    }

    /// <summary>
    /// Signs in the way a person does and returns the session (<c>data</c>). A platform account answers the MFA step
    /// through the real endpoints: it enrols on first use (the secret is remembered in <see cref="ApiFactory.MfaSecrets"/>)
    /// and answers the challenge with a current TOTP afterwards. Production behaviour is not relaxed anywhere.
    /// </summary>
    public static async Task<JsonElement> LoginDataAsync(this ApiFactory factory, HttpClient client, string email, string password = Password, Guid? tenantId = null)
    {
        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password, tenantId });
        if (res.StatusCode != System.Net.HttpStatusCode.Conflict)
        {
            await res.EnsureStatusAsync(200);
            return (await res.ReadJsonAsync()).GetProperty("data");
        }

        var error = await res.ReadJsonAsync();
        var code = error.GetProperty("code").GetString();
        var details = error.GetProperty("details");
        if (code == "MFA_REQUIRED")
        {
            await factory.ForgetLastTimeStepAsync(email); // the fixture signs in repeatedly inside one 30-second step
            var verify = await client.PostAsJsonAsync("/api/v1/auth/mfa/verify",
                new { challengeToken = details.GetProperty("challengeToken").GetString(), totpCode = CurrentCode(factory.MfaSecrets[email]) });
            await verify.EnsureStatusAsync(200);
            return (await verify.ReadJsonAsync()).GetProperty("data");
        }
        if (code == "MFA_ENROLLMENT_REQUIRED")
        {
            var start = await client.PostAsJsonAsync("/api/v1/auth/mfa/enroll/start", new { enrollmentToken = details.GetProperty("enrollmentToken").GetString() });
            await start.EnsureStatusAsync(200);
            var setup = (await start.ReadJsonAsync()).GetProperty("data");
            var secret = Application.Auth.Totp.FromBase32(setup.GetProperty("secret").GetString()!)!;
            var done = await client.PostAsJsonAsync("/api/v1/auth/mfa/enroll/complete",
                new { pendingToken = setup.GetProperty("pendingToken").GetString(), code = CurrentCode(secret) });
            await done.EnsureStatusAsync(200);
            factory.MfaSecrets[email] = secret;
            return (await done.ReadJsonAsync()).GetProperty("data").GetProperty("session");
        }
        throw new Xunit.Sdk.XunitException($"Login answered 409 {code}: {error}");
    }

    public static string CurrentCode(byte[] secret) => Application.Auth.Totp.CodeFor(secret, Application.Auth.Totp.StepOf(DateTime.UtcNow));

    /// <summary>Fixture-only: lets the same authenticator sign in again within the same 30-second step.</summary>
    public static Task ForgetLastTimeStepAsync(this ApiFactory factory, string email) =>
        factory.WithDbAsync(null, async db =>
        {
            var normalized = Domain.Identity.User.Normalize(email);
            await db.UserTotpFactors.Where(f => db.Users.Any(u => u.Id == f.UserId && u.NormalizedEmail == normalized))
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.LastUsedTimeStep, (long?)null));
        });

    /// <summary>Moves a tenant's current subscription to a seeded plan (e.g. "professional") for feature/limit tests.</summary>
    public static Task UsePlanAsync(this ApiFactory factory, Guid tenantId, string planCode) =>
        factory.WithDbAsync(tenantId, async db =>
        {
            var plan = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.Plans, p => p.Code == planCode);
            var sub = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.Subscriptions);
            sub.PlanId = plan.Id;
            await db.SaveChangesAsync();
        });

    public static object NewPatient(string? identityNumber = null, string mobile = "0551234567") => new
    {
        fullName = "محمد عبدالله",
        mobileNumber = mobile,
        gender = "Male",
        identityType = identityNumber is null ? "None" : "NationalId",
        identityNumber,
        dateOfBirth = "1990-05-01",
    };

    public static async Task<JsonElement> CreatePatientAsync(this HttpClient client, object? patient = null)
    {
        var res = await client.PostAsJsonAsync("/api/v1/patients", patient ?? NewPatient());
        await res.EnsureStatusAsync(201);
        return (await res.ReadJsonAsync()).GetProperty("data");
    }

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task EnsureStatusAsync(this HttpResponseMessage res, int expected)
    {
        if ((int)res.StatusCode != expected)
            throw new Xunit.Sdk.XunitException($"Expected {expected} but got {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
    }

    public static async Task<string> ErrorCodeAsync(this HttpResponseMessage res) =>
        (await res.ReadJsonAsync()).GetProperty("code").GetString()!;
}
