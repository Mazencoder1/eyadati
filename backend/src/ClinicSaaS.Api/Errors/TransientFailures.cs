using Microsoft.Data.SqlClient;

namespace ClinicSaaS.Api.Errors;

/// <summary>
/// Recognises failures that say "the database (or its connection) is briefly unavailable", as opposed to a bug or a rule
/// being broken. They are answered with 503 <c>TEMPORARILY_UNAVAILABLE</c> + <c>Retry-After</c> and never with details:
/// the caller can try again, and no write is ever retried automatically behind their back (see SqlResilience).
/// </summary>
public static class TransientFailures
{
    /// <summary>Seconds suggested in <c>Retry-After</c>.</summary>
    public const int RetryAfterSeconds = 5;

    // SqlException.IsTransient covers throttling, failover and deadlock victims; these add the network/timeout/pool cases
    // a clinic will actually see: -2 command timeout, 53/64/233 connection, 1222 lock timeout, 10053/10054/10060/10061
    // socket errors, 11001 name resolution, 17142/18401 server paused/starting.
    private static readonly HashSet<int> Numbers = [-2, 53, 64, 233, 1205, 1222, 4060, 10053, 10054, 10060, 10061, 11001, 17142, 18401, 40197, 40501, 40613];

    public static bool IsTransient(Exception exception) => exception switch
    {
        // Exhausted connection retries throw one AggregateException holding every attempt's SqlException.
        AggregateException a => a.InnerExceptions.Count > 0 && a.InnerExceptions.All(IsTransient),
        SqlException s => s.IsTransient || Numbers.Contains(s.Number) || s.Errors.Cast<SqlError>().Any(e => Numbers.Contains(e.Number)),
        // AppDbContext.AcquireLockAsync: another request held the booking/walk-in/completion lock for 10 s.
        TimeoutException => true,
        // "Timeout expired. The timeout period elapsed prior to obtaining a connection from the pool."
        InvalidOperationException { Message: var m } when m.Contains("obtaining a connection from the pool", StringComparison.OrdinalIgnoreCase) => true,
        _ => exception.InnerException is { } inner && IsTransient(inner),
    };

    /// <summary>A short, data-free label for the log line (never the exception message).</summary>
    public static string Describe(Exception exception) => exception switch
    {
        AggregateException a => Describe(a.InnerExceptions[0]),
        SqlException s => $"sql-{s.Number}",
        TimeoutException => "lock-or-operation-timeout",
        InvalidOperationException => "connection-pool-exhausted",
        _ => exception.InnerException is { } inner ? Describe(inner) : exception.GetType().Name,
    };
}
