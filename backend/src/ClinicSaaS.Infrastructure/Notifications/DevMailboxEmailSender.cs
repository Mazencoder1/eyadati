using ClinicSaaS.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClinicSaaS.Infrastructure.Notifications;

public sealed class DevMailboxOptions
{
    public const string Section = "Email:DevMailbox";
    public string Directory { get; set; } = ".dev-mailbox";
}

/// <summary>
/// Development-only email sender: writes each message as an .html file instead of sending it. Logs recipient
/// domain and subject only — never the body (it contains one-time links). A real provider replaces it in production (plan task 3D).
/// </summary>
public sealed class DevMailboxEmailSender(IOptions<DevMailboxOptions> options, ILogger<DevMailboxEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var dir = Path.GetFullPath(options.Value.Directory);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(file, $"<!-- to: {message.To} | subject: {message.Subject} -->\n{message.HtmlBody}", ct);

        var domain = message.To.Contains('@') ? message.To[(message.To.IndexOf('@') + 1)..] : "?";
        logger.LogInformation("Dev email '{Subject}' to *@{Domain} written to mailbox", message.Subject, domain);
    }
}
