namespace AskVest.SharedKernel;

public class Result
{
    private protected Result(bool isSuccess, Error? error)
    {
        if (isSuccess == (error is not null))
        {
            throw new ArgumentException("A result must be either a success without an error or a failure with an error.");
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error? Error { get; }

    public static Result Success() => new(true, null);

    public static Result<TValue> Success<TValue>(TValue value) => new(value);

    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Failure<TValue>(Error error) => new(error);
}
