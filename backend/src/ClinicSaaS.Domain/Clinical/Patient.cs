using ClinicSaaS.Domain.Common;

namespace ClinicSaaS.Domain.Clinical;

public enum Gender { Male, Female }
public enum IdentityDocumentType { None, NationalId, Iqama, Passport, GccId }
public enum BloodGroup { APositive, ANegative, BPositive, BNegative, ABPositive, ABNegative, OPositive, ONegative }

public sealed class Patient : TenantEntity, ISoftDeletable, IConcurrencyAware
{
    /// <summary>Per-clinic medical record number, gap-free from the tenant's number sequence (e.g. P-000001).</summary>
    public required string MedicalRecordNumber { get; set; }
    public required string FullName { get; set; }
    public string? FullNameEn { get; set; }
    /// <summary>E.164 (+9665XXXXXXXX).</summary>
    public required string MobileNumber { get; set; }
    public string? Email { get; set; }
    public IdentityDocumentType IdentityType { get; set; } = IdentityDocumentType.None;
    public string? IdentityNumber { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public Gender Gender { get; set; }
    /// <summary>ISO 3166-1 alpha-2 (SA, EG, ...). Matters for VAT: Saudi citizens are zero-rated for private healthcare.</summary>
    public string? Nationality { get; set; }
    public Guid? RegisteredBranchId { get; set; }

    // --- sensitive clinical fields: readable only with MedicalRecords.View ---
    public BloodGroup? BloodGroup { get; set; }
    public string? Allergies { get; set; }
    public string? ChronicConditions { get; set; }
    public string? MedicalNotes { get; set; }

    /// <summary>Administrative notes (non-clinical), visible to front desk.</summary>
    public string? Notes { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public static string FormatMrn(long sequence) => $"P-{sequence:D6}";
}
