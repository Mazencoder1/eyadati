using System.Security.Claims;
using ClinicSaaS.Api.Errors;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Saas;
using ClinicSaaS.Domain.Common;

namespace ClinicSaaS.Api.Infrastructure;

/// <summary>
/// Re-checks, on every authenticated request, what the access token only claims (Gate C, GD-02, GD-03):
/// - Session (GD-03, clinic AND platform tokens): the token's <c>sid</c> must still be a live session. Logout, password
///   change/reset, ending a session, switching clinic, suspension, disabling a member and refresh-token reuse all
///   revoke the session, so the access token already issued is refused at once → 401 UNAUTHORIZED. A token without a
///   <c>sid</c> never gets in. On an [AllowAnonymous] endpoint (login, refresh, public plans…) a dead session is simply
///   treated as anonymous, so a stale token in the browser can't block signing in again — and can't act as identity.
/// - Membership (GD-02): the token's membership must still be active. A disabled (or re-invited) member gets 401
///   NO_ACTIVE_MEMBERSHIP on the access token they already hold — on every clinic endpoint and on GET /auth/me. The
///   other /auth/* endpoints skip this check so the person can still sign out or switch to another clinic.
/// - Suspended / closed clinic → 403 TENANT_SUSPENDED for everything, immediately (no waiting for token expiry).
///   Suspension also revokes every session of the clinic, and reactivation does not bring them back: tokens issued
///   before the suspension stay dead (GD-15).
/// - Subscription period ended → read-only: GET/HEAD keep working, every write is 403 SUBSCRIPTION_EXPIRED.
/// Authentication endpoints (/api/v1/auth/*) skip the membership and clinic status/subscription rules, except GET
/// /auth/me: they enforce suspension themselves when they issue tokens, and a user must still be able to sign out,
/// switch clinic or change their password. /auth/me answers a suspended clinic with 403 TENANT_SUSPENDED like every
/// clinic endpoint (it used to answer 200 with access = Suspended; since suspension now ends the sessions, nothing live
/// is left to tell), which is what makes an open tab show the one clear "suspended" screen.
/// The order is deliberate: membership, then clinic status, then session, then read-only — so a disabled member and a
/// suspended clinic keep their specific error codes even though their sessions were revoked too.
/// Session and membership are verified together in one indexed query.
/// </summary>
public sealed class TenantAccessMiddleware(RequestDelegate next)
{
    private static readonly PathString AuthPath = "/api/v1/auth";
    private static readonly PathString MePath = "/api/v1/auth/me";

    public async Task InvokeAsync(HttpContext http, ICurrentUser user, TenantAccessResolver resolver, IRequestContext request)
    {
        if (!user.IsAuthenticated)
        {
            await next(http);
            return;
        }

        var tenantId = user.TenantId;
        var check = await resolver.CheckSessionAsync(
            user.UserId ?? Guid.Empty, user.SessionId ?? Guid.Empty, tenantId, user.MembershipId ?? Guid.Empty, http.RequestAborted);

        if (!check.SessionLive && http.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity());
            await next(http);
            return;
        }

        // GET /auth/me is a clinic endpoint (it reports this clinic's access); the rest of /auth/* is not.
        var clinicRules = tenantId is not null && (!http.Request.Path.StartsWithSegments(AuthPath) || http.Request.Path.StartsWithSegments(MePath));
        if (clinicRules && !check.MembershipActive)
        {
            await RefuseAsync(http, request, StatusCodes.Status401Unauthorized, ErrorCodes.NoActiveMembership);
            return;
        }

        var access = clinicRules ? await resolver.ResolveAsync(tenantId!.Value, http.RequestAborted) : TenantAccess.Full;
        if (access == TenantAccess.Suspended)
        {
            await RefuseAsync(http, request, StatusCodes.Status403Forbidden, ErrorCodes.TenantSuspended);
            return;
        }

        if (!check.SessionLive)
        {
            await RefuseAsync(http, request, StatusCodes.Status401Unauthorized, ErrorCodes.Unauthorized);
            return;
        }

        if (access == TenantAccess.ReadOnly && !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
        {
            await RefuseAsync(http, request, StatusCodes.Status403Forbidden, ErrorCodes.SubscriptionExpired);
            return;
        }

        await next(http);
    }

    private static async Task RefuseAsync(HttpContext http, IRequestContext request, int status, string code)
    {
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(new ApiError(code, ErrorCatalog.Message(code, request.Language), request.CorrelationId));
    }
}
