using AskVest.Modules.Identity.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Messaging;

public sealed class DevelopmentSmsTransport(
    IEmailTransport emailTransport,
    IOptions<MessagingOptions> options,
    ILogger<DevelopmentSmsTransport> logger) : ISmsTransport
{
    public async Task SendAsync(SmsMessage message, CancellationToken cancellationToken)
    {
        var forwardTo = options.Value.Sms.ForwardTo;
        if (string.IsNullOrWhiteSpace(forwardTo))
        {
            throw new InvalidOperationException("Development SMS adapter requires Messaging:Sms:ForwardTo.");
        }

        var email = new EmailMessage(
            forwardTo,
            $"AskVest SMS to {MaskPhone(message.To)}",
            $"To: {message.To}\n\n{message.Body}");
        await emailTransport.SendAsync(email, cancellationToken);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Development SMS adapter forwarded message to {ForwardTo}.", forwardTo);
        }
    }

    private static string MaskPhone(string phone)
    {
        if (phone.Length <= 4)
        {
            return "***";
        }

        return $"***{phone[^4..]}";
    }
}
