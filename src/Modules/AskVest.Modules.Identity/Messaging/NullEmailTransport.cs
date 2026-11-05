using Microsoft.Extensions.Logging;

namespace AskVest.Modules.Identity.Messaging;

public sealed class NullEmailTransport(ILogger<NullEmailTransport> logger) : IEmailTransport
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        logger.LogWarning("Email transport is not configured; dropping message to {RecipientDomain}.", DomainOf(message.To));
        return Task.CompletedTask;
    }

    internal static string DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at >= 0 ? address[(at + 1)..] : "unknown";
    }
}
