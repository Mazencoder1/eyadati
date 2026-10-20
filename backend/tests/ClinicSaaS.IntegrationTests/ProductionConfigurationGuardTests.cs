using ClinicSaaS.Api.Infrastructure;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// Phase 3B: a production-like process (Production, Staging, any custom name) refuses to start on configuration that is
/// obviously a development/test value. These tests need no database. Values are fake and local to the tests.
/// </summary>
public sealed class ProductionConfigurationGuardTests
{
    /// <summary>A configuration that passes every rule. Each test below breaks exactly one thing.</summary>
    private static Dictionary<string, string?> Valid() => ProductionSettings.Valid();

    private static IReadOnlyList<string> Problems(Action<Dictionary<string, string?>>? change = null)
    {
        var values = Valid();
        change?.Invoke(values);
        return ProductionConfigurationGuard.Validate(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    [Fact]
    public void A_complete_configuration_is_accepted()
    {
        Problems().Should().BeEmpty();
    }

    public static TheoryData<string, string, string?> Unsafe => new()
    {
        // signing key
        { "no signing key", "Jwt:SigningKey", null },
        { "short signing key", "Jwt:SigningKey", "too-short" },
        { "placeholder signing key", "Jwt:SigningKey", "ChangeMe-ChangeMe-ChangeMe-ChangeMe-1234567890" },
        { "the test host's key", "Jwt:SigningKey", "integration-tests-only-signing-key-0123456789-abcdefghijklmnop" },
        { "no-variety signing key", "Jwt:SigningKey", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
        { "dev previous key", "Jwt:PreviousSigningKeys:0", "dev-only-old-signing-key-0123456789-abcdefghij" },
        { "long access tokens", "Jwt:AccessTokenMinutes", "1440" },
        // database
        { "no connection string", "ConnectionStrings:Default", null },
        { "garbage connection string", "ConnectionStrings:Default", "this is not a connection string" },
        { "LocalDB", "ConnectionStrings:Default", "Server=(localdb)\\MSSQLLocalDB;Database=ClinicSaaS;Trusted_Connection=True" },
        { "unencrypted", "ConnectionStrings:Default", "Server=db.internal;Database=ClinicSaaS;User ID=clinic_app;Password=Xy9!aBcDeFgH;Encrypt=False" },
        { "trust server certificate", "ConnectionStrings:Default", "Server=db.internal;Database=ClinicSaaS;User ID=clinic_app;Password=Xy9!aBcDeFgH;Encrypt=True;TrustServerCertificate=True" },
        { "sa login", "ConnectionStrings:Default", "Server=db.internal;Database=ClinicSaaS;User ID=sa;Password=Xy9!aBcDeFgH;Encrypt=True" },
        { "no password", "ConnectionStrings:Default", "Server=db.internal;Database=ClinicSaaS;User ID=clinic_app;Encrypt=True" },
        { "dev database", "ConnectionStrings:Default", "Server=db.internal;Database=ClinicSaaS_Dev;User ID=clinic_app;Password=Xy9!aBcDeFgH;Encrypt=True" },
        { "master database", "ConnectionStrings:Default", "Server=db.internal;Database=master;User ID=clinic_app;Password=Xy9!aBcDeFgH;Encrypt=True" },
        { "auto-migrate", "Database:MigrateOnStartup", "true" },
        // web
        { "no CORS origin", "Cors:AllowedOrigins:0", null },
        { "http origin", "Cors:AllowedOrigins:0", "http://app.example.sa" },
        { "localhost origin", "Cors:AllowedOrigins:0", "https://localhost:3000" },
        { "wildcard origin", "Cors:AllowedOrigins:0", "*" },
        { "origin with a path", "Cors:AllowedOrigins:0", "https://app.example.sa/app" },
        { "no app url", "Auth:AppBaseUrl", null },
        { "http app url", "Auth:AppBaseUrl", "http://app.example.sa" },
        { "localhost app url", "Auth:AppBaseUrl", "https://localhost:3000" },
        { "app url on another origin", "Auth:AppBaseUrl", "https://other.example.sa" },
        { "no allowed hosts", "AllowedHosts", null },
        { "any host", "AllowedHosts", "*" },
        // email
        { "no email provider", "Email:Provider", null },
        { "dev mailbox", "Email:Provider", "DevMailbox" },
        { "dev mailbox, other casing", "Email:Provider", "devmailbox" },
        // limits
        { "test-grade auth limit", "RateLimiting:AuthPerMinute", "100000" },
        { "test-grade sensitive limit", "RateLimiting:SensitivePer15Minutes", "100000" },
        { "zero api limit", "RateLimiting:ApiPerMinute", "0" },
        { "two instances, in-process limiter", "RateLimiting:ApiInstances", "2" },
        { "huge lockout threshold", "Auth:MaxFailedAccessAttempts", "1000" },
        { "no lockout", "Auth:LockoutMinutes", "0" },
        // proxy
        { "no proxy configuration", "ForwardedHeaders:KnownProxies:0", null },
        { "proxy is not an address", "ForwardedHeaders:KnownProxies:0", "my-load-balancer" },
        { "proxy is 0.0.0.0", "ForwardedHeaders:KnownProxies:0", "0.0.0.0" },
        { "trust everything", "ForwardedHeaders:KnownNetworks:0", "0.0.0.0/0" },
        { "trust a /4", "ForwardedHeaders:KnownNetworks:0", "64.0.0.0/4" },
        { "trust all of IPv6", "ForwardedHeaders:KnownNetworks:0", "::/0" },
        { "network without prefix", "ForwardedHeaders:KnownNetworks:0", "10.0.0.1" },
        { "too many hops", "ForwardedHeaders:ForwardLimit", "9" },
        // logging
        { "debug logging", "Serilog:MinimumLevel:Default", "Debug" },
        { "request-URL logging (framework at Information)", "Serilog:MinimumLevel:Override:Microsoft.AspNetCore", "Information" },
        { "EF logging at Debug", "Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore", "Debug" },
        // MFA key ring (encrypts the platform admins' TOTP secrets)
        { "no MFA key ring path", "DataProtection:KeyRingPath", null },
        { "relative MFA key ring path", "DataProtection:KeyRingPath", "keys/ring" },
        { "no key-ring protection certificate", "DataProtection:CertificatePath", null },
        { "short MFA lockout", "Auth:MfaLockoutMinutes", "5" },
    };

    [Theory]
    [MemberData(nameof(Unsafe))]
    public void Unsafe_values_are_reported_by_key(string what, string key, string? value)
    {
        var problems = Problems(v => v[key] = value);

        problems.Should().NotBeEmpty($"'{what}' must not be accepted");
        // The problem names the setting (the part before the first index), so the operator knows what to fix.
        var name = key.Split(':').Where(p => !int.TryParse(p, out _)).Aggregate((a, b) => a + ":" + b);
        problems.Should().Contain(p => p.StartsWith(name.Split(':')[0], StringComparison.Ordinal), what);
    }

    [Fact]
    public void Some_extra_instances_are_accepted_only_as_an_explicit_decision()
    {
        Problems(v => { v["RateLimiting:ApiInstances"] = "2"; v["RateLimiting:AcceptPerInstanceLimits"] = "true"; }).Should().BeEmpty();
    }

    [Fact]
    public void A_direct_deployment_declares_it_instead_of_trusting_anything()
    {
        Problems(v => { v.Remove("ForwardedHeaders:KnownProxies:0"); v["ForwardedHeaders:NoProxy"] = "true"; }).Should().BeEmpty();
        Problems(v => v["ForwardedHeaders:NoProxy"] = "true").Should().Contain(p => p.Contains("NoProxy"), "NoProxy together with a trust list is contradictory");
    }

    [Fact]
    public void A_real_proxy_range_is_accepted()
    {
        Problems(v => { v.Remove("ForwardedHeaders:KnownProxies:0"); v["ForwardedHeaders:KnownNetworks:0"] = "10.20.0.0/24"; }).Should().BeEmpty();
    }

    [Fact]
    public void Every_problem_is_listed_at_once_and_no_value_is_ever_echoed()
    {
        const string secretKey = "ChangeMe-SECRETKEYVALUE";
        const string secretPassword = "TopSecretPw-12345";
        var problems = Problems(v =>
        {
            v["Jwt:SigningKey"] = secretKey;
            v["ConnectionStrings:Default"] = $"Server=(localdb)\\x;Database=ClinicSaaS_Dev;User ID=sa;Password={secretPassword};Encrypt=False";
            v["Email:Provider"] = "DevMailbox";
            v["AllowedHosts"] = "*";
        });

        problems.Count.Should().BeGreaterThan(5, "all problems are reported together");
        string.Join("\n", problems).Should().NotContain("SECRETKEYVALUE").And.NotContain(secretPassword);

        var thrown = () => ProductionConfigurationGuard.EnforceOrThrow(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SigningKey"] = secretKey }).Build(), "Production");
        thrown.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("Jwt:SigningKey").And.NotContain("SECRETKEYVALUE");
    }

    // ------------------------------------------------------------------ the environment name cannot switch the guards off

    [Fact]
    public void Development_is_development_and_Testing_counts_as_a_test_only_inside_a_test_host()
    {
        RuntimeProfiles.Of(new FakeEnv("Development"), typeof(ProductionConfigurationGuardTests).Assembly).Should().Be(RuntimeProfile.Development);
        RuntimeProfiles.Of(new FakeEnv("Testing"), typeof(ProductionConfigurationGuardTests).Assembly).Should().Be(RuntimeProfile.AutomatedTest);
        RuntimeProfiles.Of(new FakeEnv("Production"), typeof(ProductionConfigurationGuardTests).Assembly).Should().Be(RuntimeProfile.ProductionLike);
        RuntimeProfiles.Of(new FakeEnv("Staging"), typeof(ProductionConfigurationGuardTests).Assembly).Should().Be(RuntimeProfile.ProductionLike);
        RuntimeProfiles.Of(new FakeEnv("anything-else"), typeof(ProductionConfigurationGuardTests).Assembly).Should().Be(RuntimeProfile.ProductionLike);
    }

    [Fact]
    public void An_api_process_started_as_Testing_is_treated_as_production_like()
    {
        // When the API assembly itself is the executable (a real deployment), naming the environment "Testing" does not help.
        var api = typeof(RuntimeProfiles).Assembly;
        RuntimeProfiles.Of(new FakeEnv("Testing"), api).Should().Be(RuntimeProfile.ProductionLike);
        RuntimeProfiles.Of(new FakeEnv("Development"), api).Should().Be(RuntimeProfile.Development);
    }

    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "x";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    // ------------------------------------------------------------------ the real host

    private sealed class HostFactory(string environment, Dictionary<string, string?> settings, Action<IServiceCollection>? services = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            // A real deployment has no appsettings.Development.json overlay; clear what the content root would add.
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
            builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
            if (services is not null) builder.ConfigureTestServices(services);
        }
    }

    private static string AllMessages(Exception e) => e.InnerException is null ? e.Message : e.Message + " | " + AllMessages(e.InnerException);

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Qa-Preview")]
    public void The_host_refuses_to_start_in_a_production_like_environment_with_development_settings(string environment)
    {
        // The settings a developer machine has: ephemeral-style key, LocalDB, localhost, dev mailbox, auto-migrate.
        using var factory = new HostFactory(environment, new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "integration-tests-only-signing-key-0123456789-abcdefghijklmnop",
            ["ConnectionStrings:Default"] = "Server=(localdb)\\MSSQLLocalDB;Database=ClinicSaaS_Dev;Trusted_Connection=True;TrustServerCertificate=True",
            ["Cors:AllowedOrigins:0"] = "http://localhost:3000",
            ["Auth:AppBaseUrl"] = "http://localhost:3000",
            ["Email:Provider"] = "DevMailbox",
            ["Database:MigrateOnStartup"] = "true",
        });

        var act = () => factory.Services;
        var message = AllMessages(act.Should().Throw<Exception>().Which);
        message.Should().Contain("Jwt:SigningKey").And.Contain("ConnectionStrings:Default").And.Contain("Cors:AllowedOrigins")
            .And.Contain("Email:Provider").And.Contain("Database:MigrateOnStartup").And.Contain("ForwardedHeaders").And.Contain("AllowedHosts");
        message.Should().NotContain("integration-tests-only-signing-key");
    }

    [Fact]
    public async Task A_correctly_configured_production_host_starts_and_serves_liveness_without_a_database()
    {
        // The provider adapters do not exist yet, so the test supplies stand-ins for the two services the API refuses to
        // run without in production (email, attachment storage). Everything else is the real production wiring.
        var settings = Valid();
        using var factory = new HostFactory("Production", settings, s =>
        {
            s.RemoveAll<IEmailSender>();
            s.AddSingleton<IEmailSender, SilentEmailSender>();
            s.RemoveAll<IFileStorage>();
            s.AddSingleton<IFileStorage, NullFileStorage>();
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://api.example.sa") });

        (await client.GetAsync("/health/live")).IsSuccessStatusCode.Should().BeTrue();
        factory.Services.GetRequiredService<IOptions<HstsOptions>>().Value.Should().Match<HstsOptions>(
            h => h.MaxAge == TimeSpan.FromDays(730) && h.IncludeSubDomains && !h.Preload);
    }

    [Fact]
    public void A_production_host_refuses_to_start_when_the_key_ring_certificate_cannot_be_loaded()
    {
        // Fail closed: no silent fall-back to an ephemeral or default key for the MFA secrets. Only setting names are reported.
        var settings = Valid();
        settings["DataProtection:CertificatePath"] = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".pfx");
        using var factory = new HostFactory("Production", settings);

        var message = AllMessages(((Action)(() => _ = factory.Services)).Should().Throw<Exception>().Which);
        message.Should().Contain("DataProtection:CertificatePath").And.NotContain(settings["DataProtection:CertificatePassword"]!);
    }

    [Fact]
    public void A_production_host_refuses_to_start_without_a_key_ring()
    {
        var settings = Valid();
        settings.Remove("DataProtection:KeyRingPath");
        using var factory = new HostFactory("Production", settings);

        AllMessages(((Action)(() => _ = factory.Services)).Should().Throw<Exception>().Which).Should().Contain("DataProtection:KeyRingPath");
    }

    private sealed class SilentEmailSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullFileStorage : IFileStorage
    {
        public Task SaveAsync(string key, Stream content, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default) => Task.FromResult<Stream?>(null);
        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
