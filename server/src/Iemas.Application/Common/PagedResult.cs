namespace Iemas.Application.Common;

public record PagedResult<T>(List<T> Items, int TotalCount, int Page, int PageSize, int TotalPages)
{
    public static PagedResult<T> Create(List<T> items, int totalCount, int page, int pageSize) =>
        new(items, totalCount, page, pageSize, Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize)));
}
