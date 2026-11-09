using System.Text.Json;
using AskVest.Modules.Assistant.Tools;

namespace AskVest.Modules.Assistant.Policy;

/// <summary>
/// Builds trusted fact cards from the tool results the answer cited. Every
/// value comes from the tool payload verbatim; the model's wording is
/// never used.
/// </summary>
internal static class FactCardBuilder
{
    public static IReadOnlyList<FactCard> Build(
        IReadOnlyList<ToolResult> results,
        IReadOnlyList<AssistantCitation> citations)
    {
        var citedSources = citations
            .Select(citation => (citation.Type, citation.Id))
            .ToHashSet();
        var cards = new List<FactCard>();
        foreach (var result in results)
        {
            if (result.IsError || result.ResultId is null
                || !result.Sources.Any(source => citedSources.Contains((source.Type, source.Id))))
            {
                continue;
            }

            cards.AddRange(CardsFor(result));
        }

        return cards;
    }

    private static List<FactCard> CardsFor(ToolResult result)
    {
        using var document = JsonDocument.Parse(result.PayloadJson);
        var root = document.RootElement;
        return result.ToolName switch
        {
            "get_prices" => PriceCards(root).ToList(),
            "get_price_history_summary" => [HistoryCard(root)],
            "get_portfolio" => [PortfolioCard(root)],
            "get_watchlist" => [WatchlistCard(root)],
            "search_instruments" => [InstrumentSearchCard(root)],
            "get_instrument" => [InstrumentCard(root)],
            "get_token_disclosure" => [DisclosureCard(root)],
            "search_lessons" => [LessonSearchCard(root)],
            "propose_action" => [ActionCard(root)],
            _ => [],
        };
    }

    private static IEnumerable<FactCard> PriceCards(JsonElement root)
    {
        if (!root.TryGetProperty("prices", out var prices))
        {
            yield break;
        }

        foreach (var price in prices.EnumerateArray())
        {
            var symbol = Str(price, "symbol") ?? Str(price, "instrumentId") ?? "instrument";
            var fields = new List<FactCardField>
            {
                Field("price", Str(price, "price")),
                Field("currency", Str(price, "currency")),
                Field("asOf", Str(price, "asOf")),
                Field("label", Str(price, "label")),
                Field("session", Str(price, "session")),
                Field("usableForExecution", Bool(price, "usableForExecution")),
            };
            yield return new FactCard(
                "price", symbol, fields.Where(field => field.Value.Length > 0).ToList());
        }
    }

    private static FactCard HistoryCard(JsonElement root)
        => new("price_history", $"{Str(root, "symbol") ?? Str(root, "instrumentId") ?? "instrument"} history", Fields(root,
            "interval", "candleCount", "partialCandles", "start", "end", "high", "low", "changePercent", "stale"));

    private static FactCard PortfolioCard(JsonElement root)
    {
        var fields = new List<FactCardField>
        {
            Field("book", Str(root, "book")),
            Field("asOf", Str(root, "asOf")),
            Field("valuationCurrency", Str(root, "valuationCurrency")),
            Field("partial", Bool(root, "partial")),
        };
        if (root.TryGetProperty("totals", out var totals))
        {
            fields.AddRange(Fields(totals, "cash", "holdingsValue", "costBasis", "unrealized", "realized", "income", "totalValue"));
        }

        var book = Str(root, "book") ?? "portfolio";
        return new FactCard("portfolio", $"{book} portfolio", fields);
    }

    private static FactCard WatchlistCard(JsonElement root)
    {
        var fields = new List<FactCardField>();
        if (root.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var label = $"{Str(item, "symbol") ?? Str(item, "instrumentId")}: {Str(item, "price") ?? "no price"} {Str(item, "currency") ?? string.Empty} ({Str(item, "label") ?? "unavailable"})".Trim();
                fields.Add(new FactCardField(Str(item, "symbol") ?? "item", label));
            }
        }

        return new FactCard("watchlist", $"watchlist ({Int(root, "count") ?? fields.Count} items)", fields);
    }

    private static FactCard InstrumentSearchCard(JsonElement root)
    {
        var fields = new List<FactCardField> { Field("query", Str(root, "query")), Field("count", Int(root, "count")?.ToString()) };
        if (root.TryGetProperty("instruments", out var instruments))
        {
            foreach (var instrument in instruments.EnumerateArray())
            {
                fields.Add(new FactCardField(
                    Str(instrument, "symbol") ?? "instrument",
                    $"{Str(instrument, "name")} ({Str(instrument, "status")})"));
            }
        }

        return new FactCard("instrument_search", "instrument search", fields);
    }

    private static FactCard InstrumentCard(JsonElement root)
    {
        var instrument = root.TryGetProperty("instrument", out var element) ? element : root;
        return new FactCard("instrument", $"{Str(instrument, "symbol") ?? "instrument"}", Fields(instrument,
            "symbol", "name", "kind", "status", "isin", "quoteCurrency", "sector", "listingExchange",
            "countryOfListing", "shortDescription"));
    }

    private static FactCard DisclosureCard(JsonElement root)
    {
        var token = root.TryGetProperty("token", out var element) ? element : root;
        return new FactCard("token_disclosure", $"{Str(token, "symbol") ?? "token"} disclosure", Fields(token,
            "symbol", "claimType", "claimDescription", "backingDescription", "custodianDescription",
            "redemptionDescription", "dividendPolicy", "dividendNote", "votingRights", "votingNote",
            "transferRestrictions", "issuerFees", "noIssuerFees", "riskStatements",
            "disclosureDocumentLink", "reviewedAt", "reviewExpiresAt"));
    }

    private static FactCard LessonSearchCard(JsonElement root)
    {
        var fields = new List<FactCardField> { Field("query", Str(root, "query")), Field("count", Int(root, "count")?.ToString()) };
        if (root.TryGetProperty("lessons", out var lessons))
        {
            foreach (var lesson in lessons.EnumerateArray())
            {
                fields.Add(new FactCardField(
                    Str(lesson, "lessonId") ?? "lesson",
                    $"{Str(lesson, "title")} - {Str(lesson, "summary")}"));
            }
        }

        return new FactCard("lesson_search", "lesson search", fields);
    }

    private static FactCard ActionCard(JsonElement root)
    {
        var fields = new List<FactCardField>();
        if (root.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in action.EnumerateObject())
            {
                fields.Add(new FactCardField(property.Name, property.Value.ToString()));
            }
        }

        return new FactCard("action", "proposed action", fields);
    }

    private static List<FactCardField> Fields(JsonElement element, params string[] names)
    {
        var fields = new List<FactCardField>();
        foreach (var name in names)
        {
            var value = Str(element, name)
                ?? Bool(element, name)
                ?? List(element, name)
                ?? Int(element, name)?.ToString();
            if (value is not null)
            {
                fields.Add(new FactCardField(name, value));
            }
        }

        return fields;
    }

    private static FactCardField Field(string name, string? value)
        => new(name, value ?? string.Empty);

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean().ToString()
            : null;

    private static int? Int(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : null;

    private static string? List(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = property.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
            .Where(item => !string.IsNullOrEmpty(item))
            .ToList();
        return items.Count == 0 ? null : string.Join("; ", items);
    }
}
