namespace AskVest.SharedKernel;

public sealed record PageResult<TItem>
{
    public PageResult(IReadOnlyList<TItem> items, string? nextCursor, bool hasMore, int limit)
    {
        Items = items;
        NextCursor = nextCursor;
        HasMore = hasMore;
        Limit = limit;
    }

    public IReadOnlyList<TItem> Items { get; }

    public string? NextCursor { get; }

    public bool HasMore { get; }

    public int Limit { get; }
}
