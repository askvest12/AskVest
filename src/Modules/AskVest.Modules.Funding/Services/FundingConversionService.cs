using System.Globalization;
using AskVest.Modules.Funding.Configuration;
using AskVest.Modules.Funding.Domain;
using AskVest.Modules.Funding.Persistence;
using AskVest.Modules.Funding.Persistence.Entities;
using AskVest.Modules.Funding.Providers;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AskVest.Modules.Funding.Services;

/// <summary>
/// Internal currency conversion. Quotes take a mid rate from the FX
/// provider and apply spread and fee from configuration; execution posts
/// one ledger journal inside a per-user locked transaction scope.
/// </summary>
internal sealed class FundingConversionService(
    FundingDbContext db,
    IFxRateProvider fx,
    ILedger ledger,
    IOptions<FundingOptions> options,
    TimeProvider timeProvider,
    ITransactionScope scope,
    IOutbox outbox,
    FundingEventWriter events,
    ILogger<FundingConversionService> logger)
{
    public const string CompletedMessageType = "FundingConversionCompleted";

    public async Task<Result<FundingConversion>> CreateQuoteAsync(
        Guid userId,
        string pairKey,
        string sourceAmountText,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        pairKey = pairKey.Trim().ToUpperInvariant();
        var pair = ResolvePair(pairKey);
        if (pair is null)
        {
            return Result.Failure<FundingConversion>(FundingResults.NotAvailable("pair_unknown"));
        }

        if (!pair.Enabled)
        {
            return Result.Failure<FundingConversion>(FundingResults.NotAvailable("pair_disabled"));
        }

        var source = pair.From.Trim().ToUpperInvariant();
        var target = pair.To.Trim().ToUpperInvariant();
        var sourceAsset = await ResolveAssetAsync(source, cancellationToken);
        var targetAsset = await ResolveAssetAsync(target, cancellationToken);
        if (sourceAsset is null || targetAsset is null)
        {
            return Result.Failure<FundingConversion>(FundingResults.NotAvailable("pair_unknown"));
        }

        if (!decimal.TryParse(
                sourceAmountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var sourceAmount)
            || sourceAmount <= 0m)
        {
            return Result.Failure<FundingConversion>(new Error(
                ErrorCodes.ValidationFailed,
                "The source amount must be a positive number.",
                ErrorType.Validation));
        }

        if (sourceAmount != decimal.Round(sourceAmount, sourceAsset.Scale))
        {
            return Result.Failure<FundingConversion>(new Error(
                ErrorCodes.ValidationFailed,
                $"The source amount has more than {sourceAsset.Scale} decimal places.",
                ErrorType.Validation));
        }

        var limitError = await CheckPairLimitsAsync(
            userId, pair, sourceAmount, null, timeProvider.GetUtcNow(), cancellationToken);
        if (limitError is not null)
        {
            return Result.Failure<FundingConversion>(limitError);
        }

        var rate = await FetchRateAsync(pairKey, cancellationToken);
        if (rate.IsFailure)
        {
            return Result.Failure<FundingConversion>(rate.Error!);
        }

        var now = timeProvider.GetUtcNow();
        var age = now - rate.Value.AsOf;
        if (age > TimeSpan.FromSeconds(options.Value.FxMaxAgeSeconds))
        {
            return Result.Failure<FundingConversion>(FundingResults.QuoteUnavailable("fx_stale"));
        }

        var jumpError = await CheckJumpAsync(pair, rate.Value.MidRate, cancellationToken);
        if (jumpError is not null)
        {
            return Result.Failure<FundingConversion>(jumpError);
        }

        var quote = FundingMath.ComputeQuote(
            pairKey,
            source,
            target,
            sourceAmount,
            rate.Value.MidRate,
            sourceAsset.Scale,
            targetAsset.Scale,
            pair.SpreadBps,
            pair.FeeBps);

        var conversion = new FundingConversion
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Pair = pairKey,
            SourceCurrency = source,
            TargetCurrency = target,
            SourceAmount = sourceAmount,
            MidRate = rate.Value.MidRate,
            EffectiveRate = quote.EffectiveRate,
            ReceiveAmount = quote.ReceiveAmount,
            FeeAmount = quote.FeeAmount,
            NetAmount = quote.NetAmount,
            TermsHash = quote.TermsHash,
            State = ConversionState.Quoted,
            ExpiresAt = now + TimeSpan.FromSeconds(options.Value.ConversionQuoteLifetimeSeconds),
            CreatedAt = now,
        };
        db.Conversions.Add(conversion);
        await db.SaveChangesAsync(cancellationToken);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Funding conversion quote {ConversionId} created.", conversion.Id);
        }

        return Result.Success(conversion);
    }

    public async Task<Result<FundingConversion>> ExecuteAsync(
        Guid userId,
        Guid conversionId,
        string idempotencyKey,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await EnlistAsync(cancellationToken);
            await LockUserAsync(userId, cancellationToken);

            var conversion = await db.Conversions
                .FirstOrDefaultAsync(
                    row => row.UserId == userId && row.Id == conversionId,
                    cancellationToken);
            if (conversion is null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingConversion>(FundingResults.NotFound("conversion"));
            }

            if (conversion.IdempotencyKey == idempotencyKey)
            {
                await CommitIfOwnerAsync(ownsScope, cancellationToken);
                return Result.Success(conversion);
            }

            var now = timeProvider.GetUtcNow();
            if (conversion.State == ConversionState.Quoted && conversion.ExpiresAt <= now)
            {
                conversion.State = ConversionState.Expired;
                await db.SaveChangesAsync(cancellationToken);
                await CommitIfOwnerAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingConversion>(FundingResults.QuoteInvalid("expired"));
            }

            if (conversion.State != ConversionState.Quoted)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingConversion>(FundingResults.QuoteInvalid(
                    FundingText.ToDb(conversion.State)));
            }

            var pair = ResolvePair(conversion.Pair);
            if (pair is null || !pair.Enabled)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingConversion>(FundingResults.NotAvailable("pair_disabled"));
            }

            var limitError = await CheckPairLimitsAsync(
                userId, pair, conversion.SourceAmount, conversion.Id, now, cancellationToken);
            if (limitError is not null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingConversion>(limitError);
            }

            var sourceAsset = await ResolveAssetAsync(conversion.SourceCurrency, cancellationToken);
            var targetAsset = await ResolveAssetAsync(conversion.TargetCurrency, cancellationToken);
            if (sourceAsset is null || targetAsset is null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingConversion>(FundingResults.NotAvailable("pair_unknown"));
            }

            var sourceRegistered = await ledger.RegisterAssetAsync(
                conversion.SourceCurrency, sourceAsset.Kind, sourceAsset.Scale, cancellationToken);
            var targetRegistered = await ledger.RegisterAssetAsync(
                conversion.TargetCurrency, targetAsset.Kind, targetAsset.Scale, cancellationToken);
            if (sourceRegistered.IsFailure || targetRegistered.IsFailure)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingConversion>(FundingResults.Unavailable("asset_registration_failed"));
            }

            var journal = await ledger.PostJournalAsync(new PostJournalRequest(
                $"funding:conversion:{conversion.Id:N}",
                Book.Real,
                LedgerReasonCodes.Conversion,
                "funding_conversion",
                conversion.Id,
                [
                    new LedgerLine(
                        LedgerAccounts.UserAvailable(Book.Real, userId),
                        conversion.SourceCurrency,
                        -conversion.SourceAmount),
                    new LedgerLine(
                        LedgerAccounts.SystemClearing(Book.Real),
                        conversion.SourceCurrency,
                        conversion.SourceAmount),
                    new LedgerLine(
                        LedgerAccounts.SystemClearing(Book.Real),
                        conversion.TargetCurrency,
                        -conversion.ReceiveAmount),
                    new LedgerLine(
                        LedgerAccounts.UserAvailable(Book.Real, userId),
                        conversion.TargetCurrency,
                        conversion.NetAmount),
                    new LedgerLine(
                        LedgerAccounts.SystemFees(Book.Real),
                        conversion.TargetCurrency,
                        conversion.FeeAmount),
                ]), cancellationToken);
            if (journal.IsFailure)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingConversion>(journal.Error!);
            }

            var from = FundingText.ToDb(conversion.State);
            conversion.State = ConversionState.Completed;
            conversion.IdempotencyKey = idempotencyKey;
            conversion.JournalId = journal.Value.Id;
            conversion.CompletedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            events.Write(
                userId, FundingEventType.ConversionCompleted, "conversion", conversion.Id,
                from, FundingText.ToDb(ConversionState.Completed), correlationId);
            await outbox.EnqueueAsync(
                CompletedMessageType,
                new { conversionId = conversion.Id },
                $"funding:conversion:{conversion.Id:N}",
                cancellationToken);
            await CommitIfOwnerAsync(ownsScope, cancellationToken);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Funding conversion {ConversionId} completed.", conversion.Id);
            }

            return Result.Success(conversion);
        }
        catch (DbUpdateException exception) when (ownsScope
            && exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
            })
        {
            await scope.RollbackAsync(cancellationToken);
            var existing = await db.Conversions.AsNoTracking()
                .FirstOrDefaultAsync(
                    row => row.UserId == userId && row.IdempotencyKey == idempotencyKey,
                    cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return Result.Success(existing);
        }
        catch
        {
            if (ownsScope && scope.IsActive)
            {
                await scope.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    public async Task<Result<PageResult<FundingConversion>>> ListAsync(
        Guid userId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var hasCursor = TryDecodeCursor(cursor, out var cursorAt, out var cursorId);
        var query = db.Conversions.AsNoTracking().Where(row => row.UserId == userId);
        if (hasCursor)
        {
            query = query.Where(row =>
                row.CreatedAt < cursorAt
                || (row.CreatedAt == cursorAt && row.Id.CompareTo(cursorId) < 0));
        }

        var take = Math.Clamp(limit, 1, 100);
        var rows = await query
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > take;
        var page = rows.Take(take).ToList();
        var nextCursor = hasMore && page.Count > 0
            ? CursorCodec.Encode($"{page[^1].CreatedAt:O}|{page[^1].Id:N}")
            : null;
        return Result.Success(new PageResult<FundingConversion>(page, nextCursor, hasMore, take));
    }

    private async Task<Error?> CheckPairLimitsAsync(
        Guid userId,
        FundingPairOption pair,
        decimal sourceAmount,
        Guid? excludeConversionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (sourceAmount < pair.MinimumSource)
        {
            return FundingResults.LimitExceeded("below_minimum");
        }

        if (sourceAmount > pair.MaximumSource)
        {
            return FundingResults.LimitExceeded("above_maximum");
        }

        var windowStart = now - TimeSpan.FromHours(24);
        var used = await db.Conversions.AsNoTracking()
            .Where(conversion => conversion.UserId == userId
                && conversion.State == ConversionState.Completed
                && conversion.CreatedAt > windowStart
                && (excludeConversionId == null || conversion.Id != excludeConversionId))
            .SumAsync(conversion => (decimal?)conversion.SourceAmount, cancellationToken) ?? 0m;
        if (used + sourceAmount > pair.DailyCapSource)
        {
            return FundingResults.LimitExceeded("daily_cap");
        }

        return null;
    }

    private async Task<Result<FundingFxRate>> FetchRateAsync(
        string pair,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ProviderTimeoutSeconds));
        try
        {
            return Result.Success(await fx.GetMidRateAsync(pair, timeout.Token));
        }
        catch (FundingProviderException exception)
        {
            return Result.Failure<FundingFxRate>(FundingResults.QuoteUnavailable(
                exception.Kind == FundingProviderFailureKind.Rejected
                    ? "fx_pair_unknown"
                    : "fx_error"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result.Failure<FundingFxRate>(FundingResults.QuoteUnavailable("fx_timeout"));
        }
    }

    private async Task<Error?> CheckJumpAsync(
        FundingPairOption pair,
        decimal midRate,
        CancellationToken cancellationToken)
    {
        var pairKey = $"{pair.From}:{pair.To}";
        var last = await db.Conversions.AsNoTracking()
            .Where(conversion => conversion.Pair == pairKey)
            .OrderByDescending(conversion => conversion.CreatedAt)
            .Select(conversion => (decimal?)conversion.MidRate)
            .FirstOrDefaultAsync(cancellationToken);
        if (last is null || last.Value <= 0m)
        {
            return null;
        }

        var deviation = Math.Abs(midRate - last.Value) / last.Value * 100m;
        return deviation > pair.MaxJumpPercent
            ? FundingResults.QuoteUnavailable("fx_jump")
            : null;
    }

    private FundingPairOption? ResolvePair(string pairKey)
        => options.Value.Pairs.FirstOrDefault(
            pair => string.Equals(
                $"{pair.From.Trim().ToUpperInvariant()}:{pair.To.Trim().ToUpperInvariant()}",
                pairKey,
                StringComparison.Ordinal));

    /// <summary>
    /// Resolves a pair endpoint to its ledger kind and scale. Configured
    /// local currencies come from Funding:Currencies; seeded cash assets
    /// like USD and USDC come from the ledger registry.
    /// </summary>
    private async Task<LedgerAssetInfo?> ResolveAssetAsync(
        string code,
        CancellationToken cancellationToken)
    {
        var currency = options.Value.Currencies.FirstOrDefault(
            candidate => string.Equals(candidate.Code, code, StringComparison.Ordinal));
        if (currency is not null)
        {
            return new LedgerAssetInfo(AssetKind.Fiat, currency.Scale);
        }

        return await ledger.GetAssetInfoAsync(code, cancellationToken);
    }

    private async Task EnlistAsync(CancellationToken cancellationToken)
    {
        if (scope.Transaction is not null)
        {
            await db.Database.UseTransactionAsync(scope.Transaction, cancellationToken);
        }
    }

    private async Task LockUserAsync(Guid userId, CancellationToken cancellationToken)
        => await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('funding'), hashtext({userId.ToString("D")}))",
            cancellationToken);

    private async Task CommitIfOwnerAsync(bool ownsScope, CancellationToken cancellationToken)
    {
        if (ownsScope)
        {
            await scope.CommitAsync(cancellationToken);
        }
    }

    private async Task RollbackAsync(bool ownsScope, CancellationToken cancellationToken)
    {
        if (ownsScope && scope.IsActive)
        {
            await scope.RollbackAsync(cancellationToken);
        }
    }

    private static bool TryDecodeCursor(string? cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id = Guid.Empty;
        if (cursor is null || !CursorCodec.TryDecode(cursor, out var payload) || payload is null)
        {
            return false;
        }

        var separator = payload.IndexOf('|', StringComparison.Ordinal);
        return separator > 0
            && DateTimeOffset.TryParse(
                payload[..separator], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out createdAt)
            && Guid.TryParse(payload[(separator + 1)..], out id);
    }
}
