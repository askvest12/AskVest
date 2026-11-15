using AskVest.Modules.Funding;
using AskVest.Modules.Funding.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Funding;

public sealed class FundingOptionsTests
{
    private static FundingOptions Valid()
    {
        var options = new FundingOptions
        {
            FxProvider = "fake",
        };
        options.Providers.Add(new FundingProviderOption
        {
            Key = "fake-funding",
            Adapter = "fake",
            WebhookSecret = "0123456789abcdef0123456789abcdef",
        });
        options.Currencies.Add(new FundingCurrencyOption
        {
            Code = "USD",
            Scale = 2,
            Provider = "fake-funding",
            Countries = { "US" },
            DepositMinimum = 1m,
            DepositMaximum = 10_000m,
            DepositDailyMaximum = 50_000m,
            WithdrawalMinimum = 1m,
            WithdrawalMaximum = 5_000m,
            WithdrawalDaily24Maximum = 20_000m,
            WithdrawalDaily30Maximum = 100_000m,
            WithdrawalFlatFee = 0m,
            WithdrawalFeeBps = 0,
            ReviewThresholdValue = 1_000m,
        });
        options.Pairs.Add(new FundingPairOption
        {
            From = "USD",
            To = "NGN",
            SpreadBps = 50,
            FeeBps = 100,
            MinimumSource = 1m,
            MaximumSource = 10_000m,
            DailyCapSource = 50_000m,
            MaxJumpPercent = 5m,
        });
        options.FxRates.Add(new FundingFxRateOption { From = "USD", To = "NGN", Rate = 1500m });
        return options;
    }

    [Fact]
    public void Empty_Section_Is_Valid()
        => FundingOptions.Validator.Validate(null, new FundingOptions()).Succeeded.ShouldBeTrue();

    [Fact]
    public void Valid_Configuration_Passes()
        => FundingOptions.Validator.Validate(null, Valid()).Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData("short", "fake")]
    [InlineData("0123456789abcdef0123456789abcdef", "realvault")]
    public void Provider_Secret_And_Adapter_Are_Checked(string secret, string adapter)
    {
        var options = Valid();
        options.Providers[0].WebhookSecret = secret;
        options.Providers[0].Adapter = adapter;
        FundingOptions.Validator.Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Missing_Fx_Rate_For_Enabled_Pair_Fails()
    {
        var options = Valid();
        options.FxRates.Clear();
        FundingOptions.Validator.Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Inverted_Deposit_Limits_Fail()
    {
        var options = Valid();
        options.Currencies[0].DepositMaximum = 0.5m;
        FundingOptions.Validator.Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Manual_Pause_For_Unknown_Currency_Fails()
    {
        var options = Valid();
        options.ManualPauses.Add(new FundingPauseOption { Currency = "ZZZ", Reason = "ops" });
        FundingOptions.Validator.Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Production_Refuses_Fake_Wiring()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = "Host=localhost;Database=none;Username=none;Password=none",
                ["Funding:FxProvider"] = "fake",
            })
            .Build();
        Should.Throw<InvalidOperationException>(
            () => services.AddFundingModule(configuration, new TestEnvironment("Production")));
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
