using ClinicSaaS.Domain.Common;

namespace ClinicSaaS.Domain.Platform;

public sealed class Branch : TenantEntity, ISoftDeletable, IConcurrencyAware
{
    public required string Name { get; set; }
    public string? City { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public bool IsMain { get; set; }
    public bool IsActive { get; set; } = true;

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
