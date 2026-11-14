using System.Reflection;
using Shouldly;
using Xunit;

namespace AskVest.ArchitectureTests;

public sealed class WalletsModuleBoundaryTests
{
    private static readonly Assembly Wallets = typeof(AskVest.Modules.Wallets.WalletsModuleExtensions).Assembly;

    [Fact]
    public void Wallets_References_Only_Allowed_AskVest_Assemblies()
    {
        var askVestReferences = Wallets.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("AskVest.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        askVestReferences.ShouldBeSubsetOf(
            [
                "AskVest.SharedKernel",
                "AskVest.Platform",
                "AskVest.Modules.Compliance",
                "AskVest.Modules.Identity",
                "AskVest.Modules.Ledger",
                "AskVest.Modules.Catalog",
                "AskVest.Modules.MarketData",
            ]);
        Wallets.GetReferencedAssemblies()
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
    public void Wallets_Public_Surface_Stays_Within_Module_Namespaces()
    {
        var publicTypes = Wallets.GetExportedTypes()
            .Where(t => t.Namespace?.StartsWith("AskVest.Modules.Wallets", StringComparison.Ordinal) == true)
            .Select(t => t.FullName!)
            .ToList();
        publicTypes.ShouldAllBe(name =>
            name.Contains(".Contracts.", StringComparison.Ordinal)
            || name.Contains(".Persistence.", StringComparison.Ordinal)
            || name.Contains(".Domain.", StringComparison.Ordinal)
            || name.Contains(".Configuration.", StringComparison.Ordinal)
            || name.Contains(".Migrations.", StringComparison.Ordinal)
            || name.Contains(".Custody.", StringComparison.Ordinal)
            || name.Contains(".Screening.", StringComparison.Ordinal)
            || name.Contains(".Services.", StringComparison.Ordinal)
            || name.Contains(".Endpoints.", StringComparison.Ordinal)
            || name.Contains(".Application.", StringComparison.Ordinal)
            || name.Contains(".Infrastructure.", StringComparison.Ordinal)
            || name.EndsWith("WalletsModuleExtensions")
            || name.EndsWith("WalletsEndpointsExtensions"));
    }

    [Theory]
    [InlineData("AskVest.Modules.Ledger.")]
    [InlineData("AskVest.Modules.Catalog.")]
    [InlineData("AskVest.Modules.MarketData.")]
    [InlineData("AskVest.Modules.Compliance.")]
    [InlineData("AskVest.Modules.Identity.")]
    public void Wallets_Uses_Other_Modules_Only_Through_Contracts(string marker)
    {
        var projectDirectory = WalletsProjectDirectory();
        var violations = new List<string>();
        foreach (var file in SourceFiles(projectDirectory))
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

        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void No_Code_Outside_Wallets_References_Wallets_Internals()
    {
        var srcDirectory = RepoDirectory("src");
        var violations = new List<string>();
        foreach (var file in SourceFiles(srcDirectory))
        {
            var relative = Path.GetRelativePath(srcDirectory, file);
            if (relative.StartsWith($"Modules{Path.DirectorySeparatorChar}AskVest.Modules.Wallets{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"AskVest.Migrator{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var index = 0;
            const string marker = "AskVest.Modules.Wallets.";
            while ((index = text.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
            {
                var tail = text[(index + marker.Length)..];
                if (!(tail.StartsWith("Contracts", StringComparison.Ordinal)
                    || tail.StartsWith(';')
                    || tail.StartsWith("WalletsModuleExtensions", StringComparison.Ordinal)
                    || tail.StartsWith("WalletsEndpointsExtensions", StringComparison.Ordinal)))
                {
                    violations.Add($"{relative} references Wallets outside Contracts");
                    break;
                }

                index += marker.Length;
            }
        }

        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Assistant_Has_No_Wallets_Reference()
    {
        var assistant = typeof(AskVest.Modules.Assistant.AssistantModuleExtensions).Assembly;
        assistant.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ShouldNotContain("AskVest.Modules.Wallets");
        assistant.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("AskVest.", StringComparison.Ordinal))
            .ShouldAllBe(name => !name.Contains("Wallets", StringComparison.Ordinal));
    }

    [Fact]
    public void Wallets_Contains_No_Chain_Specific_Code()
    {
        var projectDirectory = WalletsProjectDirectory();
        var violations = WalletsBoundaryGuard.FindChainSpecificViolations(projectDirectory);
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Wallets_Guard_Catches_Synthetic_Chain_Violations()
    {
        WalletsBoundaryGuard.FindChainSpecificViolationsInText(
                "src/Modules/AskVest.Modules.Wallets/Services/Foo.cs",
                "var address = \"0x1234567890abcdef1234567890abcdef12345678\";")
            .ShouldNotBeEmpty();
        WalletsBoundaryGuard.FindChainSpecificViolationsInText(
                "src/Modules/AskVest.Modules.Wallets/Services/Foo.cs",
                "var family = ChainFamily.Evm;")
            .ShouldNotBeEmpty();
        WalletsBoundaryGuard.FindChainSpecificViolationsInText(
                "src/Modules/AskVest.Modules.Wallets/Services/Foo.cs",
                "var chainId = deposit.ChainId;")
            .ShouldBeEmpty();
        WalletsBoundaryGuard.FindChainSpecificViolationsInText(
                "src/Modules/AskVest.Modules.Wallets/Services/Foo.cs",
                "var address = request.Address;")
            .ShouldBeEmpty();
    }

    private static string WalletsProjectDirectory()
        => RepoDirectory(Path.Combine("src", "Modules", "AskVest.Modules.Wallets"));

    private static string RepoDirectory(string relative)
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", relative));

    private static IEnumerable<string> SourceFiles(string directory)
        => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}

internal static class WalletsBoundaryGuard
{
    private static readonly string[] ChainMarkers =
    [
        "ChainFamily",
        "EvmChain",
        "Nethereum",
        "IsValidEthereumAddress",
        "0x1234567890abcdef",
    ];

    private static readonly System.Text.RegularExpressions.Regex EvmAddressPattern = new(
        @"0x[0-9a-fA-F]{40}",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static IReadOnlyList<string> FindChainSpecificViolations(string directory)
    {
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = file.Replace('\\', '/');
            violations.AddRange(FindChainSpecificViolationsInText(relative, File.ReadAllText(file))
                .Select(v => $"{relative}: {v}"));
        }

        return violations;
    }

    public static IReadOnlyList<string> FindChainSpecificViolationsInText(string file, string text)
    {
        var violations = new List<string>();
        foreach (var marker in ChainMarkers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                violations.Add($"contains chain marker '{marker}'");
            }
        }

        if (EvmAddressPattern.IsMatch(text))
        {
            violations.Add("contains an EVM-style address literal");
        }

        return violations;
    }
}
