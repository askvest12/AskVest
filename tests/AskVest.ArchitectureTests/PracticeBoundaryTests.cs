using System.Reflection;
using Shouldly;
using Xunit;

namespace AskVest.ArchitectureTests;

public sealed class PracticeModuleBoundaryTests
{
    private static readonly Assembly Practice = typeof(AskVest.Modules.Practice.PracticeModuleExtensions).Assembly;

    [Fact]
    public void Practice_References_Only_Allowed_AskVest_Assemblies()
    {
        var askVestReferences = Practice.GetReferencedAssemblies()
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
            ]);
        Practice.GetReferencedAssemblies()
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
    public void Practice_Public_Surface_Stays_Within_Module_Namespaces()
    {
        var publicTypes = Practice.GetExportedTypes()
            .Where(t => t.Namespace?.StartsWith("AskVest.Modules.Practice", StringComparison.Ordinal) == true)
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
            || name.EndsWith("PracticeModuleExtensions")
            || name.EndsWith("PracticeEndpointsExtensions"));
    }

    [Theory]
    [InlineData("AskVest.Modules.Ledger.")]
    [InlineData("AskVest.Modules.MarketData.")]
    [InlineData("AskVest.Modules.Catalog.")]
    [InlineData("AskVest.Modules.Compliance.")]
    public void Practice_Uses_Other_Modules_Only_Through_Contracts(string marker)
    {
        var projectDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Modules", "AskVest.Modules.Practice"));
        var violations = PracticeBoundaryGuard.FindContractViolations(projectDirectory, marker)
            .ToList();
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void No_Code_Outside_Practice_References_Practice_Internals()
    {
        var srcDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        var violations = PracticeBoundaryGuard.FindExternalReferenceViolations(srcDirectory)
            .Where(v => !v.StartsWith("AskVest.Migrator.", StringComparison.Ordinal))
            .ToList();
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Practice_Code_Never_References_The_Real_Book()
    {
        var projectDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Modules", "AskVest.Modules.Practice"));
        var violations = PracticeBoundaryGuard.FindRealBookViolations(projectDirectory);
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Guard_Catches_Synthetic_Violations()
    {
        PracticeBoundaryGuard.IsAllowedPracticeReference("Contracts.IPractice.").ShouldBeTrue();
        PracticeBoundaryGuard.IsAllowedPracticeReference("Persistence.PracticeDbContext>();").ShouldBeFalse();
        PracticeBoundaryGuard.ExtractReferenceTail(
                "var db = services.AddDbContext<AskVest.Modules.Practice.Persistence.PracticeDbContext>();")
            .ShouldBe("Persistence.PracticeDbContext>();");
        PracticeBoundaryGuard.FindRealBookViolationsInText(
                "src/Modules/AskVest.Modules.Practice/Services/Foo.cs",
                "var book = Book.Real;")
            .ShouldNotBeEmpty();
        PracticeBoundaryGuard.FindRealBookViolationsInText(
                "src/Modules/AskVest.Modules.Practice/Services/Foo.cs",
                "var book = Book.Practice;")
            .ShouldBeEmpty();
    }
}

internal static class PracticeBoundaryGuard
{
    private const string Marker = "AskVest.Modules.Practice.";

    private static readonly string[] RealBookMarkers = ["Book.Real", "RealEligible"];

    public static bool IsAllowedPracticeReference(string tail)
    {
        return tail.StartsWith("Contracts", StringComparison.Ordinal)
            || tail.StartsWith(';')
            || tail.StartsWith("PracticeModuleExtensions", StringComparison.Ordinal)
            || tail.StartsWith("PracticeEndpointsExtensions", StringComparison.Ordinal);
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
            if (relative.StartsWith($"Modules{Path.DirectorySeparatorChar}AskVest.Modules.Practice{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"AskVest.Migrator{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var index = 0;
            while ((index = text.IndexOf(Marker, index, StringComparison.Ordinal)) >= 0)
            {
                var tail = text[(index + Marker.Length)..];
                if (!IsAllowedPracticeReference(tail))
                {
                    violations.Add($"{relative} references Practice outside Contracts");
                    break;
                }

                index += Marker.Length;
            }
        }

        return violations;
    }

    public static IReadOnlyList<string> FindRealBookViolations(string directory)
    {
        var violations = new List<string>();
        foreach (var file in SourceFiles(directory))
        {
            var relative = file.Replace('\\', '/');
            violations.AddRange(FindRealBookViolationsInText(relative, File.ReadAllText(file))
                .Select(v => $"{relative}: {v}"));
        }

        return violations;
    }

    public static IReadOnlyList<string> FindRealBookViolationsInText(string file, string text)
    {
        var violations = new List<string>();
        foreach (var marker in RealBookMarkers)
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
