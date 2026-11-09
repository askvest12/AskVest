using AskVest.IntegrationTests.Api;
using AskVest.IntegrationTests.Compliance;
using AskVest.Modules.Assistant.Tools;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Assistant;

[Collection(ApiCollectionDefinition.Name)]
public sealed class AssistantStartupTests
{
    [Fact]
    public void Invalid_Assistant_Configuration_Prevents_Startup()
    {
        using var factory = new ComplianceConfigFactory(new Dictionary<string, string?>
        {
            ["Assistant:FakeScenario"] = "not_a_scenario",
        });
        Should.Throw<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void Missing_Prompt_File_Prevents_Startup()
    {
        using var factory = new ComplianceConfigFactory(new Dictionary<string, string?>
        {
            ["Assistant:PromptPath"] = "Content/does-not-exist.md",
        });
        Should.Throw<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void Valid_Assistant_Configuration_Starts_And_Loads_Content()
    {
        using var factory = new ComplianceConfigFactory(new Dictionary<string, string?>());
        var client = factory.CreateClient();
        client.ShouldNotBeNull();
    }

    [Fact]
    public async Task Tool_Set_Resolves_With_All_Nine_Read_Only_Tools()
    {
        using var factory = new ComplianceConfigFactory(new Dictionary<string, string?>());
        await using var scope = factory.Services.CreateAsyncScope();
        var toolSet = scope.ServiceProvider.GetRequiredService<AssistantToolSet>();
        toolSet.Definitions.Select(definition => definition.Name).ShouldBe(
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
    }
}
