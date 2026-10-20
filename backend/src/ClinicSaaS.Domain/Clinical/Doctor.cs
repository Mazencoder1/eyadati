using ClinicSaaS.Domain.Common;

namespace ClinicSaaS.Domain.Clinical;

/// <summary>Doctor-specific profile for a clinic member (kept apart from the login identity).</summary>
public sealed class DoctorProfile : TenantEntity, ISoftDeletable, IConcurrencyAware
{
    public Guid MembershipId { get; set; }
    public required string DisplayNameAr { get; set; }
    public string? DisplayNameEn { get; set; }
    public required string Specialty { get; set; }
    /// <summary>SCFHS professional license number (الهيئة السعودية للتخصصات الصحية).</summary>
    public string? LicenseNumber { get; set; }
    public string? Bio { get; set; }
    public decimal ConsultationFee { get; set; }
    public int DefaultSlotMinutes { get; set; } = 15;
    public bool AcceptsOnlineBooking { get; set; } = true;
    public bool IsActive { get; set; } = true;

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<DoctorSchedule> Schedules { get; set; } = [];
}

/// <summary>
/// One working window for a doctor at a branch on a weekday (clinic-local time). Multiple windows per day are
/// allowed (e.g. 09:00–13:00 and 16:00–21:00, common for Saudi clinics).
/// </summary>
public sealed class DoctorSchedule : TenantEntity
{
    public Guid DoctorId { get; set; }
    public Guid BranchId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }

    public bool Covers(DayOfWeek day, TimeOnly start, TimeOnly end) =>
        day == DayOfWeek && start >= StartTime && end <= EndTime && end > start;
}

/// <summary>A bookable/billable clinic service (consultation, cleaning, X-ray…).</summary>
public sealed class ClinicService : TenantEntity, ISoftDeletable, IConcurrencyAware
{
    public required string NameAr { get; set; }
    public string? NameEn { get; set; }
    public int DurationMinutes { get; set; } = 15;
    /// <summary>Price excluding VAT.</summary>
    public decimal Price { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsBookableOnline { get; set; } = true;

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
