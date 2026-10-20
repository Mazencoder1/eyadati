using ClinicSaaS.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinicSaaS.Infrastructure.Persistence.Configurations;

/// <summary>Platform-owned (no TenantId): MFA belongs to the account, not to a clinic. At most one factor per user.</summary>
internal sealed class UserTotpFactorConfiguration : IEntityTypeConfiguration<UserTotpFactor>
{
    public void Configure(EntityTypeBuilder<UserTotpFactor> b)
    {
        b.Property(x => x.SecretProtected).HasColumnType("varbinary(256)");
        b.HasIndex(x => x.UserId).IsUnique();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UserRecoveryCodeConfiguration : IEntityTypeConfiguration<UserRecoveryCode>
{
    public void Configure(EntityTypeBuilder<UserRecoveryCode> b)
    {
        b.Property(x => x.CodeHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
        b.HasIndex(x => new { x.UserId, x.CodeHash }).IsUnique();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
