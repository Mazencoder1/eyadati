using ClinicSaaS.Domain.Common;

namespace ClinicSaaS.Application.Common;

/// <summary>An expected failure with a stable code and HTTP status. Mapped to the unified error response.</summary>
public class AppException(string code, int statusCode, string? detail = null, object? data = null) : Exception(detail ?? code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    /// <summary>Safe, non-sensitive extra data for the client (e.g. which limit was reached).</summary>
    public object? Details { get; } = data;
}

public sealed class NotFoundException(string code = ErrorCodes.NotFound) : AppException(code, 404);

public sealed class ForbiddenException(string code = ErrorCodes.Forbidden) : AppException(code, 403);

public sealed class UnauthorizedException(string code = ErrorCodes.Unauthorized) : AppException(code, 401);

public sealed class ConflictException(string code = ErrorCodes.Conflict, object? data = null) : AppException(code, 409, data: data);

public sealed class LockedException(string code) : AppException(code, 423);

public sealed class UsageLimitExceededException(string limitKey, int max)
    : AppException(ErrorCodes.UsageLimitReached, 403, data: new { limitKey, max });

public sealed class FeatureNotAvailableException(string featureKey)
    : AppException(ErrorCodes.FeatureNotAvailable, 403, data: new { featureKey });

/// <summary>Input failed validation. <see cref="Errors"/> maps field → message codes/messages.</summary>
public sealed class RequestValidationException(IDictionary<string, string[]> errors)
    : AppException(ErrorCodes.Validation, 400)
{
    public IDictionary<string, string[]> Errors { get; } = errors;
}
