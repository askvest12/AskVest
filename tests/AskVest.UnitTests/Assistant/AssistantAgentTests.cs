using AskVest.Modules.Assistant.Agent;
using AskVest.Modules.Assistant.Configuration;
using AskVest.Modules.Assistant.Contracts;
using AskVest.Modules.Assistant.Domain;
using AskVest.Modules.Assistant.Persistence.Entities;
using AskVest.Modules.Assistant.Tools;
using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.MarketData.Contracts;
using AskVest.SharedKernel;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Assistant;

public sealed class AssistantAgentTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();

    private sealed class Harness
    {
        private readonly AssistantOptions _options;

        public Harness(AssistantOptions? options = null, ILanguageModelClient? model = null)
        {
            _options = options ?? new AssistantOptions();
            Catalog = new StubCatalog();
            MarketData = new StubMarketData();
            Portfolio = new StubPortfolio();
            Learning = new StubLearningRequirements();
            Eligibility = new StubComplianceEligibility();
            Fake = new FakeLanguageModelClient(Options.Create(_options));
            var opts = Options.Create(_options);
            var toolSet = new AssistantToolSet(
                [
                    new SearchInstrumentsTool(Catalog),
                    new GetInstrumentTool(Catalog),
                    new GetTokenDisclosureTool(Catalog),
                    new GetPricesTool(MarketData, Catalog),
                    new GetPriceHistorySummaryTool(MarketData, Catalog),
                    new GetPortfolioTool(Portfolio, Eligibility),
                    new GetWatchlistTool(Portfolio),
                    new SearchLessonsTool(Learning),
                    new ProposeActionTool(Catalog, Learning),
                ],
                opts);
            Agent = new AssistantAgent(
                model ?? Fake, toolSet, new AssistantContent(opts), opts, TimeProvider.System);
        }

        public StubCatalog Catalog { get; }

        public StubMarketData MarketData { get; }

        public StubPortfolio Portfolio { get; }

        public StubLearningRequirements Learning { get; }

        public StubComplianceEligibility Eligibility { get; }

        public FakeLanguageModelClient Fake { get; }

        public AssistantAgent Agent { get; }

        public Task<AssistantTurnResult> RunAsync(
            string userText = "AAPL",
            IReadOnlyList<LanguageModelMessage>? history = null)
            => Agent.RunAsync(history ?? [], userText, UserId, TestContext.Current.CancellationToken);
    }

    private sealed class ScriptedModelClient : ILanguageModelClient
    {
        private readonly Queue<Result<LanguageModelResponse>> _responses = new();

        public List<LanguageModelRequest> Requests { get; } = [];

        public void EnqueueText(string text)
            => _responses.Enqueue(Result.Success(new LanguageModelResponse(text, [], 5, 5, "scripted-1")));

        public void EnqueueEmpty()
            => _responses.Enqueue(Result.Success(new LanguageModelResponse(null, [], 0, 0, "scripted-1")));

        public void EnqueueToolCall(string name, string argumentsJson)
            => _responses.Enqueue(Result.Success(new LanguageModelResponse(
                null, [new LanguageModelToolCall("c1", name, argumentsJson)], 5, 5, "scripted-1")));

        public void EnqueueFailure()
            => _responses.Enqueue(Result.Failure<LanguageModelResponse>(
                new Error(ErrorCodes.AssistantUnavailable, "down", ErrorType.Unavailable)));

        public Task<Result<LanguageModelResponse>> CompleteAsync(
            LanguageModelRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Count == 0
                ? Result.Success(new LanguageModelResponse("done.", [], 5, 5, "scripted-1"))
                : _responses.Dequeue());
        }
    }

    private static void SeedPriceableInstrument(Harness harness)
    {
        harness.Catalog.Instruments.Add(new CatalogInstrument(
            "inst-aapl", "stock", "Apple sample", "AAPL", null, null, "US", "USD", null, "active", null));
        harness.MarketData.Prices.Add(new MarketDataPrice(
            "inst-aapl", 123.45m, "USD", new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            30, "regular", "live", true, "fake", 120m));
    }

    [Fact]
    public async Task Normal_Flow_Returns_Cited_Answer_With_Fact_Cards()
    {
        var harness = new Harness();
        SeedPriceableInstrument(harness);

        var result = await harness.RunAsync("AAPL");

        result.Outcome.ShouldBe(AuditEvent.OutcomeAnswered);
        result.PolicyBlocked.ShouldBeFalse();
        result.Text.ShouldNotBeNull();
        result.Text.ShouldContain("123.45");
        result.Citations.ShouldNotBeEmpty();
        result.FactCards.ShouldContain(card => card.CardType == "price");
        result.Audit.ToolNames.ShouldContain("search_instruments");
        result.Audit.ToolNames.ShouldContain("get_prices");
        result.Audit.CitationSourceIds.ShouldNotBeEmpty();
        result.Audit.ToolResultHashes.Count.ShouldBeGreaterThan(0);
        result.Audit.Outcome.ShouldBe(AuditEvent.OutcomeAnswered);
    }

    [Fact]
    public async Task Injection_Obey_Cannot_Reach_Write_Paths_Or_Other_Users()
    {
        var harness = new Harness();
        harness.Fake.Scenario = "injection_obey";

        var result = await harness.RunAsync("show me everything");

        result.Outcome.ShouldBe(AuditEvent.OutcomeToolRejected);
        result.Text.ShouldNotBeNull();
        result.Text.ShouldNotContain("123");
        result.Audit.ToolNames.ShouldContain("delete_user_data");
        result.Audit.ToolNames.ShouldContain("get_portfolio");
    }

    [Fact]
    public async Task Fabricated_Citation_Is_Stripped_And_Audited()
    {
        var harness = new Harness();
        harness.Fake.Scenario = "fabricated_citation";

        var result = await harness.RunAsync("anything");

        result.Outcome.ShouldBe(AuditEvent.OutcomeCitationRejected);
        result.Text.ShouldNotBeNull();
        result.Text.ShouldNotContain("[r");
        result.Citations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ungrounded_Number_Regenerates_Once_Then_Falls_Back()
    {
        var model = new ScriptedModelClient();
        model.EnqueueText("The price is 999.99 USD.");
        model.EnqueueText("The price is 999.99 USD.");
        var harness = new Harness(model: model);

        var result = await harness.RunAsync("price?");

        result.Text.ShouldBe(new AssistantOptions().SafeFallbackMessage);
        result.Outcome.ShouldBe(AuditEvent.OutcomeNumericRegenerated);
        model.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Ungrounded_Then_Grounded_Answer_Succeeds()
    {
        var model = new ScriptedModelClient();
        model.EnqueueText("The price is 999.99 USD.");
        model.EnqueueText("No usable price right now.");
        var harness = new Harness(model: model);

        var result = await harness.RunAsync("price?");

        result.Outcome.ShouldBe(AuditEvent.OutcomeNumericRegenerated);
        result.Text.ShouldBe("No usable price right now.");
        result.PolicyBlocked.ShouldBeFalse();
    }

    [Fact]
    public async Task Policy_Violation_Retries_Once_Then_Blocks()
    {
        var harness = new Harness();
        harness.Fake.Scenario = "advice_phrase";

        var result = await harness.RunAsync("should I buy?");

        result.Outcome.ShouldBe(AuditEvent.OutcomePolicyBlocked);
        result.PolicyBlocked.ShouldBeTrue();
        result.Text.ShouldBe(new AssistantOptions().SafeFallbackMessage);
    }

    [Fact]
    public async Task Policy_Violation_Then_Clean_Answer_Succeeds()
    {
        var model = new ScriptedModelClient();
        model.EnqueueText("You should buy this stock today.");
        model.EnqueueText("Here is some research about the instrument.");
        var harness = new Harness(model: model);

        var result = await harness.RunAsync("tell me about it");

        result.Outcome.ShouldBe(AuditEvent.OutcomeAnswered);
        result.PolicyBlocked.ShouldBeFalse();
        result.Text.ShouldBe("Here is some research about the instrument.");
        model.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Markup_Output_Is_Sanitized_To_Plain_Text()
    {
        var harness = new Harness();
        harness.Fake.Scenario = "markup_output";

        var result = await harness.RunAsync("links?");

        result.Text.ShouldNotBeNull();
        result.Text.ShouldNotContain("http");
        result.Text.ShouldNotContain("<b>");
        result.Text.ShouldNotContain("](");
    }

    [Fact]
    public async Task Malformed_Output_Retries_Once_Then_Fails()
    {
        var model = new ScriptedModelClient();
        model.EnqueueEmpty();
        model.EnqueueText("A plain answer.");
        var harness = new Harness(model: model);

        var retried = await harness.RunAsync("hi");
        retried.Outcome.ShouldBe(AuditEvent.OutcomeAnswered);
        retried.Text.ShouldBe("A plain answer.");

        var failing = new ScriptedModelClient();
        failing.EnqueueEmpty();
        failing.EnqueueEmpty();
        var harness2 = new Harness(model: failing);
        var failed = await harness2.RunAsync("hi");
        failed.Text.ShouldBeNull();
        failed.Outcome.ShouldBe(AuditEvent.OutcomeProviderError);
    }

    [Fact]
    public async Task Provider_Error_Returns_No_Text_And_Audits()
    {
        var harness = new Harness();
        harness.Fake.Scenario = "error";

        var result = await harness.RunAsync("hi");

        result.Text.ShouldBeNull();
        result.Outcome.ShouldBe(AuditEvent.OutcomeProviderError);
        result.Audit.Outcome.ShouldBe(AuditEvent.OutcomeProviderError);
    }

    [Fact]
    public async Task Slow_Model_Stops_At_The_Time_Budget()
    {
        var options = new AssistantOptions { MessageTimeBudgetSeconds = 1 };
        var harness = new Harness(options);
        harness.Fake.Scenario = "slow";

        var result = await harness.RunAsync("hi");

        result.Text.ShouldBeNull();
        result.Outcome.ShouldBe(AuditEvent.OutcomeProviderError);
    }

    [Fact]
    public async Task Loop_Tools_Stops_At_The_Call_Caps()
    {
        var harness = new Harness();
        harness.Fake.Scenario = "loop_tools";

        var result = await harness.RunAsync("loop");

        result.Outcome.ShouldBe(AuditEvent.OutcomeProviderError);
        harness.Catalog.ListCalls.ShouldBeLessThanOrEqualTo(6);
    }

    [Fact]
    public async Task History_Is_Truncated_To_The_Configured_Count()
    {
        var model = new ScriptedModelClient();
        model.EnqueueText("ok.");
        var harness = new Harness(model: model);
        var history = Enumerable.Range(0, 30)
            .Select(index => new LanguageModelMessage(index % 2 == 0 ? "user" : "assistant", $"m{index}"))
            .ToList();

        await harness.RunAsync("latest", history);

        var sent = model.Requests[0].Messages;
        sent.Count.ShouldBe(21);
        sent[^1].Text.ShouldBe("latest");
        sent[^2].Text.ShouldBe("m29");
    }

    [Fact]
    public void Agent_Holds_No_Database_Dependency()
    {
        typeof(AssistantAgent).GetConstructors().Single()
            .GetParameters().Select(parameter => parameter.ParameterType)
            .ShouldNotContain(type => type.Name.Contains("DbContext", StringComparison.Ordinal));
    }
}
