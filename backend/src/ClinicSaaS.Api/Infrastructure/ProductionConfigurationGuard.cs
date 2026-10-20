using System.Net;
using ClinicSaaS.Infrastructure.Notifications;
using Microsoft.Data.SqlClient;

namespace ClinicSaaS.Api.Infrastructure;

/// <summary>
/// Fail-closed checks for production-like environments (everything except Development and the automated test host).
/// Returns EVERY problem at once, by configuration key — a value is never echoed, so a rejected secret cannot leak into
/// the startup log. Nothing here chooses a provider or invents a value: a missing setting is a problem, full stop.
/// The checks are deliberately about "obviously a development/test value", not about proving a secret is strong.
/// </summary>
public static class ProductionConfigurationGuard
{
    private static readonly string[] PlaceholderMarkers =
    [
        "changeme", "change-me", "change_me", "replace-me", "replace_me", "placeholder", "example", "your-", "your_",
        "dev-only", "test-only", "tests-only", "for-tests", "do-not-use", "not-a-real", "insecure", "signing-key-here",
    ];

    public static IReadOnlyList<string> Validate(IConfiguration config)
    {
        var problems = new List<string>();
        Jwt(config, problems);
        Database(config, problems);
        Web(config, problems);
        Email(config, problems);
        Limits(config, problems);
        Proxy(config, problems);
        Logging(config, problems);
        MfaKeyRing(config, problems);
        if (config.GetValue("Database:MigrateOnStartup", false))
            problems.Add("Database:MigrateOnStartup: must be false — production applies the reviewed migration scripts.");
        return problems;
    }

    /// <summary>Throws one exception listing every problem, or returns when the configuration is acceptable.</summary>
    public static void EnforceOrThrow(IConfiguration config, string environmentName)
    {
        var problems = Validate(config);
        if (problems.Count == 0) return;
        throw new InvalidOperationException(
            $"Unsafe configuration for environment '{environmentName}' — the API refuses to start:{Environment.NewLine}"
            + string.Join(Environment.NewLine, problems.Select(p => " - " + p)));
    }

    // ---------------------------------------------------------------- keys

    private static void Jwt(IConfiguration c, List<string> problems)
    {
        CheckKey("Jwt:SigningKey", c["Jwt:SigningKey"], required: true, problems);
        var previous = c.GetSection("Jwt:PreviousSigningKeys").Get<string[]>() ?? [];
        for (var i = 0; i < previous.Length; i++) CheckKey($"Jwt:PreviousSigningKeys:{i}", previous[i], required: false, problems);

        var minutes = c.GetValue("Jwt:AccessTokenMinutes", 15);
        if (minutes is < 1 or > 60) problems.Add("Jwt:AccessTokenMinutes: must be between 1 and 60.");
        if (string.IsNullOrWhiteSpace(c["Jwt:Issuer"])) problems.Add("Jwt:Issuer: must not be empty.");
        if (string.IsNullOrWhiteSpace(c["Jwt:Audience"])) problems.Add("Jwt:Audience: must not be empty.");
    }

