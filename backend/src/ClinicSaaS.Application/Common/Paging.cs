using System.Linq.Expressions;

namespace ClinicSaaS.Application.Common;

/// <summary>Server-side paging/sorting/search input shared by every list endpoint.</summary>
public record PageQuery
{
    public const int MaxPageSize = 100;

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public string? Search { get; init; }
    /// <summary>Field name, optionally prefixed with '-' for descending (e.g. "-createdAt").</summary>
    public string? Sort { get; init; }

    public int SafePage => Page < 1 ? 1 : Page;
    public int SafePageSize => PageSize switch { < 1 => 20, > MaxPageSize => MaxPageSize, _ => PageSize };
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public static class QueryableExtensions
{
    /// <summary>
    /// Applies a whitelisted sort. Unknown fields fall back to <paramref name="defaultSort"/> — client input
    /// never reaches the query as a raw column name.
    /// </summary>
    public static IQueryable<T> ApplySort<T>(
        this IQueryable<T> query,
        string? sort,
        IReadOnlyDictionary<string, Expression<Func<T, object>>> allowed,
        string defaultSort)
    {
        var raw = string.IsNullOrWhiteSpace(sort) ? defaultSort : sort.Trim();
        var desc = raw.StartsWith('-');
        var key = desc ? raw[1..] : raw;
        if (!allowed.TryGetValue(key, out var selector))
        {
            desc = defaultSort.StartsWith('-');
            selector = allowed[desc ? defaultSort[1..] : defaultSort];
        }
        return desc ? query.OrderByDescending(selector) : query.OrderBy(selector);
    }

    /// <summary>OFFSET/FETCH paging with a total count (two round-trips, both index-backed).</summary>
    public static async Task<PagedResult<TOut>> ToPagedResultAsync<T, TOut>(
        this IQueryable<T> query,
        PageQuery page,
        Expression<Func<T, TOut>> projection,
        CancellationToken ct)
    {
        var total = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(query, ct);
        var items = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            query.Skip((page.SafePage - 1) * page.SafePageSize).Take(page.SafePageSize).Select(projection), ct);
        return new PagedResult<TOut>(items, page.SafePage, page.SafePageSize, total);
    }
}
