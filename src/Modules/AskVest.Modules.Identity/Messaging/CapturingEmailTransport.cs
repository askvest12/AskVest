using System.Collections.Concurrent;

namespace AskVest.Modules.Identity.Messaging;

public sealed class CapturingEmailTransport : IEmailTransport
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public IReadOnlyCollection<EmailMessage> Sent => _sent.ToArray();

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        _sent.Enqueue(message);
        return Task.CompletedTask;
    }
}
