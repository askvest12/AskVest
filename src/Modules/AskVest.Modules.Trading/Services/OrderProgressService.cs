using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Trading.Configuration;
using AskVest.Modules.Trading.Domain;
using AskVest.Modules.Trading.Gateway;
using AskVest.Modules.Trading.Persistence;
using AskVest.Modules.Trading.Persistence.Entities;
using AskVest.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Trading.Services;

/// <summary>
/// Polls the gateway for one non-final order and applies the definitive
/// answer. Every mutation runs in its own transaction scope; uncertainty
/// always keeps the hold and moves the order to needs_reconciliation.
/// </summary>
internal sealed class OrderProgressService(
    TradingDbContext db,
    ICatalog catalog,
    GatewayRegistry gateways,
    ILedger ledger,
    IOptions<TradingOptions> options,
    TimeProvider timeProvider,
    ITransactionScope scope,
    IOutbox outbox,
    TradingEventWriter events,
    ILogger<OrderProgressService> logger)
{
    public const string StateChangedMessageType = "RealOrderStateChanged";

    public async Task ProcessAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == orderId, cancellationToken);
        if (order is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var settings = options.Value;
        switch (order.State)
        {
            case OrderState.PendingSubmission
                when order.UpdatedAt < now - TimeSpan.FromSeconds(settings.SubmissionStuckSeconds):
                await outbox.EnqueueAsync(
                    OrderService.SubmitMessageType,
                    new { orderId = order.Id },
                    null,
                    cancellationToken);
                return;
            case OrderState.Submitted
                when order.SubmittedAt < now - TimeSpan.FromSeconds(settings.SubmittedTimeoutSeconds):
                await MoveToReconciliationAsync(orderId, "submitted_timeout", cancellationToken);
                return;
            case OrderState.Submitted:
            case OrderState.NeedsReconciliation:
            case OrderState.Submitting
                when order.UpdatedAt < now - TimeSpan.FromSeconds(settings.SubmissionStuckSeconds):
                break;
            default:
                return;
        }

        var status = await PollGatewayAsync(order, cancellationToken);
        if (status is null)
        {
            return;
        }

        await ApplyStatusAsync(orderId, status, cancellationToken);
    }

    private async Task<GatewayOrderStatus?> PollGatewayAsync(
        TradingOrder order,
        CancellationToken cancellationToken)
    {
        if (!gateways.TryResolve(order.GatewayKey, out var gateway))
        {
            return null;
        }

        try
        {
            using var timeoutSource = new CancellationTokenSource(
                TimeSpan.FromSeconds(options.Value.GatewayTimeoutSeconds), timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutSource.Token);
            return await gateway.GetOrderStatusAsync(order.Id.ToString("N"), linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Gateway status poll failed for order {OrderId}.", order.Id);
            return null;
        }
    }

    private async Task ApplyStatusAsync(
        Guid orderId,
        GatewayOrderStatus status,
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
            await db.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM trading.orders WHERE id = {orderId} FOR UPDATE",
                cancellationToken);
            var order = await db.Orders
                .FirstOrDefaultAsync(row => row.Id == orderId, cancellationToken);
            if (order is null || OrderTransitions.IsFinal(order.State))
            {
                await CommitIfOwnerAsync(ownsScope, cancellationToken);
                return;
            }

            var now = timeProvider.GetUtcNow();
            // A fill can surface while the order is still marked submitting
            // (the acknowledgement was lost); submitted is the required
            // intermediate state before executed.
            if (status.Kind == GatewayStatusKind.Filled && order.State == OrderState.Submitting)
            {
                order.State = OrderState.Submitted;
                order.SubmittedAt = now;
                order.UpdatedAt = now;
                events.Write(
                    order.UserId, TradingEventType.OrderSubmitted, order.Id,
                    OrderState.Submitting, OrderState.Submitted, null);
            }

            switch (status.Kind)
            {
                case GatewayStatusKind.Pending:
                    order.LastStatusCheckAt = now;
                    order.UpdatedAt = now;
                    break;
                case GatewayStatusKind.Filled when status.Fill is { } fill:
                    await ApplyFillAsync(order, fill, now, cancellationToken);
                    break;
                case GatewayStatusKind.Rejected:
                    await FailAsync(order, status.ReasonCode ?? "gateway_rejected", now, cancellationToken);
                    break;
                case GatewayStatusKind.NotFound
                    when order.State == OrderState.Submitting:
                    await MoveToReconciliationLockedAsync(order, "submit_not_found", now, cancellationToken);
                    break;
                default:
                    break;
            }

            await db.SaveChangesAsync(cancellationToken);
            await CommitIfOwnerAsync(ownsScope, cancellationToken);
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    private async Task ApplyFillAsync(
        TradingOrder order,
        GatewayFillTerms fill,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var terms = new QuoteTerms(
            order.Side,
            order.Quantity,
            order.Price,
            order.GrossAmount,
            order.OurFee,
            order.GatewayFee,
            order.Total);
        if (!FillComparison.Matches(fill, terms))
        {
            await MoveToReconciliationLockedAsync(
                order, $"fill_mismatch:{FillComparison.DescribeMismatch(fill, terms)}", now, cancellationToken);
            return;
        }

        var token = await catalog.FindTokenAsync(order.TokenId, cancellationToken);
        if (token is null)
        {
            await MoveToReconciliationLockedAsync(order, "token_unknown", now, cancellationToken);
            return;
        }

        var journal = order.Side == OrderSide.Buy
            ? OrderJournals.BuyFill(
                order.Id, order.UserId, order.SettlementAsset, token.LedgerAssetCode,
                fill.Quantity, fill.GrossAmount, order.OurFee, order.GatewayFee, order.Total)
            : OrderJournals.SellFill(
                order.Id, order.UserId, order.SettlementAsset, token.LedgerAssetCode,
                fill.Quantity, fill.GrossAmount, order.OurFee, order.GatewayFee);
        var posted = await ledger.PostJournalAsync(journal, cancellationToken);
        if (posted.IsFailure)
        {
            throw new InvalidOperationException(
                $"Fill journal failed for order {order.Id}: {posted.Error!.Code}");
        }

        var from = order.State;
        order.State = OrderState.Executed;
        order.FillJournalId = posted.Value.Id;
        order.SettlementChainId = fill.SettlementChainId;
        order.SettlementReference = fill.SettlementReference;
        order.ExecutedAt = now;
        order.UpdatedAt = now;
        events.Write(order.UserId, TradingEventType.OrderExecuted, order.Id, from, OrderState.Executed, null);
        await EnqueueStateChangedAsync(order, cancellationToken);
    }

    private async Task FailAsync(
        TradingOrder order,
        string reasonCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var from = order.State;
        if (order.HoldJournalId is { } holdJournalId)
        {
            var released = await ledger.ReverseJournalAsync(
                OrderJournals.ReleaseKey(order.Id), holdJournalId, cancellationToken);
            if (released.IsFailure)
            {
                throw new InvalidOperationException(
                    $"Hold release failed for order {order.Id}: {released.Error!.Code}");
            }

            order.ReleaseJournalId = released.Value.Id;
        }

        order.State = OrderState.Failed;
        order.FailureCode = reasonCode;
        order.FailedAt = now;
        order.UpdatedAt = now;
        events.Write(order.UserId, TradingEventType.OrderFailed, order.Id, from, OrderState.Failed, null, reasonCode);
        await EnqueueStateChangedAsync(order, cancellationToken);
    }

    private async Task MoveToReconciliationAsync(
        Guid orderId,
        string detailCode,
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
            await db.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM trading.orders WHERE id = {orderId} FOR UPDATE",
                cancellationToken);
            var order = await db.Orders
                .FirstOrDefaultAsync(row => row.Id == orderId, cancellationToken);
            if (order is not null && OrderTransitions.IsNonFinal(order.State))
            {
                await MoveToReconciliationLockedAsync(
                    order, detailCode, timeProvider.GetUtcNow(), cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            await CommitIfOwnerAsync(ownsScope, cancellationToken);
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    private async Task MoveToReconciliationLockedAsync(
        TradingOrder order,
        string detailCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var from = order.State;
        order.State = OrderState.NeedsReconciliation;
        order.DetailCode = detailCode;
        order.UpdatedAt = now;
        events.Write(
            order.UserId, TradingEventType.OrderNeedsReconciliation, order.Id,
            from, OrderState.NeedsReconciliation, null, detailCode);
        await EnqueueStateChangedAsync(order, cancellationToken);
    }

    private async Task EnqueueStateChangedAsync(TradingOrder order, CancellationToken cancellationToken)
        => await outbox.EnqueueAsync(
            StateChangedMessageType,
            new { orderId = order.Id, userId = order.UserId, state = TradingText.ToDb(order.State) },
            $"trading:state:{order.Id:N}:{TradingText.ToDb(order.State)}",
            cancellationToken);

    private async Task EnlistAsync(CancellationToken cancellationToken)
    {
        if (scope.Transaction is not null)
        {
            await db.Database.UseTransactionAsync(scope.Transaction, cancellationToken);
        }
    }

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
