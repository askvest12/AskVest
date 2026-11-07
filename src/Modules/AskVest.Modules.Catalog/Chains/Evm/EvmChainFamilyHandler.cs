using System.Globalization;
using System.Text;
using AskVest.SharedKernel;
using Org.BouncyCastle.Crypto.Digests;

namespace AskVest.Modules.Catalog.Chains.Evm;

internal sealed class EvmChainFamilyHandler : IChainFamilyHandler
{
    private const string Eip155Prefix = "eip155:";
    private const string Erc20Namespace = "erc20";

    public string Family => "evm";

    public bool IsValidChainId(string chainId)
    {
        if (!chainId.StartsWith(Eip155Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var digits = chainId[Eip155Prefix.Length..];
        return digits.Length > 0
            && digits.All(char.IsAsciiDigit)
            && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var chainNumber)
            && chainNumber > 0;
    }

    public Result<string> CanonicalizeAddress(string chainId, string address)
    {
        if (!IsValidChainId(chainId))
        {
            return InvalidAddress();
        }

        if (address.Length != 42
            || !address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || address[2..].Any(c => !Uri.IsHexDigit(c)))
        {
            return InvalidAddress();
        }

        var hex = address[2..];
        var hasLetters = hex.Any(char.IsAsciiLetter);
        var isLower = hex.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        var isUpper = hex.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

        if (hasLetters && !isLower && !isUpper)
        {
            var expected = Checksum(hex);
            if (!string.Equals(hex, expected[2..], StringComparison.Ordinal))
            {
                return InvalidAddress();
            }

            return Result<string>.Success("0x" + expected[2..]);
        }

        return Result<string>.Success("0x" + Checksum(hex)[2..]);
    }

    public string BuildAssetId(string chainId, string standard, string canonicalAddress)
    {
        var normalizedStandard = standard.Equals(Erc20Namespace, StringComparison.OrdinalIgnoreCase)
            ? Erc20Namespace
            : standard.ToLowerInvariant();
        return $"{chainId}/{normalizedStandard}:{canonicalAddress}";
    }

    public string BuildExplorerLink(string chainId, string canonicalAddress, string explorerLinkTemplate)
        => explorerLinkTemplate.Replace("{address}", canonicalAddress, StringComparison.Ordinal);

    private static string Checksum(string hex)
    {
        var lower = hex.ToLowerInvariant();
        var hash = Keccak256(Encoding.ASCII.GetBytes(lower));
        var builder = new StringBuilder(42);
        builder.Append("0x");
        for (var i = 0; i < lower.Length; i++)
        {
            var c = lower[i];
            if (char.IsAsciiDigit(c))
            {
                builder.Append(c);
                continue;
            }

            var nibble = hash[i / 2] >> (i % 2 == 0 ? 4 : 0) & 0x0F;
            builder.Append(nibble >= 8 ? char.ToUpperInvariant(c) : c);
        }

        return builder.ToString();
    }

    private static byte[] Keccak256(byte[] data)
    {
        var digest = new KeccakDigest(256);
        digest.BlockUpdate(data, 0, data.Length);
        var output = new byte[digest.GetDigestSize()];
        digest.DoFinal(output, 0);
        return output;
    }

    private static Result<string> InvalidAddress()
        => Result.Failure<string>(new Error(
            ErrorCodes.CatalogAddressInvalid,
            "The address is not valid for this chain.",
            ErrorType.BusinessRule));
}
