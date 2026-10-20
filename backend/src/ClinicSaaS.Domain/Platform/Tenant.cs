using ClinicSaaS.Domain.Common;

namespace ClinicSaaS.Domain.Platform;

public enum TenantStatus { PendingVerification, Active, Suspended, Closed }

/// <summary>A clinic / medical center — the unit of isolation. Has branches, members, patients…</summary>
public sealed class Tenant : AuditableEntity, IConcurrencyAware
{
    public const string DefaultTimeZone = "Asia/Riyadh";
    public const string DefaultCurrency = "SAR";
    public const string DefaultLocale = "ar";

    public required string Name { get; set; }
    /// <summary>URL handle for the public clinic page (clinic.domain.sa/{slug}). Lowercase a-z, 0-9, '-'.</summary>
    public required string Slug { get; set; }
    public TenantStatus Status { get; set; } = TenantStatus.Active;

    public string? LegalName { get; set; }
    /// <summary>ZATCA VAT registration number (15 digits, starts and ends with 3).</summary>
    public string? VatNumber { get; set; }
    /// <summary>Commercial registration (السجل التجاري).</summary>
    public string? CommercialRegistration { get; set; }

    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? City { get; set; }

    public string TimeZone { get; set; } = DefaultTimeZone;
    public string Currency { get; set; } = DefaultCurrency;
    public string Locale { get; set; } = DefaultLocale;
    public int DefaultAppointmentMinutes { get; set; } = 15;

    public byte[] RowVersion { get; set; } = [];

    public ICollection<Branch> Branches { get; set; } = [];
}
