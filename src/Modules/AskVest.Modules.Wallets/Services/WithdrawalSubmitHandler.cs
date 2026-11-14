using System.Text.Json;
using AskVest.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace AskVest.Modules.Wallets.Services;

/// <summary>
/// Outbox handler for withdrawal submission. Payloads carry the withdrawal
/// id only. Redelivery is safe: the service claims only requested rows.
/// </summary>
internal sealed class WithdrawalSubmitHandler(IServiceScopeFactory scopeFactory)
    : IOutboxHandler
{
    public string MessageType => WithdrawalService.SubmitMessageType;

    public async Task HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!payload.TryGetProperty("withdrawalId", out var element)
            || !Guid.TryParse(element.GetString(), out var withdrawalId))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var submissions = scope.ServiceProvider.GetRequiredService<WithdrawalSubmitService>();
        await submissions.SubmitAsync(withdrawalId, cancellationToken);
    }
}
