namespace ClinicSaaS.Domain.Common;

/// <summary>Base for all entities. Ids are assigned by EF Core (SQL Server sequential GUIDs) on Add.</summary>
public abstract class Entity
{
    public Guid Id { get; set; }
}

/// <summary>Who created/changed the row and when (UTC). Set by the persistence layer, never by clients.</summary>
public interface IAuditable
{
    DateTime CreatedAtUtc { get; set; }
    Guid? CreatedByUserId { get; set; }
    DateTime? UpdatedAtUtc { get; set; }
    Guid? UpdatedByUserId { get; set; }
}

/// <summary>
/// Marks a row that belongs to exactly one tenant. The persistence layer filters every query by the
/// current tenant and force-sets/guards <see cref="TenantId"/> on save — application code never sets it.
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

/// <summary>Rows that are hidden instead of physically deleted (medical/financial history).</summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; set; }
    DateTime? DeletedAtUtc { get; set; }
    Guid? DeletedByUserId { get; set; }
}

/// <summary>Optimistic concurrency via SQL Server rowversion.</summary>
public interface IConcurrencyAware
{
    byte[] RowVersion { get; set; }
}

public abstract class AuditableEntity : Entity, IAuditable
{
    public DateTime CreatedAtUtc { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}

public abstract class TenantEntity : AuditableEntity, ITenantOwned
{
    public Guid TenantId { get; set; }
}
