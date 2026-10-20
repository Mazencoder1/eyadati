using ClinicSaaS.Domain.Common;
using ClinicSaaS.Domain.Platform;

namespace ClinicSaaS.Domain.Identity;

public enum MembershipStatus { Invited, Active, Disabled }

/// <summary>A user's seat inside one clinic: their roles and (optionally) the branches they may work in.</summary>
public sealed class TenantMembership : TenantEntity, IConcurrencyAware
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Tenant? Tenant { get; set; }
    public MembershipStatus Status { get; set; } = MembershipStatus.Active;
    /// <summary>The clinic owner. Cannot be removed or demoted by other members.</summary>
    public bool IsOwner { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<MembershipRole> Roles { get; set; } = [];
    /// <summary>Empty = access to all branches.</summary>
    public ICollection<MembershipBranch> Branches { get; set; } = [];
}

public sealed class MembershipRole : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid MembershipId { get; set; }
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
}

public sealed class MembershipBranch : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid MembershipId { get; set; }
    public Guid BranchId { get; set; }
}
