using System.Text.Json;
using System.Text.Json.Nodes;
using AskVest.Modules.Assistant.Contracts;
using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Learning.Contracts;

namespace AskVest.Modules.Assistant.Tools;

/// <summary>Substring search over reviewed lesson content.</summary>
internal sealed class SearchLessonsTool(ILearningRequirements learning) : AssistantTool
{
    private const int DefaultLimit = 5;
    private const int MaxLimit = 10;

    public override string Name => "search_lessons";

    public override string Description
        => "Search reviewed learning lessons by keyword. Returns lesson ids, titles, summaries and short excerpts.";

    public override string ParametersSchemaJson => """
        {"type":"object","properties":{"query":{"type":"string","description":"Keyword or phrase."},"limit":{"type":"integer","minimum":1,"maximum":10}},"required":["query"],"additionalProperties":false}
        """;

    public override async Task<ToolResult> ExecuteAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var queryError = ToolArguments.RequireString(arguments, "query", 100);
        if (queryError is not null)
        {
            return ToolResult.Fail("invalid_arguments", queryError);
        }

        if (!ToolArguments.TryGetOptionalInt(arguments, "limit", 1, MaxLimit, out var limit))
        {
            return ToolResult.Fail("invalid_arguments", $"'limit' must be an integer between 1 and {MaxLimit}.");
        }

        var query = arguments.GetProperty("query").GetString()!;
        var hits = await learning.SearchLessonsAsync(
            query, limit == 0 ? DefaultLimit : limit, cancellationToken);
        var asOf = context.TimeProvider.GetUtcNow();

        var payload = new
        {
            query,
            count = hits.Count,
            lessons = hits.Select(hit => new
            {
                lessonId = hit.LessonId,
                title = hit.Title,
                summary = hit.Summary,
                excerpt = hit.Excerpt,
            }),
        };

        return ToolResult.Ok(
            ToolJson.Serialize(payload),
            hits.Select(hit => new ToolSource("lesson", hit.LessonId, hit.Title, asOf)).ToList());
    }
}

/// <summary>
/// Validates and describes a client action (open a screen or prefill an
/// order form). It has no side effects and never creates orders.
/// </summary>
internal sealed class ProposeActionTool(ICatalog catalog, ILearningRequirements learning) : AssistantTool
{
    private static readonly HashSet<string> ActionTypes = new(StringComparer.Ordinal)
    {
        "open_instrument",
        "open_lesson",
        "prefill_order",
    };

    public override string Name => "propose_action";

    public override string Description
        => "Propose a client action: open an instrument page, open a lesson, or prefill an order form. It only describes what to show; it never places anything.";

    public override string ParametersSchemaJson => """
        {"type":"object","properties":{"action":{"type":"string","enum":["open_instrument","open_lesson","prefill_order"]},"instrumentId":{"type":"string"},"lessonId":{"type":"string"},"side":{"type":"string","enum":["buy","sell"]},"amount":{"type":"string","description":"Decimal amount as a string."}},"required":["action"],"additionalProperties":false}
        """;

    public override async Task<ToolResult> ExecuteAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var actionError = ToolArguments.OptionalEnum(arguments, "action", [.. ActionTypes]);
        if (actionError is not null || !arguments.TryGetProperty("action", out var actionElement)
            || actionElement.ValueKind != JsonValueKind.String)
        {
            return ToolResult.Fail("invalid_arguments", "'action' is required and must be open_instrument, open_lesson or prefill_order.");
        }

        var action = actionElement.GetString()!;
        var hasInstrument = arguments.TryGetProperty("instrumentId", out var instrumentElement)
            && instrumentElement.ValueKind == JsonValueKind.String
            && instrumentElement.GetString()!.Length > 0;
        var instrumentId = hasInstrument ? instrumentElement.GetString()! : null;
        var hasLesson = arguments.TryGetProperty("lessonId", out var lessonElement)
            && lessonElement.ValueKind == JsonValueKind.String
            && lessonElement.GetString()!.Length > 0;
        var lessonId = hasLesson ? lessonElement.GetString()! : null;
        var side = arguments.TryGetProperty("side", out var sideElement)
            && sideElement.ValueKind == JsonValueKind.String ? sideElement.GetString() : null;

