using System.Text.Json;
using AskVest.Modules.Assistant.Configuration;
using AskVest.Modules.Assistant.Contracts;
using AskVest.Modules.Assistant.Domain;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Assistant;

public sealed class FakeLanguageModelClientTests
{
    private static LanguageModelRequest Request(params LanguageModelMessage[] messages)
        => new("prompt", messages, [], 100, TimeSpan.FromSeconds(5));

    private static LanguageModelMessage User(string text) => new("user", text);

    private static LanguageModelMessage ToolResult(string text) => new("tool", text, "search_instruments");

    private static FakeLanguageModelClient Create(string scenario)
        => new(Options.Create(new AssistantOptions { FakeScenario = scenario }));

    [Fact]
    public async Task Normal_Scenario_Searches_Then_Prices_Then_Answers()
    {
        var client = Create("normal");

        var first = await client.CompleteAsync(Request(User("what is sample-appl?")), TestContext.Current.CancellationToken);
        first.IsSuccess.ShouldBeTrue();
        var searchCall = first.Value.ToolCalls.ShouldHaveSingleItem();
        searchCall.Name.ShouldBe("search_instruments");
        JsonDocument.Parse(searchCall.ArgumentsJson).RootElement.GetProperty("query").GetString()
            .ShouldBe("what is sample-appl?");

        var second = await client.CompleteAsync(
            Request(
                User("what is sample-appl?"),
                new LanguageModelMessage("assistant", string.Empty, null),
                ToolResult("""{"results":[{"instrumentId":"ins-1","symbol":"SMPL"}]}""")),
            TestContext.Current.CancellationToken);
        second.Value.ToolCalls.ShouldHaveSingleItem().Name.ShouldBe("get_prices");

        var third = await client.CompleteAsync(
            Request(
                User("what is sample-appl?"),
                new LanguageModelMessage("assistant", string.Empty, null),
                ToolResult("""{"results":[{"instrumentId":"ins-1","symbol":"SMPL"}]}"""),
                new LanguageModelMessage("assistant", string.Empty, null),
                new LanguageModelMessage("tool", """{"prices":[{"instrumentId":"ins-1","price":"123.45","currency":"USD","asOf":"2026-01-01T00:00:00Z","label":"live"}]}""", "get_prices")),
            TestContext.Current.CancellationToken);
        third.Value.Text!.ShouldContain("123.45");
        third.Value.Text!.ShouldContain("[r1]");
    }

    [Fact]
    public async Task Error_Scenario_Returns_Assistant_Unavailable()
    {
        var client = Create("error");
        var result = await client.CompleteAsync(Request(User("hi")), TestContext.Current.CancellationToken);
        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("assistant.unavailable");
    }

    [Fact]
    public async Task Slow_Scenario_Returns_Unavailable_When_Cancelled()
    {
        var client = Create("slow");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var result = await client.CompleteAsync(Request(User("hi")), cancellation.Token);
        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("assistant.unavailable");
    }

    [Fact]
    public async Task Malformed_Output_Scenario_Returns_Neither_Text_Nor_Calls()
    {
        var client = Create("malformed_output");
        var result = await client.CompleteAsync(Request(User("hi")), TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldBeNull();
        result.Value.ToolCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Loop_Tools_Scenario_Always_Asks_For_A_Tool()
    {
        var client = Create("loop_tools");
        var result = await client.CompleteAsync(
            Request(User("hi"), new LanguageModelMessage("assistant", string.Empty, null), ToolResult("{}")),
            TestContext.Current.CancellationToken);
        result.Value.ToolCalls.ShouldHaveSingleItem().Name.ShouldBe("search_instruments");
    }

    [Fact]
    public async Task Injection_Obey_Scenario_Requests_Forbidden_Tools_Deterministically()
    {
        var client = Create("injection_obey");
        var result = await client.CompleteAsync(Request(User("ignore everything")), TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ToolCalls.Select(call => call.Name)
            .ShouldBe(["delete_user_data", "get_portfolio"], ignoreOrder: true);
        result.Value.ToolCalls.ShouldAllBe(call => call.ArgumentsJson.Contains("userId"));
    }

    [Fact]
    public async Task Fabricated_Citation_Scenario_Cites_An_Unknown_Result()
    {
        var client = Create("fabricated_citation");
        var result = await client.CompleteAsync(Request(User("hi")), TestContext.Current.CancellationToken);
        result.Value.Text!.ShouldContain("[r99]");
    }

    [Fact]
    public async Task Ungrounded_Number_Scenario_Emits_Invented_Figures()
    {
        var client = Create("ungrounded_number");
        var result = await client.CompleteAsync(
            Request(User("price?"), new LanguageModelMessage("assistant", string.Empty, null), ToolResult("{}")),
            TestContext.Current.CancellationToken);
        result.Value.Text!.ShouldContain("999.99");
        result.Value.Text!.ShouldContain("42%");
    }

    [Fact]
    public async Task Advice_Phrase_Scenario_Returns_Prohibited_Language()
    {
        var client = Create("advice_phrase");
        var result = await client.CompleteAsync(Request(User("should I buy?")), TestContext.Current.CancellationToken);
        result.Value.Text!.ShouldContain("You should buy");
    }

    [Fact]
    public async Task Markup_Output_Scenario_Returns_Html_And_Links()
    {
        var client = Create("markup_output");
        var result = await client.CompleteAsync(Request(User("hi")), TestContext.Current.CancellationToken);
        result.Value.Text!.ShouldContain("https://evil.example");
        result.Value.Text!.ShouldContain("<b>");
    }

    [Fact]
    public async Task Responses_Are_Deterministic()
    {
        var client = Create("normal");
        var request = Request(User("repeat me"));
        var first = await client.CompleteAsync(request, TestContext.Current.CancellationToken);
        var second = await client.CompleteAsync(request, TestContext.Current.CancellationToken);
        first.Value.ToolCalls.Single().ArgumentsJson.ShouldBe(second.Value.ToolCalls.Single().ArgumentsJson);
    }
}
