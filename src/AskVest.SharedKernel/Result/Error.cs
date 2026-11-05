namespace AskVest.SharedKernel;

public sealed record Error
{
    public Error(string code, string message, ErrorType type, IReadOnlyList<ValidationFailure>? details = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Error code is required.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Error message is required.", nameof(message));
        }

        Code = code;
        Message = message;
        Type = type;
        Details = details;
    }

    public string Code { get; }

    public string Message { get; }

    public ErrorType Type { get; }

    public IReadOnlyList<ValidationFailure>? Details { get; }
}
