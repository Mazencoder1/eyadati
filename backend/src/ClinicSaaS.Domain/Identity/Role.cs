using ClinicSaaS.Domain.Common;

namespace ClinicSaaS.Domain.Identity;

/// <summary>A tenant's role. Created from system templates at signup; the clinic may customize permissions.</summary>
public sealed class Role : TenantEntity, IConcurrencyAware
{
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public string? Description { get; set; }
    /// <summary>Template key this role was created from (e.g. "receptionist"); null for custom roles.</summary>
    public string? SystemKey { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<RolePermission> Permissions { get; set; } = [];
}

public sealed class RolePermission : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid RoleId { get; set; }
    public required string Permission { get; set; }
}
