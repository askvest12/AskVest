namespace AskVest.Api;

internal sealed record ApiError(string Code, IReadOnlyList<ApiErrorDetail>? Details = null);
