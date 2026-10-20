using Microsoft.AspNetCore.Hosting;

namespace ClinicSaaS.IntegrationTests.Infrastructure;

/// <summary>
/// A configuration that passes every production-like rule (fake values, local to the tests). Tests that check ONE
/// production boundary — the email provider, attachment storage — start from this so the configuration guard
/// doesn't fire first, and then break exactly the thing they are about.
/// </summary>
public static class ProductionSettings
{
    public static Dictionary<string, string?> Valid() => new()
    {
        ["Jwt:SigningKey"] = "k9Vx2mQpLw7Zr4TbNc8Yh1Ud5Gs3Jf6Aq0EoXiWvBzKtRnMyPlCjHaDgSeUf",
        ["Jwt:Issuer"] = "clinic-saas",
        ["Jwt:Audience"] = "clinic-saas-web",
        ["ConnectionStrings:Default"] = "Server=sql.internal.invalid-for-tests,1433;Database=ClinicSaaS;User ID=clinic_app;Password=Xy9!aBcDeFgH;Encrypt=True",
        ["Cors:AllowedOrigins:0"] = "https://app.example.sa",
        ["Auth:AppBaseUrl"] = "https://app.example.sa",
        ["AllowedHosts"] = "api.example.sa",
        ["Email:Provider"] = "SomeRealProvider",
        ["ForwardedHeaders:KnownProxies:0"] = "10.0.0.5",
        // The MFA key ring: a persisted directory plus a certificate held outside the database. Throw-away, test-only.
        ["DataProtection:KeyRingPath"] = KeyRing.Value.Directory,
        ["DataProtection:CertificatePath"] = KeyRing.Value.CertificatePath,
        ["DataProtection:CertificatePassword"] = KeyRing.Value.CertificatePassword,
    };

    /// <summary>A throw-away key-ring directory and self-signed certificate, created once per test run in the temp folder.</summary>
    public sealed record KeyRingFiles(string Directory, string CertificatePath, string CertificatePassword);

    public static readonly Lazy<KeyRingFiles> KeyRing = new(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), $"clinicsaas-test-keyring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var password = Guid.NewGuid().ToString("N");
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=clinicsaas-tests-only", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var pfx = Path.Combine(root, "keyring-protection.pfx");
        File.WriteAllBytes(pfx, cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, password));
        return new KeyRingFiles(Path.Combine(root, "keys"), pfx, password);
    });

    /// <summary>
    /// Applies <see cref="Valid"/> plus the defaults a shared test host overrides (test-grade rate limits, auto-migrate),
    /// then <paramref name="changes"/>.
    /// </summary>
    public static void Apply(IWebHostBuilder builder, params (string Key, string? Value)[] changes)
    {
        var values = Valid();
        values["Database:MigrateOnStartup"] = "false";
        values["RateLimiting:AuthPerMinute"] = "10";
        values["RateLimiting:RefreshPerMinute"] = "120";
        values["RateLimiting:SensitivePer15Minutes"] = "5";
        values["RateLimiting:ApiPerMinute"] = "300";
        foreach (var (key, value) in changes) values[key] = value;
        foreach (var (key, value) in values) builder.UseSetting(key, value);
    }
}
