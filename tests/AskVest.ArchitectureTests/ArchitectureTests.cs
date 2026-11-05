using System.Reflection;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Shouldly;
using Xunit;

namespace AskVest.ArchitectureTests;

public sealed class ModuleBoundaryTests
{

    private static readonly Assembly Identity = typeof(AskVest.Modules.Identity.IdentityModuleExtensions).Assembly;

    [Fact]
    public void Identity_References_Only_SharedKernel_And_Platform()
    {
        var askVestReferences = Identity.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("AskVest.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        askVestReferences.ShouldBeSubsetOf(["AskVest.SharedKernel", "AskVest.Platform"]);
        Identity.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => !name.StartsWith("AskVest.", StringComparison.Ordinal))
            .ShouldAllBe(name =>
                name.StartsWith("Microsoft.", StringComparison.Ordinal)
                || name.StartsWith("System", StringComparison.Ordinal)
                || name.Equals("netstandard", StringComparison.Ordinal)
                || name.Equals("mscorlib", StringComparison.Ordinal));
    }

    [Fact]
    public void Identity_Does_Not_Expose_Infrastructure_To_Others()
    {
        var publicTypes = Identity.GetExportedTypes()
            .Where(t => t.Namespace?.StartsWith("AskVest.Modules.Identity", StringComparison.Ordinal) == true)
            .Select(t => t.FullName!)
            .ToList();
        publicTypes.ShouldAllBe(name => name.Contains(".Contracts.") || name.EndsWith("IdentityModuleExtensions") || name.EndsWith("IdentityEndpointsExtensions"));
    }

    [Fact]
    public void EndpointMarkers_Are_Public_For_Modules()
    {
        typeof(RequiresIdempotencyAttribute).IsPublic.ShouldBeTrue();
        typeof(IdempotencyExemptAttribute).IsPublic.ShouldBeTrue();
        typeof(ReturnsSecretsAttribute).IsPublic.ShouldBeTrue();
    }
}

public sealed class AnonymousGuardTests
{
    private static RouteEndpoint Endpoint(string route, params object[] metadata)
        => new(_ => Task.CompletedTask, RoutePatternFactory.Parse(route), 0, new EndpointMetadataCollection(metadata), route);

    [Fact]
    public void Guard_Flags_Anonymous_Endpoint_Outside_AllowList()
    {
        var violations = AskVest.Api.AnonymousEndpointGuard.FindViolations(
        [
            Endpoint("/api/v1/admin/users", new AllowAnonymousAttribute()),
            Endpoint("/api/v1/auth/login", new AllowAnonymousAttribute()),
        ]);
        violations.ShouldHaveSingleItem().ShouldContain("/api/v1/admin/users");
    }

    [Fact]
    public void Guard_Ignores_Protected_And_AllowListed_Endpoints()
    {
        var violations = AskVest.Api.AnonymousEndpointGuard.FindViolations(
        [
            Endpoint("/api/v1/account/me", new AuthorizeAttribute()),
            Endpoint("/health/live", new AllowAnonymousAttribute()),
        ]);
        violations.ShouldBeEmpty();
    }

    [Fact]
    public void Guard_Flags_Endpoint_With_No_Policy_And_Not_AllowListed()
    {
        var violations = AskVest.Api.AnonymousEndpointGuard.FindViolations(
        [
            Endpoint("/api/v1/account/me"),
        ]);
        violations.ShouldHaveSingleItem().ShouldContain("/api/v1/account/me");
    }
}
