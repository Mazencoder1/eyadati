using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Infrastructure.Notifications;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// Gate C email boundary (tightened by GD-05): the development mailbox (one-time links written to disk) runs only in
/// Development; any environment other than Development/Testing refuses to start without a real provider. No database is needed — the guard runs
/// before migrations or any request.
/// </summary>
public sealed class EmailProviderGuardTests
{
    private sealed class HostFactory(string environment, IReadOnlyDictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:Default", "Server=unused.invalid;Database=unused;TrustServerCertificate=True");
            builder.UseSetting("Jwt:SigningKey", "email-guard-tests-only-signing-key-0123456789-abcdefgh");
            builder.UseSetting("Database:MigrateOnStartup", "false");
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
        }
    }

    private static Exception StartupFailure(string environment, params (string Key, string? Value)[] settings)
    {
        using var factory = new HostFactory(environment, settings.ToDictionary(s => s.Key, s => s.Value));
        var act = () => factory.Services.GetRequiredService<IEmailSender>();
        return act.Should().Throw<Exception>().Which;
    }

    private static string AllMessages(Exception e) => e.InnerException is null ? e.Message : e.Message + " | " + AllMessages(e.InnerException);

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Without_an_email_provider_the_api_refuses_to_start_outside_development(string environment)
    {
        AllMessages(StartupFailure(environment)).Should().Contain("No email provider is configured");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void The_development_mailbox_is_refused_outside_development(string environment)
    {
        AllMessages(StartupFailure(environment, ("Email:Provider", "DevMailbox"))).Should().Contain("The DevMailbox email provider writes one-time links to disk");
    }

    [Fact]
    public void Development_selects_the_development_mailbox_explicitly()
    {
        using var factory = new HostFactory("Development", new Dictionary<string, string?>());
        factory.Services.GetRequiredService<IEmailSender>().Should().BeOfType<DevMailboxEmailSender>();
    }

    [Fact]
    public async Task Without_a_provider_nothing_is_ever_written_or_sent()
    {
        var sender = new NotConfiguredEmailSender();
        var act = () => sender.SendAsync(new EmailMessage("x@clinic-test.sa", "subject", "<a href=\"/reset-password?token=secret\">reset</a>"));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
