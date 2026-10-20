using ClinicSaaS.Infrastructure.Persistence;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// Phase 3B (decision 3A-D8): connection opens and plain reads outside a transaction may be retried on transient SQL
/// errors; writes, locks and anything inside a transaction never are. Real SQL Server, fault injection with a stateful
/// divide-by-zero (error 8134, declared transient for the test) that stops failing once the "outage" is over.
/// </summary>
public sealed class SqlResilienceTests
{
    private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options);

    private static SqlResilience Resilience(int[]? transient = null, int connectTries = 3, int readTries = 3) =>
        new(Options.Create(new SqlResilienceOptions
        {
            ConnectTries = connectTries, ReadTries = readTries, DeltaMilliseconds = 10, MaxIntervalMilliseconds = 20, TransientErrors = transient,
        }), NullLogger<SqlResilience>.Instance);

    private static ProbeContext Context(SqlResilience r, string database = "master") =>
        new(new DbContextOptionsBuilder<ProbeContext>()
            .UseSqlServer(ApiFactory.TestConnectionString(database))
            .AddInterceptors(new SqlConnectionRetryInterceptor(r), new SqlReadRetryInterceptor(r))
            .Options);

    /// <summary>
    /// A "database outage" that ends on cue: a query for a global temp table fails at the START of execution (error 208,
    /// declared transient for the test) until <see cref="End"/> creates it. The table lives in a connection this helper holds.
    /// </summary>
    private sealed class Outage : IAsyncDisposable
    {
        private readonly SqlConnection _keeper = new(ApiFactory.TestConnectionString("master"));
        public string Table { get; } = "##outage_" + Guid.NewGuid().ToString("N");

        public async Task StartAsync() => await _keeper.OpenAsync();

        public void End() => Exec($"CREATE TABLE {Table} (i int); INSERT {Table} VALUES (1)");

        public bool Ended()
        {
            using var cmd = new SqlCommand($"SELECT CASE WHEN OBJECT_ID('tempdb..{Table}') IS NULL THEN 0 ELSE 1 END", _keeper);
            return (int)cmd.ExecuteScalar()! == 1;
        }

        public string Read => $"SELECT COUNT(*) AS [Value] FROM {Table}";

        /// <summary>A write against the outage table (the name is a GUID generated here, never input).</summary>
        public string Write => $"INSERT {Table} VALUES (1)";

        private void Exec(string sql)
        {
            using var cmd = new SqlCommand(sql, _keeper);
            cmd.ExecuteNonQuery();
        }

        public ValueTask DisposeAsync() => _keeper.DisposeAsync();
    }

    // ------------------------------------------------------------------ connection

    [Fact]
    public async Task Opening_a_connection_is_retried_on_a_transient_error_and_then_gives_up()
    {
        var r = Resilience(connectTries: 2); // SqlClient's default transient list: error 4060 (cannot open database) is one of them
        var retries = 0;
        r.ConnectionRetrying += (_, _) => retries++;
        await using var ctx = Context(r, database: "ClinicSaaS_NoSuchDb_" + Guid.NewGuid().ToString("N"));

        var act = () => ctx.Database.OpenConnectionAsync();

        // When the attempts run out the original SqlException comes out (EF's provider adds its own non-retrying wrapper).
        var failure = (await act.Should().ThrowAsync<Exception>()).Which;
        (failure as SqlException ?? failure.InnerException as SqlException)!.Number.Should().Be(4060);
        retries.Should().Be(1, "two tries = one retry");
    }

    [Fact]
    public async Task One_try_means_no_retry()
    {
        var r = Resilience(connectTries: 1);
        var retries = 0;
        r.ConnectionRetrying += (_, _) => retries++;
        await using var ctx = Context(r, database: "ClinicSaaS_NoSuchDb_" + Guid.NewGuid().ToString("N"));

        await ctx.Awaiting(c => c.Database.OpenConnectionAsync()).Should().ThrowAsync<Exception>();
        retries.Should().Be(0);
    }

    [Fact]
    public async Task A_working_connection_opens_without_any_retry()
    {
        var r = Resilience();
        var retries = 0;
        r.ConnectionRetrying += (_, _) => retries++;
        await using var ctx = Context(r);

        await ctx.Database.OpenConnectionAsync();
        retries.Should().Be(0);
    }

    // ------------------------------------------------------------------ reads

    [Fact]
    public async Task A_plain_read_outside_a_transaction_is_retried_until_the_outage_ends()
    {
        await using var outage = new Outage();
        await outage.StartAsync();
        var r = Resilience(transient: [208]);
        var retries = 0;
        r.Reads.Retrying += (_, _) => { retries++; outage.End(); }; // the database "recovers" while we wait to retry
        await using var ctx = Context(r);

        var result = await ctx.Database.SqlQueryRaw<int>(outage.Read).ToListAsync();

        result.Should().Equal(1);
        retries.Should().Be(1);
    }

    [Fact]
    public async Task A_read_is_not_retried_for_an_error_that_is_not_transient()
    {
        await using var outage = new Outage();
        await outage.StartAsync();
        var r = Resilience(transient: [1205]); // 208 is NOT in this list
        var retries = 0;
        r.Reads.Retrying += (_, _) => retries++;
        await using var ctx = Context(r);

        await ctx.Awaiting(c => c.Database.SqlQueryRaw<int>(outage.Read).ToListAsync())
            .Should().ThrowAsync<SqlException>();
        retries.Should().Be(0);
    }

    [Fact]
    public async Task A_read_inside_a_transaction_is_never_retried()
    {
        await using var outage = new Outage();
        await outage.StartAsync();
        var r = Resilience(transient: [208]);
        var retries = 0;
        r.Reads.Retrying += (_, _) => { retries++; outage.End(); };
        await using var ctx = Context(r);
        await using var tx = await ctx.Database.BeginTransactionAsync();

        var act = () => ctx.Database.SqlQueryRaw<int>(outage.Read).ToListAsync();

        (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(208);
        retries.Should().Be(0, "a transaction's statements are never re-run on their own");
    }

    // ------------------------------------------------------------------ writes

    [Fact]
    public async Task A_write_is_never_retried_even_for_a_transient_error()
    {
        await using var outage = new Outage();
        await outage.StartAsync();
        var r = Resilience(transient: [208]);
        var retries = 0;
        r.Reads.Retrying += (_, _) => { retries++; outage.End(); };
        r.ConnectionRetrying += (_, _) => retries++;
        await using var ctx = Context(r);

        var act = () => ctx.Database.ExecuteSqlRawAsync(outage.Write);

        (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(208);
        retries.Should().Be(0);
        outage.Ended().Should().BeFalse("nothing ran a second time and nothing was created");
    }

    // ------------------------------------------------------------------ what counts as a plain read

    [Theory]
    [InlineData("SELECT 1", true)]
    [InlineData("  select [p].[Id] from [Patients] as [p] where [p].[TenantId] = @t", true)]
    [InlineData("SELECT [UpdatedAtUtc], [DeletedAtUtc] FROM [Patients]", true)]
    [InlineData("SELECT TOP(1) [u].[IsActive], CASE WHEN EXISTS (SELECT 1 FROM [RefreshTokens] AS [r]) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END FROM [Users] AS [u]", true)]
    [InlineData("INSERT [t] VALUES (1)", false)]
    [InlineData("UPDATE [t] SET [x] = 1", false)]
    [InlineData("DELETE FROM [t]", false)]
    [InlineData("SET NOCOUNT ON; INSERT INTO [t] ([x]) VALUES (1); SELECT [Id] FROM [t]", false)]
    [InlineData("SELECT * INTO [copy] FROM [t]", false)]
    [InlineData("SELECT 1; DELETE FROM [t]", false)]
    [InlineData("SELECT NEXT VALUE FOR [seq]", false)]
    [InlineData("DECLARE @r int; EXEC @r = sp_getapplock @Resource = 'x'; SELECT @r AS [Value];", false)]
    [InlineData("EXEC sp_getapplock @Resource = 'x'", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_single_plain_select_counts_as_a_retryable_read(string? sql, bool expected)
    {
        SqlResilience.IsPlainSelect(sql).Should().Be(expected);
    }
}
