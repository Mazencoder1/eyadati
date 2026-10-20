using ClinicSaaS.Application.Common;

namespace ClinicSaaS.Api.Errors;

/// <summary>Success envelope: <c>{ "success": true, "data": … }</c>.</summary>
public sealed record ApiResponse<T>(T Data)
{
    public bool Success => true;
}

public sealed record PageMeta(int Page, int PageSize, int TotalCount, int TotalPages);

/// <summary>Paged success envelope: <c>{ "success": true, "data": [...], "meta": {...} }</c>.</summary>
public sealed record ApiPagedResponse<T>(IReadOnlyList<T> Data, PageMeta Meta)
{
    public bool Success => true;

    public static ApiPagedResponse<T> From(PagedResult<T> r) =>
        new(r.Items, new PageMeta(r.Page, r.PageSize, r.TotalCount, r.TotalPages));
}

/// <summary>Unified error body. Never contains stack traces or internal exception messages.</summary>
public sealed record ApiError(
    string Code,
    string Message,
    string CorrelationId,
    IDictionary<string, string[]>? Errors = null,
    object? Details = null)
{
    public bool Success => false;
}
