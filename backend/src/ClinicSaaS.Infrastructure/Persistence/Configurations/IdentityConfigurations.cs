using ClinicSaaS.Domain.Identity;
using ClinicSaaS.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinicSaaS.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.NormalizedEmail).HasMaxLength(256);
        b.HasIndex(x => x.NormalizedEmail).IsUnique();
        b.Property(x => x.PasswordHash).HasMaxLength(512).IsUnicode(false);
        b.Property(x => x.FullName).HasMaxLength(200);
        b.Property(x => x.PhoneNumber).HasMaxLength(20).IsUnicode(false);
        b.Property(x => x.SecurityStamp).HasMaxLength(64).IsUnicode(false);
        b.Property(x => x.PreferredLocale).HasMaxLength(10).IsUnicode(false);
        b.HasIndex(x => x.PlatformRole).HasFilter("[PlatformRole] <> 'None'");
    }
}

internal sealed class TenantMembershipConfiguration : IEntityTypeConfiguration<TenantMembership>
{
    public void Configure(EntityTypeBuilder<TenantMembership> b)
    {
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
        b.HasIndex(x => x.UserId);
        b.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.User).WithMany(u => u.Memberships).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.Roles).WithOne()
            .HasForeignKey(x => new { x.TenantId, x.MembershipId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Branches).WithOne()
            .HasForeignKey(x => new { x.TenantId, x.MembershipId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MembershipRoleConfiguration : IEntityTypeConfiguration<MembershipRole>
{
    public void Configure(EntityTypeBuilder<MembershipRole> b)
    {
        b.ToTable("MembershipRoles");
        b.HasKey(x => new { x.MembershipId, x.RoleId });
        // Composite FK: the role must belong to the same tenant as the membership — enforced by SQL Server.
        b.HasOne(x => x.Role).WithMany()
            .HasForeignKey(x => new { x.TenantId, x.RoleId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MembershipBranchConfiguration : IEntityTypeConfiguration<MembershipBranch>
{
    public void Configure(EntityTypeBuilder<MembershipBranch> b)
    {
        b.ToTable("MembershipBranches");
        b.HasKey(x => new { x.MembershipId, x.BranchId });
        b.HasOne<Branch>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.BranchId })
            .HasPrincipalKey(br => new { br.TenantId, br.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.NormalizedName).HasMaxLength(100);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.SystemKey).HasMaxLength(50).IsUnicode(false);
        b.HasIndex(x => new { x.TenantId, x.NormalizedName }).IsUnique();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Permissions).WithOne()
            .HasForeignKey(x => new { x.TenantId, x.RoleId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.ToTable("RolePermissions");
        b.HasKey(x => new { x.RoleId, x.Permission });
        b.Property(x => x.Permission).HasMaxLength(100).IsUnicode(false);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.Property(x => x.TokenHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.FamilyId);
        b.HasIndex(x => new { x.UserId, x.RevokedAtUtc });
        // Cleanup job deletes expired rows by expiry.
        b.HasIndex(x => x.ExpiresAtUtc);
        b.Property(x => x.CreatedByIp).HasMaxLength(45).IsUnicode(false);
        b.Property(x => x.UserAgent).HasMaxLength(256);
        b.Property(x => x.RevokedReason).HasMaxLength(50).IsUnicode(false);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class VerificationCodeConfiguration : IEntityTypeConfiguration<VerificationCode>
{
    public void Configure(EntityTypeBuilder<VerificationCode> b)
    {
        b.Property(x => x.CodeHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
        b.HasIndex(x => x.CodeHash).IsUnique();
        b.HasIndex(x => new { x.UserId, x.Purpose });
        b.Property(x => x.Target).HasMaxLength(256);
        b.Property(x => x.CreatedByIp).HasMaxLength(45).IsUnicode(false);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
