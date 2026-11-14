using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AskVest.Modules.Wallets.Domain;

/// <summary>
/// The deterministic hash over every withdrawal term the user saw in the
/// preview. The step-up grant binds to this value, so any change to a single
/// term produces a different hash and the grant cannot be replayed.
/// </summary>
public static class WithdrawalTermsHash
{
    public static string Compute(
        Guid withdrawalId,
        Guid tokenId,
        Guid addressId,
        string chainId,
        string destinationAddress,
        decimal grossQuantity,
        decimal fee,
        decimal netQuantity,
        DateTimeOffset expiresAt)
    {
        var canonical = string.Join(
            '|',
            withdrawalId.ToString("D"),
            tokenId.ToString("D"),
            addressId.ToString("D"),
            chainId,
            destinationAddress,
            Amount(grossQuantity),
            Amount(fee),
            Amount(netQuantity),
            expiresAt.UtcTicks.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string Amount(decimal value)
        => value.ToString("0.############################", CultureInfo.InvariantCulture);
}
