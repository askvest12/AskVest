using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.MarketData.Configuration;
using AskVest.Modules.MarketData.Contracts;
using AskVest.Modules.MarketData.Domain;
using AskVest.Modules.MarketData.Domain.Enums;
using AskVest.Modules.MarketData.Persistence;
using AskVest.Modules.MarketData.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.MarketData.Services;

internal sealed class MarketDataQueryService(
    MarketDataDbContext db,
    ICatalog catalog,
    IOptions<MarketDataOptions> options,
    TimeProvider timeProvider) : IMarketData
{
    public async Task<MarketDataPrice> GetLatestPriceAsync(string instrumentId, CancellationToken cancellationToken = default)
        => (await GetLatestPricesAsync([instrumentId], cancellationToken)).Single();

    public async Task<IReadOnlyList<MarketDataPrice>> GetLatestPricesAsync(
        IReadOnlyList<string> instrumentIds,
        CancellationToken cancellationToken = default)
    {
        var activeIds = (await catalog.ListActiveInstrumentsAsync(cancellationToken))
            .Select(instrument => instrument.Id)
            .ToHashSet(StringComparer.Ordinal);
        var rows = await db.LatestPrices
            .Where(price => price.SubjectType == MarketDataVocabulary.SubjectInstrument
                && instrumentIds.Contains(price.SubjectId))
            .ToListAsync(cancellationToken);
        var bySubject = rows.ToDictionary(row => row.SubjectId, StringComparer.Ordinal);

        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        return instrumentIds
            .Select(id => Project(id, activeIds.Contains(id), bySubject.GetValueOrDefault(id), settings, now))
            .ToList();
    }

    private static MarketDataPrice Project(
        string instrumentId,
        bool isActive,
        LatestPrice? row,
        MarketDataOptions settings,
        DateTimeOffset now)
    {
        var source = settings.ProviderDisplayName;
        if (!isActive || row is null || row.UnmappedAt is not null)
        {
            return new MarketDataPrice(
                instrumentId, null, null, null, 0, "unknown", "unavailable", false, source, null);
        }

        var assessment = FreshnessLabeler.Evaluate(
            row.DelayMinutes,
            row.Session,
            row.AsOf,
            now,
            settings.LiveThresholdSeconds,
            settings.StaleThresholdSeconds);
        return new MarketDataPrice(
            instrumentId,
            row.Price,
            row.Currency,
            row.AsOf,
            assessment.AgeSeconds,
            row.Session,
            LabelText(assessment.Label),
            assessment.UsableForExecution,
            source,
            row.PreviousClose);
    }

    private static string LabelText(FreshnessLabel label) => label switch
    {
        FreshnessLabel.Live => "live",
        FreshnessLabel.Delayed => "delayed",
        FreshnessLabel.Closed => "closed",
        FreshnessLabel.Halted => "halted",
        FreshnessLabel.Stale => "stale",
        _ => "unavailable",
    };
}
