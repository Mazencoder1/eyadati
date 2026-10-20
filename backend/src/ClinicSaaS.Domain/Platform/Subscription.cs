using ClinicSaaS.Domain.Common;

namespace ClinicSaaS.Domain.Platform;

public enum SubscriptionStatus { Trialing, Active, PastDue, Canceled, Expired }
public enum BillingCycle { Monthly, Yearly }

public sealed class Subscription : TenantEntity, IConcurrencyAware
{
    public Guid PlanId { get; set; }
    public Plan? Plan { get; set; }
    public SubscriptionStatus Status { get; set; }
    public BillingCycle BillingCycle { get; set; } = BillingCycle.Monthly;
    public DateTime StartedAtUtc { get; set; }
    public DateTime CurrentPeriodStartUtc { get; set; }
    public DateTime CurrentPeriodEndUtc { get; set; }
    public DateTime? TrialEndsAtUtc { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public DateTime? CanceledAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    /// <summary>Statuses that grant access to the plan's features. Only one such row may exist per tenant.</summary>
    public static readonly SubscriptionStatus[] CurrentStatuses =
        [SubscriptionStatus.Trialing, SubscriptionStatus.Active, SubscriptionStatus.PastDue];

    public bool IsCurrent => CurrentStatuses.Contains(Status);

    /// <summary>
    /// Statuses a platform admin may set by hand under manual billing (Phase 2, D1). Canceled/Expired are never set:
    /// expiry is derived from the period end (<see cref="GrantsFullAccess"/>), and ending access early is a suspension.
    /// </summary>
    public static readonly SubscriptionStatus[] AdminSettableStatuses =
        [SubscriptionStatus.Trialing, SubscriptionStatus.Active, SubscriptionStatus.PastDue];

    /// <summary>
    /// Full (read-write) access: a current status and a period that has not ended. A clinic whose period ended is
    /// read-only until a platform admin extends it — nothing is deleted and records stay readable.
    /// </summary>
    public bool GrantsFullAccess(DateTime nowUtc) => IsCurrent && nowUtc < CurrentPeriodEndUtc;

    public static Subscription StartTrial(Guid tenantId, Plan plan, DateTime nowUtc)
    {
        var trial = plan.TrialDays > 0;
        var periodEnd = trial ? nowUtc.AddDays(plan.TrialDays) : nowUtc.AddMonths(1);
        return new Subscription
        {
            TenantId = tenantId,
            PlanId = plan.Id,
            Status = trial ? SubscriptionStatus.Trialing : SubscriptionStatus.Active,
            StartedAtUtc = nowUtc,
            CurrentPeriodStartUtc = nowUtc,
            CurrentPeriodEndUtc = periodEnd,
            TrialEndsAtUtc = trial ? periodEnd : null,
        };
    }
}
