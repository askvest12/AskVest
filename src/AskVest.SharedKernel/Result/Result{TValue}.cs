namespace AskVest.SharedKernel;

public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue value)
        : base(true, null)
    {
        _value = value;
    }

    internal Result(Error error)
        : base(false, error)
    {
    }

    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot read Value on a failed result.");
}
