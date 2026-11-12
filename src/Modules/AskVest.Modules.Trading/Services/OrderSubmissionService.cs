using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Trading.Configuration;
using AskVest.Modules.Trading.Domain;
using AskVest.Modules.Trading.Gateway;
using AskVest.Modules.Trading.Persistence;
using AskVest.Modules.Trading.Persistence.Entities;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Trading.Services;

/// <summary>
/// Delivers one pending_submission order to the issuer gateway. The state
/// claim and the acknowledgement each run in their own transaction; the
/// gateway call itself never holds a database transaction open.
/// </summary>
internal sealed class OrderSubmissionService(
    TradingDbContext db,
    ICatalog catalog,
    GatewayRegistry gateways,
    ILedger ledger,
    IOptions<TradingOptions> options,
    TimeProvider timeProvider,
    ITransactionScope scope,
    TradingEventWriter events,
    ILogger<OrderSubmissionService> logger)
{
    public async Task SubmitAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(orderId, cancellationToken);
        if (claimed is null)
        {
            return;
        }

        var order = claimed;
        var token = await catalog.FindTokenAsync(order.TokenId, cancellationToken);
        var quote = await db.Quotes.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == order.QuoteId, cancellationToken);
        if (token is null || quote is null || !gateways.TryResolve(order.GatewayKey, out var gateway))
        {
            await ApplyUnknownAsync(order.Id, "gateway_unresolvable", cancellationToken);
            return;
        }

        var reference = new GatewayTokenReference(
            token.Id,
            token.InstrumentId,
            token.ChainId,
            token.CanonicalAddress,
            token.Caip19,
            token.Symbol,
            token.Decimals,
            token.LedgerAssetCode);
        var request = new GatewaySubmitRequest(
            order.Id.ToString("N"),
            quote.GatewayQuoteId,
            reference,
            order.Side,
            order.Quantity,
            order.Price,
            order.GrossAmount,
            order.GatewayFee,
            order.Total,
            order.SettlementAsset);

        GatewaySubmitResult result;
        var timeout = TimeSpan.FromSeconds(options.Value.GatewayTimeoutSeconds);
        try
        {
            using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutSource.Token);
            result = await gateway.SubmitOrderAsync(request, linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = new GatewaySubmitResult(GatewaySubmitKind.Unknown, null, "timeout");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Gateway submission failed for order {OrderId}.", orderId);
            result = new GatewaySubmitResult(GatewaySubmitKind.Unknown, null, "error");
        }

        await ApplyResultAsync(order.Id, result, cancellationToken);
    }

    private async Task<TradingOrder?> ClaimAsync(Guid orderId, CancellationToken cancellationToken)
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
            if (order is null || order.State != OrderState.PendingSubmission)
            {
                await CommitIfOwnerAsync(ownsScope, cancellationToken);
                return null;
            }

            order.State = OrderState.Submitting;
            order.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            await CommitIfOwnerAsync(ownsScope, cancellationToken);
            return order;
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    private async Task ApplyResultAsync(
        Guid orderId,
        GatewaySubmitResult result,
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
            if (order is null || order.State != OrderState.Submitting)
            {
                await CommitIfOwnerAsync(ownsScope, cancellationToken);
                return;
            }

            var now = timeProvider.GetUtcNow();
            switch (result.Kind)
            {
                case GatewaySubmitKind.Acknowledged:
                    order.State = OrderState.Submitted;
                    order.GatewayOrderReference = result.GatewayReference;
                    order.SubmittedAt = now;
                    order.UpdatedAt = now;
                    events.Write(
                        order.UserId, TradingEventType.OrderSubmitted, order.Id,
                        OrderState.Submitting, OrderState.Submitted, null);
                    break;
                case GatewaySubmitKind.Rejected:
                    await FailAsync(order, result.ReasonCode ?? "rejected", now, cancellationToken);
                    break;
                default:
                    order.State = OrderState.NeedsReconciliation;
                    order.DetailCode = result.ReasonCode ?? "submit_unknown";
                    order.UpdatedAt = now;
                    events.Write(
                        order.UserId, TradingEventType.OrderNeedsReconciliation, order.Id,
                        OrderState.Submitting, OrderState.NeedsReconciliation, null, order.DetailCode);
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

    private async Task ApplyUnknownAsync(
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
            if (order is not null && order.State == OrderState.Submitting)
            {
                order.State = OrderState.NeedsReconciliation;
                order.DetailCode = detailCode;
                order.UpdatedAt = timeProvider.GetUtcNow();
                events.Write(
                    order.UserId, TradingEventType.OrderNeedsReconciliation, order.Id,
                    OrderState.Submitting, OrderState.NeedsReconciliation, null, detailCode);
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

    private async Task FailAsync(
        TradingOrder order,
        string reasonCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
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
        events.Write(
            order.UserId, TradingEventType.OrderFailed, order.Id,
            OrderState.Submitting, OrderState.Failed, null, reasonCode);
    }

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
