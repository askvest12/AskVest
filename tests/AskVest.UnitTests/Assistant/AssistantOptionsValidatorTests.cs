using AskVest.Modules.Assistant.Configuration;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Assistant;

public sealed class AssistantOptionsValidatorTests
{
    [Fact]
    public void Defaults_Are_Valid()
    {
        var result = AssistantOptionsValidator.Instance.Validate(null, new AssistantOptions());
        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Unknown_Fake_Scenario_Is_Rejected()
    {
        var result = AssistantOptionsValidator.Instance.Validate(
            null, new AssistantOptions { FakeScenario = "does_not_exist" });
        result.Succeeded.ShouldBeFalse();
    }

    [Theory]
    [InlineData(nameof(AssistantOptions.MaxToolCallsPerMessage), 0)]
    [InlineData(nameof(AssistantOptions.MaxModelCallsPerMessage), 0)]
    [InlineData(nameof(AssistantOptions.MessageTimeBudgetSeconds), 0)]
    [InlineData(nameof(AssistantOptions.MessagesPerHour), 0)]
    [InlineData(nameof(AssistantOptions.MaxMessageCharacters), 0)]
    public void Out_Of_Range_Limits_Are_Rejected(string property, int value)
    {
        var options = new AssistantOptions();
        typeof(AssistantOptions).GetProperty(property)!.SetValue(options, value);
        var result = AssistantOptionsValidator.Instance.Validate(null, options);
        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void Empty_Disclosure_Or_Fallback_Is_Rejected()
    {
        AssistantOptionsValidator.Instance.Validate(
                null, new AssistantOptions { DisclosureLine = " " })
            .Succeeded.ShouldBeFalse();
        AssistantOptionsValidator.Instance.Validate(
                null, new AssistantOptions { SafeFallbackMessage = " " })
            .Succeeded.ShouldBeFalse();
        AssistantOptionsValidator.Instance.Validate(
                null, new AssistantOptions { PolicyVersion = " " })
            .Succeeded.ShouldBeFalse();
    }
}
