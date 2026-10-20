using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Authorization;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Domain.Identity;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.Application.Staff;

public sealed record RoleDto(
    Guid Id, string Name, string? Description, string? SystemKey, bool IsProtected, IReadOnlyList<string> Permissions,
    int MemberCount, string RowVersion);

public sealed record SaveRoleRequest(string Name, string? Description, IReadOnlyList<string> Permissions, string? RowVersion = null);

public sealed record PermissionGroup(string Group, IReadOnlyList<string> Permissions);

public sealed class SaveRoleValidator : AbstractValidator<SaveRoleRequest>
{
    public SaveRoleValidator()
    {
        RuleFor(x => x.Name).RequiredText().MaxLen(100);
        RuleFor(x => x.Description).MaxLen(500);
        RuleFor(x => x.Permissions).NotNull().WithMessage(ValidationCodes.Required);
        RuleForEach(x => x.Permissions).Must(Permissions.IsTenantPermission).WithMessage(ValidationCodes.InvalidValue);
    }
}

/// <summary>
/// Per-clinic role management. Two invariants: the owner role is immutable, and nobody can grant a permission
/// they don't hold themselves (no privilege escalation through role editing).
/// </summary>
public sealed class RoleService(IAppDbContext db, ICurrentUser user, IClock clock, IAuditLogger audit, IValidator<SaveRoleRequest> validator)
{
    public static IReadOnlyList<PermissionGroup> Catalog() =>
        Permissions.AllTenant.GroupBy(p => p.Split('.')[0]).Select(g => new PermissionGroup(g.Key, g.ToList())).ToList();

    public async Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken ct)
    {
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions).OrderBy(r => r.SystemKey == null).ThenBy(r => r.Name).ToListAsync(ct);
        var counts = await db.TenantMemberships.SelectMany(m => m.Roles).GroupBy(r => r.RoleId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return roles.Select(r => ToDto(r, counts.GetValueOrDefault(r.Id))).ToList();
    }

    public async Task<RoleDto> CreateAsync(SaveRoleRequest req, CancellationToken ct)
    {
        await validator.ValidateOrThrowAsync(req, ct);
        EnsureNoEscalation(req.Permissions);
        var normalized = Normalize(req.Name);
        if (await db.Roles.AnyAsync(r => r.NormalizedName == normalized, ct)) throw new ConflictException(ErrorCodes.RoleNameTaken);

        var role = new Role { Name = req.Name.Trim(), NormalizedName = normalized, Description = Clean(req.Description) };
        foreach (var p in req.Permissions.Distinct()) role.Permissions.Add(new RolePermission { Permission = p });
        db.Roles.Add(role);
        audit.Add(AuditActions.RoleCreated, nameof(Role), role.Id.ToString(), details: new { permissions = role.Permissions.Count });
        await db.SaveChangesAsync(ct);
        return ToDto(role, 0);
    }

    public async Task<RoleDto> UpdateAsync(Guid id, SaveRoleRequest req, CancellationToken ct)
    {
        await validator.ValidateOrThrowAsync(req, ct);
        var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, ct)
                   ?? throw new NotFoundException(ErrorCodes.RoleNotFound);
        if (role.SystemKey == SystemRoles.TenantOwner) throw new ConflictException(ErrorCodes.SystemRoleProtected);
        db.SetOriginalRowVersion(role, RowVersions.Decode(req.RowVersion));

        var current = role.Permissions.Select(p => p.Permission).ToHashSet();
        var requested = req.Permissions.Distinct().ToHashSet();
        // Only *changes* must be within the actor's own permissions (removing is also a privileged change).
        EnsureNoEscalation(current.Except(requested).Concat(requested.Except(current)));

        var normalized = Normalize(req.Name);
        if (normalized != role.NormalizedName && await db.Roles.AnyAsync(r => r.NormalizedName == normalized && r.Id != id, ct))
            throw new ConflictException(ErrorCodes.RoleNameTaken);

        role.Name = req.Name.Trim();
        role.NormalizedName = role.SystemKey is null ? normalized : role.NormalizedName;
        role.Description = Clean(req.Description);
        foreach (var removed in role.Permissions.Where(p => !requested.Contains(p.Permission)).ToList()) role.Permissions.Remove(removed);
        foreach (var added in requested.Except(current)) role.Permissions.Add(new RolePermission { Permission = added });
        // Touch the row so its rowversion advances even when only child rows changed.
        role.UpdatedAtUtc = clock.UtcNow;

        audit.Add(AuditActions.RoleChanged, nameof(Role), id.ToString(),
            details: new { added = requested.Except(current).ToList(), removed = current.Except(requested).ToList() });
        await db.SaveChangesAsync(ct);
        return ToDto(role, await db.TenantMemberships.SelectMany(m => m.Roles).CountAsync(r => r.RoleId == id, ct));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException(ErrorCodes.RoleNotFound);
        if (role.SystemKey is not null) throw new ConflictException(ErrorCodes.SystemRoleProtected);
        if (await db.TenantMemberships.SelectMany(m => m.Roles).AnyAsync(r => r.RoleId == id, ct)) throw new ConflictException(ErrorCodes.RoleInUse);
        db.Roles.Remove(role);
        audit.Add(AuditActions.RoleDeleted, nameof(Role), id.ToString());
        await db.SaveChangesAsync(ct);
    }

    private void EnsureNoEscalation(IEnumerable<string> permissions)
    {
        if (permissions.Any(p => !user.HasPermission(p))) throw new ForbiddenException(ErrorCodes.PermissionEscalation);
    }

    private static string Normalize(string name) => name.Trim().ToUpperInvariant();
    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static RoleDto ToDto(Role r, int members) => new(r.Id, r.Name, r.Description, r.SystemKey,
        r.SystemKey == SystemRoles.TenantOwner, r.Permissions.Select(p => p.Permission).Order().ToList(), members,
        RowVersions.Encode(r.RowVersion));
}
