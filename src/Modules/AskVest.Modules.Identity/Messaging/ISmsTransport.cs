namespace AskVest.Modules.Identity.Messaging;

public interface ISmsTransport
{
    Task SendAsync(SmsMessage message, CancellationToken cancellationToken);
}
