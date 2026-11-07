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
                || name.StartsWith("Npgsql.", StringComparison.Ordinal)
                || name.StartsWith("EFCore.", StringComparison.Ordinal)
                || name.StartsWith("Konscious.", StringComparison.Ordinal)
                || name.Equals("PhoneNumbers", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.IdentityModel.", StringComparison.Ordinal)
                || name.StartsWith("FluentValidation", StringComparison.Ordinal)
                || name.StartsWith("MailKit", StringComparison.Ordinal)
                || name.StartsWith("MimeKit", StringComparison.Ordinal)
                || name.Equals("netstandard", StringComparison.Ordinal)
                || name.Equals("mscorlib", StringComparison.Ordinal));
    }

    [Fact]
    public void Identity_Public_Surface_Stays_Within_Module_Namespaces()
    {
        var publicTypes = Identity.GetExportedTypes()
            .Where(t => t.Namespace?.StartsWith("AskVest.Modules.Identity", StringComparison.Ordinal) == true)
            .Select(t => t.FullName!)
            .ToList();
        publicTypes.ShouldAllBe(name =>
            name.Contains(".Contracts.", StringComparison.Ordinal)
            || name.Contains(".Persistence.", StringComparison.Ordinal)
            || name.Contains(".Domain.", StringComparison.Ordinal)
            || name.Contains(".Configuration.", StringComparison.Ordinal)
            || name.Contains(".Migrations.", StringComparison.Ordinal)
            || name.Contains(".Crypto.", StringComparison.Ordinal)
            || name.Contains(".Messaging.", StringComparison.Ordinal)
            || name.Contains(".Middleware.", StringComparison.Ordinal)
            || name.Contains(".Services.", StringComparison.Ordinal)
            || name.Contains(".Endpoints.", StringComparison.Ordinal)
            || name.Contains(".Validation.", StringComparison.Ordinal)
            || name.EndsWith("IdentityModuleExtensions")
            || name.EndsWith("IdentityEndpointsExtensions"));
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

public sealed class ComplianceModuleBoundaryTests
{
    private static readonly Assembly Compliance = typeof(AskVest.Modules.Compliance.ComplianceModuleExtensions).Assembly;

    [Fact]
    public void Compliance_References_Only_SharedKernel_Platform_And_Identity()
    {
        var askVestReferences = Compliance.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("AskVest.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        askVestReferences.ShouldBeSubsetOf(["AskVest.SharedKernel", "AskVest.Platform", "AskVest.Modules.Identity"]);
    }

    [Fact]
    public void Compliance_Public_Surface_Stays_Within_Module_Namespaces()
    {
        var publicTypes = Compliance.GetExportedTypes()
            .Where(t => t.Namespace?.StartsWith("AskVest.Modules.Compliance", StringComparison.Ordinal) == true)
            .Select(t => t.FullName!)
            .ToList();
        publicTypes.ShouldAllBe(name =>
            name.Contains(".Contracts.", StringComparison.Ordinal)
            || name.Contains(".Persistence.", StringComparison.Ordinal)
            || name.Contains(".Domain.", StringComparison.Ordinal)
            || name.Contains(".Configuration.", StringComparison.Ordinal)
            || name.Contains(".Migrations.", StringComparison.Ordinal)
            || name.Contains(".Services.", StringComparison.Ordinal)
            || name.Contains(".Endpoints.", StringComparison.Ordinal)
            || name.Contains(".Validation.", StringComparison.Ordinal)
            || name.Contains(".Authorization.", StringComparison.Ordinal)
            || name.Contains(".Providers.", StringComparison.Ordinal)
            || name.EndsWith("ComplianceModuleExtensions")
            || name.EndsWith("ComplianceEndpointsExtensions"));
    }

    [Fact]
    public void Compliance_Uses_Identity_Only_Through_Contracts()
    {
        var projectDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Modules", "AskVest.Modules.Compliance"));
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var index = 0;
            const string marker = "AskVest.Modules.Identity.";
            while ((index = text.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
            {
                var tail = text[(index + marker.Length)..];
                if (!tail.StartsWith("Contracts", StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetFileName(file)} references Identity outside Contracts");
                    break;
                }

                index += marker.Length;
            }
        }

        violations.ShouldBeEmpty(string.Join("; ", violations));
    }
}

public sealed class RouteGroupPolicyGuardTests
{
    private static RouteEndpoint Endpoint(string route, params object[] metadata)
        => new(_ => Task.CompletedTask, RoutePatternFactory.Parse(route), 0, new EndpointMetadataCollection(metadata), route);

    private static RouteEndpoint Get(string route, string? policy)
    {
        object[] metadata = policy is null ? [] : [new AuthorizeAttribute { Policy = policy }];
        return Endpoint(route, metadata);
    }

    [Fact]
    public void Guard_Flags_Practice_Endpoint_Without_Practice_Policy()
    {
        var violations = AskVest.Api.RouteGroupPolicyGuard.FindViolations(
        [
            Get("/api/v1/practice/diagnostics/ping", "RealEligible"),
            Get("/api/v1/practice/orders", null),
        ],
            "/api/v1/practice",
            "PracticeEligible");
        violations.Count.ShouldBe(2);
    }

    [Fact]
    public void Guard_Accepts_Policy_And_Ignores_Other_Groups()
    {
        var violations = AskVest.Api.RouteGroupPolicyGuard.FindViolations(
        [
            Get("/api/v1/practice/diagnostics/ping", "PracticeEligible"),
            Get("/api/v1/real/diagnostics/ping", "RealEligible"),
            Get("/api/v1/account/me", null),
        ],
            "/api/v1/practice",
            "PracticeEligible");
        violations.ShouldBeEmpty();
    }
}

public sealed class IdempotencyGuardTests
{
    private static RouteEndpoint Endpoint(string route, params object[] metadata)
        => new(_ => Task.CompletedTask, RoutePatternFactory.Parse(route), 0, new EndpointMetadataCollection(metadata), route);

    private static RouteEndpoint Post(string route, params object[] metadata)
        => Endpoint(route, [new HttpMethodMetadata([HttpMethods.Post]), .. metadata]);

    [Fact]
    public void Guard_Flags_Mutating_Endpoint_Without_Idempotency_Declaration()
    {
        var violations = AskVest.Api.IdempotencyEndpointGuard.FindViolations(
        [
            Post("/api/v1/thing"),
        ]);
        violations.ShouldHaveSingleItem().ShouldContain("/api/v1/thing");
    }

    [Fact]
    public void Guard_Flags_Secrets_Endpoint_That_Still_Requires_Idempotency()
    {
        var violations = AskVest.Api.IdempotencyEndpointGuard.FindViolations(
        [
            Post("/api/v1/auth/login", new RequiresIdempotencyAttribute(), new ReturnsSecretsAttribute()),
        ]);
        violations.ShouldContain(v => v.Contains("idempotent replay"));
    }

    [Fact]
    public void Guard_Flags_Secrets_Endpoint_Without_Exemption()
    {
        var violations = AskVest.Api.IdempotencyEndpointGuard.FindViolations(
        [
            Post("/api/v1/auth/login", new ReturnsSecretsAttribute()),
        ]);
        violations.ShouldContain(v => v.Contains("without an idempotency exemption"));
    }

    [Fact]
    public void Guard_Accepts_Declared_And_Safe_Endpoints()
    {
        var violations = AskVest.Api.IdempotencyEndpointGuard.FindViolations(
        [
            Endpoint("/api/v1/account/me"),
            Post("/api/v1/account/me", new RequiresIdempotencyAttribute()),
            Post("/api/v1/auth/login", new IdempotencyExemptAttribute(), new ReturnsSecretsAttribute()),
        ]);
        violations.ShouldBeEmpty();
    }
}
