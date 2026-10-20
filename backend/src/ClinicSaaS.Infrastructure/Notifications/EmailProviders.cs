using ClinicSaaS.Application.Abstractions;

namespace ClinicSaaS.Infrastructure.Notifications;

/// <summary>
/// Which email provider the API uses (<c>Email:Provider</c>). Only "DevMailbox" exists today, and it is for
/// Development and the automated tests only: it writes messages — including one-time password-reset, invitation and
/// verification links — to files on the server. A real provider is chosen in the production phase.
/// </summary>
public sealed class EmailOptions
{
    public const string Section = "Email";
    public const string DevMailboxProvider = "DevMailbox";

    public string Provider { get; set; } = "";
}

/// <summary>
/// Registered when no provider is configured. Never sends and never writes anything; the API refuses to start with it
/// outside Development/Testing (see Program.cs), so this only ever surfaces in a misconfigured development setup.
/// </summary>
public sealed class NotConfiguredEmailSender : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct = default) =>
        throw new InvalidOperationException("No email provider is configured (Email:Provider).");
}
