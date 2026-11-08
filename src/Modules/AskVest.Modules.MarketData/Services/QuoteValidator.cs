using AskVest.Modules.MarketData.Domain;
using AskVest.Modules.MarketData.Providers;

namespace AskVest.Modules.MarketData.Services;

internal static class QuoteValidator
{
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromSeconds(60);

    public static string? Validate(
        ProviderQuote quote,
        string expectedCurrency,
        bool reportsSession,
        int maxFractionalDigits,
        DateTimeOffset now)
    {
        if (quote.Price <= 0m)
        {
            return "price is not positive";
        }

        if (FractionalDigits(quote.Price) > maxFractionalDigits)
        {
            return "price has too many fractional digits";
        }

        if (!string.Equals(quote.Currency, expectedCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return "currency does not match the instrument quote currency";
        }

        if (quote.AsOf > now + FutureTolerance)
        {
            return "timestamp is in the future";
        }

        if (reportsSession && !MarketDataVocabulary.Sessions.Contains(quote.Session))
        {
            return "session value is not recognized";
        }

        if (quote.PreviousClose is { } previousClose && previousClose <= 0m)
        {
            return "previous close is not positive";
        }

        return null;
    }

    private static int FractionalDigits(decimal value)
    {
        var bits = decimal.GetBits(value);
        return (bits[3] >> 16) & 0xFF;
    }
}
