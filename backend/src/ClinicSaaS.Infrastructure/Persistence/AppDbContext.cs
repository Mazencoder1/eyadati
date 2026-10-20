using System.Data;
using System.Linq.Expressions;
using System.Reflection;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ClinicSaaS.Infrastructure.Persistence;

/// <summary>
/// A write that would touch another tenant's row, or a tenant-owned row without a tenant scope. This is a
/// programming error or an attack — never a user error — so it is logged loudly and surfaced as 404.
/// </summary>
public sealed class TenantIsolationViolationException(string message) : Exception(message);

public sealed class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IClock clock) : DbContext(options), IAppDbContext
{
    /// <summary>Read by the tenant query filter on every query (EF parameterizes it per context instance).</summary>
    public Guid? CurrentTenantId => tenantContext.TenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<TenantFeatureOverride> TenantFeatureOverrides => Set<TenantFeatureOverride>();
    public DbSet<User> Users => Set<User>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<VerificationCode> VerificationCodes => Set<VerificationCode>();
    public DbSet<UserTotpFactor> UserTotpFactors => Set<UserTotpFactor>();
    public DbSet<UserRecoveryCode> UserRecoveryCodes => Set<UserRecoveryCode>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<DoctorProfile> Doctors => Set<DoctorProfile>();
    public DbSet<DoctorSchedule> DoctorSchedules => Set<DoctorSchedule>();
    public DbSet<ClinicService> Services => Set<ClinicService>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<NumberSequence> NumberSequences => Set<NumberSequence>();
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<Prescription> Prescriptions => Set<Prescription>();
    public DbSet<FileAttachment> FileAttachments => Set<FileAttachment>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // No nvarchar(max) by accident: every string gets a bound unless a configuration says otherwise.
        builder.Properties<string>().HaveMaxLength(256);
        builder.Properties<decimal>().HavePrecision(18, 2);
        // Readable enum columns in SQL (and stable if enum members are reordered).
        builder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
        // All instants are UTC: stored as datetime2, materialized with Kind=Utc.
        builder.Properties<DateTime>().HaveConversion<UtcConverter>().HaveColumnType("datetime2(3)");
        builder.Properties<DateTime?>().HaveConversion<NullableUtcConverter>().HaveColumnType("datetime2(3)");
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        foreach (var entityType in model.Model.GetEntityTypes().Where(t => t.BaseType is null && !t.IsOwned()))
        {
            var clr = entityType.ClrType;
            var builder = model.Entity(clr);

            if (typeof(ITenantOwned).IsAssignableFrom(clr))
            {
                builder.HasQueryFilter(QueryFilters.Tenant, (LambdaExpression)TenantFilterMethod.MakeGenericMethod(clr).Invoke(this, null)!);
                // Every UPDATE/DELETE becomes "WHERE Id = @id AND TenantId = @tenant". GuardTenant pins @tenant to the
                // current tenant, so the row's *stored* TenantId — not the entity the caller handed us — decides whether
                // the write lands. A forged entity carrying another tenant's id affects 0 rows. Model-only: no schema change.
                builder.Property(nameof(ITenantOwned.TenantId)).IsConcurrencyToken();
            }
            if (typeof(ISoftDeletable).IsAssignableFrom(clr))
            {
                builder.HasQueryFilter(QueryFilters.SoftDelete, (LambdaExpression)SoftDeleteFilterMethod.MakeGenericMethod(clr).Invoke(null, null)!);
                builder.Property(nameof(ISoftDeletable.IsDeleted)).HasDefaultValue(false);
            }
            if (typeof(IConcurrencyAware).IsAssignableFrom(clr))
            {
                builder.Property(nameof(IConcurrencyAware.RowVersion)).IsRowVersion();
            }
        }
    }

    private static readonly MethodInfo TenantFilterMethod =
        typeof(AppDbContext).GetMethod(nameof(TenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo SoftDeleteFilterMethod =
        typeof(AppDbContext).GetMethod(nameof(SoftDeleteFilter), BindingFlags.NonPublic | BindingFlags.Static)!;

    // No tenant in scope ⇒ CurrentTenantId is null ⇒ the predicate matches nothing (deny by default).
    private Expression<Func<T, bool>> TenantFilter<T>() where T : class, ITenantOwned =>
        e => e.TenantId == CurrentTenantId;

    private static Expression<Func<T, bool>> SoftDeleteFilter<T>() where T : class, ISoftDeletable =>
        e => !e.IsDeleted;

    // ------------------------------------------------------------------ save pipeline

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplySaveRules();
        try
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            ThrowIfCrossTenant(ex, entry => entry.GetDatabaseValues());
            throw;
        }
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        ApplySaveRules();
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Async lookup of the stored rows, then the same decision as the sync path.
            var stored = new Dictionary<EntityEntry, PropertyValues?>();
            foreach (var entry in ex.Entries.Where(e => e.Entity is ITenantOwned))
                stored[entry] = await entry.GetDatabaseValuesAsync(ct);
            ThrowIfCrossTenant(ex, entry => stored.GetValueOrDefault(entry));
            throw;
        }
    }

    /// <summary>
    /// A tenant-owned write matched no row. If the row exists under another tenant, the caller targeted a foreign
    /// record (forged/attached entity, IDOR): report it as an isolation violation (404, logged) rather than as an
    /// ordinary edit conflict. A row of the current tenant with a newer rowversion stays a 409 concurrency conflict,
    /// and a row that no longer exists stays a concurrency conflict too (indistinguishable from a concurrent delete).
    /// </summary>
    private void ThrowIfCrossTenant(DbUpdateConcurrencyException ex, Func<EntityEntry, PropertyValues?> storedValues)
    {
        foreach (var entry in ex.Entries.Where(e => e.Entity is ITenantOwned))
        {
            var stored = storedValues(entry);
            if (stored is not null && stored.GetValue<Guid>(nameof(ITenantOwned.TenantId)) != CurrentTenantId)
                throw new TenantIsolationViolationException($"{entry.State} of {entry.Metadata.ClrType.Name} belonging to another tenant.");
        }
    }

    /// <summary>
    /// The write-side tenant wall: force-set TenantId on insert, refuse any insert/update/delete of a row that
    /// belongs to another tenant, forbid moving rows between tenants. Also stamps audit columns and turns
    /// deletes of soft-deletable rows into updates.
    /// </summary>
    private void ApplySaveRules()
    {
        var now = clock.UtcNow;
        var userId = currentUser.UserId;
        var tenantId = CurrentTenantId;

        foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
        {
            if (entry.Entity is ITenantOwned owned) GuardTenant(entry, owned, tenantId);

            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDeletable soft)
            {
                entry.State = EntityState.Modified;
                soft.IsDeleted = true;
                soft.DeletedAtUtc = now;
                soft.DeletedByUserId = userId;
            }

            if (entry.Entity is IAuditable auditable)
            {
                if (entry.State == EntityState.Added)
                {
                    auditable.CreatedAtUtc = now;
                    auditable.CreatedByUserId ??= userId;
                }
                else if (entry.State == EntityState.Modified)
                {
                    entry.Property(nameof(IAuditable.CreatedAtUtc)).IsModified = false;
                    entry.Property(nameof(IAuditable.CreatedByUserId)).IsModified = false;
                    auditable.UpdatedAtUtc = now;
                    auditable.UpdatedByUserId = userId;
                }
            }
        }
    }

    private static void GuardTenant(EntityEntry entry, ITenantOwned owned, Guid? tenantId)
    {
        var type = entry.Metadata.ClrType.Name;
        if (tenantId is null)
            throw new TenantIsolationViolationException($"{entry.State} {type} without a tenant scope.");

        if (entry.State == EntityState.Added)
        {
            if (owned.TenantId == Guid.Empty) owned.TenantId = tenantId.Value;
            else if (owned.TenantId != tenantId) throw new TenantIsolationViolationException($"Insert of {type} into another tenant.");
            return;
        }

        var tenantProp = entry.Property(nameof(ITenantOwned.TenantId));
        if ((Guid)tenantProp.OriginalValue! != tenantId || owned.TenantId != tenantId)
            throw new TenantIsolationViolationException($"{entry.State} of {type} belonging to another tenant.");
    }

    // ------------------------------------------------------------------ IAppDbContext helpers

    Task<int> IAppDbContext.SaveChangesAsync(CancellationToken ct) => SaveChangesAsync(ct);

    public async Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, IsolationLevel isolation = IsolationLevel.ReadCommitted, CancellationToken ct = default)
    {
        if (Database.CurrentTransaction is not null) return await operation(ct);

        await using var tx = await Database.BeginTransactionAsync(isolation, ct);
        var result = await operation(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task AcquireLockAsync(string resource, CancellationToken ct = default)
    {
        if (Database.CurrentTransaction is null)
            throw new InvalidOperationException("Application locks must be taken inside a transaction.");
        var result = await Database.SqlQuery<int>($"""
            DECLARE @r int;
            EXEC @r = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
            SELECT @r AS [Value];
            """).ToListAsync(ct);
        if (result.Count != 1 || result[0] < 0) throw new TimeoutException($"Could not acquire lock '{resource}'.");
    }

    public void SetOriginalRowVersion<T>(T entity, byte[] rowVersion) where T : class, IConcurrencyAware =>
        Entry(entity).Property(e => e.RowVersion).OriginalValue = rowVersion;

    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}
