using AskVest.Modules.MarketData.Providers;
using AskVest.Modules.MarketData.Services;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.MarketData;

public sealed class QuoteValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static ProviderQuote Valid() => new(
        "EX:AAA",
        101.5m,
        "USD",
        Now.AddSeconds(-5),
        "regular",
        null);

    [Fact]
    public void Valid_Quote_Passes()
    {
        QuoteValidator.Validate(Valid(), "USD", true, 8, Now).ShouldBeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Non_Positive_Price_Is_Rejected(string price)
    {
        var quote = Valid() with { Price = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture) };
        QuoteValidator.Validate(quote, "USD", true, 8, Now).ShouldNotBeNull();
    }

    [Fact]
    public void Wrong_Currency_Is_Rejected()
    {
        var quote = Valid() with { Currency = "EUR" };
        QuoteValidator.Validate(quote, "USD", true, 8, Now).ShouldNotBeNull();
    }

    [Fact]
    public void Too_Many_Fractional_Digits_Is_Rejected()
    {
        var quote = Valid() with { Price = 1.123456789m };
        QuoteValidator.Validate(quote, "USD", true, 8, Now).ShouldNotBeNull();
        QuoteValidator.Validate(quote, "USD", true, 9, Now).ShouldBeNull();
    }

    [Fact]
    public void Future_Timestamp_Beyond_Tolerance_Is_Rejected()
    {
        var quote = Valid() with { AsOf = Now.AddSeconds(61) };
        QuoteValidator.Validate(quote, "USD", true, 8, Now).ShouldNotBeNull();
    }

    [Fact]
    public void Timestamp_Within_Sixty_Seconds_Is_Accepted()
    {
        var quote = Valid() with { AsOf = Now.AddSeconds(30) };
        QuoteValidator.Validate(quote, "USD", true, 8, Now).ShouldBeNull();
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("REGULAR")]
    [InlineData("")]
    public void Unknown_Session_Value_Is_Rejected_When_Provider_Reports_Session(string session)
    {
        var quote = Valid() with { Session = session };
        QuoteValidator.Validate(quote, "USD", true, 8, Now).ShouldNotBeNull();
    }

    [Fact]
    public void Session_Is_Not_Checked_When_Provider_Does_Not_Report()
    {
        var quote = Valid() with { Session = "anything" };
        QuoteValidator.Validate(quote, "USD", false, 8, Now).ShouldBeNull();
    }

    [Fact]
    public void Non_Positive_Previous_Close_Is_Rejected()
    {
        var quote = Valid() with { PreviousClose = 0m };
        QuoteValidator.Validate(quote, "USD", true, 8, Now).ShouldNotBeNull();
    }
}
