using System.Reflection;
using Shouldly;
using Xunit;

namespace AskVest.ArchitectureTests;

public sealed class PortfolioModuleBoundaryTests
{
    private static readonly Assembly Portfolio = typeof(AskVest.Modules.Portfolio.PortfolioModuleExtensions).Assembly;

    [Fact]
    public void Portfolio_References_Only_Allowed_AskVest_Assemblies()
    {
        var askVestReferences = Portfolio.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("AskVest.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        askVestReferences.ShouldBeSubsetOf(
            [
                "AskVest.SharedKernel",
                "AskVest.Platform",
                "AskVest.Modules.Compliance",
                "AskVest.Modules.Ledger",
                "AskVest.Modules.Catalog",
                "AskVest.Modules.MarketData",
                "AskVest.Modules.Practice",
            ]);
        Portfolio.GetReferencedAssemblies()
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
    public void Portfolio_Public_Surface_Stays_Within_Module_Namespaces()
    {
        var publicTypes = Portfolio.GetExportedTypes()
            .Where(t => t.Namespace?.StartsWith("AskVest.Modules.Portfolio", StringComparison.Ordinal) == true)
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
            || name.Contains(".Application.", StringComparison.Ordinal)
            || name.Contains(".Infrastructure.", StringComparison.Ordinal)
            || name.EndsWith("PortfolioModuleExtensions")
            || name.EndsWith("PortfolioEndpointsExtensions"));
    }

    [Theory]
    [InlineData("AskVest.Modules.Ledger.")]
    [InlineData("AskVest.Modules.Catalog.")]
    [InlineData("AskVest.Modules.MarketData.")]
    [InlineData("AskVest.Modules.Practice.")]
    [InlineData("AskVest.Modules.Compliance.")]
    public void Portfolio_Uses_Other_Modules_Only_Through_Contracts(string marker)
    {
        var projectDirectory = PortfolioProjectDirectory();
        var violations = PortfolioBoundaryGuard.FindContractViolations(projectDirectory, marker)
            .ToList();
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void No_Code_Outside_Portfolio_References_Portfolio_Internals()
    {
        var srcDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        var violations = PortfolioBoundaryGuard.FindExternalReferenceViolations(srcDirectory)
            .Where(v => !v.StartsWith("AskVest.Migrator.", StringComparison.Ordinal))
            .ToList();
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Portfolio_Never_Calls_Ledger_Writes()
    {
        var violations = PortfolioBoundaryGuard.FindLedgerWriteViolations(PortfolioProjectDirectory());
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Portfolio_Has_No_Chain_Specific_Code()
    {
        var violations = PortfolioBoundaryGuard.FindChainSpecificViolations(PortfolioProjectDirectory());
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Guard_Catches_Synthetic_Violations()
    {
        PortfolioBoundaryGuard.IsAllowedPortfolioReference("Contracts.IPortfolio.").ShouldBeTrue();
        PortfolioBoundaryGuard.IsAllowedPortfolioReference("Persistence.PortfolioDbContext>();").ShouldBeFalse();
        PortfolioBoundaryGuard.ExtractReferenceTail(
                "var db = services.AddDbContext<AskVest.Modules.Portfolio.Persistence.PortfolioDbContext>();")
            .ShouldBe("Persistence.PortfolioDbContext>();");
        PortfolioBoundaryGuard.FindLedgerWriteViolationsInText(
                "src/Modules/AskVest.Modules.Portfolio/Services/Foo.cs",
                "await ledger.PostJournalAsync(request);")
            .ShouldNotBeEmpty();
        PortfolioBoundaryGuard.FindLedgerWriteViolationsInText(
                "src/Modules/AskVest.Modules.Portfolio/Services/Foo.cs",
                "var movements = await ledger.GetMovementsAsync(userId, book, 100, null);")
            .ShouldBeEmpty();
        PortfolioBoundaryGuard.FindChainSpecificViolationsInText(
                "src/Modules/AskVest.Modules.Portfolio/Services/Foo.cs",
                "var handler = new EvmChainFamilyHandler();")
            .ShouldNotBeEmpty();
        PortfolioBoundaryGuard.FindChainSpecificViolationsInText(
                "src/Modules/AskVest.Modules.Portfolio/Services/Foo.cs",
                "var chainId = token.ChainId;")
            .ShouldBeEmpty();
    }

    private static string PortfolioProjectDirectory()
    {
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Modules", "AskVest.Modules.Portfolio"));
    }
}

internal static class PortfolioBoundaryGuard
{
    private const string Marker = "AskVest.Modules.Portfolio.";

    private static readonly string[] LedgerWriteMarkers =
        ["PostJournalAsync", "ReverseJournalAsync", "RegisterAssetAsync"];

    private static readonly string[] ChainSpecificMarkers =
        ["ChainFamily", "EvmChain", "SvmChain", "MoveChain", "Caip19", "Caip2", "ChainAddress"];

    public static bool IsAllowedPortfolioReference(string tail)
    {
        return tail.StartsWith("Contracts", StringComparison.Ordinal)
            || tail.StartsWith(';')
            || tail.StartsWith("PortfolioModuleExtensions", StringComparison.Ordinal)
            || tail.StartsWith("PortfolioEndpointsExtensions", StringComparison.Ordinal);
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
            if (relative.StartsWith($"Modules{Path.DirectorySeparatorChar}AskVest.Modules.Portfolio{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"AskVest.Migrator{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var index = 0;
            while ((index = text.IndexOf(Marker, index, StringComparison.Ordinal)) >= 0)
            {
                var tail = text[(index + Marker.Length)..];
                if (!IsAllowedPortfolioReference(tail))
                {
                    violations.Add($"{relative} references Portfolio outside Contracts");
                    break;
                }

                index += Marker.Length;
            }
        }

        return violations;
    }

    public static IReadOnlyList<string> FindLedgerWriteViolations(string directory)
    {
        var violations = new List<string>();
        foreach (var file in SourceFiles(directory))
        {
            var relative = file.Replace('\\', '/');
            violations.AddRange(FindLedgerWriteViolationsInText(relative, File.ReadAllText(file))
                .Select(v => $"{relative}: {v}"));
        }

        return violations;
    }

    public static IReadOnlyList<string> FindLedgerWriteViolationsInText(string file, string text)
    {
        var violations = new List<string>();
        foreach (var marker in LedgerWriteMarkers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                violations.Add($"{file} mentions {marker}");
            }
        }

        return violations;
    }

    public static IReadOnlyList<string> FindChainSpecificViolations(string directory)
    {
        var violations = new List<string>();
        foreach (var file in SourceFiles(directory))
        {
            var relative = file.Replace('\\', '/');
            violations.AddRange(FindChainSpecificViolationsInText(relative, File.ReadAllText(file))
                .Select(v => $"{relative}: {v}"));
        }

        return violations;
    }

    public static IReadOnlyList<string> FindChainSpecificViolationsInText(string file, string text)
    {
        var violations = new List<string>();
        foreach (var marker in ChainSpecificMarkers)
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
