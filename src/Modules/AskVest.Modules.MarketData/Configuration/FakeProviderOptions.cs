namespace AskVest.Modules.MarketData.Configuration;

public sealed class FakeProviderOptions
{
    public string Scenario { get; set; } = "normal";

    public string Session { get; set; } = "regular";

    public int DelayMinutes { get; set; } = 15;

    public int RateLimitRetrySeconds { get; set; } = 5;

    public string Currency { get; set; } = "USD";
}
