using System.Threading.Channels;
using AskVest.Modules.Identity.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Messaging;

public sealed class MessageChannel : IMessageChannel
{
    private readonly Channel<OutboundMessage> _channel;
    private readonly ILogger<MessageChannel> _logger;

    public MessageChannel(IOptions<MessagingOptions> options, ILogger<MessageChannel> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<OutboundMessage>(new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public bool TryEnqueue(OutboundMessage message) => _channel.Writer.TryWrite(message);

    public ChannelReader<OutboundMessage> Reader => _channel.Reader;
}
