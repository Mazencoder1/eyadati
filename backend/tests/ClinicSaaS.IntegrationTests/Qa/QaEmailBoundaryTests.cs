using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Infrastructure.Notifications;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicSaaS.IntegrationTests.Qa;

/// <summary>
/// Gate D — configuration edge cases of the email production boundary (Gate C). The host is built for each
/// environment/provider combination; no database is touched. GD-05 (fixed): DevMailbox is Development-only.
/// </summary>
public sealed class QaEmailBoundaryTests
{
    private sealed class Host(string environment, string? provider) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:Default", "Server=unused.invalid;Database=unused;TrustServerCertificate=True");
            builder.UseSetting("Jwt:SigningKey", "qa-email-boundary-only-signing-key-0123456789-abcdefgh");
            builder.UseSetting("Database:MigrateOnStartup", "false");
            // Outside Development/Testing start from valid production settings, so the configuration guard stays quiet
            // about everything except the email provider this class is about.
            if (!environment.Equals("Development", StringComparison.OrdinalIgnoreCase) && !environment.Equals("Testing", StringComparison.OrdinalIgnoreCase))
                ProductionSettings.Apply(builder, ("Email:Provider", null));
            if (provider is not null) builder.UseSetting("Email:Provider", provider);
        }
    }

    private static string? StartupError(string environment, string? provider)
    {
        using var host = new Host(environment, provider);
        try
        {
            _ = host.Services.GetRequiredService<IEmailSender>();
            return null;
        }
        catch (Exception e)
        {
            var messages = new List<string>();
            for (var x = e; x is not null; x = x.InnerException) messages.Add(x.Message);
            return string.Join(" | ", messages);
        }
    }

    [Theory]
    [InlineData("Production", "devmailbox")]   // case-insensitive match still selects DevMailbox → refused
    [InlineData("Production", "DEVMAILBOX")]
    [InlineData("production", "DevMailbox")]   // environment names compare case-insensitively
    [InlineData("Staging", "DevMailbox")]
    [InlineData("Local", "DevMailbox")]        // any custom environment name is "not Development"
    [InlineData("Docker", "DevMailbox")]
    [InlineData("Testing", "DevMailbox")]      // GD-05: Testing no longer exempt
    [InlineData("testing", "devmailbox")]
    [InlineData("Production-like", "DevMailbox")]
    public void Development_mailbox_is_refused_outside_development(string environment, string provider)
    {
        StartupError(environment, provider).Should().Contain("DevMailbox email provider writes one-time links to disk");
    }

    [Theory]
    [InlineData("Production", null)]
    [InlineData("Production", "")]
    [InlineData("Production", " DevMailbox ")] // padded value is not a provider → treated as none, refused
    [InlineData("Production", "Smtp")]         // an unknown provider name is not silently accepted
    [InlineData("Production", "SendGrid")]
    [InlineData("Staging", "")]
    public void Missing_or_unknown_provider_is_refused_outside_development_and_testing(string environment, string? provider)
    {
        StartupError(environment, provider).Should().Contain("No email provider is configured");
    }

    [Fact]
    public void Development_without_the_setting_has_no_sender_that_writes_anywhere()
    {
        // appsettings.Development.json selects DevMailbox; an explicit empty override leaves the non-writing sender.
        using var host = new Host("Development", "");
        host.Services.GetRequiredService<IEmailSender>().Should().BeOfType<NotConfiguredEmailSender>();
    }

    [Fact]
    public void Fixed_GD05_Testing_environment_does_not_need_the_development_mailbox()
    {
        // GD-05: Testing starts without any provider (the automated tests replace the sender) and never selects the
        // development mailbox on its own.
        StartupError("Testing", null).Should().BeNull();
        using var host = new Host("Testing", null);
        host.Services.GetRequiredService<IEmailSender>().Should().BeOfType<NotConfiguredEmailSender>();
    }

    [Fact]
    public void Development_still_accepts_the_development_mailbox()
    {
        StartupError("Development", "DevMailbox").Should().BeNull();
        using var host = new Host("Development", "DevMailbox");
        host.Services.GetRequiredService<IEmailSender>().Should().BeOfType<DevMailboxEmailSender>();
    }
}
