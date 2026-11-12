using System.Data;
using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Compliance.Contracts;
using AskVest.Modules.Identity.Contracts;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Trading.Configuration;
using AskVest.Modules.Trading.Domain;
using AskVest.Modules.Trading.Endpoints;
using AskVest.Modules.Trading.Persistence;
using AskVest.Modules.Trading.Persistence.Entities;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AskVest.Modules.Trading.Services;

/// <summary>
/// Real order placement, reads and cancellation. Placement runs as one
/// transaction: quote validation, step-up consumption, the ledger hold, the
/// order row, its event and the submission outbox message commit or roll back
/// together.
/// </summary>
internal sealed class OrderService(
    TradingDbContext db,
    ICatalog catalog,
    IComplianceEligibility eligibility,
    IStepUpAuthorizer stepUp,
    ILedger ledger,
    IOptions<TradingOptions> options,
    TimeProvider timeProvider,
    ITransactionScope scope,
    IOutbox outbox,
    TradingEventWriter events,
    ILogger<OrderService> logger)
{
    public const string SubmitMessageType = "TradingOrderSubmit";

    public async Task<Result<TradingOrder>> PlaceAsync(
        Guid userId,
        Guid sessionId,
        OrderRequest request,
        string idempotencyKey,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (request.QuoteId is null || string.IsNullOrWhiteSpace(request.StepUpToken))
        {
            return Result.Failure<TradingOrder>(TradingResults.OrderInvalid("quoteId and stepUpToken are required"));
        }

        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await EnlistAsync(cancellationToken);
            await LockUserAsync(userId, cancellationToken);

            var replay = await db.Orders.AsNoTracking()
                .FirstOrDefaultAsync(
                    order => order.UserId == userId && order.IdempotencyKey == idempotencyKey,
                    cancellationToken);
            if (replay is not null)
            {
                await CommitIfOwnerAsync(ownsScope, cancellationToken);
                return Result.Success(replay);
            }

            var quote = await LoadQuoteForUpdateAsync(userId, request.QuoteId.Value, cancellationToken);
            if (quote is null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<TradingOrder>(new Error(
                    ErrorCodes.ResourceNotFound,
                    "The quote was not found.",
                    ErrorType.NotFound));
            }

            var existing = await db.Orders.AsNoTracking()
                .FirstOrDefaultAsync(order => order.QuoteId == quote.Id, cancellationToken);
            if (existing is not null)
            {
                await CommitIfOwnerAsync(ownsScope, cancellationToken);
                return Result.Success(existing);
            }

            var now = timeProvider.GetUtcNow();
            if (quote.State != QuoteState.Open)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<TradingOrder>(TradingResults.QuoteInvalid("already_used"));
            }

            if (quote.ExpiresAt <= now)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<TradingOrder>(TradingResults.QuoteInvalid("expired"));
            }

            var token = await catalog.FindTokenAsync(quote.TokenId, cancellationToken);
            if (token is null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<TradingOrder>(TradingResults.NotAvailable("token_unknown"));
            }

            var availability = await catalog.GetAvailabilityAsync(userId, token.Id, cancellationToken);
            var allowed = quote.Side == OrderSide.Buy ? availability?.Buyable : availability?.Sellable;
            if (allowed != true)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<TradingOrder>(TradingResults.NotAvailable("token_not_tradable"));
            }

            var settings = options.Value;
            var switchOff = quote.Side == OrderSide.Buy ? !settings.BuyEnabled : !settings.SellEnabled;
            if (switchOff)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<TradingOrder>(TradingResults.NotAvailable(
                    quote.Side == OrderSide.Buy ? "buy_disabled" : "sell_disabled"));
            }

            // Sells are never blocked by review expiry or country changes; only
            // catalog availability and the sell switch may stop them.
            if (quote.Side == OrderSide.Buy)
            {
                var userEligibility = await eligibility.GetEligibilityAsync(userId, cancellationToken);
                if (!userEligibility.RealAllowed)
                {
                    await RollbackAsync(ownsScope, cancellationToken);
                    return Result.Failure<TradingOrder>(new Error(
                        ErrorCodes.AuthForbidden,
                        "Real trading is not available for this account.",
                        ErrorType.Forbidden));
                }
            }

            var limits = new TradingLimits(
                settings.MinOrderAmount,
                settings.MaxOrderAmount,
                settings.Rolling24HourMax,
                settings.MaxNonFinalOrders);
            var usage = await ReadUsageAsync(userId, now, cancellationToken);
            var violation = LimitEvaluation.Evaluate(limits, usage, quote.Total);
            if (violation != LimitViolation.None)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<TradingOrder>(TradingResults.OrderInvalid(violation switch
                {
                    LimitViolation.BelowMinimum => "below_minimum",
                    LimitViolation.AboveMaximum => "above_maximum",
                    LimitViolation.RollingLimit => "rolling_limit",
                    _ => "too_many_open_orders",
                }));
            }

            var consumed = await stepUp.ConsumeAsync(
                userId,
                sessionId,
                StepUpPurposes.AuthorizeOrder,
                quote.TermsHash,
                request.StepUpToken!,
                cancellationToken);
            if (consumed.IsFailure)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<TradingOrder>(consumed.Error!);
            }

            var orderId = Guid.CreateVersion7();
            var hold = OrderJournals.Hold(
                orderId,
                userId,
                quote.Side,
                quote.SettlementAsset,
                token.LedgerAssetCode,
                quote.Total,
                quote.Quantity);
            var posted = await ledger.PostJournalAsync(hold, cancellationToken);
            if (posted.IsFailure)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<TradingOrder>(posted.Error!);
            }

            quote.State = QuoteState.Used;
            var order = new TradingOrder
            {
                Id = orderId,
                UserId = userId,
                QuoteId = quote.Id,
                TokenId = token.Id,
                InstrumentId = token.InstrumentId,
                Side = quote.Side,
                State = OrderState.PendingSubmission,
                SettlementAsset = quote.SettlementAsset,
                Quantity = quote.Quantity,
                Price = quote.Price,
                GrossAmount = quote.GrossAmount,
                OurFee = quote.OurFee,
                GatewayFee = quote.GatewayFee,
                Total = quote.Total,
                IdempotencyKey = idempotencyKey,
                GatewayKey = token.GatewayKey,
                HoldJournalId = posted.Value.Id,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Orders.Add(order);
            events.Write(userId, TradingEventType.OrderPlaced, order.Id, null, OrderState.PendingSubmission, correlationId);
            await db.SaveChangesAsync(cancellationToken);
            await outbox.EnqueueAsync(
                SubmitMessageType,
                new { orderId = order.Id },
                $"trading:submit:{order.Id:N}",
                cancellationToken);

            if (ownsScope)
            {
                await scope.CommitAsync(cancellationToken);
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Real order {OrderId} placed.", order.Id);
            }

            return Result.Success(order);
        }
        catch (DbUpdateException exception) when (ownsScope
            && exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
            })
        {
            // The unique indexes on (user_id, idempotency_key) and quote_id are
            // the last line of defense when the middleware is bypassed.
            await scope.RollbackAsync(cancellationToken);
            var existing = await db.Orders.AsNoTracking()
                .FirstOrDefaultAsync(
                    order => order.UserId == userId && order.IdempotencyKey == idempotencyKey,
                    cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return Result.Success(existing);
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    public async Task<Result<TradingOrder>> GetAsync(
        Guid userId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.Id == orderId && row.UserId == userId,
                cancellationToken);
        return order is null
            ? Result.Failure<TradingOrder>(new Error(
                ErrorCodes.ResourceNotFound,
                "The order was not found.",
                ErrorType.NotFound))
            : Result.Success(order);
    }

    public async Task<Result<IReadOnlyList<TradingOrder>>> ListAsync(
        Guid userId,
        int limit,
        CancellationToken cancellationToken)
    {
        var orders = await db.Orders.AsNoTracking()
            .Where(order => order.UserId == userId)
            .OrderByDescending(order => order.CreatedAt)
            .ThenByDescending(order => order.Id)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<TradingOrder>>(orders);
    }

    public async Task<Result<TradingOrder>> CancelAsync(
        Guid userId,
        Guid orderId,
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
            // The row lock serializes cancel against the submission dispatcher;
            // exactly one observes a cancellable state.
            await db.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM trading.orders WHERE id = {orderId} FOR UPDATE",
                cancellationToken);
            var order = await db.Orders
                .FirstOrDefaultAsync(
                    row => row.Id == orderId && row.UserId == userId,
                    cancellationToken);
            if (order is null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<TradingOrder>(new Error(
                    ErrorCodes.ResourceNotFound,
                    "The order was not found.",
                    ErrorType.NotFound));
            }

            if (!OrderTransitions.CanTransition(order.State, OrderState.Cancelled))
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<TradingOrder>(TradingResults.OrderInvalid("not_cancellable"));
            }

            if (order.HoldJournalId is { } holdJournalId)
            {
                var released = await ledger.ReverseJournalAsync(
                    OrderJournals.ReleaseKey(order.Id), holdJournalId, cancellationToken);
                if (released.IsFailure)
                {
                    await RollbackAsync(ownsScope, cancellationToken);
                    return Result.Failure<TradingOrder>(released.Error!);
                }

                order.ReleaseJournalId = released.Value.Id;
            }

            var from = order.State;
            order.State = OrderState.Cancelled;
            order.CancelledAt = timeProvider.GetUtcNow();
            order.UpdatedAt = order.CancelledAt.Value;
            events.Write(userId, TradingEventType.OrderCancelled, order.Id, from, OrderState.Cancelled, correlationId);
            await db.SaveChangesAsync(cancellationToken);

            if (ownsScope)
            {
                await scope.CommitAsync(cancellationToken);
            }

            return Result.Success(order);
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    private async Task<TradingQuote?> LoadQuoteForUpdateAsync(
        Guid userId,
        Guid quoteId,
        CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM trading.quotes WHERE id = {quoteId} AND user_id = {userId} FOR UPDATE",
            cancellationToken);
        return await db.Quotes
            .FirstOrDefaultAsync(quote => quote.Id == quoteId && quote.UserId == userId, cancellationToken);
    }

    private async Task<TradingUsage> ReadUsageAsync(
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var windowStart = now - TimeSpan.FromHours(24);
        var rows = await db.Orders.AsNoTracking()
            .Where(order => order.UserId == userId && order.CreatedAt > windowStart)
            .Select(order => new { order.State, order.Total })
            .ToListAsync(cancellationToken);
        var rolling = rows
            .Where(row => OrderTransitions.CountsTowardRollingTotal(row.State))
            .Sum(row => row.Total);
        var nonFinal = rows.Count(row => OrderTransitions.IsNonFinal(row.State));
        return new TradingUsage(rolling, nonFinal);
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
            $"SELECT pg_advisory_xact_lock(hashtext('trading'), hashtext({userId.ToString("D")}))",
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
}
