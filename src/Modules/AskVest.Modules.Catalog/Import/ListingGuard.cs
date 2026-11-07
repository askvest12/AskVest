namespace AskVest.Modules.Catalog.Import;

/// <summary>
/// The single listing guard shared by the importer now and admin endpoints
/// later. It returns stable reason codes; an empty list means the token may
/// be listed.
/// </summary>
internal static class ListingGuard
{
    public const string IssuerNotApproved = "issuer_not_approved";
    public const string ChainDisabled = "chain_disabled";
    public const string UnsupportedChainFamily = "unsupported_chain_family";
    public const string IncompleteDisclosures = "incomplete_disclosures";
    public const string NoAllowedCountries = "no_allowed_countries";
    public const string MissingGatewayKey = "missing_gateway_key";
    public const string DecimalsAboveMaximum = "decimals_above_maximum";
    public const string ReviewMissing = "review_missing";
    public const string ReviewDateInFuture = "review_date_in_future";
    public const string ReviewExpired = "review_expired";
    public const string ReviewWindowTooLong = "review_window_too_long";
    public const string InstrumentNotActive = "instrument_not_active";

    public static IReadOnlyList<string> Evaluate(ListingGuardInput input)
    {
        var failures = new List<string>();
        if (!input.IssuerApproved)
        {
            failures.Add(IssuerNotApproved);
        }

        if (!input.ChainEnabled)
        {
            failures.Add(ChainDisabled);
        }

        if (!input.ChainFamilySupported)
        {
            failures.Add(UnsupportedChainFamily);
        }

        if (!input.DisclosuresComplete)
        {
            failures.Add(IncompleteDisclosures);
        }

        if (input.AllowedCountryCount < 1)
        {
            failures.Add(NoAllowedCountries);
        }

        if (!input.HasGatewayKey)
        {
            failures.Add(MissingGatewayKey);
        }

        if (input.Decimals > 18)
        {
            failures.Add(DecimalsAboveMaximum);
        }

        if (input.ReviewedAt is null || input.ReviewExpiresAt is null)
        {
            failures.Add(ReviewMissing);
        }
        else
        {
            if (input.ReviewedAt > input.Now)
            {
                failures.Add(ReviewDateInFuture);
            }

            if (input.ReviewExpiresAt <= input.Now)
            {
                failures.Add(ReviewExpired);
            }

            if (input.ReviewExpiresAt > input.ReviewedAt.Value.AddMonths(input.ReviewMaxMonths))
            {
                failures.Add(ReviewWindowTooLong);
            }
        }

        if (!input.InstrumentActive)
        {
            failures.Add(InstrumentNotActive);
        }

        return failures;
    }
}
