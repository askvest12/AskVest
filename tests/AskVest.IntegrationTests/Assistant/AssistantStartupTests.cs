using AskVest.IntegrationTests.Api;
using AskVest.IntegrationTests.Compliance;
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
}
