using ClinicSaaS.Infrastructure.Persistence;
using ClinicSaaS.IntegrationTests.Infrastructure;
using ClinicSaaS.IntegrationTests.Qa;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// Migration 005 (<c>PlatformAdminMfa</c>): exactly two new tables, the approved columns / keys / indexes / cascade, and
/// NOTHING else in the schema changes (an Up → Down → Up round-trip on a throw-away database compares every other
/// table, column, index, foreign key and check constraint before and after).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PlatformMfaSchemaTests(ApiFactory factory)
{
    private const string BeforeMigration = "VisitCompletion";
    private const string Migration = "PlatformAdminMfa";

    private sealed record Col(string Table, string Name, string Type, int MaxLength, bool Nullable);

    private static async Task<List<Col>> ColumnsAsync(AppDbContext db, string table) =>
        (await db.Database.SqlQuery<string>($"""
            SELECT c.name + '|' + ty.name + '|' + CAST(c.max_length AS varchar(10)) + '|' + CAST(c.is_nullable AS varchar(1)) AS Value
            FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID({"dbo." + table}) ORDER BY c.column_id
            """).ToListAsync())
        .Select(s => s.Split('|')).Select(p => new Col(table, p[0], p[1], int.Parse(p[2]), p[3] == "1")).ToList();

    [Fact]
    public async Task The_two_tables_have_exactly_the_approved_columns()
    {
        await factory.WithDbAsync(null, async db =>
        {
            (await ColumnsAsync(db, "UserTotpFactors")).Should().BeEquivalentTo(
            [
                new Col("UserTotpFactors", "Id", "uniqueidentifier", 16, false),
                new Col("UserTotpFactors", "UserId", "uniqueidentifier", 16, false),
                new Col("UserTotpFactors", "SecretProtected", "varbinary", 256, false),
                new Col("UserTotpFactors", "LastUsedTimeStep", "bigint", 8, true),
                new Col("UserTotpFactors", "CreatedAtUtc", "datetime2", 7, false),
            ], o => o.WithStrictOrdering());

            (await ColumnsAsync(db, "UserRecoveryCodes")).Should().BeEquivalentTo(
            [
                new Col("UserRecoveryCodes", "Id", "uniqueidentifier", 16, false),
                new Col("UserRecoveryCodes", "UserId", "uniqueidentifier", 16, false),
                new Col("UserRecoveryCodes", "CodeHash", "char", 64, false),
                new Col("UserRecoveryCodes", "CreatedAtUtc", "datetime2", 7, false),
                new Col("UserRecoveryCodes", "ConsumedAtUtc", "datetime2", 7, true),
            ], o => o.WithStrictOrdering());
        });
    }

    [Fact]
    public async Task Keys_indexes_and_cascading_foreign_keys_are_as_approved()
    {
        await factory.WithDbAsync(null, async db =>
        {
            var pks = await db.Database.SqlQuery<string>($"""
                SELECT OBJECT_NAME(i.object_id) + '.' + c.name AS Value FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.is_primary_key = 1 AND OBJECT_NAME(i.object_id) IN (N'UserTotpFactors', N'UserRecoveryCodes')
                """).ToListAsync();
            pks.Should().BeEquivalentTo("UserTotpFactors.Id", "UserRecoveryCodes.Id");

            var unique = await db.Database.SqlQuery<string>($"""
                SELECT OBJECT_NAME(i.object_id) + ':' + i.name + ':' + STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal) AS Value
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.is_unique = 1 AND i.is_primary_key = 0 AND OBJECT_NAME(i.object_id) IN (N'UserTotpFactors', N'UserRecoveryCodes')
                GROUP BY i.object_id, i.name
                """).ToListAsync();
            unique.Should().BeEquivalentTo("UserTotpFactors:IX_UserTotpFactors_UserId:UserId", "UserRecoveryCodes:IX_UserRecoveryCodes_UserId_CodeHash:UserId,CodeHash");

            var fks = await db.Database.SqlQuery<string>($"""
                SELECT OBJECT_NAME(parent_object_id) + '->' + OBJECT_NAME(referenced_object_id) + ':' + delete_referential_action_desc AS Value
                FROM sys.foreign_keys WHERE OBJECT_NAME(parent_object_id) IN (N'UserTotpFactors', N'UserRecoveryCodes')
                """).ToListAsync();
            fks.Should().BeEquivalentTo("UserTotpFactors->Users:CASCADE", "UserRecoveryCodes->Users:CASCADE");
        });
    }

    [Fact]
    public async Task The_database_enforces_one_factor_per_user_and_unique_codes_and_cascades_on_user_delete()
    {
        var email = await factory.CreatePlatformUserAsync(Domain.Identity.PlatformRole.SuperAdmin);
        await factory.WithDbAsync(null, async db =>
        {
            var userId = await db.Users.Where(u => u.NormalizedEmail == Domain.Identity.User.Normalize(email)).Select(u => u.Id).SingleAsync();
            await db.Database.ExecuteSqlAsync($"INSERT INTO UserTotpFactors (Id, UserId, SecretProtected, CreatedAtUtc) VALUES (NEWID(), {userId}, 0x01, SYSUTCDATETIME())");
            var second = () => db.Database.ExecuteSqlAsync($"INSERT INTO UserTotpFactors (Id, UserId, SecretProtected, CreatedAtUtc) VALUES (NEWID(), {userId}, 0x02, SYSUTCDATETIME())");
            await second.Should().ThrowAsync<SqlException>().Where(e => e.Number == 2601 || e.Number == 2627);

            var hash = new string('a', 64);
            await db.Database.ExecuteSqlAsync($"INSERT INTO UserRecoveryCodes (Id, UserId, CodeHash, CreatedAtUtc) VALUES (NEWID(), {userId}, {hash}, SYSUTCDATETIME())");
            var dup = () => db.Database.ExecuteSqlAsync($"INSERT INTO UserRecoveryCodes (Id, UserId, CodeHash, CreatedAtUtc) VALUES (NEWID(), {userId}, {hash}, SYSUTCDATETIME())");
            await dup.Should().ThrowAsync<SqlException>().Where(e => e.Number == 2601 || e.Number == 2627);

            var orphan = () => db.Database.ExecuteSqlAsync($"INSERT INTO UserRecoveryCodes (Id, UserId, CodeHash, CreatedAtUtc) VALUES (NEWID(), NEWID(), {new string('b', 64)}, SYSUTCDATETIME())");
            await orphan.Should().ThrowAsync<SqlException>().Where(e => e.Number == 547, "a code needs a real user");

            await db.Database.ExecuteSqlAsync($"DELETE FROM Users WHERE Id = {userId}");
            (await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM UserTotpFactors WHERE UserId = {userId}").SingleAsync()).Should().Be(0);
            (await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM UserRecoveryCodes WHERE UserId = {userId}").SingleAsync()).Should().Be(0);
        });
    }

    /// <summary>Every table, column, index, foreign key and check constraint outside the two new tables, as one comparable text.</summary>
    private static async Task<string> FingerprintOfEverythingElseAsync(AppDbContext db)
    {
        var rows = await db.Database.SqlQuery<string>($"""
            SELECT 'C|' + t.name COLLATE DATABASE_DEFAULT + '|' + c.name COLLATE DATABASE_DEFAULT + '|' + ty.name COLLATE DATABASE_DEFAULT + '|' + CAST(c.max_length AS varchar(10)) + '|' + CAST(c.is_nullable AS varchar(1)) AS Value
              FROM sys.tables t JOIN sys.columns c ON c.object_id = t.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id
             WHERE t.name NOT IN (N'UserTotpFactors', N'UserRecoveryCodes', N'__EFMigrationsHistory')
            UNION ALL
            SELECT 'I|' + OBJECT_NAME(i.object_id) COLLATE DATABASE_DEFAULT + '|' + i.name COLLATE DATABASE_DEFAULT + '|' + CAST(i.is_unique AS varchar(1)) + '|' + ISNULL(i.filter_definition COLLATE DATABASE_DEFAULT, '')
              FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id
             WHERE i.name IS NOT NULL AND t.name NOT IN (N'UserTotpFactors', N'UserRecoveryCodes', N'__EFMigrationsHistory')
            UNION ALL
            SELECT 'F|' + OBJECT_NAME(parent_object_id) COLLATE DATABASE_DEFAULT + '|' + name COLLATE DATABASE_DEFAULT + '|' + OBJECT_NAME(referenced_object_id) COLLATE DATABASE_DEFAULT + '|' + delete_referential_action_desc COLLATE DATABASE_DEFAULT
              FROM sys.foreign_keys WHERE OBJECT_NAME(parent_object_id) NOT IN (N'UserTotpFactors', N'UserRecoveryCodes')
            UNION ALL
            SELECT 'K|' + OBJECT_NAME(parent_object_id) COLLATE DATABASE_DEFAULT + '|' + name COLLATE DATABASE_DEFAULT + '|' + definition COLLATE DATABASE_DEFAULT FROM sys.check_constraints
            """).ToListAsync();
        return string.Join("\n", rows.Order(StringComparer.Ordinal));
    }

    private static Task<List<string>> NewTablesAsync(AppDbContext db) =>
        db.Database.SqlQuery<string>($"SELECT name AS Value FROM sys.tables WHERE name IN (N'UserTotpFactors', N'UserRecoveryCodes') ORDER BY name").ToListAsync();

    [Fact]
    public async Task Migration_round_trips_up_down_up_and_changes_nothing_but_the_two_new_tables()
    {
        var database = $"ClinicSaaS_Test_{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.SetConnectionString(ApiFactory.TestConnectionString(database));
        var migrator = db.GetService<IMigrator>();

        try
        {
            await migrator.MigrateAsync(BeforeMigration);
            var before = await FingerprintOfEverythingElseAsync(db);
            (await NewTablesAsync(db)).Should().BeEmpty();
            before.Should().Contain("C|Users|PasswordHash").And.Contain("C|RefreshTokens|FamilyId");

            await migrator.MigrateAsync(Migration);
            (await NewTablesAsync(db)).Should().Equal("UserRecoveryCodes", "UserTotpFactors");
            (await FingerprintOfEverythingElseAsync(db)).Should().Be(before, "no existing table, column, index, foreign key or constraint may change");

            await migrator.MigrateAsync(BeforeMigration);
            (await NewTablesAsync(db)).Should().BeEmpty();
            (await FingerprintOfEverythingElseAsync(db)).Should().Be(before);

            await migrator.MigrateAsync(Migration);
            (await NewTablesAsync(db)).Should().Equal("UserRecoveryCodes", "UserTotpFactors");
            (await FingerprintOfEverythingElseAsync(db)).Should().Be(before);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task The_model_has_no_pending_changes_beyond_the_migration()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.HasPendingModelChanges().Should().BeFalse();
        (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(m => m.EndsWith("_" + Migration));
    }
}
