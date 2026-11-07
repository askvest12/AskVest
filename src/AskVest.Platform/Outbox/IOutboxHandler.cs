using System.Text.Json;

namespace AskVest.Platform;

public interface IOutboxHandler
{
    string MessageType { get; }

    Task HandleAsync(JsonElement payload, CancellationToken cancellationToken);
}
