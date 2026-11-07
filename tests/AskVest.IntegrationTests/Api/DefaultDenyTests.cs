using AskVest.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Api;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class DefaultDenyTests
{
    [Fact]
    public void Every_Endpoint_Is_Authorized_Or_On_Anonymous_AllowList()
    {
        using var factory = new ApiHostFactory();
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        var violations = AskVest.Api.AnonymousEndpointGuard.FindViolations(dataSource.Endpoints);
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Non_Anonymous_Endpoints_Carry_Authorization_Metadata()
    {
        using var factory = new ApiHostFactory();
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        var unprotected = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null
                && !endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any())
            .Select(endpoint => endpoint.DisplayName)
            .ToList();
        unprotected.ShouldBeEmpty(string.Join("; ", unprotected));
    }

    [Theory]
    [InlineData("/api/v1/practice", "PracticeEligible")]
    [InlineData("/api/v1/real", "RealEligible")]
    public void Route_Groups_Carry_Their_Eligibility_Policy(string prefix, string policy)
    {
        using var factory = new ApiHostFactory();
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        var violations = AskVest.Api.RouteGroupPolicyGuard.FindViolations(dataSource.Endpoints, prefix, policy);
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }
}
