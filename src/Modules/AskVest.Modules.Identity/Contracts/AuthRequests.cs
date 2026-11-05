namespace AskVest.Modules.Identity.Contracts;

public sealed record SignUpRequest(string? Email, string? Password);

public sealed record VerifyEmailRequest(string? Email, string? Code);

public sealed record ResendVerificationRequest(string? Email);
