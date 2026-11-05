using AskVest.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Api;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class IdempotencyArchitectureTests(PostgresFixture fixture)
{
    [Fact]
    public void Every_NonGet_Endpoint_Declares_Idempotency_Or_Exemption()
    {
        using var factory = new IdempotencyApiFactory(fixture.AppConnectionString);
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        var offenders = AskVest.Api.IdempotencyEndpointGuard.FindViolations(dataSource.Endpoints);
        offenders.ShouldBeEmpty(string.Join("; ", offenders));
    }
}
