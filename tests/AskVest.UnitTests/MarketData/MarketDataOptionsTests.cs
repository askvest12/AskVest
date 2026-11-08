using AskVest.Modules.MarketData;
using AskVest.Modules.MarketData.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.MarketData;

public sealed class MarketDataOptionsTests
{
    private static MarketDataOptions Options()
    {
        var options = new MarketDataOptions();
        options.ApplyIntervalDefaults();
        return options;
    }

    private static ValidateOptionsResult Validate(MarketDataOptions options)
        => MarketDataOptions.Validator.Validate(null, options);

    [Fact]
    public void Defaults_Are_Valid()
    {
        Validate(Options()).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("15m", true)]
    [InlineData("5m", false)]
    [InlineData("1w", false)]
    public void Candle_Interval_Must_Be_Supported(string interval, bool valid)
    {
        var options = Options();
        options.CandleIntervals.Clear();
        options.CandleIntervals.Add(new CandleIntervalOptions
        {
            Interval = interval,
            MaxRangeDays = 7,
            MaxCandles = 500,
            OpenCacheSeconds = 60,
        });

        Validate(options).Succeeded.ShouldBe(valid);
    }

    [Fact]
    public void Duplicate_Intervals_Fail()
    {
        var options = Options();
        options.CandleIntervals.Add(new CandleIntervalOptions
        {
            Interval = "15m",
            MaxRangeDays = 7,
            MaxCandles = 500,
            OpenCacheSeconds = 60,
        });

        Validate(options).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void Empty_Intervals_Fail()
    {
        var options = Options();
        options.CandleIntervals.Clear();

        Validate(options).Succeeded.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    [InlineData(300)]
    [InlineData(301)]
    public void Stale_Threshold_Must_Exceed_Live_Threshold(int stale)
    {
        var options = Options();
        options.LiveThresholdSeconds = 30;
        options.StaleThresholdSeconds = stale;
        Validate(options).Succeeded.ShouldBe(stale > 30);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void Batch_Size_Bounds_Are_Enforced(int batchSize)
    {
        var options = Options();
        options.BatchSize = batchSize;
        Validate(options).Succeeded.ShouldBeFalse();
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("normal ")]
    public void Fake_Scenario_Must_Be_Known(string scenario)
    {
        var options = Options();
        options.Fake.Scenario = scenario;
        Validate(options).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void Unknown_Provider_Fails()
    {
        var options = Options();
        options.Provider = "vendor-x";
        Validate(options).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void Fake_Provider_In_Production_Throws()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Platform"] = "Host=localhost;Database=x",
        }).Build();

        Should.Throw<InvalidOperationException>(() =>
            services.AddMarketDataModule(configuration, new StubEnvironment("Production")));
    }

    [Fact]
    public void Fake_Provider_In_Development_Does_Not_Throw()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Platform"] = "Host=localhost;Database=x",
        }).Build();

        services.AddMarketDataModule(configuration, new StubEnvironment("Development"));
    }

    [Fact]
    public void Null_Environment_Skips_Guard_For_Migrator()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Platform"] = "Host=localhost;Database=x",
        }).Build();

        services.AddMarketDataModule(configuration);
    }

    private sealed class StubEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
