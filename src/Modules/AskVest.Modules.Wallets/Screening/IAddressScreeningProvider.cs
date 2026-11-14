namespace AskVest.Modules.Wallets.Screening;

/// <summary>
/// The address screening seam. Every destination and deposit source passes
/// through it; providers must fail closed on uncertainty.
/// </summary>
public interface IAddressScreeningProvider
{
    /// <summary>The provider name used in configuration.</summary>
    string ProviderName { get; }

    Task<ScreeningResult> ScreenAsync(
        string chainId,
        string address,
        CancellationToken cancellationToken = default);
}
