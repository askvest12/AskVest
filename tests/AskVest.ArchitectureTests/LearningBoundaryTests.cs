using System.Reflection;
using Shouldly;
using Xunit;

namespace AskVest.ArchitectureTests;

public sealed class LearningModuleBoundaryTests
{
    private static readonly Assembly Learning = typeof(AskVest.Modules.Learning.LearningModuleExtensions).Assembly;

    [Fact]
    public void Learning_References_Only_Allowed_AskVest_Assemblies()
    {
        var askVestReferences = Learning.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("AskVest.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        askVestReferences.ShouldBeSubsetOf(
            [
                "AskVest.SharedKernel",
                "AskVest.Platform",
                "AskVest.Modules.Identity",
            ]);
        Learning.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => !name.StartsWith("AskVest.", StringComparison.Ordinal))
            .ShouldAllBe(name =>
                name.StartsWith("Microsoft.", StringComparison.Ordinal)
                || name.StartsWith("System", StringComparison.Ordinal)
                || name.StartsWith("Npgsql", StringComparison.Ordinal)
                || name.StartsWith("EFCore.", StringComparison.Ordinal)
                || name.Equals("netstandard", StringComparison.Ordinal)
                || name.Equals("mscorlib", StringComparison.Ordinal));
    }

    [Fact]
    public void Learning_Public_Surface_Stays_Within_Module_Namespaces()
    {
        var publicTypes = Learning.GetExportedTypes()
            .Where(t => t.Namespace?.StartsWith("AskVest.Modules.Learning", StringComparison.Ordinal) == true)
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
            || name.EndsWith("LearningModuleExtensions")
            || name.EndsWith("LearningEndpointsExtensions"));
    }

    [Theory]
    [InlineData("AskVest.Modules.Identity.")]
    public void Learning_Uses_Other_Modules_Only_Through_Contracts(string marker)
    {
        var projectDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Modules", "AskVest.Modules.Learning"));
        var violations = LearningBoundaryGuard.FindContractViolations(projectDirectory, marker)
            .ToList();
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Learning_Never_References_Money_Practice_Trading_Or_Compliance()
    {
        var projectDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Modules", "AskVest.Modules.Learning"));
        var violations = LearningBoundaryGuard.FindForbiddenReferences(projectDirectory);
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void No_Code_Outside_Learning_References_Learning_Internals()
    {
        var srcDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        var violations = LearningBoundaryGuard.FindExternalReferenceViolations(srcDirectory)
            .Where(v => !v.StartsWith("AskVest.Migrator.", StringComparison.Ordinal))
            .ToList();
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Compliance_Uses_Learning_Only_Through_Contracts()
    {
        var complianceDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Modules", "AskVest.Modules.Compliance"));
        var violations = LearningBoundaryGuard.FindContractViolations(
            complianceDirectory, "AskVest.Modules.Learning.");
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Guard_Catches_Synthetic_Violations()
    {
        LearningBoundaryGuard.IsAllowedLearningReference("Contracts.ILearningProgress.").ShouldBeTrue();
        LearningBoundaryGuard.IsAllowedLearningReference("Persistence.LearningDbContext>();").ShouldBeFalse();
        LearningBoundaryGuard.FindForbiddenReferencesInText(
                "src/Modules/AskVest.Modules.Learning/Services/Foo.cs",
                "var orders = services.AddScoped<AskVest.Modules.Practice.Services.PracticeOrderService>();")
            .ShouldNotBeEmpty();
        LearningBoundaryGuard.FindForbiddenReferencesInText(
                "src/Modules/AskVest.Modules.Learning/Services/Foo.cs",
                "var lessons = services.AddScoped<LearningLessonService>();")
            .ShouldBeEmpty();
        LearningBoundaryGuard.FindContractViolationsInText(
                "src/Modules/AskVest.Modules.Compliance/Services/Foo.cs",
                "var x = typeof(AskVest.Modules.Learning.Persistence.LearningDbContext);")
            .ShouldNotBeEmpty();
        LearningBoundaryGuard.FindContractViolationsInText(
                "src/Modules/AskVest.Modules.Compliance/Services/Foo.cs",
                "var x = typeof(AskVest.Modules.Learning.Contracts.ILearningProgress);")
            .ShouldBeEmpty();
    }
}

internal static class LearningBoundaryGuard
{
    private const string Marker = "AskVest.Modules.Learning.";

    private static readonly string[] ForbiddenMarkers =
    [
        "AskVest.Modules.Ledger.",
        "AskVest.Modules.Practice.",
        "AskVest.Modules.MarketData.",
        "AskVest.Modules.Catalog.",
        "AskVest.Modules.Compliance.",
        "AskVest.Modules.Trading.",
        "AskVest.Modules.Funding.",
        "AskVest.Modules.Wallets.",
        "AskVest.Modules.Portfolio.",
        "AskVest.Modules.Notifications.",
        "AskVest.Modules.Earn.",
        "Book.Real",
        "PracticeEligible",
        "RealEligible",
    ];

    public static bool IsAllowedLearningReference(string tail)
    {
        return tail.StartsWith("Contracts", StringComparison.Ordinal)
            || tail.StartsWith(';')
            || tail.StartsWith("LearningModuleExtensions", StringComparison.Ordinal)
            || tail.StartsWith("LearningEndpointsExtensions", StringComparison.Ordinal);
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

    public static IReadOnlyList<string> FindContractViolationsInText(string file, string text)
    {
        const string marker = "AskVest.Modules.Learning.";
        var violations = new List<string>();
        var index = 0;
        while ((index = text.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            var tail = text[(index + marker.Length)..];
            if (!tail.StartsWith("Contracts", StringComparison.Ordinal))
            {
                violations.Add($"{file} references Learning outside Contracts");
                break;
            }

            index += marker.Length;
        }

        return violations;
    }

    public static IReadOnlyList<string> FindForbiddenReferences(string directory)
    {
        var violations = new List<string>();
        foreach (var file in SourceFiles(directory))
        {
            var relative = file.Replace('\\', '/');
            violations.AddRange(FindForbiddenReferencesInText(relative, File.ReadAllText(file)));
        }

        return violations;
    }

    public static IReadOnlyList<string> FindForbiddenReferencesInText(string file, string text)
    {
        var violations = new List<string>();
        foreach (var marker in ForbiddenMarkers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                violations.Add($"{file} references forbidden marker {marker}");
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
            if (relative.StartsWith($"Modules{Path.DirectorySeparatorChar}AskVest.Modules.Learning{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"AskVest.Migrator{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var index = 0;
            while ((index = text.IndexOf(Marker, index, StringComparison.Ordinal)) >= 0)
            {
                var tail = text[(index + Marker.Length)..];
                if (!IsAllowedLearningReference(tail))
                {
                    violations.Add($"{relative} references Learning outside Contracts");
                    break;
                }

                index += Marker.Length;
            }
        }

        return violations;
    }

    private static IEnumerable<string> SourceFiles(string directory)
    {
        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
