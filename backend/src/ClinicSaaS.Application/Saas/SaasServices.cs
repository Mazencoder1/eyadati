using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicSaaS.Application.Saas;

/// <summary>Environment-level kill switches (appsettings), highest precedence over tenant and plan.</summary>
public sealed class FeatureFlagOptions
{
    public const string Section = "FeatureFlags";
    /// <summary>Features switched off for the whole environment (e.g. "whatsapp" in staging).</summary>
    public string[] DisabledFeatures { get; set; } = [];
}

public interface IFeatureGate
{
    Task<bool> IsEnabledAsync(string featureKey, CancellationToken ct = default);
    /// <summary>Throws <see cref="FeatureNotAvailableException"/> (403, "upgrade plan") when off.</summary>
    Task EnsureEnabledAsync(string featureKey, CancellationToken ct = default);
}

public interface IUsageLimiter
{
    /// <summary>Throws <see cref="UsageLimitExceededException"/> when adding one more would exceed the plan limit.</summary>
    Task EnsureCanAddAsync(string limitKey, CancellationToken ct = default);
    Task<IReadOnlyList<UsageItem>> GetUsageAsync(CancellationToken ct = default);
}

public sealed record UsageItem(string LimitKey, int Used, int? Max);

/// <summary>Resolves the tenant's current plan once per request.</summary>
public sealed class CurrentPlanResolver(IAppDbContext db, ITenantContext tenant)
{
    private Plan? _plan;
    private bool _loaded;

    public async Task<Plan?> GetAsync(CancellationToken ct)
    {
        if (_loaded) return _plan;
        if (tenant.TenantId is null) throw new ForbiddenException(ErrorCodes.TenantRequired);

        var planId = await db.Subscriptions
            .Where(s => Subscription.CurrentStatuses.Contains(s.Status))
            .OrderByDescending(s => s.CurrentPeriodEndUtc)
            .Select(s => (Guid?)s.PlanId)
            .FirstOrDefaultAsync(ct);

        _plan = planId is null
            ? null
            : await db.Plans.AsNoTracking().Include(p => p.Features).Include(p => p.Limits).FirstOrDefaultAsync(p => p.Id == planId, ct);
        _loaded = true;
        return _plan;
    }
}

public sealed class FeatureGate(IAppDbContext db, CurrentPlanResolver plans, IOptions<FeatureFlagOptions> flags) : IFeatureGate
{
    public async Task<bool> IsEnabledAsync(string featureKey, CancellationToken ct = default)
    {
        if (flags.Value.DisabledFeatures.Contains(featureKey, StringComparer.OrdinalIgnoreCase)) return false;

        var overrideValue = await db.TenantFeatureOverrides
            .Where(o => o.FeatureKey == featureKey)
            .Select(o => (bool?)o.IsEnabled)
            .FirstOrDefaultAsync(ct);
        if (overrideValue is not null) return overrideValue.Value;

        var plan = await plans.GetAsync(ct);
        return plan?.Features.Any(f => f.FeatureKey == featureKey) ?? false;
    }

    public async Task EnsureEnabledAsync(string featureKey, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(featureKey, ct)) throw new FeatureNotAvailableException(featureKey);
    }
}

/// <summary>
/// Plan limit enforcement. Only *new* resources are gated: after a downgrade, existing data stays readable and
/// editable. The count-then-insert is not serialized, so concurrent creates may exceed a limit by a few rows —
/// an accepted soft-limit trade-off (limits are commercial, not safety, constraints).
/// </summary>
public sealed class UsageLimiter(IAppDbContext db, CurrentPlanResolver plans, IClock clock) : IUsageLimiter
{
    public async Task EnsureCanAddAsync(string limitKey, CancellationToken ct = default)
    {
        var plan = await plans.GetAsync(ct);
        // No current subscription ⇒ treat as limit 0 for every metered resource.
        var max = plan is null ? 0 : plan.Limits.FirstOrDefault(l => l.LimitKey == limitKey) is { } l ? l.MaxValue : 0;
        if (max is null) return; // unlimited

        var used = await CountAsync(limitKey, ct);
        if (used >= max) throw new UsageLimitExceededException(limitKey, max.Value);
    }

    public async Task<IReadOnlyList<UsageItem>> GetUsageAsync(CancellationToken ct = default)
    {
        var plan = await plans.GetAsync(ct);
        var result = new List<UsageItem>();
        foreach (var key in LimitKeys.Enforced)
        {
            var max = plan?.Limits.FirstOrDefault(l => l.LimitKey == key)?.MaxValue;
            result.Add(new UsageItem(key, await CountAsync(key, ct), plan is null ? 0 : max));
        }
        return result;
    }

    private Task<int> CountAsync(string limitKey, CancellationToken ct)
    {
        var monthStart = new DateTime(clock.UtcNow.Year, clock.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return limitKey switch
        {
            LimitKeys.MaxPatients => db.Patients.CountAsync(ct),
            LimitKeys.MaxDoctors => db.Doctors.CountAsync(d => d.IsActive, ct),
            LimitKeys.MaxBranches => db.Branches.CountAsync(b => b.IsActive, ct),
            LimitKeys.MaxUsers => db.TenantMemberships.CountAsync(m => m.Status != MembershipStatus.Disabled, ct),
            LimitKeys.MaxAppointmentsPerMonth => db.Appointments.CountAsync(a => a.CreatedAtUtc >= monthStart, ct),
            _ => Task.FromResult(0),
        };
    }
}
