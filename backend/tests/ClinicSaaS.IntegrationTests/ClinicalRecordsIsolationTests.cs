using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Infrastructure.Persistence;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// Tenant isolation for the clinical records of migration 003 (visits, prescriptions, files) and the appointments
/// they hang off. These tables have no endpoints yet, so the guarantees are verified where they live today: the
/// data-access layer (read filter + save guard, including direct access by id) and the database (composite FKs).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ClinicalRecordsIsolationTests(ApiFactory factory)
{
    private sealed record ClinicalRecordsOfA(
        Scenarios.BookableClinic Clinic, Guid AppointmentId, Guid VisitId, Guid PrescriptionId, Guid ItemId, Guid FileId);

    /// <summary>Tenant A with one appointment, a visit for it, a prescription with one item and a patient file.</summary>
    private async Task<ClinicalRecordsOfA> SeedTenantAAsync()
    {
        var a = await factory.BookableClinicAsync();
        var booked = await a.Owner.Client.BookAsync(a, Scenarios.RiyadhUtc(1, 10));
        await booked.EnsureStatusAsync(201);
        var appointmentId = (await booked.ReadJsonAsync()).GetProperty("data").GetProperty("id").GetGuid();

        Guid visitId = default, prescriptionId = default, itemId = default, fileId = default;
        await factory.WithDbAsync(a.Owner.TenantId, async db =>
        {
            var visit = new Visit
            {
                BranchId = a.BranchId, PatientId = a.PatientId, DoctorId = a.DoctorId, AppointmentId = appointmentId,
                VisitedAtUtc = DateTime.UtcNow, Diagnosis = "سري",
            };
            db.Set<Visit>().Add(visit);
            await db.SaveChangesAsync();

            var item = new PrescriptionItem { LineNumber = 1, MedicationName = "Paracetamol" };
            var prescription = new Prescription
            {
                Number = Prescription.FormatNumber(1), PatientId = a.PatientId, DoctorId = a.DoctorId, VisitId = visit.Id,
                IssuedAtUtc = DateTime.UtcNow, Items = [item],
            };
            db.Set<Prescription>().Add(prescription);

            var file = new FileAttachment
            {
                PatientId = a.PatientId, VisitId = visit.Id, FileName = "lab.pdf", ContentType = "application/pdf",
                SizeBytes = 1024, StorageKey = $"t/{Guid.NewGuid():N}", Sha256 = new string('a', 64),
            };
            db.Set<FileAttachment>().Add(file);
            await db.SaveChangesAsync();

            (visitId, prescriptionId, itemId, fileId) = (visit.Id, prescription.Id, item.Id, file.Id);
        });
        return new ClinicalRecordsOfA(a, appointmentId, visitId, prescriptionId, itemId, fileId);
    }

    // ------------------------------------------------------------------ reads

    [Fact]
    public async Task Tenant_B_cannot_read_tenant_A_records_even_by_id()
    {
        var a = await SeedTenantAAsync();
        var b = await factory.RegisterClinicAsync();

        await factory.WithDbAsync(b.TenantId, async db =>
        {
            // Direct lookups by A's ids — as a forged id in a request would do.
            (await db.Set<Visit>().FirstOrDefaultAsync(x => x.Id == a.VisitId)).Should().BeNull();
            (await db.Set<Prescription>().FirstOrDefaultAsync(x => x.Id == a.PrescriptionId)).Should().BeNull();
            (await db.Set<PrescriptionItem>().FirstOrDefaultAsync(x => x.Id == a.ItemId)).Should().BeNull();
            (await db.Set<FileAttachment>().FirstOrDefaultAsync(x => x.Id == a.FileId)).Should().BeNull();
            (await db.Appointments.FirstOrDefaultAsync(x => x.Id == a.AppointmentId)).Should().BeNull();

            // Find() also goes through the tenant filter.
            (await db.Set<Visit>().FindAsync(a.VisitId)).Should().BeNull();
            (await db.Set<Prescription>().FindAsync(a.PrescriptionId)).Should().BeNull();
            (await db.Set<FileAttachment>().FindAsync(a.FileId)).Should().BeNull();
            (await db.Appointments.FindAsync(a.AppointmentId)).Should().BeNull();

            // Navigations and lists see nothing either.
            (await db.Set<Prescription>().Include(p => p.Items).CountAsync()).Should().Be(0);
            (await db.Set<Visit>().CountAsync()).Should().Be(0);
            (await db.Set<FileAttachment>().CountAsync()).Should().Be(0);
            (await db.Appointments.CountAsync()).Should().Be(0);
        });

        // No tenant scope at all ⇒ nothing.
        await factory.WithDbAsync(null, async db =>
        {
            (await db.Set<Visit>().CountAsync()).Should().Be(0);
            (await db.Set<Prescription>().CountAsync()).Should().Be(0);
            (await db.Set<PrescriptionItem>().CountAsync()).Should().Be(0);
            (await db.Set<FileAttachment>().CountAsync()).Should().Be(0);
        });

        // A still sees its own records.
        await factory.WithDbAsync(a.Clinic.Owner.TenantId, async db =>
        {
            (await db.Set<Visit>().SingleAsync()).Id.Should().Be(a.VisitId);
            (await db.Set<Prescription>().Include(p => p.Items).SingleAsync()).Items.Should().ContainSingle();
            (await db.Set<FileAttachment>().SingleAsync()).Id.Should().Be(a.FileId);
        });
    }

    // ------------------------------------------------------------------ writes

    [Fact]
    public async Task Tenant_B_cannot_update_or_delete_tenant_A_records_loaded_past_the_filter()
    {
        var a = await SeedTenantAAsync();
        var b = await factory.RegisterClinicAsync();

        // A buggy query that skipped the filter must still not be able to write: every attempt is refused on save.
        await AssertWriteRefusedAsync(b.TenantId, db => db.Set<Visit>().IgnoreQueryFilters().SingleAsync(x => x.Id == a.VisitId),
            v => v.Diagnosis = "tampered");
        await AssertWriteRefusedAsync(b.TenantId, db => db.Set<Prescription>().IgnoreQueryFilters().SingleAsync(x => x.Id == a.PrescriptionId),
            p => p.Notes = "tampered");
        await AssertWriteRefusedAsync(b.TenantId, db => db.Set<PrescriptionItem>().IgnoreQueryFilters().SingleAsync(x => x.Id == a.ItemId),
            i => i.MedicationName = "tampered");
        await AssertWriteRefusedAsync(b.TenantId, db => db.Set<FileAttachment>().IgnoreQueryFilters().SingleAsync(x => x.Id == a.FileId),
            f => f.TransitionScanTo(FileScanStatus.Scanning));
        await AssertWriteRefusedAsync(b.TenantId, db => db.Appointments.IgnoreQueryFilters().SingleAsync(x => x.Id == a.AppointmentId),
            x => x.Reason = "tampered");

        await AssertDeleteRefusedAsync<Visit>(b.TenantId, a.VisitId);
        await AssertDeleteRefusedAsync<Prescription>(b.TenantId, a.PrescriptionId);
        await AssertDeleteRefusedAsync<PrescriptionItem>(b.TenantId, a.ItemId);
        await AssertDeleteRefusedAsync<FileAttachment>(b.TenantId, a.FileId);
        await AssertDeleteRefusedAsync<Appointment>(b.TenantId, a.AppointmentId);

        // A's records are untouched.
        await factory.WithDbAsync(a.Clinic.Owner.TenantId, async db =>
        {
            (await db.Set<Visit>().SingleAsync()).Diagnosis.Should().Be("سري");
            (await db.Set<PrescriptionItem>().SingleAsync()).MedicationName.Should().Be("Paracetamol");
            (await db.Set<FileAttachment>().SingleAsync()).ScanStatus.Should().Be(FileScanStatus.Pending);
        });
    }

    [Fact]
    public async Task Tenant_B_cannot_overwrite_tenant_A_records_by_attaching_a_forged_entity_with_their_id()
    {
        var a = await SeedTenantAAsync();
        var b = await factory.BookableClinicAsync();

        // An entity built from client input with A's id but B's tenant (never loaded from the database). The save
        // guard sees TenantId == current tenant; the row's stored TenantId (in the UPDATE's WHERE) must refuse it.
        await factory.WithDbAsync(b.Owner.TenantId, async db =>
        {
            db.Set<PrescriptionItem>().Update(new PrescriptionItem
            {
                Id = a.ItemId, TenantId = b.Owner.TenantId, PrescriptionId = a.PrescriptionId, LineNumber = 1, MedicationName = "forged",
            });
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<TenantIsolationViolationException>();
        });
        await factory.WithDbAsync(b.Owner.TenantId, async db =>
        {
            db.Set<FileAttachment>().Update(ForgedFile(a.FileId, b.Owner.TenantId, b.PatientId));
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<TenantIsolationViolationException>();
        });

        await factory.WithDbAsync(a.Clinic.Owner.TenantId, async db =>
        {
            (await db.Set<PrescriptionItem>().SingleAsync()).MedicationName.Should().Be("Paracetamol");
            var file = await db.Set<FileAttachment>().SingleAsync();
            file.FileName.Should().Be("lab.pdf");
            file.PatientId.Should().Be(a.Clinic.PatientId);
        });
    }

    // ------------------------------------------------------------------ FileAttachment ownership (regression: forged-entity overwrite)

    [Fact]
    public async Task Owner_tenant_can_still_modify_its_own_file()
    {
        var a = await SeedTenantAAsync();
        await factory.WithDbAsync(a.Clinic.Owner.TenantId, async db =>
        {
            var file = await db.Set<FileAttachment>().SingleAsync(f => f.Id == a.FileId);
            file.Description = "نتيجة تحليل الدم";
            file.TransitionScanTo(FileScanStatus.Scanning);
            file.TransitionScanTo(FileScanStatus.Clean);
            await db.SaveChangesAsync();
        });
        await factory.WithDbAsync(a.Clinic.Owner.TenantId, async db =>
        {
            var file = await db.Set<FileAttachment>().SingleAsync(f => f.Id == a.FileId);
            file.Description.Should().Be("نتيجة تحليل الدم");
            file.CanDownload.Should().BeTrue();
            file.TenantId.Should().Be(a.Clinic.Owner.TenantId);
        });
    }

    [Fact]
    public async Task Tenant_B_cannot_modify_tenant_A_file_even_knowing_its_id()
    {
        var a = await SeedTenantAAsync();
        var b = await factory.BookableClinicAsync();

        // Attach a stub with A's id (no load) and flip a single property — the narrowest possible forged write.
        await factory.WithDbAsync(b.Owner.TenantId, async db =>
        {
            var stub = ForgedFile(a.FileId, b.Owner.TenantId, b.PatientId);
            db.Set<FileAttachment>().Attach(stub);
            stub.Description = "forged";
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<TenantIsolationViolationException>();
        });

        await AssertFileOfAUntouchedAsync(a);
    }

    [Fact]
    public async Task Tenant_B_cannot_move_tenant_A_file_to_another_tenant()
    {
        var a = await SeedTenantAAsync();
        var b = await factory.BookableClinicAsync();

        // 1) Forged entity claiming the file now belongs to B (the original exploit).
        await factory.WithDbAsync(b.Owner.TenantId, async db =>
        {
            db.Set<FileAttachment>().Update(ForgedFile(a.FileId, b.Owner.TenantId, b.PatientId));
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<TenantIsolationViolationException>();
        });

        // 2) B loads A's real row past the filter and re-stamps TenantId.
        await factory.WithDbAsync(b.Owner.TenantId, async db =>
        {
            var leaked = await db.Set<FileAttachment>().IgnoreQueryFilters().SingleAsync(f => f.Id == a.FileId);
            leaked.TenantId = b.Owner.TenantId;
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<TenantIsolationViolationException>();
        });

        // 3) Even tenant A itself cannot hand its file to another tenant.
        await factory.WithDbAsync(a.Clinic.Owner.TenantId, async db =>
        {
            var own = await db.Set<FileAttachment>().SingleAsync(f => f.Id == a.FileId);
            own.TenantId = b.Owner.TenantId;
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<TenantIsolationViolationException>();
        });

        await AssertFileOfAUntouchedAsync(a);
    }

    [Fact]
    public async Task Tenant_B_cannot_delete_tenant_A_file_or_other_records_with_forged_entities()
    {
        var a = await SeedTenantAAsync();
        var b = await factory.BookableClinicAsync();

        // Soft-deletable (the delete becomes an UPDATE of IsDeleted).
        await factory.WithDbAsync(b.Owner.TenantId, async db =>
        {
            db.Set<FileAttachment>().Remove(ForgedFile(a.FileId, b.Owner.TenantId, b.PatientId));
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<TenantIsolationViolationException>();
        });
        // Hard-deleted (a real DELETE statement).
        await factory.WithDbAsync(b.Owner.TenantId, async db =>
        {
            db.Set<PrescriptionItem>().Remove(new PrescriptionItem
            {
                Id = a.ItemId, TenantId = b.Owner.TenantId, PrescriptionId = a.PrescriptionId, LineNumber = 1, MedicationName = "x",
            });
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<TenantIsolationViolationException>();
        });
        // A rowversioned entity (Patient) forged the same way is reported as a tenant violation, not a mere conflict.
        await factory.WithDbAsync(b.Owner.TenantId, async db =>
        {
            db.Patients.Remove(new Patient
            {
                Id = a.Clinic.PatientId, TenantId = b.Owner.TenantId, MedicalRecordNumber = "x", FullName = "x", MobileNumber = "+966500000000",
            });
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<TenantIsolationViolationException>();
        });

        await AssertFileOfAUntouchedAsync(a);
        await factory.WithDbAsync(a.Clinic.Owner.TenantId, async db =>
        {
            (await db.Set<PrescriptionItem>().CountAsync()).Should().Be(1);
            (await db.Patients.AnyAsync(p => p.Id == a.Clinic.PatientId)).Should().BeTrue();
        });
    }

    [Fact]
    public async Task Same_tenant_concurrency_conflicts_and_deletes_behave_as_before()
    {
        var a = await SeedTenantAAsync();
        var ta = a.Clinic.Owner.TenantId;

        // A stale rowversion inside the same tenant is still an ordinary 409 conflict, not a tenant violation.
        await factory.WithDbAsync(ta, async db =>
        {
            var visit = await db.Set<Visit>().SingleAsync();
            db.SetOriginalRowVersion(visit, new byte[8]);
            visit.Diagnosis = "updated";
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<DbUpdateConcurrencyException>();
        });

        // Normal soft delete of the tenant's own file.
        await factory.WithDbAsync(ta, async db =>
        {
            db.Set<FileAttachment>().Remove(await db.Set<FileAttachment>().SingleAsync());
            await db.SaveChangesAsync();
        });
        await factory.WithDbAsync(ta, async db =>
        {
            (await db.Set<FileAttachment>().CountAsync()).Should().Be(0);
            var deleted = await db.Set<FileAttachment>().IgnoreQueryFilters([Application.Common.QueryFilters.SoftDelete]).SingleAsync();
            deleted.IsDeleted.Should().BeTrue();
            deleted.TenantId.Should().Be(ta);
        });
    }

    [Fact]
    public async Task Inserting_records_stamped_with_another_tenant_or_without_a_tenant_is_refused()
    {
        var a = await SeedTenantAAsync();
        var b = await factory.RegisterClinicAsync();

        await factory.WithDbAsync(b.TenantId, async db =>
        {
            db.Set<Visit>().Add(new Visit
            {
                TenantId = a.Clinic.Owner.TenantId, BranchId = a.Clinic.BranchId, PatientId = a.Clinic.PatientId,
                DoctorId = a.Clinic.DoctorId, VisitedAtUtc = DateTime.UtcNow,
            });
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<TenantIsolationViolationException>();
        });

        await factory.WithDbAsync(null, async db =>
        {
            db.Set<FileAttachment>().Add(new FileAttachment
            {
                PatientId = a.Clinic.PatientId, FileName = "x", ContentType = "x", SizeBytes = 1,
                StorageKey = $"x/{Guid.NewGuid():N}", Sha256 = new string('c', 64),
            });
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<TenantIsolationViolationException>();
        });
    }

    // ------------------------------------------------------------------ database layer

    [Fact]
    public async Task Database_rejects_cross_tenant_clinical_references_even_with_raw_sql()
    {
        var a = await SeedTenantAAsync();
        var b = await factory.BookableClinicAsync();
        var tb = b.Owner.TenantId;

        await factory.WithDbAsync(tb, async db =>
        {
            // A visit of B pointing at A's patient.
            await ShouldViolateForeignKey(() => db.Database.ExecuteSqlAsync($"""
                INSERT INTO Visits (Id, TenantId, BranchId, PatientId, DoctorId, VisitedAtUtc, CreatedAtUtc)
                VALUES (NEWID(), {tb}, {b.BranchId}, {a.Clinic.PatientId}, {b.DoctorId}, SYSUTCDATETIME(), SYSUTCDATETIME())
                """));
            // A visit of B pointing at A's appointment (relies on AK_Appointments_TenantId_Id).
            await ShouldViolateForeignKey(() => db.Database.ExecuteSqlAsync($"""
                INSERT INTO Visits (Id, TenantId, BranchId, PatientId, DoctorId, AppointmentId, VisitedAtUtc, CreatedAtUtc)
                VALUES (NEWID(), {tb}, {b.BranchId}, {b.PatientId}, {b.DoctorId}, {a.AppointmentId}, SYSUTCDATETIME(), SYSUTCDATETIME())
                """));
            // A prescription of B attached to A's visit.
            await ShouldViolateForeignKey(() => db.Database.ExecuteSqlAsync($"""
                INSERT INTO Prescriptions (Id, TenantId, Number, PatientId, DoctorId, VisitId, IssuedAtUtc, CreatedAtUtc)
                VALUES (NEWID(), {tb}, 'RX-RAW-1', {b.PatientId}, {b.DoctorId}, {a.VisitId}, SYSUTCDATETIME(), SYSUTCDATETIME())
                """));
            // An item of B added to A's prescription.
            await ShouldViolateForeignKey(() => db.Database.ExecuteSqlAsync($"""
                INSERT INTO PrescriptionItems (Id, TenantId, PrescriptionId, LineNumber, MedicationName, CreatedAtUtc)
                VALUES (NEWID(), {tb}, {a.PrescriptionId}, 2, N'x', SYSUTCDATETIME())
                """));
            // A file of B on A's patient, and one on A's visit.
            await ShouldViolateForeignKey(() => db.Database.ExecuteSqlAsync($"""
                INSERT INTO FileAttachments (Id, TenantId, PatientId, FileName, ContentType, SizeBytes, StorageKey, Sha256, Category, ScanStatus, CreatedAtUtc)
                VALUES (NEWID(), {tb}, {a.Clinic.PatientId}, N'x', 'x', 1, {"raw/" + Guid.NewGuid().ToString("N")}, REPLICATE('d', 64), 'Other', 'Pending', SYSUTCDATETIME())
                """));
            await ShouldViolateForeignKey(() => db.Database.ExecuteSqlAsync($"""
                INSERT INTO FileAttachments (Id, TenantId, PatientId, VisitId, FileName, ContentType, SizeBytes, StorageKey, Sha256, Category, ScanStatus, CreatedAtUtc)
                VALUES (NEWID(), {tb}, {b.PatientId}, {a.VisitId}, N'x', 'x', 1, {"raw/" + Guid.NewGuid().ToString("N")}, REPLICATE('d', 64), 'Other', 'Pending', SYSUTCDATETIME())
                """));
        });
    }

    [Fact]
    public async Task Database_refuses_unknown_scan_statuses()
    {
        var a = await SeedTenantAAsync();
        var ta = a.Clinic.Owner.TenantId;

        await factory.WithDbAsync(ta, async db =>
        {
            var insert = () => db.Database.ExecuteSqlAsync($"""
                INSERT INTO FileAttachments (Id, TenantId, PatientId, FileName, ContentType, SizeBytes, StorageKey, Sha256, Category, ScanStatus, CreatedAtUtc)
                VALUES (NEWID(), {ta}, {a.Clinic.PatientId}, N'x', 'x', 1, {"raw/" + Guid.NewGuid().ToString("N")}, REPLICATE('e', 64), 'Other', 'Safe', SYSUTCDATETIME())
                """);
            // 547 = CHECK constraint violation (same error number as FK violations).
            await insert.Should().ThrowAsync<SqlException>().Where(e => e.Number == 547 && e.Message.Contains("CK_FileAttachments_ScanStatus"));
        });
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A FileAttachment built from "request input": another tenant's id, the attacker's tenant and patient.</summary>
    private static FileAttachment ForgedFile(Guid victimFileId, Guid attackerTenant, Guid attackerPatient) => new()
    {
        Id = victimFileId, TenantId = attackerTenant, PatientId = attackerPatient, FileName = "forged", ContentType = "x",
        SizeBytes = 1, StorageKey = $"forged/{Guid.NewGuid():N}", Sha256 = new string('b', 64),
    };

    private Task AssertFileOfAUntouchedAsync(ClinicalRecordsOfA a) =>
        factory.WithDbAsync(a.Clinic.Owner.TenantId, async db =>
        {
            var file = await db.Set<FileAttachment>().SingleAsync(f => f.Id == a.FileId);
            file.TenantId.Should().Be(a.Clinic.Owner.TenantId);
            file.PatientId.Should().Be(a.Clinic.PatientId);
            file.FileName.Should().Be("lab.pdf");
            file.Description.Should().BeNull();
            file.IsDeleted.Should().BeFalse();
        });

    private Task AssertWriteRefusedAsync<T>(Guid actingTenant, Func<AppDbContext, Task<T>> loadPastFilter, Action<T> change) where T : class =>
        factory.WithDbAsync(actingTenant, async db =>
        {
            var leaked = await loadPastFilter(db);
            change(leaked);
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<TenantIsolationViolationException>();
        });

    private Task AssertDeleteRefusedAsync<T>(Guid actingTenant, Guid id) where T : class =>
        factory.WithDbAsync(actingTenant, async db =>
        {
            var leaked = await db.Set<T>().IgnoreQueryFilters().SingleAsync(x => EF.Property<Guid>(x, "Id") == id);
            db.Remove(leaked);
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<TenantIsolationViolationException>();
        });

    private static async Task ShouldViolateForeignKey(Func<Task<int>> rawInsert) =>
        await rawInsert.Should().ThrowAsync<SqlException>().Where(e => e.Number == 547 && e.Message.Contains("FOREIGN KEY"));
}
