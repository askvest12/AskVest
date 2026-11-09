using System.Text.Json;
using AskVest.Modules.Assistant.Tools;
using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Learning.Contracts;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Portfolio.Contracts;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Assistant;

public sealed class AssistantUserToolTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();

    private static ToolContext Context() => new(UserId, TimeProvider.System);

    private static JsonElement Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static PortfolioResponse PracticePortfolio()
        => new(
            Book.Practice,
            new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            "USD",
            false,
            [new PortfolioCashLine("USD", "fiat", 1000m, 0m, true)],
            [],
            new PortfolioTotals(1000m, 0m, 0m, 0m, 0m, 0m, 1000m));

    [Fact]
    public async Task GetPortfolio_Practice_Returns_Summary()
    {
        var portfolio = new StubPortfolio { Portfolio = PracticePortfolio() };
        var tool = new GetPortfolioTool(portfolio, new StubComplianceEligibility());

        var result = await tool.ExecuteAsync(
            Args("""{"book":"practice"}"""), Context(), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        using var payload = JsonDocument.Parse(result.PayloadJson);
        payload.RootElement.GetProperty("book").GetString().ShouldBe("practice");
        payload.RootElement.GetProperty("totals").GetProperty("totalValue").GetString().ShouldBe("1000");
        result.Sources.ShouldContain(source => source.Type == "portfolio");
    }

    [Fact]
    public async Task GetPortfolio_Real_Is_Data_Not_Eligible_When_Not_Real_Eligible()
    {
        var portfolio = new StubPortfolio { Portfolio = PracticePortfolio() };
        var eligibility = new StubComplianceEligibility { RealAllowed = false };
        var tool = new GetPortfolioTool(portfolio, eligibility);

        var result = await tool.ExecuteAsync(
            Args("""{"book":"real"}"""), Context(), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        using var payload = JsonDocument.Parse(result.PayloadJson);
        payload.RootElement.GetProperty("available").GetBoolean().ShouldBeFalse();
        payload.RootElement.GetProperty("reason").GetString().ShouldBe("not_eligible");
        payload.RootElement.TryGetProperty("holdings", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task GetPortfolio_Real_Returns_Data_When_Eligible()
    {
        var portfolio = new StubPortfolio { Portfolio = PracticePortfolio() with { Book = Book.Real } };
        var eligibility = new StubComplianceEligibility { RealAllowed = true };
        var tool = new GetPortfolioTool(portfolio, eligibility);

        var result = await tool.ExecuteAsync(
            Args("""{"book":"real"}"""), Context(), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        using var payload = JsonDocument.Parse(result.PayloadJson);
        payload.RootElement.GetProperty("book").GetString().ShouldBe("real");
    }

    [Fact]
    public async Task GetPortfolio_Rejects_Missing_Or_Bad_Book()
    {
        var tool = new GetPortfolioTool(new StubPortfolio(), new StubComplianceEligibility());

        var missing = await tool.ExecuteAsync(
            Args("""{}"""), Context(), TestContext.Current.CancellationToken);
        missing.IsError.ShouldBeTrue();

        var bad = await tool.ExecuteAsync(
            Args("""{"book":"crypto"}"""), Context(), TestContext.Current.CancellationToken);
        bad.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task GetWatchlist_Returns_Entries_With_Labels()
    {
        var portfolio = new StubPortfolio();
        portfolio.Watchlist.Add(new WatchlistEntry(
            "inst-aapl", "AAPL", "Apple sample", "active", 123.45m, "USD", "live", "regular",
            true, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        var tool = new GetWatchlistTool(portfolio);

        var result = await tool.ExecuteAsync(
            Args("""{}"""), Context(), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        using var payload = JsonDocument.Parse(result.PayloadJson);
        var item = payload.RootElement.GetProperty("items")[0];
        item.GetProperty("label").GetString().ShouldBe("live");
        result.Sources.ShouldContain(source => source.Type == "watchlist_item");
    }

    [Fact]
    public async Task SearchLessons_Returns_Hits()
    {
        var learning = new StubLearningRequirements();
        learning.Hits.Add(new LessonSearchHit(
            "lesson-diversification", "Diversification", "Spread risk across assets.", "excerpt"));
        var tool = new SearchLessonsTool(learning);

        var result = await tool.ExecuteAsync(
            Args("""{"query":"divers"}"""), Context(), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        using var payload = JsonDocument.Parse(result.PayloadJson);
        payload.RootElement.GetProperty("lessons")[0].GetProperty("lessonId").GetString()
            .ShouldBe("lesson-diversification");
    }

    [Fact]
    public async Task ProposeAction_OpenInstrument_Validates_Target()
    {
        var catalog = new StubCatalog();
        catalog.Instruments.Add(new CatalogInstrument(
            "inst-aapl", "stock", "Apple sample", "AAPL", null, null, "US", "USD", null, "active", null));
        catalog.Instruments.Add(new CatalogInstrument(
            "inst-old", "stock", "Old sample", "OLD", null, null, "US", "USD", null, "delisted", null));
        var tool = new ProposeActionTool(catalog, new StubLearningRequirements());

        var ok = await tool.ExecuteAsync(
            Args("""{"action":"open_instrument","instrumentId":"inst-aapl"}"""),
            Context(), TestContext.Current.CancellationToken);
        ok.IsError.ShouldBeFalse();
        ok.Action.ShouldNotBeNull();
        ok.Action!.Type.ShouldBe("open_instrument");

        var inactive = await tool.ExecuteAsync(
            Args("""{"action":"open_instrument","instrumentId":"inst-old"}"""),
            Context(), TestContext.Current.CancellationToken);
        inactive.ErrorCode.ShouldBe("not_active");

        var missing = await tool.ExecuteAsync(
            Args("""{"action":"open_instrument","instrumentId":"inst-nope"}"""),
            Context(), TestContext.Current.CancellationToken);
        missing.ErrorCode.ShouldBe("not_found");
    }

    [Fact]
    public async Task ProposeAction_PrefillOrder_Requires_Side_And_Valid_Amount()
    {
        var catalog = new StubCatalog();
        catalog.Instruments.Add(new CatalogInstrument(
            "inst-aapl", "stock", "Apple sample", "AAPL", null, null, "US", "USD", null, "active", null));
        var tool = new ProposeActionTool(catalog, new StubLearningRequirements());

        var noSide = await tool.ExecuteAsync(
            Args("""{"action":"prefill_order","instrumentId":"inst-aapl"}"""),
            Context(), TestContext.Current.CancellationToken);
        noSide.ErrorCode.ShouldBe("invalid_arguments");

        var badAmount = await tool.ExecuteAsync(
            Args("""{"action":"prefill_order","instrumentId":"inst-aapl","side":"buy","amount":"abc"}"""),
            Context(), TestContext.Current.CancellationToken);
        badAmount.ErrorCode.ShouldBe("invalid_arguments");

        var ok = await tool.ExecuteAsync(
            Args("""{"action":"prefill_order","instrumentId":"inst-aapl","side":"buy","amount":"10.5"}"""),
            Context(), TestContext.Current.CancellationToken);
        ok.IsError.ShouldBeFalse();
        ok.Action!.Type.ShouldBe("prefill_order");
        using var payload = JsonDocument.Parse(ok.Action.PayloadJson);
        payload.RootElement.GetProperty("type").GetString().ShouldBe("prefill_order");
        payload.RootElement.GetProperty("amount").GetString().ShouldBe("10.5");
    }

    [Fact]
    public async Task ProposeAction_Rejects_Stray_And_Unknown_Actions()
    {
        var catalog = new StubCatalog();
        catalog.Instruments.Add(new CatalogInstrument(
            "inst-aapl", "stock", "Apple sample", "AAPL", null, null, "US", "USD", null, "active", null));
        var tool = new ProposeActionTool(catalog, new StubLearningRequirements());

        var stray = await tool.ExecuteAsync(
            Args("""{"action":"open_instrument","instrumentId":"inst-aapl","side":"buy"}"""),
            Context(), TestContext.Current.CancellationToken);
        stray.ErrorCode.ShouldBe("invalid_arguments");

        var unknown = await tool.ExecuteAsync(
            Args("""{"action":"place_order","instrumentId":"inst-aapl"}"""),
            Context(), TestContext.Current.CancellationToken);
        unknown.ErrorCode.ShouldBe("invalid_arguments");
    }

    [Fact]
    public async Task ProposeAction_OpenLesson_Requires_Known_Lesson()
    {
        var learning = new StubLearningRequirements();
        learning.Hits.Add(new LessonSearchHit("lesson-one", "Lesson One", "Summary", "excerpt"));
        var tool = new ProposeActionTool(new StubCatalog(), learning);

        var missing = await tool.ExecuteAsync(
            Args("""{"action":"open_lesson","lessonId":"lesson-two"}"""),
            Context(), TestContext.Current.CancellationToken);
        missing.ErrorCode.ShouldBe("not_found");

        var ok = await tool.ExecuteAsync(
            Args("""{"action":"open_lesson","lessonId":"lesson-one"}"""),
            Context(), TestContext.Current.CancellationToken);
        ok.IsError.ShouldBeFalse();
        ok.Action!.Type.ShouldBe("open_lesson");
    }
}
