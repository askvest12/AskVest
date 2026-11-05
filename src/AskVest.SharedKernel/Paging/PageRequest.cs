namespace AskVest.SharedKernel;

public sealed record PageRequest
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    public PageRequest(int limit = DefaultLimit, string? cursor = null)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be at least 1.");
        }

        if (limit > MaxLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"Limit must not exceed {MaxLimit}.");
        }

        Limit = limit;
        Cursor = cursor;
    }

    public int Limit { get; }

    public string? Cursor { get; }
}
