namespace AskVest.Api;

internal sealed record ApiResponse<TData>(
    bool Success,
    string Message,
    TData? Data,
    ApiError? Error,
    string CorrelationId);
