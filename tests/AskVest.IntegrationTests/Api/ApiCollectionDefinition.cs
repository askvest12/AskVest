using Xunit;

namespace AskVest.IntegrationTests.Api;

[CollectionDefinition(ApiCollectionDefinition.Name)]
public sealed class ApiCollectionDefinition
{
    public const string Name = "api";
}
