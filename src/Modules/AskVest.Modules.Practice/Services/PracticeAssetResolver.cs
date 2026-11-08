using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Practice.Configuration;
using AskVest.Modules.Practice.Persistence;
using AskVest.Modules.Practice.Persistence.Entities;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Practice.Services;

/// <summary>
/// Lazily maps a catalog instrument to its practice ledger asset. The mapping
/// row is the single source for which instruments are practiceable.
/// </summary>
internal sealed class PracticeAssetResolver(
    PracticeDbContext db,
    ICatalog catalog,
    ILedger ledger,
    IOptions<PracticeOptions> options,
    TimeProvider timeProvider,
    ILogger<PracticeAssetResolver> logger)
{
    public async Task<Result<string>> ResolveAsync(string instrumentId, CancellationToken cancellationToken)
    {
        var instrument = await catalog.FindInstrumentAsync(instrumentId, cancellationToken);
        if (!IsPracticeable(instrument))
        {
            return InstrumentUnavailable(instrumentId, "instrument not found, inactive or not USD quoted");
        }

        if (!PracticeAssetCodes.TryCreateCode(instrument!, out var assetCode))
        {
            return InstrumentUnavailable(instrumentId, "symbol cannot form a valid practice asset code");
        }

        var mapping = await db.PracticeInstruments
            .FirstOrDefaultAsync(row => row.InstrumentId == instrumentId, cancellationToken);
        if (mapping is null)
        {
            var collision = await db.PracticeInstruments.AnyAsync(
                row => row.AssetCode == assetCode && row.InstrumentId != instrumentId,
                cancellationToken);
            if (collision)
            {
                return InstrumentUnavailable(instrumentId, "practice asset code is already taken by another instrument");
            }

            var entity = new PracticeInstrument
            {
                InstrumentId = instrumentId,
                AssetCode = assetCode,
                CreatedAt = timeProvider.GetUtcNow(),
            };
            db.PracticeInstruments.Add(entity);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                mapping = entity;
            }
            catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException
            {
                SqlState: Npgsql.PostgresErrorCodes.UniqueViolation,
            })
            {
                // The failed insert poisons the ambient transaction, so the whole
                // enclosing operation must roll back; a retry finds the mapping.
                db.Entry(entity).State = EntityState.Detached;
                return InstrumentUnavailable(instrumentId, "practice asset code raced to another instrument");
            }
        }

        var registered = await ledger.RegisterAssetAsync(
            mapping.AssetCode, AssetKind.Token, options.Value.QuantityScale, cancellationToken);
        if (registered.IsFailure)
        {
            logger.LogWarning(
                "Practice asset registration failed for instrument {InstrumentId}: {ErrorCode}",
                instrumentId, registered.Error!.Code);
            return InstrumentUnavailable(instrumentId, "practice asset could not be registered");
        }

        return Result.Success(mapping.AssetCode);
    }

    private static bool IsPracticeable(CatalogInstrument? instrument)
        => instrument is not null
            && instrument.Status == "active"
            && string.Equals(instrument.QuoteCurrency, "USD", StringComparison.OrdinalIgnoreCase);

    private Result<string> InstrumentUnavailable(string instrumentId, string reason)
    {
        logger.LogWarning("Instrument {InstrumentId} is unavailable for practice: {Reason}", instrumentId, reason);
        return Result.Failure<string>(new Error(
            ErrorCodes.PracticeInstrumentUnavailable,
            "The instrument is not available for practice.",
            ErrorType.BusinessRule));
    }
}
