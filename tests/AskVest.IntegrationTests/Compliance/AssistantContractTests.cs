using System.Text.Json;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Compliance.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Compliance;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class AssistantContractTests(PostgresFixture fixture) : IAsyncLifetime
{
    private AuthApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = ComplianceTestUsers.CreateFactory(fixture);
        _factory.CreateClient();
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Missing_Document_Types_Respect_The_Required_For_Category()
    {
        var client = _factory.CreateClient();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        var userId = UserIdForToken(token);

        await using var scope = _factory.Services.CreateAsyncScope();
        var eligibility = scope.ServiceProvider.GetRequiredService<IComplianceEligibility>();

        var assistantMissing = await eligibility.GetMissingDocumentTypesAsync(
            userId, "assistant", TestContext.Current.CancellationToken);
        assistantMissing.ShouldBe(["ai_processing"]);

        var practiceMissing = await eligibility.GetMissingDocumentTypesAsync(
            userId, "practice", TestContext.Current.CancellationToken);
        practiceMissing.ShouldBe(["terms", "privacy"], ignoreOrder: true);

        var realMissing = await eligibility.GetMissingDocumentTypesAsync(
            userId, "real", TestContext.Current.CancellationToken);
        realMissing.ShouldBe(["risk_disclosure", "token_disclosure"], ignoreOrder: true);

        await ComplianceTestUsers.AcceptDocumentsAsync(
            client, token, ("ai_processing", "v1", new string('e', 64)));

        var after = await eligibility.GetMissingDocumentTypesAsync(
            userId, "assistant", TestContext.Current.CancellationToken);
        after.ShouldBeEmpty();
    }

    private static Guid UserIdForToken(string token)
    {
        var payload = token.Split('.')[1];
        var json = JsonDocument.Parse(Convert.FromBase64String(Pad(payload))).RootElement;
        return json.GetProperty("sub").GetGuid();
    }

    private static string Pad(string value)
        => value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
}
