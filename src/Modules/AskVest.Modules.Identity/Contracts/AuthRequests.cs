namespace AskVest.Modules.Identity.Contracts;

public sealed record SignUpRequest(string? Email, string? Password);

public sealed record VerifyEmailRequest(string? Email, string? Code);

public sealed record ResendVerificationRequest(string? Email);

public sealed record DeviceInfoRequest(
    string? InstallationId,
    string? Platform,
    string? DeviceName,
    string? AppVersion);

public sealed record LoginRequest(
    string? Email,
    string? Password,
    DeviceInfoRequest? Device);

public sealed record VerifyDeviceRequest(
    Guid? ChallengeId,
    string? Code);

public sealed record RefreshRequest(
    string? RefreshToken,
    string? InstallationId);

public sealed record ForgotPasswordRequest(string? Email);

public sealed record ResetPasswordRequest(string? Email, string? Code, string? Password);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? Password);

public sealed record DeletionRequest(string? Password, string? StepUpToken);
