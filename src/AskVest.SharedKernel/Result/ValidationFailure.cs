namespace AskVest.SharedKernel;

public sealed record ValidationFailure(string Field, string Message);
