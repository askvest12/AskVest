namespace AskVest.Modules.Identity.Contracts;

public sealed record AddPhoneRequest(string? Phone);

public sealed record VerifyPhoneRequest(string? Code);
