using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinicSaaS.Infrastructure.Persistence.Configurations;

/// <summary>
/// Visits, prescriptions and file attachments (migration 003). As everywhere else, every reference to another
/// tenant-owned table is a composite (TenantId, X) → (TenantId, Id) key, so the database itself rejects a row that
/// points at another clinic's patient, doctor, appointment or visit.
/// </summary>
internal sealed class VisitConfiguration : IEntityTypeConfiguration<Visit>
{
    public void Configure(EntityTypeBuilder<Visit> b)
    {
        // Explicit plural names (no DbSet yet, so EF would otherwise use the singular CLR name).
        b.ToTable("Visits", t =>
        {
            t.HasCheckConstraint("CK_Visits_Bp", "([SystolicBp] IS NULL OR [SystolicBp] BETWEEN 40 AND 300) AND ([DiastolicBp] IS NULL OR [DiastolicBp] BETWEEN 20 AND 200)");
            t.HasCheckConstraint("CK_Visits_Spo2", "[OxygenSaturation] IS NULL OR [OxygenSaturation] BETWEEN 40 AND 100");
            // Migration 004. The clinical-edit lock trusts Status, so the database refuses any other value
            // (same pattern as CK_FileAttachments_ScanStatus) ...
            t.HasCheckConstraint("CK_Visits_Status", "[Status] IN ('Open','Completed')");
            // ... and refuses a half-completed row: Completed always carries its timestamp, Open never carries completion
            // stamps. CompletedByUserId may be NULL on a Completed row (system/unknown actor, like the other *ByUserId columns).
            // Written with "<> 'Completed'" so it only checks consistency; the value domain stays CK_Visits_Status's job.
            t.HasCheckConstraint("CK_Visits_Completion",
                "([Status] = 'Completed' AND [CompletedAtUtc] IS NOT NULL) OR ([Status] <> 'Completed' AND [CompletedAtUtc] IS NULL AND [CompletedByUserId] IS NULL)");
        });
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.Symptoms).HasMaxLength(1000);
        b.Property(x => x.Diagnosis).HasMaxLength(1000);
        b.Property(x => x.DiagnosisCode).HasMaxLength(20).IsUnicode(false);
        b.Property(x => x.ClinicalNotes).HasMaxLength(4000);
        b.Property(x => x.TreatmentPlan).HasMaxLength(2000);
        b.Property(x => x.FollowUpNotes).HasMaxLength(500);
        b.Property(x => x.TemperatureC).HasPrecision(4, 1);
        b.Property(x => x.WeightKg).HasPrecision(5, 2);
        b.Property(x => x.HeightCm).HasPrecision(5, 1);
        // DEFAULT 'Open' fills every pre-existing row when migration 004 adds the column (additive, no data rewrite).
        b.Property(x => x.Status).HasDefaultValue(VisitStatus.Open);

        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Patient>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.PatientId }).HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DoctorProfile>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.DoctorId }).HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Branch>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.BranchId }).HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Appointment>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.AppointmentId }).HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // One visit per appointment (soft-deleted visits free the appointment again).
        b.HasIndex(x => new { x.TenantId, x.AppointmentId }, "UX_Visits_Appointment").IsUnique()
            .HasFilter("[AppointmentId] IS NOT NULL AND [IsDeleted] = 0");
        b.HasIndex(x => new { x.TenantId, x.PatientId, x.VisitedAtUtc });
        b.HasIndex(x => new { x.TenantId, x.DoctorId, x.VisitedAtUtc });
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.VisitedAtUtc });
    }
}

internal sealed class PrescriptionConfiguration : IEntityTypeConfiguration<Prescription>
{
    public void Configure(EntityTypeBuilder<Prescription> b)
    {
        b.ToTable("Prescriptions");
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.Number).HasMaxLength(20).IsUnicode(false);
        b.Property(x => x.Notes).HasMaxLength(1000);

        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Patient>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.PatientId }).HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DoctorProfile>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.DoctorId }).HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Visit>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.VisitId }).HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.Items).WithOne()
            .HasForeignKey(x => new { x.TenantId, x.PrescriptionId }).HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.PatientId, x.IssuedAtUtc });
        b.HasIndex(x => new { x.TenantId, x.VisitId }).HasFilter("[VisitId] IS NOT NULL");
    }
}

internal sealed class PrescriptionItemConfiguration : IEntityTypeConfiguration<PrescriptionItem>
{
    public void Configure(EntityTypeBuilder<PrescriptionItem> b)
    {
        b.ToTable("PrescriptionItems", t => t.HasCheckConstraint("CK_PrescriptionItems_Duration", "[DurationDays] IS NULL OR [DurationDays] BETWEEN 1 AND 365"));
        b.Property(x => x.MedicationName).HasMaxLength(200);
        b.Property(x => x.Strength).HasMaxLength(100);
        b.Property(x => x.Dosage).HasMaxLength(100);
        b.Property(x => x.Frequency).HasMaxLength(100);
        b.Property(x => x.Route).HasMaxLength(50);
        b.Property(x => x.Instructions).HasMaxLength(500);
        b.HasIndex(x => new { x.TenantId, x.PrescriptionId, x.LineNumber }).IsUnique();
    }
}

internal sealed class FileAttachmentConfiguration : IEntityTypeConfiguration<FileAttachment>
{
    public void Configure(EntityTypeBuilder<FileAttachment> b)
    {
        b.ToTable("FileAttachments", t =>
        {
            t.HasCheckConstraint("CK_FileAttachments_Size", "[SizeBytes] > 0");
            // The download gate trusts this column, so the database refuses any value outside the scan lifecycle.
            t.HasCheckConstraint("CK_FileAttachments_ScanStatus", "[ScanStatus] IN ('Pending','Scanning','Clean','Rejected','Failed')");
        });
        b.Property(x => x.FileName).HasMaxLength(255);
        b.Property(x => x.ContentType).HasMaxLength(100).IsUnicode(false);
        b.Property(x => x.StorageKey).HasMaxLength(300).IsUnicode(false);
        b.Property(x => x.Sha256).HasMaxLength(64).IsUnicode(false).IsFixedLength();
        b.Property(x => x.Description).HasMaxLength(500);

        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Patient>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.PatientId }).HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Visit>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.VisitId }).HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.StorageKey).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.PatientId, x.CreatedAtUtc });
        b.HasIndex(x => new { x.TenantId, x.VisitId }).HasFilter("[VisitId] IS NOT NULL");
    }
}
