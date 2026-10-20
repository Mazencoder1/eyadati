using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Platform;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.Application.Saas;

public sealed record SubscriptionView(
    string PlanCode, string PlanNameAr, string PlanNameEn, SubscriptionStatus Status, BillingCycle BillingCycle,
    DateTime CurrentPeriodEndUtc, DateTime? TrialEndsAtUtc, bool CancelAtPeriodEnd,
    IReadOnlyDictionary<string, bool> Features, IReadOnlyList<UsageItem> Usage, bool HasFullAccess);

/// <summary>
/// The clinic's own subscription page. Shows only features the product actually has (FeatureKeys.Offered) and the
/// enforced limits; <see cref="SubscriptionView.HasFullAccess"/> is false once the period has ended (read-only).
/// </summary>
public sealed class SubscriptionQueries(IAppDbContext db, IFeatureGate features, IUsageLimiter limits, IClock clock)
{
    public async Task<SubscriptionView?> GetCurrentAsync(CancellationToken ct)
    {
        var sub = await db.Subscriptions.AsNoTracking().Include(s => s.Plan)
            .Where(s => Subscription.CurrentStatuses.Contains(s.Status))
            .OrderByDescending(s => s.CurrentPeriodEndUtc)
            .FirstOrDefaultAsync(ct);
        if (sub is null) return null;

        var flags = new Dictionary<string, bool>();
        foreach (var key in FeatureKeys.Offered) flags[key] = await features.IsEnabledAsync(key, ct);
        return new SubscriptionView(sub.Plan!.Code, sub.Plan.NameAr, sub.Plan.NameEn, sub.Status, sub.BillingCycle,
            sub.CurrentPeriodEndUtc, sub.TrialEndsAtUtc, sub.CancelAtPeriodEnd, flags, await limits.GetUsageAsync(ct),
            sub.GrantsFullAccess(clock.UtcNow));
    }
}

public sealed record AuditLogQuery : PageQuery
{
    public string? Action { get; init; }
    public Guid? UserId { get; init; }
    public string? EntityType { get; init; }
    public string? EntityId { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
}

public sealed record AuditLogItem(
    long Id, DateTime OccurredAtUtc, string Action, string? EntityType, string? EntityId, AuditResult Result,
    Guid? UserId, string? UserName, string? IpAddress, string? Details);

/// <summary>
/// A clinic's own audit trail. AuditLog is not an ITenantOwned entity (platform events have no tenant), so the
/// tenant predicate is explicit here — and this is the only tenant-facing reader of the table.
/// Visibility of a clinic's own audit trail is a core security capability, not a plan feature (D13): only the
/// AuditLogs.View permission gates it.
public sealed class AuditLogQueries(IAppDbContext db, ClinicContext clinic)
{
    public async Task<PagedResult<AuditLogItem>> ListAsync(AuditLogQuery query, CancellationToken ct)
    {
        var tenantId = clinic.TenantId;
        var q = db.AuditLogs.AsNoTracking().Where(l => l.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(query.Action)) q = q.Where(l => l.Action.StartsWith(query.Action));
        if (query.UserId is { } u) q = q.Where(l => l.UserId == u);
        if (!string.IsNullOrWhiteSpace(query.EntityType)) q = q.Where(l => l.EntityType == query.EntityType);
        if (!string.IsNullOrWhiteSpace(query.EntityId)) q = q.Where(l => l.EntityId == query.EntityId);
        if (query.FromUtc is { } f) q = q.Where(l => l.OccurredAtUtc >= f);
        if (query.ToUtc is { } t) q = q.Where(l => l.OccurredAtUtc < t);

        var total = await q.CountAsync(ct);
        var items = await (
            from l in q.OrderByDescending(l => l.OccurredAtUtc).ThenByDescending(l => l.Id)
                .Skip((query.SafePage - 1) * query.SafePageSize).Take(query.SafePageSize)
            join usr in db.Users on l.UserId equals (Guid?)usr.Id into users
            from usr in users.DefaultIfEmpty()
            select new AuditLogItem(l.Id, l.OccurredAtUtc, l.Action, l.EntityType, l.EntityId, l.Result, l.UserId,
                usr != null ? usr.FullName : null, l.IpAddress, l.Details)).ToListAsync(ct);
        return new PagedResult<AuditLogItem>(items, query.SafePage, query.SafePageSize, total);
    }
}
