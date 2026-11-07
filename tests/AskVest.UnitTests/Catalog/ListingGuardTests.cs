using AskVest.Modules.Catalog.Import;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Catalog;

public sealed class ListingGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_Returns_No_Failures_For_A_Healthy_Token()
    {
        ListingGuard.Evaluate(Healthy()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(nameof(ListingGuardInput.IssuerApproved), false, ListingGuard.IssuerNotApproved)]
    [InlineData(nameof(ListingGuardInput.ChainEnabled), false, ListingGuard.ChainDisabled)]
    [InlineData(nameof(ListingGuardInput.ChainFamilySupported), false, ListingGuard.UnsupportedChainFamily)]
    [InlineData(nameof(ListingGuardInput.DisclosuresComplete), false, ListingGuard.IncompleteDisclosures)]
    [InlineData(nameof(ListingGuardInput.HasGatewayKey), false, ListingGuard.MissingGatewayKey)]
    [InlineData(nameof(ListingGuardInput.InstrumentActive), false, ListingGuard.InstrumentNotActive)]
    public void Evaluate_Reports_Each_Boolean_Guard_Alone(string property, object value, string expected)
    {
        var input = With(Healthy(), property, value);
        ListingGuard.Evaluate(input).ShouldBe([expected]);
    }

    [Fact]
    public void Evaluate_Reports_Each_Numeric_And_Date_Guard()
    {
        ListingGuard.Evaluate(Healthy() with { AllowedCountryCount = 0 })
            .ShouldBe([ListingGuard.NoAllowedCountries]);
        ListingGuard.Evaluate(Healthy() with { Decimals = 19 })
            .ShouldBe([ListingGuard.DecimalsAboveMaximum]);
        ListingGuard.Evaluate(Healthy() with { ReviewedAt = null, ReviewExpiresAt = null })
            .ShouldBe([ListingGuard.ReviewMissing]);
        ListingGuard.Evaluate(Healthy() with { ReviewedAt = Now.AddDays(1) })
            .ShouldBe([ListingGuard.ReviewDateInFuture]);
        ListingGuard.Evaluate(Healthy() with { ReviewExpiresAt = Now.AddDays(-1) })
            .ShouldBe([ListingGuard.ReviewExpired]);
        ListingGuard.Evaluate(Healthy() with
        {
            ReviewedAt = Now.AddDays(-400),
            ReviewExpiresAt = Now.AddDays(1),
        }).ShouldBe([ListingGuard.ReviewWindowTooLong]);
    }

    [Fact]
    public void Evaluate_Reports_Every_Failure_When_Multiple_Guards_Break()
    {
        var failures = ListingGuard.Evaluate(Healthy() with
        {
            IssuerApproved = false,
            ChainEnabled = false,
            DisclosuresComplete = false,
            AllowedCountryCount = 0,
            HasGatewayKey = false,
            Decimals = 24,
            ReviewedAt = null,
            ReviewExpiresAt = null,
            InstrumentActive = false,
        });

        failures.Count.ShouldBe(8);
        failures.ShouldContain(ListingGuard.IssuerNotApproved);
        failures.ShouldContain(ListingGuard.ChainDisabled);
        failures.ShouldContain(ListingGuard.IncompleteDisclosures);
        failures.ShouldContain(ListingGuard.NoAllowedCountries);
        failures.ShouldContain(ListingGuard.MissingGatewayKey);
        failures.ShouldContain(ListingGuard.DecimalsAboveMaximum);
        failures.ShouldContain(ListingGuard.ReviewMissing);
        failures.ShouldContain(ListingGuard.InstrumentNotActive);
    }

    [Fact]
    public void Evaluate_Allows_Review_Window_Up_To_The_Configured_Maximum()
    {
        var reviewedAt = Now.AddDays(-364);
        var input = Healthy() with
        {
            ReviewedAt = reviewedAt,
            ReviewExpiresAt = reviewedAt.AddMonths(12),
        };
        ListingGuard.Evaluate(input).ShouldBeEmpty();
    }

    private static ListingGuardInput Healthy() => new(
        IssuerApproved: true,
        ChainEnabled: true,
        ChainFamilySupported: true,
        DisclosuresComplete: true,
        AllowedCountryCount: 2,
        HasGatewayKey: true,
        Decimals: 18,
        ReviewedAt: Now.AddDays(-30),
        ReviewExpiresAt: Now.AddDays(335),
        Now: Now,
        ReviewMaxMonths: 12,
        InstrumentActive: true);

    private static ListingGuardInput With(ListingGuardInput input, string property, object value)
        => property switch
        {
            nameof(ListingGuardInput.IssuerApproved) => input with { IssuerApproved = (bool)value },
            nameof(ListingGuardInput.ChainEnabled) => input with { ChainEnabled = (bool)value },
            nameof(ListingGuardInput.ChainFamilySupported) => input with { ChainFamilySupported = (bool)value },
            nameof(ListingGuardInput.DisclosuresComplete) => input with { DisclosuresComplete = (bool)value },
            nameof(ListingGuardInput.HasGatewayKey) => input with { HasGatewayKey = (bool)value },
            nameof(ListingGuardInput.InstrumentActive) => input with { InstrumentActive = (bool)value },
            _ => throw new ArgumentOutOfRangeException(nameof(property), property, "Unknown guard property."),
        };
}
