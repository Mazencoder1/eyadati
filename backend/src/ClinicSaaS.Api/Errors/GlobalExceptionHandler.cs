using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.Api.Errors;

/// <summary>Maps every exception to the unified error body. Unexpected errors are logged with the correlation id only.</summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var request = http.RequestServices.GetRequiredService<IRequestContext>();
        var lang = request.Language;
        ApiError Error(string code) => new(code, ErrorCatalog.Message(code, lang), request.CorrelationId);
        var (status, error) = exception switch
        {
            RequestValidationException v => (400, new ApiError(v.Code, ErrorCatalog.Message(v.Code, lang), request.CorrelationId,
                v.Errors.ToDictionary(e => e.Key, e => e.Value.Select(c => ErrorCatalog.Has(c) ? ErrorCatalog.Message(c, lang) : c).ToArray()))),
            AppException a => (a.StatusCode, new ApiError(a.Code, ErrorCatalog.Message(a.Code, lang), request.CorrelationId, Details: a.Details)),
            DomainException d => (422, new ApiError(d.Code, ErrorCatalog.Message(d.Code, lang), request.CorrelationId)),
            DbUpdateConcurrencyException => (409, Error(ErrorCodes.ConcurrencyConflict)),
            DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } } => (409, Error(ErrorCodes.Conflict)),
            // Cross-tenant write attempt: indistinguishable from "not found" for the caller, loud in the logs.
            TenantIsolationViolationException => (404, Error(ErrorCodes.NotFound)),
            BadHttpRequestException => (400, Error(ErrorCodes.Validation)),
            OperationCanceledException when http.RequestAborted.IsCancellationRequested => (499, Error(ErrorCodes.Internal)),
            // The database or a lock was briefly unavailable. Reads and connection opens were already retried; writes never
            // are, so the caller decides whether to try again (see SqlResilience). No internal detail is returned.
            _ when TransientFailures.IsTransient(exception) => (503, Error(ErrorCodes.TemporarilyUnavailable)),
            _ => (500, Error(ErrorCodes.Internal)),
        };
        if (status == 503) http.Response.Headers.RetryAfter = TransientFailures.RetryAfterSeconds.ToString();

        switch (exception)
        {
            case TenantIsolationViolationException:
                logger.LogCritical(SecurityLogEvents.TenantIsolationViolation, exception, "Tenant isolation violation blocked");
                break;
            case AppException or DomainException or DbUpdateConcurrencyException:
                logger.LogInformation("Request failed with {Code}", error.Code);
                break;
            default:
                if (status == 503)
                    // Operators need the cause; the label has no data, the exception carries the server-side detail.
                    logger.LogWarning(SecurityLogEvents.DatabaseTemporarilyUnavailable, exception, "Database temporarily unavailable ({Reason}); answered 503 with Retry-After", TransientFailures.Describe(exception));
                else if (status >= 500)
                    logger.LogError(SecurityLogEvents.UnhandledException, exception, "Unhandled exception");
                break;
        }

        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(error, ct);
        return true;
    }

}
