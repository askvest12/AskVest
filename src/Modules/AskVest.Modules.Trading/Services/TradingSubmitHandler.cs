using System.Text.Json;
using AskVest.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace AskVest.Modules.Trading.Services;

/// <summary>
/// Outbox handler for order submission. Payloads carry the order id only.
/// Redelivery is safe: the service claims only pending_submission orders.
/// </summary>
internal sealed class TradingSubmitHandler(IServiceScopeFactory scopeFactory)
    : IOutboxHandler
{
    public string MessageType => OrderService.SubmitMessageType;

    public async Task HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!payload.TryGetProperty("orderId", out var element)
            || !Guid.TryParse(element.GetString(), out var orderId))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var submissions = scope.ServiceProvider.GetRequiredService<OrderSubmissionService>();
        await submissions.SubmitAsync(orderId, cancellationToken);
    }
}
