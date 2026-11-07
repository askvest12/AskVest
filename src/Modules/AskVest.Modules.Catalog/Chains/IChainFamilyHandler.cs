using AskVest.SharedKernel;

namespace AskVest.Modules.Catalog.Chains;

/// <summary>
/// The only seam that knows chain details: address validation and
/// canonicalization, CAIP-2 and CAIP-19 handling and explorer links for one
/// chain family. Adding a family means one implementation and data files.
/// </summary>
public interface IChainFamilyHandler
{
    string Family { get; }

    bool IsValidChainId(string chainId);

    Result<string> CanonicalizeAddress(string chainId, string address);

    string BuildAssetId(string chainId, string standard, string canonicalAddress);

    string BuildExplorerLink(string chainId, string canonicalAddress, string explorerLinkTemplate);
}
