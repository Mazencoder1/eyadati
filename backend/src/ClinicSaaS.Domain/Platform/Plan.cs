using ClinicSaaS.Domain.Common;

namespace ClinicSaaS.Domain.Platform;

/// <summary>A subscription plan. Plans, their features and limits are data (editable by super admin), never hard-coded.</summary>
public sealed class Plan : AuditableEntity, IConcurrencyAware
{
    public required string Code { get; set; }
    public required string NameAr { get; set; }
    public required string NameEn { get; set; }
    public string? DescriptionAr { get; set; }
    public string? DescriptionEn { get; set; }
    /// <summary>Prices exclude VAT; VAT is applied on the platform invoice.</summary>
    public decimal MonthlyPrice { get; set; }
    public decimal YearlyPrice { get; set; }
    public string Currency { get; set; } = Tenant.DefaultCurrency;
    public int TrialDays { get; set; }
    public bool IsPublic { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<PlanFeature> Features { get; set; } = [];
    public ICollection<PlanLimit> Limits { get; set; } = [];
}

/// <summary>Presence of a row = the feature is included in the plan.</summary>
public sealed class PlanFeature
{
    public Guid PlanId { get; set; }
    public required string FeatureKey { get; set; }
}

public sealed class PlanLimit
{
    public Guid PlanId { get; set; }
    public required string LimitKey { get; set; }
    /// <summary>null = unlimited.</summary>
    public int? MaxValue { get; set; }
}

/// <summary>Per-tenant switch that wins over the plan (grant a beta feature, or disable one).</summary>
public sealed class TenantFeatureOverride : ITenantOwned
{
    public Guid TenantId { get; set; }
    public required string FeatureKey { get; set; }
    public bool IsEnabled { get; set; }
}

/// <summary>Feature keys referenced by code (the catalog). Which plan has which is data.</summary>
public static class FeatureKeys
{
    public const string Invoicing = "invoicing";
    public const string Reports = "reports";
    public const string SmsNotifications = "sms";
    public const string WhatsAppNotifications = "whatsapp";
    public const string MultipleBranches = "multi_branch";
    public const string OnlineBooking = "online_booking";
    public const string PublicPageCustomization = "public_page_custom";
    public const string ApiAccess = "api_access";
    public const string AuditLogs = "audit_logs";
    public const string Export = "export";

    public static readonly IReadOnlyList<string> All =
        [Invoicing, Reports, SmsNotifications, WhatsAppNotifications, MultipleBranches, OnlineBooking,
         PublicPageCustomization, ApiAccess, AuditLogs, Export];

    /// <summary>
    /// Features that exist in the product and are gated by plan — the only ones the catalog and the subscription
    /// view show. The other keys are planned capabilities (rows may exist in plan data) and are never advertised.
    /// Audit logs are not a plan feature: their visibility is a core capability for every clinic (D13).
    /// </summary>
    public static readonly IReadOnlyList<string> Offered = [MultipleBranches];
}

public static class LimitKeys
{
    public const string MaxUsers = "max_users";
    public const string MaxDoctors = "max_doctors";
    public const string MaxPatients = "max_patients";
    public const string MaxAppointmentsPerMonth = "max_appointments_month";
    public const string MaxBranches = "max_branches";
    public const string MaxStorageMb = "max_storage_mb";
    public const string MaxSmsPerMonth = "max_sms_month";
    public const string MaxWhatsAppPerMonth = "max_whatsapp_month";

    public static readonly IReadOnlyList<string> All =
        [MaxUsers, MaxDoctors, MaxPatients, MaxAppointmentsPerMonth, MaxBranches, MaxStorageMb, MaxSmsPerMonth, MaxWhatsAppPerMonth];

    /// <summary>Limits the application enforces — the only ones the catalog shows (storage/SMS/WhatsApp don't exist yet).</summary>
    public static readonly IReadOnlyList<string> Enforced = [MaxUsers, MaxDoctors, MaxPatients, MaxAppointmentsPerMonth, MaxBranches];
}
