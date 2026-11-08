using AskVest.Modules.Catalog.Availability;
using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Catalog.Domain.Enums;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Catalog;

public sealed class AvailabilityEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly HashSet<string> Countries = new(StringComparer.Ordinal) { "US", "NG" };

    public static IEnumerable<object[]> StatusCases()
    {
        yield return [TokenStatus.Draft, false, false];
        yield return [TokenStatus.Listed, true, true];
        yield return [TokenStatus.Suspended, false, false];
        yield return [TokenStatus.Delisted, false, true];
    }

    [Theory]
    [MemberData(nameof(StatusCases))]
    public void Evaluate_Covers_Every_Status(TokenStatus status, bool buyable, bool sellable)
    {
        var result = AvailabilityEvaluator.Evaluate(Facts(status));
        result.Buyable.ShouldBe(buyable);
        result.Sellable.ShouldBe(sellable);
    }

    [Fact]
    public void Evaluate_Healthy_Listed_Token_Is_Buyable_And_Sellable()
    {
        var result = AvailabilityEvaluator.Evaluate(Facts(TokenStatus.Listed));
        result.Buyable.ShouldBeTrue();
        result.Sellable.ShouldBeTrue();
        result.BuyBlockers.ShouldBeEmpty();
        result.SellBlockers.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_Listed_Buy_Guards_Each_Produce_Their_Blocker()
    {
        AvailabilityEvaluator.Evaluate(Facts(TokenStatus.Listed) with { IssuerApproved = false })
            .BuyBlockers.ShouldBe([AvailabilityEvaluator.IssuerNotApproved]);
        AvailabilityEvaluator.Evaluate(Facts(TokenStatus.Listed) with { ChainEnabled = false })
            .BuyBlockers.ShouldBe([AvailabilityEvaluator.ChainDisabled]);
        AvailabilityEvaluator.Evaluate(Facts(TokenStatus.Listed) with { ReviewExpiresAt = Now.AddSeconds(-1) })
            .BuyBlockers.ShouldBe([AvailabilityEvaluator.DisclosureReviewExpired]);
        AvailabilityEvaluator.Evaluate(Facts(TokenStatus.Listed) with { ReviewExpiresAt = null })
            .BuyBlockers.ShouldBe([AvailabilityEvaluator.DisclosureReviewExpired]);
        AvailabilityEvaluator.Evaluate(Facts(TokenStatus.Listed) with { UserCountry = "FR" })
            .BuyBlockers.ShouldBe([AvailabilityEvaluator.CountryNotAllowed]);
        AvailabilityEvaluator.Evaluate(Facts(TokenStatus.Listed) with { UserCountry = null })
            .BuyBlockers.ShouldBe([AvailabilityEvaluator.CountryUnknown]);
        AvailabilityEvaluator.Evaluate(Facts(TokenStatus.Listed) with { HasGatewayKey = false })
            .BuyBlockers.ShouldBe([AvailabilityEvaluator.NoExecutionRoute]);
    }

    [Fact]
    public void Evaluate_Combinations_Accumulate_Blockers()
    {
        var result = AvailabilityEvaluator.Evaluate(Facts(TokenStatus.Listed) with
        {
            IssuerApproved = false,
            ChainEnabled = false,
            UserCountry = null,
            HasGatewayKey = false,
        });
        result.Buyable.ShouldBeFalse();
        result.BuyBlockers.ShouldBe(
        [
            AvailabilityEvaluator.IssuerNotApproved,
            AvailabilityEvaluator.ChainDisabled,
            AvailabilityEvaluator.CountryUnknown,
            AvailabilityEvaluator.NoExecutionRoute,
        ]);
    }

    [Fact]
    public void Evaluate_Sell_On_Listed_And_Delisted_Only_Blocks_On_Chain_Or_Gateway()
    {
        foreach (var status in new[] { TokenStatus.Listed, TokenStatus.Delisted })
        {
            var healthy = AvailabilityEvaluator.Evaluate(Facts(status));
            healthy.Sellable.ShouldBeTrue();

            AvailabilityEvaluator.Evaluate(Facts(status) with { ChainEnabled = false })
                .SellBlockers.ShouldBe([AvailabilityEvaluator.ChainDisabled]);
            AvailabilityEvaluator.Evaluate(Facts(status) with { HasGatewayKey = false })
                .SellBlockers.ShouldBe([AvailabilityEvaluator.NoExecutionRoute]);
        }
    }

    [Fact]
    public void Evaluate_Sell_Ignores_Review_Expiry_And_Country()
    {
        var result = AvailabilityEvaluator.Evaluate(Facts(TokenStatus.Listed) with
        {
            ReviewExpiresAt = Now.AddDays(-30),
            UserCountry = "FR",
        });
        result.Sellable.ShouldBeTrue();
        result.SellBlockers.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_Draft_And_Suspended_Are_Not_Sellable()
    {
        AvailabilityEvaluator.Evaluate(Facts(TokenStatus.Draft)).SellBlockers
            .ShouldBe([AvailabilityEvaluator.TokenNotListed]);
        AvailabilityEvaluator.Evaluate(Facts(TokenStatus.Suspended)).SellBlockers
            .ShouldBe([AvailabilityEvaluator.TokenSuspended]);
    }

    [Fact]
    public void Evaluate_Delisted_Is_Not_Buyable_Even_When_All_Guards_Healthy()
    {
        var result = AvailabilityEvaluator.Evaluate(Facts(TokenStatus.Delisted));
        result.Buyable.ShouldBeFalse();
        result.BuyBlockers.ShouldBe([AvailabilityEvaluator.TokenNotListed]);
    }

    [Fact]
    public void Evaluate_Review_Expiring_Exactly_Now_Is_Expired()
    {
        var result = AvailabilityEvaluator.Evaluate(Facts(TokenStatus.Listed) with { ReviewExpiresAt = Now });
        result.BuyBlockers.ShouldBe([AvailabilityEvaluator.DisclosureReviewExpired]);
    }

    private static AvailabilityFacts Facts(TokenStatus status) => new(
        status,
        IssuerApproved: true,
        ChainEnabled: true,
        ReviewExpiresAt: Now.AddDays(30),
        Now: Now,
        AllowedCountries: Countries,
        UserCountry: "US",
        HasGatewayKey: true);
}
