using ClinicSaaS.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinicSaaS.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Slug).HasMaxLength(63).IsUnicode(false);
        b.HasIndex(x => x.Slug).IsUnique();
        b.Property(x => x.LegalName).HasMaxLength(200);
        b.Property(x => x.VatNumber).HasMaxLength(15).IsUnicode(false);
        b.Property(x => x.CommercialRegistration).HasMaxLength(20).IsUnicode(false);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Phone).HasMaxLength(20).IsUnicode(false);
        b.Property(x => x.City).HasMaxLength(100);
        b.Property(x => x.TimeZone).HasMaxLength(64).IsUnicode(false);
        b.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false).IsFixedLength();
        b.Property(x => x.Locale).HasMaxLength(10).IsUnicode(false);
        b.HasIndex(x => x.Status);
        b.ToTable(t => t.HasCheckConstraint("CK_Tenants_DefaultAppointmentMinutes", "[DefaultAppointmentMinutes] BETWEEN 5 AND 240"));
    }
}

internal sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.City).HasMaxLength(100);
        b.Property(x => x.Address).HasMaxLength(500);
        b.Property(x => x.Phone).HasMaxLength(20).IsUnicode(false);
        b.Property(x => x.Latitude).HasPrecision(9, 6);
        b.Property(x => x.Longitude).HasPrecision(9, 6);
        // Principal for composite (TenantId, BranchId) foreign keys.
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.HasOne<Tenant>().WithMany(t => t.Branches).HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> b)
    {
        b.Property(x => x.Code).HasMaxLength(50).IsUnicode(false);
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.NameAr).HasMaxLength(100);
        b.Property(x => x.NameEn).HasMaxLength(100);
        b.Property(x => x.DescriptionAr).HasMaxLength(500);
        b.Property(x => x.DescriptionEn).HasMaxLength(500);
        b.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false).IsFixedLength();
        b.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Plans_Prices", "[MonthlyPrice] >= 0 AND [YearlyPrice] >= 0");
            t.HasCheckConstraint("CK_Plans_TrialDays", "[TrialDays] BETWEEN 0 AND 90");
        });

        b.HasMany(x => x.Features).WithOne().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Limits).WithOne().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PlanFeatureConfiguration : IEntityTypeConfiguration<PlanFeature>
{
    public void Configure(EntityTypeBuilder<PlanFeature> b)
    {
        b.ToTable("PlanFeatures");
        b.HasKey(x => new { x.PlanId, x.FeatureKey });
        b.Property(x => x.FeatureKey).HasMaxLength(64).IsUnicode(false);
    }
}

internal sealed class PlanLimitConfiguration : IEntityTypeConfiguration<PlanLimit>
{
    public void Configure(EntityTypeBuilder<PlanLimit> b)
    {
        b.HasKey(x => new { x.PlanId, x.LimitKey });
        b.Property(x => x.LimitKey).HasMaxLength(64).IsUnicode(false);
        b.ToTable("PlanLimits", t => t.HasCheckConstraint("CK_PlanLimits_MaxValue", "[MaxValue] IS NULL OR [MaxValue] >= 0"));
    }
}

internal sealed class TenantFeatureOverrideConfiguration : IEntityTypeConfiguration<TenantFeatureOverride>
{
    public void Configure(EntityTypeBuilder<TenantFeatureOverride> b)
    {
        b.HasKey(x => new { x.TenantId, x.FeatureKey });
        b.Property(x => x.FeatureKey).HasMaxLength(64).IsUnicode(false);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> b)
    {
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Status });
        // At most one subscription that grants access per tenant.
        b.HasIndex(x => x.TenantId, "UX_Subscriptions_Tenant_Current").IsUnique()
            .HasFilter("[Status] IN ('Trialing','Active','PastDue')");
        // Renewal / expiry jobs scan by period end.
        b.HasIndex(x => new { x.Status, x.CurrentPeriodEndUtc });
        b.ToTable(t => t.HasCheckConstraint("CK_Subscriptions_Period", "[CurrentPeriodEndUtc] > [CurrentPeriodStartUtc]"));
    }
}
