using Microsoft.Extensions.Logging;

namespace ClinicSaaS.Application.Abstractions;

/// <summary>
/// Best-effort delivery of transactional email. The message is sent AFTER the database commit (the account, the reset
/// code or the invitation already exists), so a provider outage must not turn that request into a 500 that hides the
/// committed result: the failure is logged for the operator and the request still succeeds. The person can ask again
/// (resend verification, resend invitation, "forgot password"); password reset already answers the same way whether or
/// not an email goes out, so this also keeps that answer uniform.
/// There is no outbox (it would need a table), so a failed message is not queued — that is the known trade-off.
/// What is logged: the purpose, the recipient's DOMAIN and the exception TYPES. Never the message, the link, the
/// recipient's full address or the exception text (provider errors can echo credentials or addresses).
/// </summary>
public static class EmailDelivery
{
    public static async Task TrySendAsync(
        this IEmailSender sender, ILogger logger, string purpose, EmailMessage message, CancellationToken ct)
    {
        try
        {
            await sender.SendAsync(message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var domain = message.To.Contains('@') ? message.To[(message.To.IndexOf('@') + 1)..] : "?";
            logger.LogError(SecurityLogEvents.EmailDeliveryFailed, "Email delivery failed for {Purpose} to *@{Domain}: {FailureTypes}", purpose, domain, FailureTypes(ex));
        }
    }

    private static string FailureTypes(Exception ex)
    {
        var types = new List<string>();
        for (var e = ex; e is not null && types.Count < 5; e = e.InnerException!) types.Add(e.GetType().Name);
        return string.Join(" > ", types);
    }
}
