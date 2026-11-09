using System.Text.Json;
using AskVest.Modules.Assistant.Configuration;
using AskVest.Modules.Assistant.Contracts;
using AskVest.Modules.Assistant.Tools;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Assistant;

public sealed class AssistantToolSetTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();

    private static ToolContext Context() => new(UserId, TimeProvider.System);

    private static LanguageModelToolCall Call(string name, string argumentsJson)
        => new("call-1", name, argumentsJson);

    private static AssistantToolSet ToolSet(params AssistantTool[] tools)
        => new(tools, Options.Create(new AssistantOptions()));

    [Fact]
    public void Definitions_Are_The_Nine_Read_Only_Tools()
    {
        var catalog = new StubCatalog();
        var marketData = new StubMarketData();
        var portfolio = new StubPortfolio();
        var learning = new StubLearningRequirements();
        var eligibility = new StubComplianceEligibility();
        AssistantTool[] tools =
        [
            new SearchInstrumentsTool(catalog),
            new GetInstrumentTool(catalog),
            new GetTokenDisclosureTool(catalog),
            new GetPricesTool(marketData, catalog),
            new GetPriceHistorySummaryTool(marketData, catalog),
            new GetPortfolioTool(portfolio, eligibility),
            new GetWatchlistTool(portfolio),
            new SearchLessonsTool(learning),
            new ProposeActionTool(catalog, learning),
        ];

        var set = ToolSet(tools);
        set.Definitions.Select(definition => definition.Name).ShouldBe(
        [
            "search_instruments",
            "get_instrument",
            "get_token_disclosure",
            "get_prices",
            "get_price_history_summary",
            "get_portfolio",
            "get_watchlist",
            "search_lessons",
            "propose_action",
        ]);

        foreach (var definition in set.Definitions)
        {
            using var schema = JsonDocument.Parse(definition.ParametersSchemaJson);
            schema.RootElement.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Execute_Rejects_Unknown_Tool()
    {
        var result = await ToolSet(new EchoTool()).ExecuteAsync(
            Call("delete_everything", "{}"), Context(), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        result.ErrorCode.ShouldBe("unknown_tool");
        result.ResultId.ShouldBeNull();
        result.ToolName.ShouldBe("delete_everything");
    }

    [Fact]
    public async Task Execute_Rejects_Malformed_Json_And_Non_Object_Arguments()
    {
        var set = ToolSet(new EchoTool());
        var malformed = await set.ExecuteAsync(
            Call("echo", "{not json"), Context(), TestContext.Current.CancellationToken);
        malformed.IsError.ShouldBeTrue();
        malformed.ErrorCode.ShouldBe("invalid_arguments");

        var array = await set.ExecuteAsync(
            Call("echo", "[1,2]"), Context(), TestContext.Current.CancellationToken);
        array.IsError.ShouldBeTrue();
        array.ErrorCode.ShouldBe("invalid_arguments");
    }

    [Fact]
    public async Task Execute_Rejects_UserId_Properties_At_Any_Depth()
    {
        var set = ToolSet(new EchoTool("userId", "nested"));

        var topLevel = await set.ExecuteAsync(
            Call("echo", """{"userId":"abc"}"""), Context(), TestContext.Current.CancellationToken);
        topLevel.ErrorCode.ShouldBe(ToolArguments.ForbiddenUserIdProperty);

        var nested = await set.ExecuteAsync(
            Call("echo", """{"nested":{"user_id":"abc"}}"""), Context(), TestContext.Current.CancellationToken);
        nested.ErrorCode.ShouldBe(ToolArguments.ForbiddenUserIdProperty);
    }

    [Fact]
    public async Task Execute_Rejects_Unknown_Properties()
    {
        var set = ToolSet(new EchoTool("known"));
        var result = await set.ExecuteAsync(
            Call("echo", """{"known":1,"surprise":2}"""), Context(), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        result.ErrorCode.ShouldBe("invalid_arguments");
        result.PayloadJson.ShouldContain("surprise");
    }

    [Fact]
    public async Task Execute_Assigns_ResultId_And_AsOf_To_Successes_Only()
    {
        var set = ToolSet(new EchoTool());
        var context = Context();

        var success = await set.ExecuteAsync(
            Call("echo", "{}"), context, TestContext.Current.CancellationToken);
        success.IsError.ShouldBeFalse();
        success.ResultId.ShouldBe("r1");
        success.AsOf.ShouldBeGreaterThan(default(DateTimeOffset));

        var failure = await set.ExecuteAsync(
            Call("missing", "{}"), context, TestContext.Current.CancellationToken);
        failure.IsError.ShouldBeTrue();
        failure.ResultId.ShouldBeNull();
    }

    [Fact]
    public async Task Execute_Times_Out_Slow_Tools()
    {
        var options = Options.Create(new AssistantOptions { ToolTimeoutSeconds = 1 });
        var set = new AssistantToolSet([new SlowTool()], options);

        var result = await set.ExecuteAsync(
            Call("slow", "{}"), Context(), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        result.ErrorCode.ShouldBe("tool_timeout");
    }

    private sealed class EchoTool(params string[] allowedProperties) : AssistantTool
    {
        public override string Name => "echo";

        public override string Description => "Test echo tool.";

        public override string ParametersSchemaJson
            => $$"""{"type":"object","properties":{ {{string.Join(",", allowedProperties.Select(p => $"\"{p}\":{{\"type\":\"string\"}}"))}} },"additionalProperties":false}""";

        public override Task<ToolResult> ExecuteAsync(
            JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
            => Task.FromResult(ToolResult.Ok("{}", []));
    }

    private sealed class SlowTool : AssistantTool
    {
        public override string Name => "slow";

        public override string Description => "Never finishes in time.";

        public override string ParametersSchemaJson => """{"type":"object","properties":{},"additionalProperties":false}""";

        public override async Task<ToolResult> ExecuteAsync(
            JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return ToolResult.Ok("{}", []);
        }
    }
}
