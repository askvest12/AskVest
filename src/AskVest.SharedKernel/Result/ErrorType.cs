namespace AskVest.SharedKernel;

public enum ErrorType
{
    Validation,
    Unauthenticated,
    Forbidden,
    NotFound,
    Conflict,
    BusinessRule,
    RateLimited,
    Unavailable,
    Failure
}