    private static void CheckKey(string name, string? key, bool required, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            if (required) problems.Add($"{name}: is not set — supply it from the secret store.");
            return;
        }
        if (System.Text.Encoding.UTF8.GetByteCount(key) < 32) problems.Add($"{name}: must be at least 32 bytes.");
        if (PlaceholderMarkers.Any(m => key.Contains(m, StringComparison.OrdinalIgnoreCase)))
            problems.Add($"{name}: looks like a placeholder or a development/test key — generate a random key.");
        if (key.Distinct().Count() < 12)
            problems.Add($"{name}: has too little variety to be a random key.");
    }

    // ---------------------------------------------------------------- database

    private static void Database(IConfiguration c, List<string> problems)
    {
        var raw = c.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(raw))
        {
            problems.Add("ConnectionStrings:Default: is not set — supply it from the secret store.");
            return;
        }
        SqlConnectionStringBuilder b;
        try { b = new SqlConnectionStringBuilder(raw); }
        catch (ArgumentException) { problems.Add("ConnectionStrings:Default: is not a valid SQL Server connection string."); return; }

        if (string.IsNullOrWhiteSpace(b.DataSource) || b.DataSource.Contains("(localdb)", StringComparison.OrdinalIgnoreCase))
            problems.Add("ConnectionStrings:Default: must name a real database server (not LocalDB).");
        if (b.Encrypt == SqlConnectionEncryptOption.Optional)
            problems.Add("ConnectionStrings:Default: must encrypt the connection (Encrypt=True or Strict).");
        if (b.TrustServerCertificate)
            problems.Add("ConnectionStrings:Default: must not use TrustServerCertificate=True — install a trusted server certificate.");
        if (string.Equals(b.UserID, "sa", StringComparison.OrdinalIgnoreCase))
            problems.Add("ConnectionStrings:Default: must not use the 'sa' login — use the least-privilege application login.");
        if (!b.IntegratedSecurity && (string.IsNullOrEmpty(b.UserID) || string.IsNullOrEmpty(b.Password)))
            problems.Add("ConnectionStrings:Default: SQL authentication needs both a user and a password.");
        var db = b.InitialCatalog;
        if (string.IsNullOrWhiteSpace(db)
            || db.Equals("master", StringComparison.OrdinalIgnoreCase)
            || db.StartsWith("ClinicSaaS_Dev", StringComparison.OrdinalIgnoreCase)
            || db.StartsWith("ClinicSaaS_Test", StringComparison.OrdinalIgnoreCase)
            || db.StartsWith("ClinicSaaS_E2E", StringComparison.OrdinalIgnoreCase))
            problems.Add("ConnectionStrings:Default: the database name must be set and must not be master or a development/test database.");
    }

    // ---------------------------------------------------------------- web

    private static void Web(IConfiguration c, List<string> problems)
    {
        var origins = c.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        if (origins.Length == 0) problems.Add("Cors:AllowedOrigins: must list the web app's origin (https://app.<domain>).");
        var normalized = new List<string>();
        for (var i = 0; i < origins.Length; i++)
        {
            if (!TryHttpsOrigin(origins[i], out var origin))
                problems.Add($"Cors:AllowedOrigins:{i}: must be an https origin without path, such as https://app.example.sa (no wildcard, no localhost).");
            else
                normalized.Add(origin);
        }

        var baseUrl = c["Auth:AppBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            problems.Add("Auth:AppBaseUrl: must be the public https URL of the web app (links in emails are built from it).");
        else if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || IsLocal(uri.Host))
            problems.Add("Auth:AppBaseUrl: must be an https URL that is not localhost.");
        else if (normalized.Count > 0 && !normalized.Contains(uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant()))
            problems.Add("Auth:AppBaseUrl: its origin must be one of Cors:AllowedOrigins (the web app that receives the links).");

        var hosts = c["AllowedHosts"];
        if (string.IsNullOrWhiteSpace(hosts) || hosts.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(h => h == "*"))
            problems.Add("AllowedHosts: must list the API host name(s) (not empty and not '*').");
    }

    private static bool TryHttpsOrigin(string? raw, out string origin)
    {
        origin = "";
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains('*')) return false;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        if (IsLocal(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        if (uri.AbsolutePath != "/") return false;
        origin = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        return true;
    }

    private static bool IsLocal(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)));

    // ---------------------------------------------------------------- email

    private static void Email(IConfiguration c, List<string> problems)
    {
        var provider = c[$"{EmailOptions.Section}:Provider"];
        if (string.IsNullOrWhiteSpace(provider))
            problems.Add("Email:Provider: No email provider is configured. Invitations, email verification and password reset cannot work without one.");
        else if (provider.Equals(EmailOptions.DevMailboxProvider, StringComparison.OrdinalIgnoreCase))
            problems.Add("Email:Provider: The DevMailbox email provider writes one-time links to disk and is only allowed in Development.");
    }

    // ---------------------------------------------------------------- MFA key ring

    /// <summary>
    /// The Data Protection key ring encrypts the platform admins' TOTP secrets. It must be persisted outside the
    /// database and protected by a key that is not in the database either; without it the API refuses to start rather
    /// than keep secrets under an ephemeral or default key. Values are never echoed.
    /// </summary>
    private static void MfaKeyRing(IConfiguration c, List<string> problems)
    {
        var path = c[$"{MfaDataProtection.Section}:KeyRingPath"];
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            problems.Add($"{MfaDataProtection.Section}:KeyRingPath: must be an absolute path to a persisted key ring outside the database (it encrypts the MFA secrets).");
        if (string.IsNullOrWhiteSpace(c[$"{MfaDataProtection.Section}:CertificatePath"]) && string.IsNullOrWhiteSpace(c[$"{MfaDataProtection.Section}:CertificateThumbprint"]))
            problems.Add($"{MfaDataProtection.Section}:CertificatePath (or CertificateThumbprint): the key ring must be protected by a certificate held outside the database — supply it from the secret store.");
        if (c.GetValue("Auth:MfaLockoutMinutes", 60) < 15)
            problems.Add("Auth:MfaLockoutMinutes: must be at least 15.");
    }

    // ---------------------------------------------------------------- limits

    private static void Limits(IConfiguration c, List<string> problems)
    {
        // Test-grade values (the browser suite raises these to 100000) must not reach production. The ceilings are ten
        // times the defaults: generous for real use, far below "unlimited".
        CheckRange(c, "RateLimiting:AuthPerMinute", 10, problems);
        CheckRange(c, "RateLimiting:RefreshPerMinute", 120, problems);
        CheckRange(c, "RateLimiting:SensitivePer15Minutes", 5, problems);
        CheckRange(c, "RateLimiting:ApiPerMinute", 300, problems);

        var lockout = c.GetValue("Auth:MaxFailedAccessAttempts", 5);
        if (lockout is < 3 or > 10) problems.Add("Auth:MaxFailedAccessAttempts: must be between 3 and 10.");
        if (c.GetValue("Auth:LockoutMinutes", 15) < 5) problems.Add("Auth:LockoutMinutes: must be at least 5.");
        if (c.GetValue("Auth:RefreshTokenDays", 14) is < 1 or > 90) problems.Add("Auth:RefreshTokenDays: must be between 1 and 90.");

        // The limiter lives in process memory: N API instances give N times the budget. Running more than one instance
        // needs a shared store (not built yet) or an explicit, recorded acceptance of per-instance limits.
        var instances = c.GetValue("RateLimiting:ApiInstances", 1);
        if (instances < 1) problems.Add("RateLimiting:ApiInstances: must be at least 1.");
        else if (instances > 1 && !c.GetValue("RateLimiting:AcceptPerInstanceLimits", false))
            problems.Add("RateLimiting:ApiInstances: more than one API instance needs a shared rate-limit store (not implemented); "
                         + "set RateLimiting:AcceptPerInstanceLimits=true only as a recorded decision to accept per-instance limits.");
    }

    private static void CheckRange(IConfiguration c, string key, int defaultValue, List<string> problems)
    {
        var value = c.GetValue(key, defaultValue);
        if (value < 1 || value > defaultValue * 10) problems.Add($"{key}: must be between 1 and {defaultValue * 10} (higher values are test-grade).");
    }

    // ---------------------------------------------------------------- proxy

    private static void Proxy(IConfiguration c, List<string> problems)
    {
        var settings = ForwardedHeadersSettings.Read(c, problems);
        if (settings.NoProxy && settings.HasTrustList)
            problems.Add("ForwardedHeaders: NoProxy=true cannot be combined with KnownProxies/KnownNetworks.");
        else if (!settings.NoProxy && !settings.HasTrustList)
            problems.Add("ForwardedHeaders: configure the trusted reverse proxy/load balancer (KnownProxies and/or KnownNetworks), "
                         + "or set NoProxy=true if the API is reached directly. Without it every client shares the proxy's address.");
    }

    // ---------------------------------------------------------------- logging

    private static void Logging(IConfiguration c, List<string> problems)
    {
        var level = c["Serilog:MinimumLevel:Default"];
        if (level is not null && (level.Equals("Verbose", StringComparison.OrdinalIgnoreCase) || level.Equals("Debug", StringComparison.OrdinalIgnoreCase)))
            problems.Add("Serilog:MinimumLevel:Default: Debug/Verbose logging is not allowed in production.");

        // The framework's own request logging prints the full URL including the query string (patient searches carry a
        // mobile or national-ID number there). Keep every Microsoft.*/System.* category at Warning or above.
        foreach (var over in c.GetSection("Serilog:MinimumLevel:Override").GetChildren())
        {
            var isFramework = over.Key.StartsWith("Microsoft", StringComparison.Ordinal) || over.Key.StartsWith("System", StringComparison.Ordinal);
            if (isFramework && over.Value is { } v
                && (v.Equals("Verbose", StringComparison.OrdinalIgnoreCase) || v.Equals("Debug", StringComparison.OrdinalIgnoreCase) || v.Equals("Information", StringComparison.OrdinalIgnoreCase)))
                problems.Add($"Serilog:MinimumLevel:Override:{over.Key}: framework categories must stay at Warning or above (they log full request URLs).");
        }
    }
}
