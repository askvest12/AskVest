using System.Collections.Concurrent;

namespace AskVest.Modules.Identity.Messaging;

public sealed class CapturingSmsTransport : ISmsTransport
{
    private readonly ConcurrentQueue<SmsMessage> _sent = new();

    public IReadOnlyCollection<SmsMessage> Sent => _sent.ToArray();

    public Task SendAsync(SmsMessage message, CancellationToken cancellationToken)
    {
        _sent.Enqueue(message);
        return Task.CompletedTask;
    }
}
