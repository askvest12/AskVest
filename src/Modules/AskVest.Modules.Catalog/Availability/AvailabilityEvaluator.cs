using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Catalog.Domain.Enums;

namespace AskVest.Modules.Catalog.Availability;

/// <summary>
/// The single availability function from the phase spec. Selling follows the
/// exit principle: an expired review or a country change never blocks it, and
/// delisted tokens stay sellable while the chain and gateway still work.
/// </summary>
internal static class AvailabilityEvaluator
{
    public const string TokenNotListed = "token_not_listed";
    public const string TokenSuspended = "token_suspended";
    public const string IssuerNotApproved = "issuer_not_approved";
    public const string ChainDisabled = "chain_disabled";
    public const string DisclosureReviewExpired = "disclosure_review_expired";
    public const string CountryNotAllowed = "country_not_allowed";
    public const string CountryUnknown = "country_unknown";
    public const string NoExecutionRoute = "no_execution_route";

    public static TokenAvailability Evaluate(AvailabilityFacts facts)
    {
        var buyBlockers = new List<string>();
        var sellBlockers = new List<string>();

        switch (facts.Status)
        {
            case TokenStatus.Draft:
                buyBlockers.Add(TokenNotListed);
                sellBlockers.Add(TokenNotListed);
                break;
            case TokenStatus.Suspended:
                buyBlockers.Add(TokenNotListed);
                sellBlockers.Add(TokenSuspended);
                break;
            case TokenStatus.Listed:
                EvaluateBuyGuards(facts, buyBlockers);
                EvaluateSellGuards(facts, sellBlockers);
                break;
            case TokenStatus.Delisted:
                buyBlockers.Add(TokenNotListed);
                EvaluateSellGuards(facts, sellBlockers);
                break;
        }

        return new TokenAvailability(
            buyBlockers.Count == 0,
            sellBlockers.Count == 0,
            buyBlockers,
            sellBlockers);
    }

    private static void EvaluateBuyGuards(AvailabilityFacts facts, List<string> blockers)
    {
        if (!facts.IssuerApproved)
        {
            blockers.Add(IssuerNotApproved);
        }

        if (!facts.ChainEnabled)
        {
            blockers.Add(ChainDisabled);
        }

        if (facts.ReviewExpiresAt is null || facts.ReviewExpiresAt <= facts.Now)
        {
            blockers.Add(DisclosureReviewExpired);
        }

        if (facts.UserCountry is null)
        {
            blockers.Add(CountryUnknown);
        }
        else if (!facts.AllowedCountries.Contains(facts.UserCountry))
        {
            blockers.Add(CountryNotAllowed);
        }

        if (!facts.HasGatewayKey)
        {
            blockers.Add(NoExecutionRoute);
        }
    }

    private static void EvaluateSellGuards(AvailabilityFacts facts, List<string> blockers)
    {
        if (!facts.ChainEnabled)
        {
            blockers.Add(ChainDisabled);
        }

        if (!facts.HasGatewayKey)
        {
            blockers.Add(NoExecutionRoute);
        }
    }
}
