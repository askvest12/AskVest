using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Configuration;

public sealed class IdentityOptions
{
    public const string SectionName = "Identity";

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan RefreshIdleLifetime { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan RefreshAbsoluteLifetime { get; set; } = TimeSpan.FromDays(90);
    public TimeSpan RefreshReuseGrace { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan OneTimeCodeLifetime { get; set; } = TimeSpan.FromMinutes(10);
    public int OneTimeCodeMaxAttempts { get; set; } = 5;
    public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromSeconds(60);
    public int CodesPerDestinationPerHour { get; set; } = 5;
    public int LoginFailuresPerAccount { get; set; } = 5;
    public int LoginFailuresPerClientIp { get; set; } = 20;
    public TimeSpan LoginThrottleWindow { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan LoginThrottleDuration { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan LoginThrottleMaxDuration { get; set; } = TimeSpan.FromHours(24);
    public int PinLength { get; set; } = 6;
    public int PinMaxFailedAttempts { get; set; } = 5;
    public TimeSpan PinLockDuration { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan PinLockMaxDuration { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan StepUpGrantLifetime { get; set; } = TimeSpan.FromSeconds(120);
    public int MaxActiveSessions { get; set; } = 10;
    public TimeSpan SessionLastUsedInterval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan DeletionCoolingOff { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan AnonymizationInterval { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromMinutes(10);
    public int PasswordMinLength { get; set; } = 10;
    public int PasswordMaxLength { get; set; } = 128;
    public int EmailMaxLength { get; set; } = 254;
    public int Argon2MemoryKiB { get; set; } = 19456;
    public int Argon2Iterations { get; set; } = 2;
    public int Argon2Parallelism { get; set; } = 1;
    public int CredentialHashLengthBytes { get; set; } = 32;
    public int CredentialSaltLengthBytes { get; set; } = 16;
    public int CredentialHashMaxConcurrency { get; set; } = 4;
    public int CredentialHashQueueTimeoutSeconds { get; set; } = 10;
    public string AccessTokenIssuer { get; set; } = "askvest";
    public string AccessTokenAudience { get; set; } = "askvest-api";
    public bool PasswordBreachCheckEnabled { get; set; } = true;
    public string? DefaultPhoneRegion { get; set; }
    public bool PhoneVerificationEnabled { get; set; }

    public static IValidateOptions<IdentityOptions> Validator { get; } = new IdentityOptionsValidator();

    private sealed class IdentityOptionsValidator : IValidateOptions<IdentityOptions>
    {
        public ValidateOptionsResult Validate(string? name, IdentityOptions options)
        {
            var failures = new List<string>();
            void Check(bool condition, string message)
            {
                if (!condition)
                {
                    failures.Add(message);
                }
            }

            Check(options.AccessTokenLifetime > TimeSpan.Zero, "AccessTokenLifetime must be positive.");
            Check(options.RefreshIdleLifetime > TimeSpan.Zero, "RefreshIdleLifetime must be positive.");
            Check(options.RefreshAbsoluteLifetime > options.RefreshIdleLifetime, "RefreshAbsoluteLifetime must exceed RefreshIdleLifetime.");
            Check(options.RefreshReuseGrace >= TimeSpan.Zero, "RefreshReuseGrace cannot be negative.");
            Check(options.OneTimeCodeLifetime > TimeSpan.Zero, "OneTimeCodeLifetime must be positive.");
            Check(options.OneTimeCodeMaxAttempts > 0, "OneTimeCodeMaxAttempts must be positive.");
            Check(options.ResendCooldown >= TimeSpan.Zero, "ResendCooldown cannot be negative.");
            Check(options.CodesPerDestinationPerHour > 0, "CodesPerDestinationPerHour must be positive.");
            Check(options.LoginFailuresPerAccount > 0, "LoginFailuresPerAccount must be positive.");
            Check(options.LoginFailuresPerClientIp > 0, "LoginFailuresPerClientIp must be positive.");
            Check(options.LoginThrottleWindow > TimeSpan.Zero, "LoginThrottleWindow must be positive.");
            Check(options.LoginThrottleDuration > TimeSpan.Zero, "LoginThrottleDuration must be positive.");
            Check(options.LoginThrottleMaxDuration >= options.LoginThrottleDuration, "LoginThrottleMaxDuration must be at least LoginThrottleDuration.");
            Check(options.PinLength == 6, "PinLength must be exactly 6.");
            Check(options.PinMaxFailedAttempts > 0, "PinMaxFailedAttempts must be positive.");
            Check(options.PinLockDuration > TimeSpan.Zero, "PinLockDuration must be positive.");
            Check(options.PinLockMaxDuration >= options.PinLockDuration, "PinLockMaxDuration must be at least PinLockDuration.");
            Check(options.StepUpGrantLifetime > TimeSpan.Zero, "StepUpGrantLifetime must be positive.");
            Check(options.MaxActiveSessions > 0, "MaxActiveSessions must be positive.");
            Check(options.SessionLastUsedInterval > TimeSpan.Zero, "SessionLastUsedInterval must be positive.");
            Check(options.DeletionCoolingOff > TimeSpan.Zero, "DeletionCoolingOff must be positive.");
            Check(options.AnonymizationInterval > TimeSpan.Zero, "AnonymizationInterval must be positive.");
            Check(options.MaintenanceInterval > TimeSpan.Zero, "MaintenanceInterval must be positive.");
            Check(options.PasswordMinLength >= 8 && options.PasswordMinLength <= options.PasswordMaxLength, "PasswordMinLength must be at least 8 and not exceed PasswordMaxLength.");
            Check(options.PasswordMaxLength <= 256, "PasswordMaxLength must not exceed 256.");
            Check(options.EmailMaxLength is >= 3 and <= 254, "EmailMaxLength must be between 3 and 254.");
            Check(options.Argon2MemoryKiB >= 1024, "Argon2MemoryKiB must be at least 1024.");
            Check(options.Argon2Iterations > 0, "Argon2Iterations must be positive.");
            Check(options.Argon2Parallelism > 0, "Argon2Parallelism must be positive.");
            Check(options.CredentialHashLengthBytes >= 16, "CredentialHashLengthBytes must be at least 16.");
            Check(options.CredentialSaltLengthBytes >= 8, "CredentialSaltLengthBytes must be at least 8.");
            Check(options.CredentialHashMaxConcurrency > 0, "CredentialHashMaxConcurrency must be positive.");
            Check(options.CredentialHashQueueTimeoutSeconds > 0, "CredentialHashQueueTimeoutSeconds must be positive.");
            Check(!string.IsNullOrWhiteSpace(options.AccessTokenIssuer), "AccessTokenIssuer is required.");
            Check(!string.IsNullOrWhiteSpace(options.AccessTokenAudience), "AccessTokenAudience is required.");

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }
}
