namespace AskVest.Modules.Funding.Providers;

/// <summary>
/// The only FX rate seam. Funding applies spread and fees itself; provider
/// converted amounts are never trusted. Failures throw
/// <see cref="FundingProviderException"/>.
/// </summary>
public interface IFxRateProvider
{
    /// <summary>Returns the mid rate for a "FROM:TO" pair.</summary>
    Task<FundingFxRate> GetMidRateAsync(string pair, CancellationToken cancellationToken = default);
}

public sealed record FundingFxRate(
    string Pair,
    decimal MidRate,
    DateTimeOffset AsOf,
    string Source);
