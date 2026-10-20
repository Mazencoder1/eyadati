using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.Domain.Platform;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.Application.Saas;

/// <summary>What a clinic session may do right now, from the clinic's status and its subscription.</summary>
public enum TenantAccess
{
    /// <summary>Active clinic with a subscription period that has not ended.</summary>
    Full,
    /// <summary>The subscription period ended (trial over or not renewed): sign-in and reads only, every write refused.</summary>
    ReadOnly,
    /// <summary>Suspended (or closed) by the platform: nothing but signing out / switching clinic.</summary>
    Suspended,
}

/// <summary>
/// Resolves <see cref="TenantAccess"/> once per request with one small query (tenant status + whether a current
/// subscription period is still running). Used by the API's access middleware on every clinic request — so
/// suspension and expiry act immediately instead of at the next token refresh — and by GET /auth/me.
/// </summary>
public sealed class TenantAccessResolver(IAppDbContext db, IClock clock)
{
    private Guid? _tenantId;
    private TenantAccess _access;

    public async Task<TenantAccess> ResolveAsync(Guid tenantId, CancellationToken ct)
    {
        if (_tenantId == tenantId) return _access;
        var now = clock.UtcNow;
        var row = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new
            {
                t.Status,
                Running = db.Subscriptions.IgnoreQueryFilters(QueryFilters.TenantOnly).Any(s =>
                    s.TenantId == tenantId && Subscription.CurrentStatuses.Contains(s.Status) && s.CurrentPeriodEndUtc > now),
            })
            .FirstOrDefaultAsync(ct);

        _access = row is null || row.Status is TenantStatus.Suspended or TenantStatus.Closed
            ? TenantAccess.Suspended
            : row.Running ? TenantAccess.Full : TenantAccess.ReadOnly;
        _tenantId = tenantId;
        return _access;
    }

    /// <summary>
    /// GD-02 + GD-03, in one small query on existing indexes: whether the session an access token belongs to is still
    /// alive, and (for a clinic token) whether the membership it was issued for is still active.
    /// - Session: a session is its refresh-token family (the token's <c>sid</c>). It is alive while the family still has
    ///   an unrevoked, unexpired token that belongs to the same user and the same clinic (or platform plane) as the
    ///   access token. Logout, password change/reset, ending a session, switching clinic, suspension, disabling a member
    ///   and refresh-token reuse all revoke the family, so the access token stops working on the very next request.
    ///   Rotation adds the new token and revokes the old one in one transaction, so a rotating session never looks dead.
    /// - Membership: checked on clinic tokens so disabling a member takes effect on the token they already hold. A
    ///   re-enabled seat becomes a new pending invitation (QA-02), so it stays refused until the person accepts again.
    /// A deactivated user has no live session.
    /// </summary>
    public async Task<SessionCheck> CheckSessionAsync(
        Guid userId, Guid sessionId, Guid? tenantId, Guid membershipId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var row = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.IsActive,
                // A platform account without an authenticator holds no session (legacy sessions, a removed factor,
                // unexpected data, future bugs): MFA is part of what makes a platform session valid.
                MfaOk = u.PlatformRole == PlatformRole.None || db.UserTotpFactors.Any(f => f.UserId == userId),
                SessionLive = db.RefreshTokens.Any(t =>
                    t.FamilyId == sessionId && t.UserId == userId && t.TenantId == tenantId
                    && t.RevokedAtUtc == null && t.ExpiresAtUtc > now),
                MembershipActive = db.TenantMemberships.IgnoreQueryFilters(QueryFilters.TenantOnly).Any(m =>
                    m.Id == membershipId && m.TenantId == tenantId && m.UserId == userId && m.Status == MembershipStatus.Active),
            })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? new SessionCheck(SessionLive: false, MembershipActive: false)
            : new SessionCheck(row.IsActive && row.MfaOk && row.SessionLive, row.MembershipActive);
    }
}

/// <summary>Result of <see cref="TenantAccessResolver.CheckSessionAsync"/>. <c>MembershipActive</c> only matters for clinic tokens.</summary>
public readonly record struct SessionCheck(bool SessionLive, bool MembershipActive);
