using System.Text.RegularExpressions;
using AskVest.Modules.Catalog.Contracts;

namespace AskVest.Modules.Practice.Services;

/// <summary>
/// Builds the ledger asset code for a practice instrument: "P." plus the
/// uppercase symbol, matching the ledger asset code pattern and length limit.
/// </summary>
internal static partial class PracticeAssetCodes
{
    public const string Prefix = "P.";

    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]{0,15}$")]
    private static partial Regex AssetCodePattern();

    public static bool TryCreateCode(CatalogInstrument instrument, out string assetCode)
    {
        assetCode = string.Empty;
        if (string.IsNullOrWhiteSpace(instrument.Symbol))
        {
            return false;
        }

        var code = Prefix + instrument.Symbol.Trim().ToUpperInvariant();
        if (!AssetCodePattern().IsMatch(code))
        {
            return false;
        }

        assetCode = code;
        return true;
    }
}
