namespace AskVest.Modules.Identity.Messaging;

public interface IMessageChannel
{
    bool TryEnqueue(OutboundMessage message);
}
