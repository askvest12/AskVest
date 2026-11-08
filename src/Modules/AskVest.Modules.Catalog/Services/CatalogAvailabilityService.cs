using AskVest.Modules.Catalog.Availability;
using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Catalog.Domain.Enums;
using AskVest.Modules.Catalog.Persistence;
using AskVest.Modules.Compliance.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Catalog.Services;

/// <summary>
/// Loads the catalog facts for one token and the caller's effective country,
/// then defers to the pure availability evaluator.
/// </summary>
internal sealed class CatalogAvailabilityService(
    CatalogDbContext db,
    IComplianceEligibility compliance,
    TimeProvider timeProvider)
{
    public async Task<TokenAvailability?> GetAsync(Guid userId, Guid tokenId, CancellationToken cancellationToken)
    {
        var token = await db.Tokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tokenId, cancellationToken);
        if (token is null)
        {
            return null;
        }

        var issuerApproved = await db.Issuers.AsNoTracking()
            .Where(i => i.Id == token.IssuerId)
            .Select(i => i.Status == IssuerStatus.Approved)
            .FirstOrDefaultAsync(cancellationToken);
        var chainEnabled = await db.Chains.AsNoTracking()
            .Where(c => c.Id == token.ChainId)
            .Select(c => c.Status == ChainStatus.Enabled)
            .FirstOrDefaultAsync(cancellationToken);
        var countries = await db.TokenCountries.AsNoTracking()
            .Where(tc => tc.TokenId == tokenId)
            .Select(tc => tc.CountryCode)
            .ToListAsync(cancellationToken);
        var userCountry = await compliance.GetEffectiveCountryAsync(userId, cancellationToken);

        return AvailabilityEvaluator.Evaluate(new AvailabilityFacts(
            token.Status,
            issuerApproved,
            chainEnabled,
            token.ReviewExpiresAt,
            timeProvider.GetUtcNow(),
            countries.ToHashSet(StringComparer.Ordinal),
            userCountry,
            !string.IsNullOrWhiteSpace(token.GatewayKey)));
    }
}
