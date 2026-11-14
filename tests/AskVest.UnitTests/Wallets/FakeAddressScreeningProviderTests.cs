using AskVest.Modules.Wallets.Configuration;
using AskVest.Modules.Wallets.Screening;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Wallets;

public sealed class FakeAddressScreeningProviderTests
{
    private static FakeAddressScreeningProvider Provider(string scenario, params string[] markers)
    {
        var options = new WalletsOptions();
        options.Screening.Fake.Scenario = scenario;
        foreach (var marker in markers)
        {
            options.Screening.Fake.HitMarkers.Add(marker);
        }

        return new FakeAddressScreeningProvider(Options.Create(options));
    }

    [Fact]
    public async Task Clean_Address_Screens_Clear()
    {
        var result = await Provider("normal", "bad-actor").ScreenAsync(
            "eip155:1", "ordinary-address", TestContext.Current.CancellationToken);
        result.Kind.ShouldBe(ScreeningOutcomeKind.Clear);
        result.Reference.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Marked_Address_Screens_Hit()
    {
        var result = await Provider("normal", "bad-actor").ScreenAsync(
            "eip155:1", "addr-bad-actor-1", TestContext.Current.CancellationToken);
        result.Kind.ShouldBe(ScreeningOutcomeKind.Hit);
        result.Reference.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Marker_Match_Is_Case_Insensitive()
    {
        var result = await Provider("normal", "BAD-ACTOR").ScreenAsync(
            "eip155:1", "addr-bad-actor-1", TestContext.Current.CancellationToken);
        result.Kind.ShouldBe(ScreeningOutcomeKind.Hit);
    }

    [Fact]
    public async Task Error_Scenario_Fails()
    {
        var result = await Provider("error").ScreenAsync(
            "eip155:1", "any", TestContext.Current.CancellationToken);
        result.Kind.ShouldBe(ScreeningOutcomeKind.Error);
    }
}
