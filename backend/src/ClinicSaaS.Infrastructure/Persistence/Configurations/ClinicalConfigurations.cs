using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinicSaaS.Infrastructure.Persistence.Configurations;

internal sealed class PatientConfiguration : IEntityTypeConfiguration<Patient>
{
    public void Configure(EntityTypeBuilder<Patient> b)
    {
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.MedicalRecordNumber).HasMaxLength(20).IsUnicode(false);
        b.Property(x => x.FullName).HasMaxLength(200);
        b.Property(x => x.FullNameEn).HasMaxLength(200);
        b.Property(x => x.MobileNumber).HasMaxLength(20).IsUnicode(false);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.IdentityNumber).HasMaxLength(20).IsUnicode(false);
        b.Property(x => x.Nationality).HasMaxLength(2).IsUnicode(false).IsFixedLength();
        b.Property(x => x.Allergies).HasMaxLength(1000);
        b.Property(x => x.ChronicConditions).HasMaxLength(1000);
        b.Property(x => x.MedicalNotes).HasMaxLength(2000);
        b.Property(x => x.Notes).HasMaxLength(1000);

        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Branch>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.RegisteredBranchId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.MedicalRecordNumber }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.MobileNumber });
        b.HasIndex(x => new { x.TenantId, x.FullName });
        b.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
        // One active patient per identity document per clinic.
        b.HasIndex(x => new { x.TenantId, x.IdentityType, x.IdentityNumber }, "UX_Patients_Tenant_Identity").IsUnique()
            .HasFilter("[IdentityNumber] IS NOT NULL AND [IsDeleted] = 0");
        b.HasIndex(x => new { x.TenantId, x.IdentityNumber }).HasFilter("[IdentityNumber] IS NOT NULL");
    }
}

internal sealed class DoctorProfileConfiguration : IEntityTypeConfiguration<DoctorProfile>
{
    public void Configure(EntityTypeBuilder<DoctorProfile> b)
    {
        b.ToTable("DoctorProfiles", t =>
        {
            t.HasCheckConstraint("CK_DoctorProfiles_Fee", "[ConsultationFee] >= 0");
            t.HasCheckConstraint("CK_DoctorProfiles_Slot", "[DefaultSlotMinutes] BETWEEN 5 AND 240");
        });
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.DisplayNameAr).HasMaxLength(200);
        b.Property(x => x.DisplayNameEn).HasMaxLength(200);
        b.Property(x => x.Specialty).HasMaxLength(100);
        b.Property(x => x.LicenseNumber).HasMaxLength(50).IsUnicode(false);
        b.Property(x => x.Bio).HasMaxLength(1000);

        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TenantMembership>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.MembershipId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.MembershipId }).IsUnique().HasFilter("[IsDeleted] = 0");

        b.HasMany(x => x.Schedules).WithOne()
            .HasForeignKey(x => new { x.TenantId, x.DoctorId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DoctorScheduleConfiguration : IEntityTypeConfiguration<DoctorSchedule>
{
    public void Configure(EntityTypeBuilder<DoctorSchedule> b)
    {
        b.ToTable(t =>
        {
            t.HasCheckConstraint("CK_DoctorSchedules_Range", "[EndTime] > [StartTime]");
            t.HasCheckConstraint("CK_DoctorSchedules_Day", "[DayOfWeek] BETWEEN 0 AND 6");
        });
        b.Property(x => x.DayOfWeek).HasConversion<byte>();
        b.Property(x => x.StartTime).HasColumnType("time(0)");
        b.Property(x => x.EndTime).HasColumnType("time(0)");
        b.HasOne<Branch>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.BranchId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.DoctorId, x.DayOfWeek });
    }
}

internal sealed class ClinicServiceConfiguration : IEntityTypeConfiguration<ClinicService>
{
    public void Configure(EntityTypeBuilder<ClinicService> b)
    {
        b.ToTable("ClinicServices", t =>
        {
            t.HasCheckConstraint("CK_ClinicServices_Price", "[Price] >= 0");
            t.HasCheckConstraint("CK_ClinicServices_Duration", "[DurationMinutes] BETWEEN 5 AND 480");
        });
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.NameAr).HasMaxLength(200);
        b.Property(x => x.NameEn).HasMaxLength(200);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.NameAr });
    }
}

internal sealed class AppointmentConfiguration : IEntityTypeConfiguration<Appointment>
{
    public void Configure(EntityTypeBuilder<Appointment> b)
    {
        b.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Appointments_Range", "[EndUtc] > [StartUtc]");
            // The overlap query bounds its index seek with "StartUtc >= newStart - 480 min"; this makes that safe.
            t.HasCheckConstraint("CK_Appointments_MaxDuration", "DATEDIFF(minute, [StartUtc], [EndUtc]) <= 480");
        });
        // Principal key for composite (TenantId, AppointmentId) references from Visits (migration 003), like the
        // other clinical tables. Id is already the primary key, so the constraint can never reject existing rows.
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.TokenNumber).HasMaxLength(10).IsUnicode(false);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.CancellationReason).HasMaxLength(500);

        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        // Every reference is (TenantId, X) — an appointment can never point at another clinic's patient/doctor/branch.
        b.HasOne<Patient>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.PatientId }).HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DoctorProfile>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.DoctorId }).HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Branch>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.BranchId }).HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ClinicService>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.ServiceId }).HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Overlap lookups for the double-booking guard (range on StartUtc, residual on EndUtc/Status).
        b.HasIndex(x => new { x.TenantId, x.DoctorId, x.StartUtc }, "IX_Appointments_Doctor_Start")
            .IncludeProperties(x => new { x.EndUtc, x.Status, x.Source });
        // Deterministic backstop for two concurrent bookings of the exact same slot.
        b.HasIndex(x => new { x.TenantId, x.DoctorId, x.StartUtc }, "UX_Appointments_ActiveSlot").IsUnique()
            .HasFilter("[Status] IN ('Scheduled','Confirmed','CheckedIn') AND [Source] <> 'WalkIn'");
        // Clinic-wide daily board (all doctors, all branches).
        b.HasIndex(x => new { x.TenantId, x.StartUtc }, "IX_Appointments_Tenant_Start").IncludeProperties(x => new { x.Status, x.Source });
        b.HasIndex(x => new { x.TenantId, x.PatientId, x.StartUtc });
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.StartUtc });
    }
}

internal sealed class NumberSequenceConfiguration : IEntityTypeConfiguration<NumberSequence>
{
    public void Configure(EntityTypeBuilder<NumberSequence> b)
    {
        b.HasKey(x => new { x.TenantId, x.Name });
        b.Property(x => x.Name).HasMaxLength(50).IsUnicode(false);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("CK_NumberSequences_NextValue", "[NextValue] >= 1"));
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Action).HasMaxLength(100).IsUnicode(false);
        b.Property(x => x.EntityType).HasMaxLength(100).IsUnicode(false);
        b.Property(x => x.EntityId).HasMaxLength(64).IsUnicode(false);
        b.Property(x => x.IpAddress).HasMaxLength(45).IsUnicode(false);
        b.Property(x => x.UserAgent).HasMaxLength(256);
        b.Property(x => x.CorrelationId).HasMaxLength(64).IsUnicode(false);
        b.Property(x => x.Details).HasMaxLength(2000);
        b.HasIndex(x => new { x.TenantId, x.OccurredAtUtc });
        b.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId });
        b.HasIndex(x => new { x.UserId, x.OccurredAtUtc });
    }
}
