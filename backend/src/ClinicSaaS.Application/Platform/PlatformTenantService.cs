using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.Domain.Platform;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.Application.Platform;

public sealed record PlatformTenantQuery : PageQuery
{
    public TenantStatus? Status { get; init; }
}

public sealed record PlatformTenantItem(
    Guid Id, string Name, string Slug, TenantStatus Status, string? City, string? PlanCode, SubscriptionStatus? SubscriptionStatus,
    int ActiveMembers, DateTime CreatedAtUtc);

/// <summary>
/// Super-admin view across clinics. This is the one place that reads tenant-owned tables without a tenant
/// scope, so every cross-tenant read lifts *only* the tenant filter, explicitly, and every change is audited.
/// Clinical data (patients, visits) is deliberately not exposed here.
/// </summary>
public sealed class PlatformTenantService(IAppDbContext db, IClock clock, IAuditLogger audit)
{
    public async Task<PagedResult<PlatformTenantItem>> ListAsync(PlatformTenantQuery query, CancellationToken ct)
    {
        var q = db.Tenants.AsNoTracking();
        if (query.Status is { } s) q = q.Where(t => t.Status == s);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(t => t.Name.StartsWith(term) || t.Slug.StartsWith(term.ToLower()));
        }

        var total = await q.CountAsync(ct);
        var subs = db.Subscriptions.IgnoreQueryFilters(QueryFilters.TenantOnly).Where(x => Subscription.CurrentStatuses.Contains(x.Status));
        var members = db.TenantMemberships.IgnoreQueryFilters(QueryFilters.TenantOnly);
        var items = await q.OrderByDescending(t => t.CreatedAtUtc)
            .Skip((query.SafePage - 1) * query.SafePageSize).Take(query.SafePageSize)
            .Select(t => new PlatformTenantItem(
                t.Id, t.Name, t.Slug, t.Status, t.City,
                subs.Where(x => x.TenantId == t.Id).Select(x => x.Plan!.Code).FirstOrDefault(),
                subs.Where(x => x.TenantId == t.Id).Select(x => (SubscriptionStatus?)x.Status).FirstOrDefault(),
                members.Count(m => m.TenantId == t.Id && m.Status == MembershipStatus.Active),
                t.CreatedAtUtc))
            .ToListAsync(ct);
        return new PagedResult<PlatformTenantItem>(items, query.SafePage, query.SafePageSize, total);
    }

    public async Task SetStatusAsync(Guid tenantId, TenantStatus status, CancellationToken ct)
    {
        if (status is not (TenantStatus.Active or TenantStatus.Suspended))
            throw new RequestValidationException(new Dictionary<string, string[]> { ["status"] = [ValidationCodes.InvalidValue] });
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct) ?? throw new NotFoundException(ErrorCodes.TenantNotFound);
        if (tenant.Status == status) return;

        tenant.Status = status;
        if (status == TenantStatus.Suspended)
        {
            var now = clock.UtcNow;
            // Suspension takes effect at the next refresh (≤ access-token lifetime) for every member.
            await db.RefreshTokens.Where(t => t.TenantId == tenantId && t.RevokedAtUtc == null)
                .ForEachAsync(t => t.Revoke(now, "tenant_suspended"), ct);
        }
        audit.Add(status == TenantStatus.Suspended ? AuditActions.TenantSuspended : AuditActions.TenantReactivated,
            nameof(Tenant), tenantId.ToString(), tenantId: tenantId);
        await db.SaveChangesAsync(ct);
    }
}
