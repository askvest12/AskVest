using AskVest.Modules.Identity.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Messaging;

public sealed class MessageDispatchWorker(
    MessageChannel channel,
    IEmailTransport emailTransport,
    ISmsTransport smsTransport,
    IOptions<MessagingOptions> options,
    TimeProvider timeProvider,
    ILogger<MessageDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = channel.Reader;
        try
        {
            await foreach (var message in reader.ReadAllAsync(stoppingToken))
            {
                await DispatchWithRetriesAsync(message, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: queued messages are dropped; users can request a resend.
        }
    }

    private async Task DispatchWithRetriesAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        for (var attempt = 1; attempt <= settings.MaxAttempts; attempt++)
        {
            try
            {
                switch (message)
                {
                    case QueuedEmail email:
                        await emailTransport.SendAsync(email.Message, cancellationToken);
                        break;
                    case QueuedSms sms:
                        await smsTransport.SendAsync(sms.Message, cancellationToken);
                        break;
                }

                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (attempt < settings.MaxAttempts)
                {
                    var delay = settings.RetryDelay(attempt);
                    logger.LogWarning(exception,
                        "Message delivery attempt {Attempt}/{MaxAttempts} failed; retrying in {DelayMs}ms.",
                        attempt, settings.MaxAttempts, (int)delay.TotalMilliseconds);
                    await Task.Delay(delay, timeProvider, cancellationToken);
                    continue;
                }

                logger.LogError(exception,
                    "Message delivery failed permanently after {MaxAttempts} attempts.",
                    settings.MaxAttempts);
            }
        }
    }
}
