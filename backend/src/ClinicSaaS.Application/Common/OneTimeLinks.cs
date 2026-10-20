namespace ClinicSaaS.Application.Common;

/// <summary>
/// Links in emails that carry a one-time token (password reset, email verification, invitation).
/// The token goes in the URL FRAGMENT (<c>/reset-password#token=…</c>), never the query string. A browser does not send
/// the fragment to any server, so the token cannot appear in the web server's, a reverse proxy's or a load balancer's
/// access log, in telemetry, or in a Referer header. The web app reads it client-side and removes it from the address
/// bar. (Links emailed before this change used <c>?token=</c>; the web app still reads that form.)
/// </summary>
public static class OneTimeLinks
{
    public static string Build(string appBaseUrl, string path, string token) =>
        $"{appBaseUrl.TrimEnd('/')}/{path.Trim('/')}#token={Uri.EscapeDataString(token)}";
}
