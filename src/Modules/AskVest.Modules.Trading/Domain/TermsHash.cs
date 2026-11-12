using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AskVest.Modules.Trading.Domain;

/// <summary>
/// The deterministic hash over every quote term the user saw. The step-up
/// grant binds to this value, so any change to a single term produces a
/// different hash and the grant cannot be replayed against other terms.
/// </summary>
public static class TermsHash
{
    public static string Compute(
        Guid quoteId,
        Guid tokenId,
        OrderSide side,
        decimal quantity,
        decimal price,
        decimal grossAmount,
        decimal ourFee,
        decimal gatewayFee,
        decimal total,
        string settlementAsset,
        DateTimeOffset expiresAt)
    {
        var canonical = string.Join(
            '|',
            quoteId.ToString("D"),
            tokenId.ToString("D"),
            side == OrderSide.Buy ? "buy" : "sell",
            Amount(quantity),
            Amount(price),
            Amount(grossAmount),
            Amount(ourFee),
            Amount(gatewayFee),
            Amount(total),
            settlementAsset,
            expiresAt.UtcTicks.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string Amount(decimal value)
        => value.ToString("0.############################", CultureInfo.InvariantCulture);
}
