using System.Data;
using System.Globalization;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.MarketData.Contracts;
using AskVest.Modules.Practice.Configuration;
using AskVest.Modules.Practice.Domain;
using AskVest.Modules.Practice.Endpoints;
using AskVest.Modules.Practice.Persistence;
using AskVest.Modules.Practice.Persistence.Entities;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AskVest.Modules.Practice.Services;

/// <summary>
/// Practice order placement and reads. Every state change runs inside one
/// transaction scope together with its ledger journals, event and outbox row.
/// </summary>
internal sealed class PracticeOrderService(
    PracticeDbContext db,
    ILedger ledger,
    IMarketData marketData,
    PracticeAssetResolver assets,
    IOptions<PracticeOptions> options,
    TimeProvider timeProvider,
    ITransactionScope scope,
    IOutbox outbox,
    PracticeEventWriter events,
    ILogger<PracticeOrderService> logger)
{
    internal const string OrderFilledMessageType = "PracticeOrderFilled";

    public async Task<Result<PracticeOrder>> PlaceAsync(
        Guid userId,
        PracticeOrderRequest request,
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
            await PracticeScope.EnlistAsync(db, scope, cancellationToken);

            var started = await db.PracticeAccounts
                .AnyAsync(account => account.UserId == userId, cancellationToken);
            if (!started)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<PracticeOrder>(PracticeResults.AccountNotStarted());
            }

            if (!TryParse(request, out var parsed, out var invalid))
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<PracticeOrder>(invalid!);
            }

            if (parsed.Type == PracticeOrderType.StopLoss)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<PracticeOrder>(OrderInvalid("stop_loss orders arrive with the stop-loss step"));
            }

            var asset = await assets.ResolveAsync(parsed.InstrumentId, cancellationToken);
            if (asset.IsFailure)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<PracticeOrder>(asset.Error!);
            }

            var price = await marketData.GetLatestPriceAsync(parsed.InstrumentId, cancellationToken);
            if (!IsUsable(price))
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<PracticeOrder>(MarketUnavailable(price));
            }

            var result = parsed.Side == PracticeOrderSide.Buy
                ? await PlaceMarketBuyAsync(userId, parsed, asset.Value, price, idempotencyKey, correlationId, cancellationToken)
                : await PlaceMarketSellAsync(userId, parsed, asset.Value, price, idempotencyKey, correlationId, cancellationToken);

            if (result.IsFailure)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return result;
            }

            if (ownsScope)
            {
                await scope.CommitAsync(cancellationToken);
            }

            return result;
        }
        catch (DbUpdateException exception) when (ownsScope
            && exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
            })
        {
            // The (user, idempotency key) unique index is the last line of
            // defense when the middleware is bypassed; replay the stored order.
            await scope.RollbackAsync(cancellationToken);
            var existing = await db.PracticeOrders.AsNoTracking()
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

    public async Task<Result<PracticeOrderItem>> CancelAsync(
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
            await PracticeScope.EnlistAsync(db, scope, cancellationToken);
            // The row lock serializes cancel against a concurrent stop trigger;
            // exactly one of them observes state 'open'.
            await db.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM practice.practice_orders WHERE id = {orderId} FOR UPDATE",
                cancellationToken);
            var order = await db.PracticeOrders
                .FirstOrDefaultAsync(row => row.Id == orderId && row.UserId == userId, cancellationToken);
            if (order is null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<PracticeOrderItem>(new Error(
                    ErrorCodes.ResourceNotFound,
                    "The order was not found.",
                    ErrorType.NotFound));
            }

            if (order.State != PracticeOrderState.Open)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<PracticeOrderItem>(new Error(
                    ErrorCodes.ResourceConflict,
                    "Only an open order can be cancelled.",
                    ErrorType.Conflict));
            }

            if (order.HoldJournalId is { } holdJournalId)
            {
                var released = await ledger.ReverseJournalAsync(
                    PracticeOrderFills.ReleaseKey(order.Id), holdJournalId, cancellationToken);
                if (released.IsFailure)
                {
                    await RollbackAsync(ownsScope, cancellationToken);
                    return Result.Failure<PracticeOrderItem>(released.Error!);
                }
            }

            order.State = PracticeOrderState.Cancelled;
            order.CancelledAt = timeProvider.GetUtcNow();
            order.CancelReason = "user";
            events.Write(userId, PracticeEventType.OrderCancelled, order.Id, correlationId);
            await db.SaveChangesAsync(cancellationToken);

            if (ownsScope)
            {
                await scope.CommitAsync(cancellationToken);
            }

            return Result.Success(ToItem(order));
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    public async Task<Result<PracticeOrderItem>> GetAsync(
        Guid userId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var order = await db.PracticeOrders.AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.Id == orderId && row.UserId == userId,
                cancellationToken);
        return order is null
            ? Result.Failure<PracticeOrderItem>(new Error(
                ErrorCodes.ResourceNotFound,
                "The order was not found.",
                ErrorType.NotFound))
            : Result.Success(ToItem(order));
    }

    public async Task<Result<PageResult<PracticeOrderItem>>> ListAsync(
        Guid userId,
        string? state,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        PracticeOrderState? stateFilter = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            stateFilter = state.Trim().ToLowerInvariant() switch
            {
                "open" => PracticeOrderState.Open,
                "filled" => PracticeOrderState.Filled,
                "cancelled" => PracticeOrderState.Cancelled,
                _ => null,
            };
            if (stateFilter is null)
            {
                return Result.Failure<PageResult<PracticeOrderItem>>(new Error(
                    ErrorCodes.ValidationFailed,
                    "The state filter is not recognized.",
                    ErrorType.Validation));
            }
        }

        var hasCursor = TryDecodeCursor(cursor, out var cursorAt, out var cursorId);
        var connection = db.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, created_at FROM practice.practice_orders
            WHERE user_id = @userId
              AND (@state::text IS NULL OR state = @state::text)
              AND (@hasCursor = FALSE
                   OR created_at < @cursorAt::timestamptz
                   OR (created_at = @cursorAt::timestamptz AND id < @cursorId::uuid))
            ORDER BY created_at DESC, id DESC
            LIMIT @limit
            """;
        AddParameter(command, "userId", userId);
        AddParameter(command, "state", stateFilter is null ? (object)DBNull.Value : PracticeText.ToDb(stateFilter.Value));
        AddParameter(command, "hasCursor", hasCursor);
        AddParameter(command, "cursorAt", hasCursor ? cursorAt : (object)DBNull.Value);
        AddParameter(command, "cursorId", hasCursor ? cursorId : (object)DBNull.Value);
        AddParameter(command, "limit", limit + 1);

        var rows = new List<(Guid Id, DateTimeOffset CreatedAt)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var createdValue = await reader.GetFieldValueAsync<object>(1, cancellationToken);
                rows.Add((
                    await reader.GetFieldValueAsync<Guid>(0, cancellationToken),
                    createdValue is DateTime created
                        ? new DateTimeOffset(DateTime.SpecifyKind(created, DateTimeKind.Utc))
                        : (DateTimeOffset)createdValue));
            }
        }

        if (closeConnection)
        {
            await connection.CloseAsync();
        }

        var hasMore = rows.Count > limit;
        var pageIds = rows.Take(limit).Select(row => row.Id).ToList();
        var orders = await db.PracticeOrders.AsNoTracking()
            .Where(order => order.UserId == userId && pageIds.Contains(order.Id))
            .ToListAsync(cancellationToken);
        var byId = orders.ToDictionary(order => order.Id);
        var items = pageIds
            .Where(byId.ContainsKey)
            .Select(id => ToItem(byId[id]))
            .ToList();
        var nextCursor = hasMore && items.Count > 0
            ? CursorCodec.Encode($"{items[^1].CreatedAt:O}|{items[^1].Id}")
            : null;
        return Result.Success(new PageResult<PracticeOrderItem>(items, nextCursor, hasMore, limit));
    }

    private async Task<Result<PracticeOrder>> PlaceMarketBuyAsync(
        Guid userId,
        ParsedOrder parsed,
        string assetCode,
        MarketDataPrice price,
        string idempotencyKey,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var amount = parsed.Amount!.Value;
        if (amount < options.Value.MinOrderAmount)
        {
            return Result.Failure<PracticeOrder>(OrderInvalid("amount_below_minimum"));
        }

        if (amount > options.Value.MaxOrderAmount)
        {
            return Result.Failure<PracticeOrder>(OrderInvalid("amount_above_maximum"));
        }

        var computation = PracticeOrderMath.ComputeBuy(
            amount, price.Price!.Value, options.Value.QuantityScale, options.Value.FeeBasisPoints);
        if (computation.Quantity == 0m || computation.Cost == 0m)
        {
            return Result.Failure<PracticeOrder>(OrderInvalid("quantity_rounds_to_zero"));
        }

        var orderId = Guid.CreateVersion7();
        var journal = PracticeOrderFills.BuyFill(
            orderId, userId, assetCode, computation.Quantity, computation.Cost, computation.Fee);
        var posted = await ledger.PostJournalAsync(journal, cancellationToken);
        if (posted.IsFailure)
        {
            return Result.Failure<PracticeOrder>(posted.Error!);
        }

        var now = timeProvider.GetUtcNow();
        var order = new PracticeOrder
        {
            Id = orderId,
            UserId = userId,
            InstrumentId = parsed.InstrumentId,
            Side = PracticeOrderSide.Buy,
            Type = PracticeOrderType.Market,
            State = PracticeOrderState.Filled,
            RequestedAmount = amount,
            FilledQuantity = computation.Quantity,
            FillPrice = price.Price.Value,
            CashAmount = computation.Cost + computation.Fee,
            Fee = computation.Fee,
            PriceAsOf = price.AsOf,
            PriceLabel = price.Label,
            PriceSession = price.Session,
            IdempotencyKey = idempotencyKey,
            FillJournalId = posted.Value.Id,
            CreatedAt = now,
            FilledAt = now,
        };
        return await CompleteFillAsync(order, userId, correlationId, cancellationToken);
    }

    private async Task<Result<PracticeOrder>> PlaceMarketSellAsync(
        Guid userId,
        ParsedOrder parsed,
        string assetCode,
        MarketDataPrice price,
        string idempotencyKey,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var balances = await ledger.GetBalancesAsync(userId, Book.Practice, cancellationToken);
        if (balances.IsFailure)
        {
            return Result.Failure<PracticeOrder>(balances.Error!);
        }

        var available = balances.Value
            .FirstOrDefault(balance => balance.AssetCode == assetCode)?.Available ?? 0m;
        var quantity = parsed.SellAll
            ? available
            : PracticeRounding.RoundQuantity(parsed.Quantity!.Value, options.Value.QuantityScale);
        if (available <= 0m || quantity <= 0m || quantity > available)
        {
            return Result.Failure<PracticeOrder>(new Error(
                ErrorCodes.PracticeInsufficientPosition,
                "The sell quantity exceeds the available practice position.",
                ErrorType.BusinessRule));
        }

        var computation = PracticeOrderMath.ComputeSell(
            quantity, price.Price!.Value, options.Value.FeeBasisPoints);
        if (computation.Proceeds < options.Value.MinOrderAmount
            || computation.Proceeds > options.Value.MaxOrderAmount)
        {
            return Result.Failure<PracticeOrder>(OrderInvalid(
                computation.Proceeds < options.Value.MinOrderAmount
                    ? "proceeds_below_minimum"
                    : "proceeds_above_maximum"));
        }

        if (computation.Proceeds == 0m)
        {
            return Result.Failure<PracticeOrder>(OrderInvalid("proceeds_round_to_zero"));
        }

        var orderId = Guid.CreateVersion7();
        var journal = PracticeOrderFills.SellFill(
            orderId, userId, assetCode, quantity, computation.Proceeds, computation.Fee, fromHeld: false);
        var posted = await ledger.PostJournalAsync(journal, cancellationToken);
        if (posted.IsFailure)
        {
            return Result.Failure<PracticeOrder>(posted.Error!);
        }

        var now = timeProvider.GetUtcNow();
        var order = new PracticeOrder
        {
            Id = orderId,
            UserId = userId,
            InstrumentId = parsed.InstrumentId,
            Side = PracticeOrderSide.Sell,
            Type = PracticeOrderType.Market,
            State = PracticeOrderState.Filled,
            RequestedQuantity = parsed.SellAll ? null : quantity,
            SellAll = parsed.SellAll,
            FilledQuantity = quantity,
            FillPrice = price.Price.Value,
            CashAmount = computation.Proceeds - computation.Fee,
            Fee = computation.Fee,
            PriceAsOf = price.AsOf,
            PriceLabel = price.Label,
            PriceSession = price.Session,
            IdempotencyKey = idempotencyKey,
            FillJournalId = posted.Value.Id,
            CreatedAt = now,
            FilledAt = now,
        };
        return await CompleteFillAsync(order, userId, correlationId, cancellationToken);
    }

    private async Task<Result<PracticeOrder>> CompleteFillAsync(
        PracticeOrder order,
        Guid userId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        db.PracticeOrders.Add(order);
        events.Write(userId, PracticeEventType.OrderFilled, order.Id, correlationId);
        await db.SaveChangesAsync(cancellationToken);
        await outbox.EnqueueAsync(
            OrderFilledMessageType,
            new { orderId = order.Id, userId },
            $"practice:order-filled:{order.Id:N}",
            cancellationToken);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Practice order {OrderId} filled.", order.Id);
        }
        return Result.Success(order);
    }

    private static bool TryParse(
        PracticeOrderRequest request,
        out ParsedOrder parsed,
        out Error? error)
    {
        parsed = default;
        error = null;
        var side = request.Side?.Trim().ToLowerInvariant();
        var type = request.Type?.Trim().ToLowerInvariant();
        var instrumentId = request.InstrumentId?.Trim();
        if (side is not ("buy" or "sell") || type is not ("market" or "stop_loss")
            || string.IsNullOrEmpty(instrumentId))
        {
            error = OrderInvalid("side, type or instrumentId is not recognized");
            return false;
        }

        var isBuy = side == "buy";
        var isStop = type == "stop_loss";
        if (isBuy && isStop)
        {
            error = OrderInvalid("stop orders are sell only");
            return false;
        }

        if (!TryDecimal(request.Amount, out var amount) && request.Amount is not null)
        {
            error = OrderInvalid("amount is not a valid decimal");
            return false;
        }

        if (!TryDecimal(request.Quantity, out var quantity) && request.Quantity is not null)
        {
            error = OrderInvalid("quantity is not a valid decimal");
            return false;
        }

        if (!TryDecimal(request.StopPrice, out var stopPrice) && request.StopPrice is not null)
        {
            error = OrderInvalid("stopPrice is not a valid decimal");
            return false;
        }

        var sellAll = request.SellAll == true;
        if (isBuy)
        {
            if (amount is null or <= 0m || quantity is not null || sellAll || stopPrice is not null)
            {
                error = OrderInvalid("a market buy requires a positive amount and nothing else");
                return false;
            }
        }
        else
        {
            var hasQuantity = quantity is not null;
            if (amount is not null || (hasQuantity == sellAll))
            {
                error = OrderInvalid("a sell requires exactly one of quantity or sellAll");
                return false;
            }

            if (hasQuantity && quantity <= 0m)
            {
                error = OrderInvalid("quantity must be positive");
                return false;
            }

            if (isStop && stopPrice is null or <= 0m)
            {
                error = OrderInvalid("a stop-loss requires a positive stopPrice");
                return false;
            }

            if (!isStop && stopPrice is not null)
            {
                error = OrderInvalid("a market order cannot carry a stopPrice");
                return false;
            }
        }

        parsed = new ParsedOrder(
            instrumentId,
            isBuy ? PracticeOrderSide.Buy : PracticeOrderSide.Sell,
            isStop ? PracticeOrderType.StopLoss : PracticeOrderType.Market,
            amount,
            quantity,
            sellAll,
            stopPrice);
        return true;
    }

    private static bool TryDecimal(string? text, out decimal? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool IsUsable(MarketDataPrice? price)
        => price is not null && price.UsableForExecution && price.Price is > 0m;

    private static Error MarketUnavailable(MarketDataPrice? price)
        => new(
            ErrorCodes.PracticeMarketUnavailable,
            "The latest price is not usable for execution.",
            ErrorType.BusinessRule,
            [
                new ValidationFailure("label", price?.Label ?? "unavailable"),
                new ValidationFailure("session", price?.Session ?? "unknown"),
            ]);

    internal static Error OrderInvalid(string reason)
        => new(
            ErrorCodes.PracticeOrderInvalid,
            $"The order is not valid: {reason}.",
            ErrorType.BusinessRule,
            [new ValidationFailure("reason", reason)]);

    internal static PracticeOrderItem ToItem(PracticeOrder order)
        => new(
            order.Id,
            order.InstrumentId,
            PracticeText.ToDb(order.Side) == "buy" ? "buy" : "sell",
            PracticeText.ToDb(order.Type),
            PracticeText.ToDb(order.State),
            order.RequestedAmount,
            order.RequestedQuantity,
            order.SellAll,
            order.StopPrice,
            order.FilledQuantity,
            order.FillPrice,
            order.CashAmount,
            order.Fee,
            order.PriceAsOf,
            order.PriceLabel,
            order.PriceSession,
            order.CancelReason,
            order.CreatedAt,
            order.FilledAt,
            order.CancelledAt);

    private static bool TryDecodeCursor(string? cursor, out DateTimeOffset createdAt, out Guid orderId)
    {
        createdAt = default;
        orderId = Guid.Empty;
        if (cursor is null || !CursorCodec.TryDecode(cursor, out var payload) || payload is null)
        {
            return false;
        }

        var separator = payload.IndexOf('|', StringComparison.Ordinal);
        return separator > 0
            && DateTimeOffset.TryParse(payload[..separator], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out createdAt)
            && Guid.TryParse(payload[(separator + 1)..], out orderId);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private async Task RollbackAsync(bool ownsScope, CancellationToken cancellationToken)
    {
        if (ownsScope)
        {
            await scope.RollbackAsync(cancellationToken);
        }
    }

    private readonly record struct ParsedOrder(
        string InstrumentId,
        PracticeOrderSide Side,
        PracticeOrderType Type,
        decimal? Amount,
        decimal? Quantity,
        bool SellAll,
        decimal? StopPrice);
}
