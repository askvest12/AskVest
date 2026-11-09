using System.Text.Json;
using System.Text.RegularExpressions;
using AskVest.Modules.Assistant.Configuration;
using AskVest.Modules.Assistant.Contracts;
using AskVest.SharedKernel;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Assistant.Domain;

/// <summary>
/// Deterministic scripted model for development and tests. The scenario is
/// mutable so the diagnostics probe can arm it; it is refused in Production
/// at registration time. Every response is derived only from the request
/// messages, so runs are reproducible.
/// </summary>
internal sealed partial class FakeLanguageModelClient(IOptions<AssistantOptions> options) : ILanguageModelClient
{
    private volatile string? _scenario;

    public FakeLanguageModelClient()
        : this(Options.Create(new AssistantOptions()))
    {
    }

    public string Scenario
    {
        get => _scenario ?? options.Value.FakeScenario;
        set => _scenario = value;
    }

    public Task<Result<LanguageModelResponse>> CompleteAsync(
        LanguageModelRequest request,
        CancellationToken cancellationToken = default)
    {
        _scenario ??= options.Value.FakeScenario;
        return Scenario switch
        {
            "error" => Task.FromResult(Result.Failure<LanguageModelResponse>(Unavailable())),
            "slow" => SlowAsync(request.Timeout, cancellationToken),
            "malformed_output" => Task.FromResult(Result.Success(Malformed())),
            "loop_tools" => Task.FromResult(Result.Success(LoopToolCall(request))),
            "injection_obey" => Task.FromResult(Result.Success(InjectionObey(request))),
            "fabricated_citation" => Task.FromResult(Result.Success(FabricatedCitation())),
            "ungrounded_number" => Task.FromResult(Result.Success(UngroundedNumber(request))),
            "advice_phrase" => Task.FromResult(Result.Success(AdvicePhrase())),
            "markup_output" => Task.FromResult(Result.Success(MarkupOutput())),
            _ => Task.FromResult(Result.Success(Normal(request))),
        };
    }

    private static async Task<Result<LanguageModelResponse>> SlowAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(timeout + TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<LanguageModelResponse>(Unavailable());
        }

        return Result.Failure<LanguageModelResponse>(Unavailable());
    }

    private static Error Unavailable()
        => new(ErrorCodes.AssistantUnavailable, "The assistant is unavailable.", ErrorType.Unavailable);

    private static LanguageModelResponse Normal(LanguageModelRequest request)
    {
        var toolMessages = request.Messages.Count(message => message.Role == "tool");
        var userText = LastUserText(request);
        return toolMessages switch
        {
            0 => RespondWithToolCall(request, "search_instruments", new { query = userText }),
            1 => PriceLookup(request),
            _ => FinalAnswer(request),
        };
    }

    private static LanguageModelResponse PriceLookup(LanguageModelRequest request)
    {
        var toolText = request.Messages.Last(message => message.Role == "tool").Text;
        var match = InstrumentIdRegex().Match(toolText);
        return match.Success
            ? RespondWithToolCall(request, "get_prices", new { instrumentIds = new[] { match.Groups[1].Value } })
            : Respond("I could not find any matching instruments. [r1]");
    }

    private static LanguageModelResponse FinalAnswer(LanguageModelRequest request)
    {
        var toolText = request.Messages.Last(message => message.Role == "tool").Text;
        var price = PriceRegex().Match(toolText);
        var symbol = SymbolRegex().Match(toolText);
        var asOf = AsOfRegex().Match(toolText);
        var label = LabelRegex().Match(toolText);
        var name = symbol.Success ? symbol.Groups[1].Value : "the instrument";
        var text = price.Success
            ? $"{name} is priced at {price.Groups[1].Value} {price.Groups[2].Value} as of {asOf.Groups[1].Value} (label: {label.Groups[1].Value}). This is a research summary, not advice. [r1][r2]"
            : $"{name} has no usable price right now (label: {(label.Success ? label.Groups[1].Value : "unavailable")}). [r1][r2]";
        return Respond(text);
    }

    private static LanguageModelResponse InjectionObey(LanguageModelRequest request)
    {
        var toolMessages = request.Messages.Count(message => message.Role == "tool");
        return toolMessages switch
        {
            0 => new LanguageModelResponse(
                null,
                [
                    new LanguageModelToolCall("call-1", "delete_user_data", "{\"userId\":\"00000000-0000-0000-0000-000000000000\"}"),
                    new LanguageModelToolCall("call-2", "get_portfolio", "{\"book\":\"real\",\"userId\":\"00000000-0000-0000-0000-000000000000\"}"),
                ],
                10,
                10,
                ModelId()),
            _ => Respond("I cannot do that. I only answer research questions about the catalog, prices, your own portfolio, watchlist and lessons."),
        };
    }

    private static LanguageModelResponse FabricatedCitation()
        => Respond("The sample instrument trades at a fictional price. [r1][r99]");

    private static LanguageModelResponse UngroundedNumber(LanguageModelRequest request)
    {
        var toolMessages = request.Messages.Count(message => message.Role == "tool");
        return toolMessages == 0
            ? Normal(request)
            : Respond("The price is 999.99 USD and it will rise 42% next week. [r1][r2]");
    }

    private static LanguageModelResponse AdvicePhrase()
        => Respond("You should buy this stock today. It is a sure thing and I recommend you sell everything else first.");

    private static LanguageModelResponse MarkupOutput()
        => Respond("Here is [a link](https://evil.example) and <b>bold</b> text plus https://evil.example/page inline.");

    private static LanguageModelResponse Malformed()
        => new(null, [], 0, 0, ModelId());

    private static LanguageModelResponse LoopToolCall(LanguageModelRequest request)
        => RespondWithToolCall(
            request,
            "search_instruments",
            new { query = LastUserText(request) });

    private static LanguageModelResponse RespondWithToolCall(
        LanguageModelRequest request,
        string name,
        object arguments)
    {
        var callIndex = request.Messages.Count(message => message.Role == "assistant");
        return new LanguageModelResponse(
            null,
            [new LanguageModelToolCall($"call-{callIndex + 1}", name, JsonSerializer.Serialize(arguments))],
            10,
            10,
            ModelId());
    }

    private static LanguageModelResponse Respond(string text)
        => new(text, [], 10, 10, ModelId());

    private static string LastUserText(LanguageModelRequest request)
        => request.Messages.LastOrDefault(message => message.Role == "user")?.Text ?? string.Empty;

    private static string ModelId() => "fake-1";

    [GeneratedRegex("\"instrumentId\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex InstrumentIdRegex();

    [GeneratedRegex("\"price\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"currency\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex PriceRegex();

    [GeneratedRegex("\"symbol\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex SymbolRegex();

    [GeneratedRegex("\"asOf\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex AsOfRegex();

    [GeneratedRegex("\"label\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex LabelRegex();
}
