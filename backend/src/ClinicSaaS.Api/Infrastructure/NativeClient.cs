using ClinicSaaS.Application.Common;
using ClinicSaaS.Domain.Common;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ClinicSaaS.Api.Infrastructure;

/// <summary>
/// Separates the two ways a client can hold its refresh token:
/// - Browser (default): an httpOnly cookie. Script on the page can never read it.
/// - Native app (<c>X-Client-Type: mobile</c>): in the response body, for the OS keychain/keystore.
/// The native mode must be unreachable from a browser, otherwise script running on the web origin (XSS) could ask for
/// it and walk away with a long-lived refresh token. A page cannot hide that it is a page: browsers add
/// <c>Origin</c> to every cross-origin and every POST request and <c>Sec-Fetch-*</c> to every request, and page script
/// cannot set or remove either (both are forbidden header names). Native HTTP stacks send neither. CORS also does
/// not allow the <c>X-Client-Type</c> header, so cross-origin browser calls fail at the preflight already.
/// A non-browser attacker can send any headers — but they would need the refresh token first, so nothing is gained.
/// </summary>
public static class NativeClient
{
    public const string Header = "X-Client-Type";
    private const string NativeValue = "mobile";

    public static bool IsRequested(HttpRequest request) =>
        string.Equals(request.Headers[Header], NativeValue, StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikeBrowser(HttpRequest request) =>
        request.Headers.ContainsKey("Origin") ||
        request.Headers.Keys.Any(name => name.StartsWith("Sec-Fetch-", StringComparison.OrdinalIgnoreCase));

    /// <summary>The caller asked for native mode and is not a native client.</summary>
    public static bool IsBrowserAskingForNativeMode(HttpRequest request) => IsRequested(request) && LooksLikeBrowser(request);
}

/// <summary>
/// CSRF defence in depth for the two endpoints that are authenticated by the refresh COOKIE rather than a bearer token
/// (<c>/auth/refresh</c>, <c>/auth/logout</c>). The cookie is SameSite=Strict, which already stops cross-SITE requests; this
/// also stops a request from another origin on the same site (a sibling subdomain of the registrable domain) by requiring
/// that a browser-supplied <c>Origin</c> be one of <c>Cors:AllowedOrigins</c>. Requests without an Origin (native apps,
/// tools) are unaffected — they have no ambient cookie to abuse. Bearer-token endpoints need no CSRF check: the browser
/// never attaches that token by itself.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireTrustedOriginAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var request = context.HttpContext.Request;
        if (!request.Headers.TryGetValue("Origin", out var origin) || origin.Count == 0) return;

        var allowed = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()
            .GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        var presented = origin.ToString().TrimEnd('/');
        if (!allowed.Any(a => string.Equals(a.TrimEnd('/'), presented, StringComparison.OrdinalIgnoreCase)))
            throw new ForbiddenException();
    }
}

/// <summary>
/// Refuses native-mode requests that come from a browser before anything else happens — before model binding and, above
/// all, before a refresh token is rotated or a session is created, so a refused call has no side effects.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RejectBrowserNativeModeAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (NativeClient.IsBrowserAskingForNativeMode(context.HttpContext.Request))
            throw new ForbiddenException(ErrorCodes.ClientTypeNotAllowed);
    }
}