        if (instrumentId is not null && instrumentId.Length > 128)
        {
            return ToolResult.Fail("invalid_arguments", "'instrumentId' must be at most 128 characters.");
        }

        if (lessonId is not null && lessonId.Length > 128)
        {
            return ToolResult.Fail("invalid_arguments", "'lessonId' must be at most 128 characters.");
        }

        string[] allowed = action switch
        {
            "open_instrument" => ["action", "instrumentId"],
            "open_lesson" => ["action", "lessonId"],
            _ => ["action", "instrumentId", "side", "amount"],
        };
        var stray = arguments.EnumerateObject()
            .Select(property => property.Name)
            .FirstOrDefault(name => !allowed.Contains(name, StringComparer.Ordinal));
        if (stray is not null)
        {
            return ToolResult.Fail("invalid_arguments", $"'{stray}' is not valid for action '{action}'.");
        }

        if (action is "open_instrument" or "prefill_order")
        {
            if (instrumentId is null)
            {
                return ToolResult.Fail("invalid_arguments", $"'{action}' requires 'instrumentId'.");
            }

            var instrument = await catalog.FindInstrumentAsync(instrumentId, cancellationToken);
            if (instrument is null)
            {
                return ToolResult.Fail("not_found", $"No instrument with id '{instrumentId}'.");
            }

            if (!string.Equals(instrument.Status, "active", StringComparison.Ordinal))
            {
                return ToolResult.Fail("not_active", $"Instrument '{instrumentId}' is not active.");
            }
        }

        if (action == "open_lesson")
        {
            if (lessonId is null)
            {
                return ToolResult.Fail("invalid_arguments", "'open_lesson' requires 'lessonId'.");
            }

            var hits = await learning.SearchLessonsAsync(lessonId, 10, cancellationToken);
            if (hits.All(hit => !string.Equals(hit.LessonId, lessonId, StringComparison.Ordinal)))
            {
                return ToolResult.Fail("not_found", $"No lesson with id '{lessonId}'.");
            }
        }

        if (action == "prefill_order")
        {
            if (side is null || (side != "buy" && side != "sell"))
            {
                return ToolResult.Fail("invalid_arguments", "'prefill_order' requires 'side' to be 'buy' or 'sell'.");
            }

            if (!ToolArguments.TryGetOptionalDecimalString(arguments, "amount", out _))
            {
                return ToolResult.Fail("invalid_arguments", "'amount' must be a positive decimal string when present.");
            }
        }

        var descriptor = new JsonObject { ["type"] = action };
        if (instrumentId is not null)
        {
            descriptor["instrumentId"] = instrumentId;
        }

        if (lessonId is not null)
        {
            descriptor["lessonId"] = lessonId;
        }

        if (side is not null)
        {
            descriptor["side"] = side;
        }

        if (arguments.TryGetProperty("amount", out var amountElement)
            && amountElement.ValueKind == JsonValueKind.String)
        {
            descriptor["amount"] = amountElement.GetString();
        }

        var actionJson = descriptor.ToJsonString();
        var asOf = context.TimeProvider.GetUtcNow();
        var sources = new List<ToolSource>();
        if (instrumentId is not null)
        {
            var instrument = await catalog.FindInstrumentAsync(instrumentId, cancellationToken);
            if (instrument is not null)
            {
                sources.Add(new ToolSource("instrument", instrument.Id, instrument.Name, asOf));
            }
        }
        else if (lessonId is not null)
        {
            var hits = await learning.SearchLessonsAsync(lessonId, 10, cancellationToken);
            var hit = hits.FirstOrDefault(h => string.Equals(h.LessonId, lessonId, StringComparison.Ordinal));
            if (hit is not null)
            {
                sources.Add(new ToolSource("lesson", hit.LessonId, hit.Title, asOf));
            }
        }

        var payload = new { action = descriptor, note = "This describes a screen for the user; nothing was placed or changed." };
        return ToolResult.Ok(
            ToolJson.Serialize(payload),
            sources,
            new AssistantAction(action, actionJson));
    }
}
