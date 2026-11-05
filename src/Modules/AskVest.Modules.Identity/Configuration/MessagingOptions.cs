using System.Collections.ObjectModel;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Configuration;

public enum SmsAdapterMode
{
    Disabled = 0,
    Development = 1,
}

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    public SmtpOptions Smtp { get; set; } = new();
    public SmsOptions Sms { get; set; } = new();
    public int QueueCapacity { get; set; } = 512;
    public int MaxAttempts { get; set; } = 3;
    public Collection<int> RetryBackoffSeconds { get; } = [1, 5, 25];

    public TimeSpan RetryDelay(int attempt)
    {
        var index = Math.Clamp(attempt - 1, 0, RetryBackoffSeconds.Count - 1);
        return TimeSpan.FromSeconds(RetryBackoffSeconds[index]);
    }

    public static IValidateOptions<MessagingOptions> Validator { get; } = new MessagingOptionsValidator();

    private sealed class MessagingOptionsValidator : IValidateOptions<MessagingOptions>
    {
        public ValidateOptionsResult Validate(string? name, MessagingOptions options)
        {
            var failures = new List<string>();
            if (options.QueueCapacity is < 1 or > 100_000)
            {
                failures.Add("QueueCapacity must be between 1 and 100000.");
            }

            if (options.MaxAttempts is < 1 or > 10)
            {
                failures.Add("MaxAttempts must be between 1 and 10.");
            }

            if (options.RetryBackoffSeconds.Count == 0
                || options.RetryBackoffSeconds.Any(seconds => seconds < 0 || seconds > 3600))
            {
                failures.Add("RetryBackoffSeconds must contain values between 0 and 3600.");
            }

            if (options.Smtp.Port is < 1 or > 65535)
            {
                failures.Add("Smtp:Port must be between 1 and 65535.");
            }

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }
}

public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? FromAddress { get; set; }
    public string? FromName { get; set; }
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public bool EnableStartTls { get; set; } = true;
}

public sealed class SmsOptions
{
    public SmsAdapterMode Mode { get; set; } = SmsAdapterMode.Disabled;
    public string? ForwardTo { get; set; }
}
