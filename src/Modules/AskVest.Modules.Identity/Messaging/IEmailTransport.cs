namespace AskVest.Modules.Identity.Messaging;

public interface IEmailTransport
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
