using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClinicSaaS.Infrastructure.Persistence;

public sealed class SqlResilienceOptions
{
    public const string Section = "Database:Resilience";

    /// <summary>Attempts to open a connection (1 = no retry).</summary>
    public int ConnectTries { get; set; } = 2;
    /// <summary>Attempts for a plain read outside any transaction (1 = no retry).</summary>
    public int ReadTries { get; set; } = 3;
    public int DeltaMilliseconds { get; set; } = 300;
    public int MaxIntervalMilliseconds { get; set; } = 2000;
    /// <summary>Overrides SqlClient's list of transient error numbers. Tests only; leave null in production.</summary>
    public int[]? TransientErrors { get; set; }
}

/// <summary>
/// The only automatic retries the API performs against SQL Server — deliberately narrow (Phase 3B, decision 3A-D8):
/// <list type="bullet">
/// <item><b>Opening a connection</b>: nothing has been sent yet, so retrying a transient failure is always safe.</item>
/// <item><b>A plain read</b>: a <c>SELECT</c> that is not inside a transaction, which has no side effects.</item>
/// </list>
/// Everything else — inserts, updates, deletes, <c>sp_getapplock</c>, and anything inside an explicit transaction
/// (booking, rescheduling, patient registration, prescriptions, visit completion…) — is never retried here. If the
/// connection drops after the server committed but before the client heard, a retry would repeat the write; without an
/// idempotency key (which would need a table) the only safe answer is to fail and let the person retry knowingly.
/// Such failures become 503 <c>TEMPORARILY_UNAVAILABLE</c> with <c>Retry-After</c> (see GlobalExceptionHandler).
/// Neither mechanism is EF's execution strategy, so no unit of work is ever re-run: connection opens use a small loop of
/// our own (it keeps the original SqlException), plain reads use SqlClient's configurable retry logic.
/// </summary>
public sealed partial class SqlResilience
{
    public SqlRetryLogicBaseProvider Reads { get; }

    private readonly SqlResilienceOptions _options;
    private readonly ILogger _logger;

    /// <summary>Raised before each connection retry: attempt number that failed, SQL error number.</summary>
    public event Action<int, int>? ConnectionRetrying;

    public SqlResilience(IOptions<SqlResilienceOptions> options, ILogger<SqlResilience> logger)
    {
        var o = options.Value;
        _options = o;
        _logger = logger;
        Reads = Create(o, o.ReadTries);
        Reads.Retrying += (_, e) => Log(logger, "read", e);
    }

    private static SqlRetryLogicBaseProvider Create(SqlResilienceOptions o, int tries)
    {
        var retry = new SqlRetryLogicOption
        {
            NumberOfTries = Math.Max(1, tries),
            DeltaTime = TimeSpan.FromMilliseconds(Math.Max(1, o.DeltaMilliseconds)),
            MaxTimeInterval = TimeSpan.FromMilliseconds(Math.Max(1, o.MaxIntervalMilliseconds)),
            TransientErrors = o.TransientErrors,
            // Belt and braces: even if a provider were attached to the wrong command, SqlClient itself would refuse to
            // retry anything that is not a single plain SELECT.
            AuthorizedSqlCondition = IsPlainSelect,
        };
        return SqlConfigurableRetryFactory.CreateExponentialRetryProvider(retry);
    }

    private static void Log(ILogger logger, string what, SqlRetryingEventArgs e)
    {
        // Operators need to see transient trouble; the message carries no data (error number and timing only).
        var number = (e.Exceptions.LastOrDefault() as SqlException)?.Number;
        logger.LogWarning("Transient SQL {Operation} failure (error {ErrorNumber}); retry {Attempt} in {DelayMs} ms",
            what, number, e.RetryCount, (int)e.Delay.TotalMilliseconds);
    }

