using AskVest.Api;
using AskVest.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http;
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
        var offenders = new List<string>();
        foreach (var endpoint in dataSource.Endpoints.OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods;
            if (methods is null || methods.All(m => m == HttpMethods.Get || m == HttpMethods.Head || m == HttpMethods.Options))
            {
                continue;
            }

            var declared = endpoint.Metadata.GetMetadata<RequiresIdempotencyAttribute>() is not null
                || endpoint.Metadata.GetMetadata<IdempotencyExemptAttribute>() is not null;
            if (!declared)
            {
                offenders.Add($"{string.Join(',', methods)} {endpoint.RoutePattern.RawText}");
            }
        }

        offenders.ShouldBeEmpty(string.Join("; ", offenders));
    }
}
