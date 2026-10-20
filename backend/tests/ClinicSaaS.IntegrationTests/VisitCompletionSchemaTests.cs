using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Infrastructure.Persistence;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// Migration 004 (<c>VisitCompletion</c>): the database guards on <c>Visits.Status</c>, its default, persistence of
/// <see cref="Visit.Complete"/>, tenant isolation of the new columns, and an Up → Down → Up round-trip on a separate
/// throw-away database.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class VisitCompletionSchemaTests(ApiFactory factory)
{
    private const string BeforeMigration = "ClinicalRecords";
    private const string Migration = "VisitCompletion";

    // ------------------------------------------------------------------ constraints and default

    [Fact]
    public async Task Database_refuses_unknown_visit_statuses()
    {
        var a = await factory.BookableClinicAsync();
        await factory.WithDbAsync(a.Owner.TenantId, async db =>
        {
            var insert = () => db.Database.ExecuteSqlAsync($"""
                INSERT INTO Visits (Id, TenantId, BranchId, PatientId, DoctorId, VisitedAtUtc, CreatedAtUtc, Status)
                VALUES (NEWID(), {a.Owner.TenantId}, {a.BranchId}, {a.PatientId}, {a.DoctorId}, SYSUTCDATETIME(), SYSUTCDATETIME(), 'Closed')
                """);
            // 547 = CHECK constraint violation.
            await insert.Should().ThrowAsync<SqlException>().Where(e => e.Number == 547 && e.Message.Contains("CK_Visits_Status"));
        });
    }

    [Fact]
    public async Task Rows_inserted_without_a_status_default_to_open()
    {
        var a = await factory.BookableClinicAsync();
        var id = Guid.NewGuid();
        await factory.WithDbAsync(a.Owner.TenantId, async db =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO Visits (Id, TenantId, BranchId, PatientId, DoctorId, VisitedAtUtc, CreatedAtUtc)
                VALUES ({id}, {a.Owner.TenantId}, {a.BranchId}, {a.PatientId}, {a.DoctorId}, SYSUTCDATETIME(), SYSUTCDATETIME())
                """);

            var visit = await db.Visits.AsNoTracking().SingleAsync(x => x.Id == id);
            visit.Status.Should().Be(VisitStatus.Open);
            visit.CompletedAtUtc.Should().BeNull();
            visit.CompletedByUserId.Should().BeNull();
            visit.CanEditClinicalContent.Should().BeTrue();
        });
    }

    [Fact]
    public async Task Database_refuses_half_completed_visits()
    {
        var a = await factory.BookableClinicAsync();
        var t = a.Owner.TenantId;
        await factory.WithDbAsync(t, async db =>
        {
            // Completed without its timestamp.
            var completedWithoutTime = () => db.Database.ExecuteSqlAsync($"""
                INSERT INTO Visits (Id, TenantId, BranchId, PatientId, DoctorId, VisitedAtUtc, CreatedAtUtc, Status)
                VALUES (NEWID(), {t}, {a.BranchId}, {a.PatientId}, {a.DoctorId}, SYSUTCDATETIME(), SYSUTCDATETIME(), 'Completed')
                """);
            await completedWithoutTime.Should().ThrowAsync<SqlException>().Where(e => e.Number == 547 && e.Message.Contains("CK_Visits_Completion"));

            // Open but carrying completion stamps.
            var openWithStamps = () => db.Database.ExecuteSqlAsync($"""
                INSERT INTO Visits (Id, TenantId, BranchId, PatientId, DoctorId, VisitedAtUtc, CreatedAtUtc, Status, CompletedAtUtc, CompletedByUserId)
                VALUES (NEWID(), {t}, {a.BranchId}, {a.PatientId}, {a.DoctorId}, SYSUTCDATETIME(), SYSUTCDATETIME(), 'Open', SYSUTCDATETIME(), NEWID())
                """);
            await openWithStamps.Should().ThrowAsync<SqlException>().Where(e => e.Number == 547 && e.Message.Contains("CK_Visits_Completion"));

            // Completed with a timestamp but no known actor is allowed.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO Visits (Id, TenantId, BranchId, PatientId, DoctorId, VisitedAtUtc, CreatedAtUtc, Status, CompletedAtUtc)
                VALUES (NEWID(), {t}, {a.BranchId}, {a.PatientId}, {a.DoctorId}, SYSUTCDATETIME(), SYSUTCDATETIME(), 'Completed', SYSUTCDATETIME())
                """);
        });
    }

    // ------------------------------------------------------------------ persistence and tenant isolation

    [Fact]
    public async Task Completing_a_visit_persists_status_time_and_user()
    {
        var a = await factory.BookableClinicAsync();
        var visitId = await SeedVisitAsync(a);
        var at = new DateTime(2026, 10, 6, 8, 15, 30, 123, DateTimeKind.Utc);

        await factory.WithDbAsync(a.Owner.TenantId, async db =>
        {
            var visit = await db.Visits.SingleAsync(x => x.Id == visitId);
            visit.Complete(at, a.Owner.UserId);
            await db.SaveChangesAsync();
        });

        await factory.WithDbAsync(a.Owner.TenantId, async db =>
        {
            var visit = await db.Visits.AsNoTracking().SingleAsync(x => x.Id == visitId);
            visit.Status.Should().Be(VisitStatus.Completed);
            visit.CompletedAtUtc.Should().Be(at);
            visit.CompletedAtUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
            visit.CompletedByUserId.Should().Be(a.Owner.UserId);
            visit.CanEditClinicalContent.Should().BeFalse();
        });
    }

    [Fact]
    public async Task Tenant_B_cannot_complete_tenant_A_visit_loaded_past_the_filter()
    {
        var a = await factory.BookableClinicAsync();
        var visitId = await SeedVisitAsync(a);
        var b = await factory.RegisterClinicAsync();

        await factory.WithDbAsync(b.TenantId, async db =>
        {
            (await db.Visits.FirstOrDefaultAsync(x => x.Id == visitId)).Should().BeNull();

            var leaked = await db.Visits.IgnoreQueryFilters().SingleAsync(x => x.Id == visitId);
            leaked.Complete(DateTime.UtcNow, b.UserId);
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<TenantIsolationViolationException>();
        });

        await factory.WithDbAsync(a.Owner.TenantId, async db =>
            (await db.Visits.AsNoTracking().SingleAsync(x => x.Id == visitId)).Status.Should().Be(VisitStatus.Open));
    }

    private async Task<Guid> SeedVisitAsync(Scenarios.BookableClinic a)
    {
        var id = Guid.Empty;
        await factory.WithDbAsync(a.Owner.TenantId, async db =>
        {
            var visit = new Visit { BranchId = a.BranchId, PatientId = a.PatientId, DoctorId = a.DoctorId, VisitedAtUtc = DateTime.UtcNow };
            db.Visits.Add(visit);
            await db.SaveChangesAsync();
            id = visit.Id;
        });
        return id;
    }

    // ------------------------------------------------------------------ migration round-trip

    /// <summary>
    /// Up(004) → Down(to 003) → Up(004) on its own throw-away LocalDB database (never a development database), with a
    /// visit that existed before 004 to prove the column default fills it and that Down keeps the rest of the row.
    /// </summary>
    [Fact]
    public async Task Migration_round_trips_up_down_up_on_a_throw_away_database()
    {
        var database = $"ClinicSaaS_Test_{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.SetConnectionString(ApiFactory.TestConnectionString(database));
        var migrator = db.GetService<IMigrator>();

        try
        {
            await migrator.MigrateAsync(BeforeMigration);
            (await CompletionColumnCountAsync(db)).Should().Be(0);

            // A visit recorded before 004. FK/check enforcement is switched off for Visits on this throw-away database
            // only, so the row does not need a whole tenant graph; the 004 constraints are still validated against it
            // because ADD CONSTRAINT checks existing rows.
            var legacyVisit = Guid.NewGuid();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Visits NOCHECK CONSTRAINT ALL");
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO Visits (Id, TenantId, BranchId, PatientId, DoctorId, VisitedAtUtc, CreatedAtUtc, Diagnosis)
                VALUES ({legacyVisit}, NEWID(), NEWID(), NEWID(), NEWID(), SYSUTCDATETIME(), SYSUTCDATETIME(), N'legacy')
                """);

            // Up
            await migrator.MigrateAsync(Migration);
            (await CompletionColumnCountAsync(db)).Should().Be(3);
            (await CheckConstraintCountAsync(db)).Should().Be(2);
            (await StatusOfAsync(db, legacyVisit)).Should().Be("Open");
            await db.Database.ExecuteSqlAsync(
                $"UPDATE Visits SET Status = 'Completed', CompletedAtUtc = SYSUTCDATETIME() WHERE Id = {legacyVisit}");

            // Down: the three columns and two constraints go away; the visit row and its other data remain.
            await migrator.MigrateAsync(BeforeMigration);
            (await CompletionColumnCountAsync(db)).Should().Be(0);
            (await CheckConstraintCountAsync(db)).Should().Be(0);
            (await db.Database.SqlQuery<string>($"SELECT Diagnosis AS Value FROM Visits WHERE Id = {legacyVisit}").SingleAsync())
                .Should().Be("legacy");
            (await AppliedAsync(db)).Should().NotContain(m => m.EndsWith("_" + Migration));

            // Up again: completion state is not restored by a rollback (documented), every row is Open again.
            await migrator.MigrateAsync(Migration);
            (await CompletionColumnCountAsync(db)).Should().Be(3);
            (await CheckConstraintCountAsync(db)).Should().Be(2);
            (await StatusOfAsync(db, legacyVisit)).Should().Be("Open");
            (await AppliedAsync(db)).Should().Contain(m => m.EndsWith("_" + Migration));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    private static Task<int> CompletionColumnCountAsync(AppDbContext db) =>
        db.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS Value FROM sys.columns
            WHERE object_id = OBJECT_ID(N'dbo.Visits') AND name IN (N'Status', N'CompletedAtUtc', N'CompletedByUserId')
            """).SingleAsync();

    private static Task<int> CheckConstraintCountAsync(AppDbContext db) =>
        db.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS Value FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'dbo.Visits') AND name IN (N'CK_Visits_Status', N'CK_Visits_Completion')
            """).SingleAsync();

    private static Task<string> StatusOfAsync(AppDbContext db, Guid id) =>
        db.Database.SqlQuery<string>($"SELECT Status AS Value FROM Visits WHERE Id = {id}").SingleAsync();

    private static async Task<List<string>> AppliedAsync(AppDbContext db) =>
        (await db.Database.GetAppliedMigrationsAsync()).ToList();
}