    // The error numbers SqlClient's own retry logic treats as transient by default (throttling, failover, deadlock victim,
    // lock timeout, cannot-open-database, network resets/timeouts).
    private static readonly HashSet<int> DefaultTransient =
        [20, 64, 233, 997, 1204, 1205, 1222, 4060, 4221, 10053, 10054, 10060, 10928, 10929, 40143, 40197, 40501, 40540, 40613, 49918, 49919, 49920];

    private bool IsTransient(SqlException e) =>
        _options.TransientErrors is { } custom ? custom.Contains(e.Number) : e.IsTransient || DefaultTransient.Contains(e.Number);

    /// <summary>
    /// Opens the connection, retrying a transient failure up to <see cref="SqlResilienceOptions.ConnectTries"/> times with
    /// exponential back-off. Safe by construction: a connection that has not opened has executed nothing. When the attempts
    /// run out, the last failure is rethrown unchanged.
    /// </summary>
    public async ValueTask OpenAsync(DbConnection connection, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await connection.OpenAsync(ct);
                return;
            }
            catch (SqlException e) when (attempt < Math.Max(1, _options.ConnectTries) && IsTransient(e))
            {
                var delay = NextDelay(attempt, e);
                await Task.Delay(delay, ct);
            }
        }
    }

    public void Open(DbConnection connection)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                connection.Open();
                return;
            }
            catch (SqlException e) when (attempt < Math.Max(1, _options.ConnectTries) && IsTransient(e))
            {
                Thread.Sleep(NextDelay(attempt, e));
            }
        }
    }

    private TimeSpan NextDelay(int failedAttempt, SqlException e)
    {
        var ms = Math.Min(_options.MaxIntervalMilliseconds, _options.DeltaMilliseconds * Math.Pow(2, failedAttempt - 1));
        _logger.LogWarning("Transient SQL connection failure (error {ErrorNumber}); retry {Attempt} in {DelayMs} ms", e.Number, failedAttempt, (int)ms);
        ConnectionRetrying?.Invoke(failedAttempt, e.Number);
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>A single SELECT statement: no data-changing keyword, no EXEC, no INTO, no second statement.</summary>
    public static bool IsPlainSelect(string? sql) =>
        sql is not null && StartsWithSelect().IsMatch(sql) && !Forbidden().IsMatch(sql) && !sql.TrimEnd().TrimEnd(';').Contains(';');

    /// <summary>The rule that decides whether a command may carry the read-retry provider.</summary>
    public static bool IsRetryableRead(DbCommand command) =>
        command is SqlCommand && command.Transaction is null && IsPlainSelect(command.CommandText);

    [GeneratedRegex(@"^\s*SELECT\b", RegexOptions.IgnoreCase)]
    private static partial Regex StartsWithSelect();

    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|MERGE|EXEC|EXECUTE|INTO|DECLARE|TRUNCATE|DROP|ALTER|CREATE|NEXT\s+VALUE\s+FOR)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();
}

/// <summary>
/// Opens every connection through <see cref="SqlResilience.OpenAsync"/>, so a transient failure to connect is retried.
/// It performs the open itself and tells EF to skip its own (<c>Suppress</c>), which is what keeps the ORIGINAL
/// <see cref="SqlException"/> flowing out when the attempts run out: EF's "does this database exist?" probe relies on
/// seeing error 4060 as a plain SqlException, and SqlClient's retry provider would have wrapped it in an AggregateException.
/// </summary>
public sealed class SqlConnectionRetryInterceptor(SqlResilience resilience) : DbConnectionInterceptor
{
    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        resilience.Open(connection);
        return InterceptionResult.Suppress();
    }

    public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        await resilience.OpenAsync(connection, cancellationToken);
        return InterceptionResult.Suppress();
    }
}

/// <summary>Gives plain reads outside a transaction the read-retry provider. No other command is touched.</summary>
public sealed class SqlReadRetryInterceptor(SqlResilience resilience) : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Attach(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Attach(command);
        return ValueTask.FromResult(result);
    }

    private void Attach(DbCommand command)
    {
        if (SqlResilience.IsRetryableRead(command)) ((SqlCommand)command).RetryLogicProvider = resilience.Reads;
    }
}
