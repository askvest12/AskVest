using Microsoft.Extensions.Logging;

namespace AskVest.Modules.Identity.Messaging;

public sealed class NullSmsTransport(ILogger<NullSmsTransport> logger) : ISmsTransport
{
    public Task SendAsync(SmsMessage message, CancellationToken cancellationToken)
    {
        logger.LogWarning("SMS transport is disabled; dropping message to {RecipientSuffix}.", Suffix(message.To));
        return Task.CompletedTask;
    }

    private static string Suffix(string phone)
        => phone.Length <= 4 ? "***" : $"***{phone[^4..]}";
}
