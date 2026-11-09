using AskVest.SharedKernel;

namespace AskVest.Modules.Assistant.Contracts;

/// <summary>
/// The only seam to a language model. Sends a system prompt, a bounded
/// message history, fixed tool definitions and limits; returns final text
/// or tool calls plus token usage. Never streams. The deterministic fake
/// ships for development and tests; a real adapter is chosen after
/// evaluation. This is the module's single outbound network boundary.
/// </summary>
public interface ILanguageModelClient
{
    Task<Result<LanguageModelResponse>> CompleteAsync(
        LanguageModelRequest request,
        CancellationToken cancellationToken = default);
}
