using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Domain.Platform;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.Application.Platform;

/// <summary>A clinic's subscription as the platform admin sees it. <see cref="HasFullAccess"/> is derived, not stored.</summary>
public sealed record PlatformSubscriptionDto(
    Guid TenantId, string PlanCode, string PlanNameAr, string PlanNameEn, SubscriptionStatus Status, BillingCycle BillingCycle,
    DateTime CurrentPeriodStartUtc, DateTime CurrentPeriodEndUtc, DateTime? TrialEndsAtUtc, bool HasFullAccess, string RowVersion);

/// <summary>
/// Manual billing (D1): the platform admin records what the clinic paid for. <see cref="RowVersion"/> is required when
/// the clinic already has a current subscription (optimistic concurrency) and ignored otherwise.
/// </summary>
public sealed record UpdatePlatformSubscriptionRequest(
    string PlanCode, SubscriptionStatus Status, BillingCycle BillingCycle, DateTime CurrentPeriodEndUtc, string? RowVersion);

public sealed class UpdatePlatformSubscriptionValidator : AbstractValidator<UpdatePlatformSubscriptionRequest>
{
    /// <summary>Sanity bound for a manually entered period end (longest sold cycle is a year; allow some slack).</summary>
    public static readonly TimeSpan MaxPeriodAhead = TimeSpan.FromDays(3 * 366);

    public UpdatePlatformSubscriptionValidator(IClock clock)
    {
        RuleFor(x => x.PlanCode).RequiredText().MaxLen(50);
        RuleFor(x => x.Status).Must(s => Subscription.AdminSettableStatuses.Contains(s)).WithMessage(ValidationCodes.InvalidValue);
        RuleFor(x => x.BillingCycle).IsInEnum().WithMessage(ValidationCodes.InvalidValue);
        RuleFor(x => x.CurrentPeriodEndUtc)
            .Must(end => end > clock.UtcNow && end <= clock.UtcNow.Add(MaxPeriodAhead)).WithMessage(ValidationCodes.OutOfRange);
    }
}

/// <summary>
/// Changes a clinic's plan, status and billing period by hand. Works inside an explicit tenant scope, so the data
/// layer's tenant wall still applies to the write. Every change is audited (old and new plan/status/period).
/// Downgrades never delete or hide data: plan limits only gate new resources.
/// </summary>
public sealed class PlatformSubscriptionService(
    IAppDbContext db,
    ITenantContext tenantContext,
    IClock clock,
    IAuditLogger audit,
    IValidator<UpdatePlatformSubscriptionRequest> validator)
{
    public async Task<PlatformSubscriptionDto?> GetAsync(Guid tenantId, CancellationToken ct)
    {
        await EnsureTenantAsync(tenantId, ct);
        using var _ = tenantContext.UseTenant(tenantId);
        var sub = await CurrentAsync(ct, tracking: false);
        return sub is null ? null : ToDto(sub);
    }

    public async Task<PlatformSubscriptionDto> UpdateAsync(Guid tenantId, UpdatePlatformSubscriptionRequest req, CancellationToken ct)
    {
        await validator.ValidateOrThrowAsync(req, ct);
        await EnsureTenantAsync(tenantId, ct);
        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Code == req.PlanCode.Trim() && p.IsActive, ct)
                   ?? throw new NotFoundException(ErrorCodes.PlanNotFound);

        using var _ = tenantContext.UseTenant(tenantId);
        var now = clock.UtcNow;
        var sub = await CurrentAsync(ct, tracking: true);
        // Enum names, not numbers, so the audit trail reads on its own.
        var before = sub is null ? null : new { plan = sub.Plan!.Code, status = sub.Status.ToString(), cycle = sub.BillingCycle.ToString(), periodEnd = sub.CurrentPeriodEndUtc };

        if (sub is null)
        {
            sub = new Subscription { TenantId = tenantId, PlanId = plan.Id, StartedAtUtc = now, CurrentPeriodStartUtc = now };
            db.Subscriptions.Add(sub);
        }
        else
        {
            db.SetOriginalRowVersion(sub, RowVersions.Decode(req.RowVersion));
            // A new period starts when the plan or the period end changes; a status-only correction keeps the period.
            if (sub.PlanId != plan.Id || sub.CurrentPeriodEndUtc != req.CurrentPeriodEndUtc) sub.CurrentPeriodStartUtc = now;
            sub.PlanId = plan.Id;
        }

        sub.Status = req.Status;
        sub.BillingCycle = req.BillingCycle;
        sub.CurrentPeriodEndUtc = req.CurrentPeriodEndUtc;
        sub.TrialEndsAtUtc = req.Status == SubscriptionStatus.Trialing ? req.CurrentPeriodEndUtc : null;
        sub.CancelAtPeriodEnd = false;
        sub.CanceledAtUtc = null;

        audit.Add(AuditActions.SubscriptionChanged, nameof(Subscription), sub.Id.ToString(), tenantId: tenantId, details: new
        {
            before,
            after = new { plan = plan.Code, status = req.Status.ToString(), cycle = req.BillingCycle.ToString(), periodEnd = req.CurrentPeriodEndUtc },
        });
        await db.SaveChangesAsync(ct);

        return ToDto((await CurrentAsync(ct, tracking: false))!);
    }

    private async Task EnsureTenantAsync(Guid tenantId, CancellationToken ct)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, ct)) throw new NotFoundException(ErrorCodes.TenantNotFound);
    }

    /// <summary>The one current row (Trialing/Active/PastDue — a filtered unique index guarantees at most one).</summary>
    private Task<Subscription?> CurrentAsync(CancellationToken ct, bool tracking)
    {
        var q = db.Subscriptions.Include(s => s.Plan).Where(s => Subscription.CurrentStatuses.Contains(s.Status));
        return (tracking ? q : q.AsNoTracking()).FirstOrDefaultAsync(ct);
    }

    private PlatformSubscriptionDto ToDto(Subscription s) => new(
        s.TenantId, s.Plan!.Code, s.Plan.NameAr, s.Plan.NameEn, s.Status, s.BillingCycle, s.CurrentPeriodStartUtc,
        s.CurrentPeriodEndUtc, s.TrialEndsAtUtc, s.GrantsFullAccess(clock.UtcNow), RowVersions.Encode(s.RowVersion));
}
