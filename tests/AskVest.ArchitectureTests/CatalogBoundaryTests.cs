using System.Reflection;
using Shouldly;
using Xunit;

namespace AskVest.ArchitectureTests;

public sealed class CatalogModuleBoundaryTests
{
    private static readonly Assembly Catalog = typeof(AskVest.Modules.Catalog.CatalogModuleExtensions).Assembly;

    [Fact]
    public void Catalog_References_Only_SharedKernel_Platform_And_Module_Contracts()
    {
        var askVestReferences = Catalog.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("AskVest.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        askVestReferences.ShouldBeSubsetOf(
            [
                "AskVest.SharedKernel",
                "AskVest.Platform",
                "AskVest.Modules.Identity",
                "AskVest.Modules.Compliance",
                "AskVest.Modules.Ledger",
            ]);
        Catalog.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => !name.StartsWith("AskVest.", StringComparison.Ordinal))
            .ShouldAllBe(name =>
                name.StartsWith("Microsoft.", StringComparison.Ordinal)
                || name.StartsWith("System", StringComparison.Ordinal)
                || name.StartsWith("Npgsql", StringComparison.Ordinal)
                || name.StartsWith("EFCore.", StringComparison.Ordinal)
                || name.StartsWith("FluentValidation", StringComparison.Ordinal)
                || name.StartsWith("BouncyCastle", StringComparison.Ordinal)
                || name.Equals("netstandard", StringComparison.Ordinal)
                || name.Equals("mscorlib", StringComparison.Ordinal));
    }

    [Fact]
    public void Catalog_Public_Surface_Stays_Within_Module_Namespaces()
    {
        var publicTypes = Catalog.GetExportedTypes()
            .Where(t => t.Namespace?.StartsWith("AskVest.Modules.Catalog", StringComparison.Ordinal) == true)
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
            || name.Contains(".Chains.", StringComparison.Ordinal)
            || name.EndsWith("CatalogModuleExtensions")
            || name.EndsWith("CatalogEndpointsExtensions"));
    }

    [Fact]
    public void Catalog_Uses_Other_Modules_Only_Through_Contracts()
    {
        var projectDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Modules", "AskVest.Modules.Catalog"));
        var violations = CatalogBoundaryGuard.FindContractViolations(projectDirectory, "AskVest.Modules.Identity.")
            .Concat(CatalogBoundaryGuard.FindContractViolations(projectDirectory, "AskVest.Modules.Compliance."))
            .Concat(CatalogBoundaryGuard.FindContractViolations(projectDirectory, "AskVest.Modules.Ledger."))
            .ToList();
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void No_Code_Outside_Catalog_References_Catalog_Internals()
    {
        var srcDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        var violations = CatalogBoundaryGuard.FindExternalReferenceViolations(srcDirectory)
            .Where(v => !v.StartsWith("AskVest.Migrator.", StringComparison.Ordinal))
            .ToList();
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Chain_Specific_Library_Types_Stay_Inside_The_Evm_Handler()
    {
        var srcDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Modules", "AskVest.Modules.Catalog"));
        var violations = CatalogBoundaryGuard.FindChainSpecificViolations(srcDirectory);
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Guard_Catches_Synthetic_Violations()
    {
        CatalogBoundaryGuard.IsAllowedCatalogReference("Contracts.ICatalog").ShouldBeTrue();
        CatalogBoundaryGuard.IsAllowedCatalogReference("Persistence.CatalogDbContext>();").ShouldBeFalse();
        CatalogBoundaryGuard.ExtractReferenceTail(
                "var db = services.AddDbContext<AskVest.Modules.Catalog.Persistence.CatalogDbContext>();")
            .ShouldBe("Persistence.CatalogDbContext>();");
        CatalogBoundaryGuard.FindChainSpecificViolationsInText(
                "src/Modules/AskVest.Modules.Catalog/Services/Foo.cs",
                "using Org.BouncyCastle.Crypto.Digests;")
            .ShouldNotBeEmpty();
        CatalogBoundaryGuard.FindChainSpecificViolationsInText(
                "src/Modules/AskVest.Modules.Catalog/Chains/Evm/EvmChainFamilyHandler.cs",
                "using Org.BouncyCastle.Crypto.Digests;")
            .ShouldBeEmpty();
    }
}

internal static class CatalogBoundaryGuard
{
    private const string Marker = "AskVest.Modules.Catalog.";

    private static readonly string[] ChainSpecificNamespaces =
        ["Org.BouncyCastle", "Nethereum", "Web3"];

    public static bool IsAllowedCatalogReference(string tail)
    {
        return tail.StartsWith("Contracts", StringComparison.Ordinal)
            || tail.StartsWith(';')
            || tail.StartsWith("CatalogModuleExtensions", StringComparison.Ordinal)
            || tail.StartsWith("CatalogEndpointsExtensions", StringComparison.Ordinal);
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
            if (relative.StartsWith($"Modules{Path.DirectorySeparatorChar}AskVest.Modules.Catalog{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"AskVest.Migrator{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var index = 0;
            while ((index = text.IndexOf(Marker, index, StringComparison.Ordinal)) >= 0)
            {
                var tail = text[(index + Marker.Length)..];
                if (!IsAllowedCatalogReference(tail))
                {
                    violations.Add($"{relative} references Catalog outside Contracts");
                    break;
                }

                index += Marker.Length;
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

    public static IReadOnlyList<string> FindChainSpecificViolationsInText(string relativePath, string text)
    {
        var isEvmHandler = relativePath.Contains("/Chains/Evm/", StringComparison.Ordinal)
            || relativePath.EndsWith("EvmChainFamilyHandler.cs", StringComparison.Ordinal);
        if (isEvmHandler)
        {
            return [];
        }

        var violations = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*'))
            {
                continue;
            }

            foreach (var ns in ChainSpecificNamespaces)
            {
                if (line.Contains(ns, StringComparison.Ordinal))
                {
                    violations.Add($"chain-specific type '{ns}' outside the EVM handler");
                    break;
                }
            }
        }

        return violations;
    }

    private static IEnumerable<string> SourceFiles(string directory)
    {
        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
