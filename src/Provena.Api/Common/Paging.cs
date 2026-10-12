using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Contracts;
namespace Provena.Api.Common;
/// <summary>
/// Shared offset paging for tenant-wide internal list endpoints. Only ordered queries are accepted so ordering is part of the contract;
/// callers must end the ordering with a unique tiebreak (for example <c>ThenBy(x =&gt; x.Id)</c>) so pages never overlap.
/// </summary>
internal static class Paging
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public static bool IsValid(int page, int pageSize) => page >= 1 && pageSize is >= 1 and <= MaxPageSize;
    public static Task<PageResult<T>> ToPageAsync<T>(this IOrderedQueryable<T> query, int page, int pageSize, CancellationToken ct)
        => query.ToPageAsync(x => x, page, pageSize, ct);
    // The selector runs after Skip/Take because Select would drop the IOrderedQueryable type.
    public static async Task<PageResult<TResult>> ToPageAsync<TSource, TResult>(this IOrderedQueryable<TSource> query, Expression<Func<TSource, TResult>> selector, int page, int pageSize, CancellationToken ct)
    {
        if (!IsValid(page, pageSize)) throw new ApiException(400, $"Invalid pagination. page >= 1 and pageSize 1-{MaxPageSize} are required.");
        var skip = (long)(page - 1) * pageSize;
        if (skip > int.MaxValue) throw new ApiException(400, "Page is out of range.");
        var count = await query.CountAsync(ct);
        var items = await query.Skip((int)skip).Take(pageSize).Select(selector).ToListAsync(ct);
        return new(items, page, pageSize, count);
    }
}
