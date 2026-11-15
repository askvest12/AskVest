using System.Text.Json;
using AskVest.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace AskVest.Modules.Funding.Services;

/// <summary>
/// Outbox handler for cash withdrawal submission. Payloads carry the
/// withdrawal id only. Redelivery is safe: the service claims only
/// requested rows.
/// </summary>
internal sealed class FundingPayoutSubmitHandler(IServiceScopeFactory scopeFactory)
    : IOutboxHandler
{
    public string MessageType => FundingWithdrawalService.SubmitMessageType;

    public async Task HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!payload.TryGetProperty("withdrawalId", out var element)
            || !Guid.TryParse(element.GetString(), out var withdrawalId))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var submissions = scope.ServiceProvider.GetRequiredService<FundingPayoutSubmitService>();
        await submissions.SubmitAsync(withdrawalId, cancellationToken);
    }
}
