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
    private int _callCount;

    public FakeLanguageModelClient()
        : this(Options.Create(new AssistantOptions()))
    {
    }

    /// <summary>Model calls served since the host started; tests use it as the replay spy.</summary>
    public int CallCount => _callCount;

    public string Scenario
    {
        get => _scenario ?? options.Value.FakeScenario;
        set => _scenario = value;
    }

    public Task<Result<LanguageModelResponse>> CompleteAsync(
        LanguageModelRequest request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
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
        if (toolMessages == 0)
        {
            return FirstToolCall(request, userText);
        }

        var lastTool = request.Messages.Last(message => message.Role == "tool");
        if (toolMessages == 1 && lastTool.ToolName == "search_instruments")
        {
            return NextStep(request, userText, lastTool.Text);
        }

        return toolMessages == 1
            ? SimpleAnswer(lastTool.ToolName, lastTool.Text)
            : FinalAnswer(lastTool);
    }

    private static LanguageModelResponse FirstToolCall(LanguageModelRequest request, string userText)
    {
        if (ActionRequestRegex().IsMatch(userText))
        {
            return Respond("I can't place trades, move money or change your account. I can explain instruments, prices, your portfolio, your watchlist and lessons instead.");
        }

        if (DisclosureRequestRegex().IsMatch(userText))
        {
            return Respond("I can't share my instructions or internal configuration. I can still explain instruments, prices, your portfolio, your watchlist and lessons.");
        }

        if (userText.Contains("watchlist", StringComparison.OrdinalIgnoreCase)
            || userText.Contains("watch list", StringComparison.OrdinalIgnoreCase))
        {
            return RespondWithToolCall(request, "get_watchlist", new { });
        }

        if (PortfolioRegex().IsMatch(userText))
        {
            var book = RealBookRegex().IsMatch(userText) ? "real" : "practice";
            return RespondWithToolCall(request, "get_portfolio", new { book });
        }

        if (LessonRegex().IsMatch(userText))
        {
            return RespondWithToolCall(request, "search_lessons", new { query = ExtractQuery(userText) });
        }

        return RespondWithToolCall(request, "search_instruments", new { query = ExtractQuery(userText) });
    }

    private static LanguageModelResponse NextStep(
        LanguageModelRequest request,
        string userText,
        string searchToolText)
    {
        var instrumentId = InstrumentIdRegex().Match(searchToolText);
        if (!instrumentId.Success)
        {
            return Respond("I could not find any matching instruments. [r1]");
        }

        if (WantsPrices(userText))
        {
            return RespondWithToolCall(
                request, "get_prices", new { instrumentIds = new[] { instrumentId.Groups[1].Value } });
        }

        if (WantsHistory(userText))
        {
            return RespondWithToolCall(
                request,
                "get_price_history_summary",
                new
                {
                    instrumentId = instrumentId.Groups[1].Value,
                    interval = "1d",
                    from = "2026-01-01T00:00:00Z",
                    to = "2026-01-08T00:00:00Z",
                });
        }

        if (WantsDetails(userText))
        {
            return RespondWithToolCall(
                request, "get_instrument", new { instrumentId = instrumentId.Groups[1].Value });
        }

        return RespondWithToolCall(
            request, "get_prices", new { instrumentIds = new[] { instrumentId.Groups[1].Value } });
    }

    private static bool WantsPrices(string userText)
        => userText.Contains("price", StringComparison.OrdinalIgnoreCase)
            || userText.Contains("cost", StringComparison.OrdinalIgnoreCase)
            || userText.Contains("worth", StringComparison.OrdinalIgnoreCase)
            || userText.Contains("quote", StringComparison.OrdinalIgnoreCase)
            || userText.Contains("trading at", StringComparison.OrdinalIgnoreCase);

    private static bool WantsHistory(string userText)
        => HistoryRegex().IsMatch(userText);

    private static bool WantsDetails(string userText)
        => DetailsRegex().IsMatch(userText);

    private static LanguageModelResponse SimpleAnswer(string? toolName, string toolText)
    {
        var text = toolName switch
        {
            "get_portfolio" when toolText.Contains("\"available\":false", StringComparison.Ordinal) =>
                "The real portfolio is not available for your account right now. [r1]",
            "get_portfolio" => "Here is your portfolio summary. [r1]",
            "get_watchlist" => "Here is your watchlist. [r1]",
            "search_lessons" when toolText.Contains("\"count\":0", StringComparison.Ordinal) =>
                "I could not find a matching lesson. [r1]",
            "search_lessons" => "I found matching lessons you can read for more detail. [r1]",
            _ => "I found the information you asked for. [r1]",
        };
        return Respond(text);
    }

    private static string ExtractQuery(string userText)
    {
        var cleaned = PreClean(userText);
        var tokens = KeywordRegex().Matches(cleaned)
            .Select(match => match.Value.Trim('-'))
            .Where(token => token.Length >= 3 && !StopWords.Contains(token))
            .ToList();
        var best = tokens
            .OrderByDescending(IdentifierScore)
            .ThenByDescending(token => token.Length)
            .FirstOrDefault();
        return Truncate(best ?? cleaned);
    }

    private static int IdentifierScore(string token)
        => token.Any(char.IsDigit) || token.Contains('-') || token.All(char.IsUpper) ? 1 : 0;

    private static string PreClean(string text)
    {
        var withoutImages = MarkdownImageStrip().Replace(text, " ");
        var withoutLinks = MarkdownLinkStrip().Replace(withoutImages, "$1");
        var withoutTags = HtmlTagStrip().Replace(withoutLinks, " ");
        return UrlStrip().Replace(withoutTags, " ");
    }

    private static string Truncate(string text)
        => text.Length <= 100 ? text : text[..100];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "is", "are", "was", "were", "what", "when", "where", "which",
        "who", "whom", "why", "how", "do", "does", "did", "can", "could", "should",
        "would", "will", "shall", "may", "might", "must", "i", "me", "my", "we", "our",
        "you", "your", "it", "its", "this", "that", "these", "those", "there", "here",
        "to", "of", "in", "on", "for", "with", "about", "into", "over", "under", "and",
        "or", "but", "if", "then", "than", "so", "at", "by", "from", "as", "be", "been",
        "being", "have", "has", "had", "not", "no", "please", "thanks", "hello", "hi",
        "hey", "oh", "vs", "versus", "etc", "per", "via", "within", "without", "between",
        "among", "onto", "upon", "along", "across", "behind", "beyond", "plus", "minus",
        "latest", "price", "prices", "cost", "costs", "worth", "quote", "quotes",
        "trading", "trade", "trades", "market", "order", "orders", "buy", "buys",
        "sell", "sells", "selling", "purchase", "purchases", "predict", "predicts",
        "prediction", "predictions", "forecast", "forecasts", "month", "months",
        "week", "weeks", "year", "years", "today", "now", "next", "last", "past",
        "represent", "represents", "representation", "means", "mean", "actually",
        "really", "just", "also", "much", "many", "some", "any", "each", "every",
        "own", "tell", "tells", "show", "shows", "give", "gives", "explain", "explains",
        "describe", "describes", "description", "information", "data", "want", "wants",
        "need", "needs", "like", "likes", "know", "knows", "understand", "understands",
        "thing", "things", "good", "bad", "best", "better", "worst", "way", "ways",
        "use", "uses", "used", "using", "make", "makes", "made", "get", "gets", "got",
        "let", "lets", "say", "says", "said", "see", "sees", "saw", "look", "looks",
        "looked", "find", "finds", "found", "keep", "keeps", "put", "puts", "set",
        "sets", "try", "tries", "asked", "question", "questions", "answer", "answers",
        "help", "helps", "stock", "stocks", "share", "shares", "dividend", "dividends",
        "fee", "fees", "charge", "charges", "custody", "perform", "performs",
        "performance", "trend", "trends", "change", "changes", "changed", "moved",
        "move", "moves", "movement", "movements", "live", "halted", "stale", "delayed",
        "session", "history", "histories", "compare", "comparison", "difference",
        "differences", "review", "reviews", "rating", "ratings", "lesson", "lessons",
        "learn", "learning", "study", "studies", "account", "accounts", "balance",
        "balances", "position", "positions", "holding", "holdings", "portfolio",
        "token", "tokens", "security", "securities", "asset", "assets", "instrument",
        "instruments", "company", "companies", "corporation", "corporations", "inc",
        "corp", "ltd", "llc", "right", "wrong", "new", "old", "first", "second",
        "third", "long", "short", "big", "small", "much", "many", "lot", "lots",
        "kind", "sort", "type", "types", "part", "parts", "side", "sides", "top",
        "bottom", "front", "back", "end", "ends", "start", "starts", "point", "points",
        "line", "lines", "case", "cases", "fact", "facts", "example", "examples",
        "http", "https", "www", "com", "org", "net",
    };

    [GeneratedRegex("[A-Za-z0-9][A-Za-z0-9\\-]*")]
    private static partial Regex KeywordRegex();

    [GeneratedRegex(@"\b(place|placing|execute|executing|submit|submitting|send|sending)\b[^.?!]{0,100}\b(order|trade|transaction|purchase)\b|\b(buy|sell|purchase)\s+(this|that|\d)[^.?!]{0,60}\b(for me|on my account|in my (practice|real) portfolio)\b|\b(order|trade)\s+(to\s+)?(buy|sell|purchase|place)\b|\b(cancel|cancelling|close|closing|delete|deleting|withdraw|withdrawing|transfer|transferring|redeem|redeeming|move|moving)\b[^.?!]{0,60}\b(order|orders|trade|trades|position|positions|shares?|tokens?|funds?|money|cash|balance)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ActionRequestRegex();

    [GeneratedRegex(@"\b(system prompt|instructions?|prompt|rules?|ignore (all |your |the |previous )?(instructions?|rules?|prompts?)|reveal|print|repeat|disclose|leak|jailbreak|developer mode|do anything now)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DisclosureRequestRegex();

    [GeneratedRegex(@"\b(portfolio|holdings?|positions?|balances?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PortfolioRegex();

    [GeneratedRegex(@"\breal\b", RegexOptions.IgnoreCase)]
    private static partial Regex RealBookRegex();

    [GeneratedRegex(@"\b(lesson|lessons|learn|learning|study|studies|custodian|tokenized)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LessonRegex();

    [GeneratedRegex(@"\b(history|histories|performance|perform|past|trend|trends|week|weeks|month|months|year|years|change|changes|changed|moved|movement|movements)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HistoryRegex();

    [GeneratedRegex(@"\b(describe|description|detail|details|represent|represents|meaning|overview|profile|background|custodian|backing|redemption|dividend|dividends|risk|risks|voting|transfer|fee|fees)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DetailsRegex();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex MarkdownImageStrip();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLinkStrip();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex HtmlTagStrip();

    [GeneratedRegex(@"(?:https?://|www\.)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlStrip();

    private static LanguageModelResponse FinalAnswer(LanguageModelMessage lastTool)
    {
        var toolText = lastTool.Text;
        return lastTool.ToolName switch
        {
            "get_instrument" => InstrumentAnswer(toolText),
            "get_price_history_summary" => HistoryAnswer(toolText),
            _ => PriceAnswer(toolText),
        };
    }

    private static LanguageModelResponse PriceAnswer(string toolText)
    {
        var price = PriceRegex().Match(toolText);
        var symbol = SymbolRegex().Match(toolText);
        var asOf = AsOfRegex().Match(toolText);
        var label = LabelRegex().Match(toolText);
        var name = symbol.Success ? symbol.Groups[1].Value : "the instrument";
        var asOfText = asOf.Success ? $" as of {asOf.Groups[1].Value}" : string.Empty;
        var labelText = label.Success ? label.Groups[1].Value : "unavailable";
        var text = price.Success
            ? $"{name} is priced at {price.Groups[1].Value} {price.Groups[2].Value}{asOfText} (label: {labelText}). This is a research summary, not advice. [r1][r2]"
            : $"{name} has no usable price right now (label: {labelText}). [r1][r2]";
        return Respond(text);
    }

    private static LanguageModelResponse InstrumentAnswer(string toolText)
    {
        var symbol = SymbolRegex().Match(toolText);
        var name = symbol.Success ? symbol.Groups[1].Value : "the instrument";
        return Respond($"Here are the catalog details for {name}. This is a research summary, not advice. [r1][r2]");
    }

    private static LanguageModelResponse HistoryAnswer(string toolText)
    {
        var symbol = SymbolRegex().Match(toolText);
        var change = ChangePercentRegex().Match(toolText);
        var name = symbol.Success ? symbol.Groups[1].Value : "the instrument";
        var text = change.Success
            ? $"{name} moved {change.Groups[1].Value}% over the sampled week. This is a research summary, not advice. [r1][r2]"
            : $"{name} has no price history for that range. [r1][r2]";
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
            : Respond("The price is 999.99 USD and the weekly change is 42%. [r1][r2]");
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

    [GeneratedRegex("\"(?:instrumentId|id)\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex InstrumentIdRegex();

    [GeneratedRegex("\"price\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"currency\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex PriceRegex();

    [GeneratedRegex("\"symbol\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex SymbolRegex();

    [GeneratedRegex("\"asOf\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex AsOfRegex();

    [GeneratedRegex("\"label\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex LabelRegex();

    [GeneratedRegex("\"changePercent\"\\s*:\\s*\"(-?[\\d.]+)\"")]
    private static partial Regex ChangePercentRegex();
}
