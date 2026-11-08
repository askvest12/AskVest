using Microsoft.Extensions.Options;

namespace AskVest.Modules.Practice.Configuration;

public sealed class PracticeOptions
{
    public const string SectionName = "Practice";

    public decimal StartingCredits { get; set; } = 10000m;

    public int FeeBasisPoints { get; set; }

    public int QuantityScale { get; set; } = 6;

    public decimal MinOrderAmount { get; set; } = 1m;

    public decimal MaxOrderAmount { get; set; } = 100000m;

    public int MaxOpenStopOrders { get; set; } = 20;

    public int StopEvaluationIntervalSeconds { get; set; } = 15;

    public int ResetCooldownHours { get; set; } = 24;

    public int MaxResetsPer30Days { get; set; } = 3;

    public static IValidateOptions<PracticeOptions> Validator { get; } = new PracticeOptionsValidator();

    private sealed class PracticeOptionsValidator : IValidateOptions<PracticeOptions>
    {
        public ValidateOptionsResult Validate(string? name, PracticeOptions options)
        {
            var failures = new List<string>();
            void Check(bool condition, string message)
            {
                if (!condition)
                {
                    failures.Add(message);
                }
            }

            Check(options.StartingCredits > 0m, "Practice:StartingCredits must be positive.");
            Check(options.FeeBasisPoints >= 0, "Practice:FeeBasisPoints must not be negative.");
            Check(options.QuantityScale is >= 0 and <= 18, "Practice:QuantityScale must be between 0 and 18.");
            Check(options.MinOrderAmount > 0m, "Practice:MinOrderAmount must be positive.");
            Check(options.MaxOrderAmount >= options.MinOrderAmount,
                "Practice:MaxOrderAmount must be at least Practice:MinOrderAmount.");
            Check(options.MaxOpenStopOrders > 0, "Practice:MaxOpenStopOrders must be positive.");
            Check(options.StopEvaluationIntervalSeconds > 0,
                "Practice:StopEvaluationIntervalSeconds must be positive.");
            Check(options.ResetCooldownHours >= 0, "Practice:ResetCooldownHours must not be negative.");
            Check(options.MaxResetsPer30Days > 0, "Practice:MaxResetsPer30Days must be positive.");

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }
    }
}
