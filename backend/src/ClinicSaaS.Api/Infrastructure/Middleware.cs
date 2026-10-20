using System.Diagnostics;
using System.Text.RegularExpressions;
using ClinicSaaS.Application.Abstractions;
using Serilog.Context;

namespace ClinicSaaS.Api.Infrastructure;

/// <summary>
/// Accepts a well-formed inbound X-Correlation-Id (or creates one), echoes it on the response and pushes it,
/// plus user and tenant ids, into the log context for every log line of the request.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        var inbound = http.Request.Headers[HttpRequestContext.CorrelationHeader].ToString();
        http.TraceIdentifier = SafeId().IsMatch(inbound) ? inbound : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        http.Response.Headers[HttpRequestContext.CorrelationHeader] = http.TraceIdentifier;

        using (LogContext.PushProperty("CorrelationId", http.TraceIdentifier))
        {
            await next(http);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex SafeId();
}

/// <summary>Adds user/tenant to the log context once authentication has run.</summary>
public sealed class LogEnrichmentMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, ICurrentUser user)
    {
        using (LogContext.PushProperty("UserId", user.UserId))
        using (LogContext.PushProperty("TenantId", user.TenantId))
        {
            await next(http);
        }
    }
}

/// <summary>Defensive headers for a JSON API (the web app sets its own CSP).</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext http)
    {
        var h = http.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        // API responses may carry personal/medical data: never cache them in shared caches.
        if (!h.ContainsKey("Cache-Control")) h["Cache-Control"] = "no-store";
        return next(http);
    }
}
