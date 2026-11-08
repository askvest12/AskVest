using System.Reflection;
using Shouldly;
using Xunit;

namespace AskVest.ArchitectureTests;

public sealed class MarketDataModuleBoundaryTests
{
    private static readonly Assembly MarketData = typeof(AskVest.Modules.MarketData.MarketDataModuleExtensions).Assembly;

    [Fact]
    public void MarketData_References_Only_SharedKernel_Platform_And_Catalog()
    {
        var askVestReferences = MarketData.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("AskVest.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        askVestReferences.ShouldBeSubsetOf(
            [
                "AskVest.SharedKernel",
                "AskVest.Platform",
                "AskVest.Modules.Catalog",
            ]);
        MarketData.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => !name.StartsWith("AskVest.", StringComparison.Ordinal))
            .ShouldAllBe(name =>
                name.StartsWith("Microsoft.", StringComparison.Ordinal)
                || name.StartsWith("System", StringComparison.Ordinal)
                || name.StartsWith("Npgsql", StringComparison.Ordinal)
                || name.StartsWith("EFCore.", StringComparison.Ordinal)
                || name.StartsWith("FluentValidation", StringComparison.Ordinal)
                || name.Equals("netstandard", StringComparison.Ordinal)
                || name.Equals("mscorlib", StringComparison.Ordinal));
    }

    [Fact]
    public void MarketData_Public_Surface_Stays_Within_Module_Namespaces()
    {
        var publicTypes = MarketData.GetExportedTypes()
            .Where(t => t.Namespace?.StartsWith("AskVest.Modules.MarketData", StringComparison.Ordinal) == true)
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
            || name.Contains(".Providers.", StringComparison.Ordinal)
            || name.Contains(".Application.", StringComparison.Ordinal)
            || name.Contains(".Infrastructure.", StringComparison.Ordinal)
            || name.EndsWith("MarketDataModuleExtensions")
            || name.EndsWith("MarketDataEndpointsExtensions"));
    }

    [Fact]
    public void MarketData_Uses_Catalog_Only_Through_Contracts()
    {
        var projectDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Modules", "AskVest.Modules.MarketData"));
        var violations = MarketDataBoundaryGuard.FindContractViolations(projectDirectory, "AskVest.Modules.Catalog.")
            .ToList();
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void No_Code_Outside_MarketData_References_MarketData_Internals()
    {
        var srcDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        var violations = MarketDataBoundaryGuard.FindExternalReferenceViolations(srcDirectory)
            .Where(v => !v.StartsWith("AskVest.Migrator.", StringComparison.Ordinal))
            .ToList();
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void MarketData_Never_References_Chain_Or_Token_Types()
    {
        var projectDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Modules", "AskVest.Modules.MarketData"));
        var violations = MarketDataBoundaryGuard.FindChainOrTokenViolations(projectDirectory);
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Guard_Catches_Synthetic_Violations()
    {
        MarketDataBoundaryGuard.IsAllowedMarketDataReference("Contracts.IMarketData.").ShouldBeTrue();
        MarketDataBoundaryGuard.IsAllowedMarketDataReference("Persistence.MarketDataDbContext>();").ShouldBeFalse();
        MarketDataBoundaryGuard.ExtractReferenceTail(
                "var db = services.AddDbContext<AskVest.Modules.MarketData.Persistence.MarketDataDbContext>();")
            .ShouldBe("Persistence.MarketDataDbContext>();");
        MarketDataBoundaryGuard.FindChainOrTokenViolationsInText(
                "src/Modules/AskVest.Modules.MarketData/Services/Foo.cs",
                "var token = catalog.FindTokenAsync(id);")
            .ShouldNotBeEmpty();
        MarketDataBoundaryGuard.FindChainOrTokenViolationsInText(
                "src/Modules/AskVest.Modules.MarketData/Services/Foo.cs",
                "var price = await prices.GetLatestAsync(instrumentId);")
            .ShouldBeEmpty();
    }
}

internal static class MarketDataBoundaryGuard
{
    private const string Marker = "AskVest.Modules.MarketData.";

    private static readonly string[] ChainOrTokenMarkers =
        ["CatalogToken", "CatalogChain", "TokenAvailability", "FindToken", "FindChain", "ValidateAddress", "GetAvailability", "ListListedTokens"];

    public static bool IsAllowedMarketDataReference(string tail)
    {
        return tail.StartsWith("Contracts", StringComparison.Ordinal)
            || tail.StartsWith(';')
            || tail.StartsWith("MarketDataModuleExtensions", StringComparison.Ordinal)
            || tail.StartsWith("MarketDataEndpointsExtensions", StringComparison.Ordinal);
    }

    public static string? ExtractReferenceTail(string text)
    {
        var index = text.IndexOf(Marker, StringComparison.Ordinal);
        return index < 0 ? null : text[(index + Marker.Length)..];
    }

    public static IReadOnlyList<string> FindContractViolations(string directory, string marker)
    {
        var violations = new List<string>();
        foreach (var file in SourceFiles(directory))
        {
            var text = File.ReadAllText(file);
            var index = 0;
            while ((index = text.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
            {
                var tail = text[(index + marker.Length)..];
                if (!tail.StartsWith("Contracts", StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetFileName(file)} references {marker} outside Contracts");
                    break;
                }

                index += marker.Length;
            }
        }

        return violations;
    }

    public static IReadOnlyList<string> FindExternalReferenceViolations(string srcDirectory)
    {
        var violations = new List<string>();
        foreach (var file in SourceFiles(srcDirectory))
        {
            var relative = Path.GetRelativePath(srcDirectory, file);
            if (relative.StartsWith($"Modules{Path.DirectorySeparatorChar}AskVest.Modules.MarketData{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"AskVest.Migrator{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var index = 0;
            while ((index = text.IndexOf(Marker, index, StringComparison.Ordinal)) >= 0)
            {
                var tail = text[(index + Marker.Length)..];
                if (!IsAllowedMarketDataReference(tail))
                {
                    violations.Add($"{relative} references MarketData outside Contracts");
                    break;
                }

                index += Marker.Length;
            }
        }

        return violations;
    }

    public static IReadOnlyList<string> FindChainOrTokenViolations(string directory)
    {
        var violations = new List<string>();
        foreach (var file in SourceFiles(directory))
        {
            var relative = file.Replace('\\', '/');
            violations.AddRange(FindChainOrTokenViolationsInText(relative, File.ReadAllText(file))
                .Select(v => $"{relative}: {v}"));
        }

        return violations;
    }

    public static IReadOnlyList<string> FindChainOrTokenViolationsInText(string file, string text)
    {
        var violations = new List<string>();
        foreach (var marker in ChainOrTokenMarkers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                violations.Add($"{file} mentions {marker}");
            }
        }

        return violations;
    }

    private static IEnumerable<string> SourceFiles(string directory)
    {
        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
