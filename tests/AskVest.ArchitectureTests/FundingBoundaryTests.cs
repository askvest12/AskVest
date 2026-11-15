using System.Reflection;
using Shouldly;
using Xunit;

namespace AskVest.ArchitectureTests;

public sealed class FundingModuleBoundaryTests
{
    private static readonly Assembly Funding = typeof(AskVest.Modules.Funding.FundingModuleExtensions).Assembly;

    [Fact]
    public void Funding_References_Only_Allowed_AskVest_Assemblies()
    {
        var askVestReferences = Funding.GetReferencedAssemblies()
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
            ]);
        Funding.GetReferencedAssemblies()
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
    public void Funding_Public_Surface_Stays_Within_Module_Namespaces()
    {
        var publicTypes = Funding.GetExportedTypes()
            .Where(t => t.Namespace?.StartsWith("AskVest.Modules.Funding", StringComparison.Ordinal) == true)
            .Select(t => t.FullName!)
            .ToList();
        publicTypes.ShouldAllBe(name =>
            name.Contains(".Contracts.", StringComparison.Ordinal)
            || name.Contains(".Persistence.", StringComparison.Ordinal)
            || name.Contains(".Domain.", StringComparison.Ordinal)
            || name.Contains(".Configuration.", StringComparison.Ordinal)
            || name.Contains(".Migrations.", StringComparison.Ordinal)
            || name.Contains(".Providers.", StringComparison.Ordinal)
            || name.Contains(".Services.", StringComparison.Ordinal)
            || name.Contains(".Endpoints.", StringComparison.Ordinal)
            || name.Contains(".Application.", StringComparison.Ordinal)
            || name.Contains(".Infrastructure.", StringComparison.Ordinal)
            || name.EndsWith("FundingModuleExtensions")
            || name.EndsWith("FundingEndpointsExtensions"));
    }

    [Theory]
    [InlineData("AskVest.Modules.Ledger.")]
    [InlineData("AskVest.Modules.Compliance.")]
    [InlineData("AskVest.Modules.Identity.")]
    public void Funding_Uses_Other_Modules_Only_Through_Contracts(string marker)
    {
        var projectDirectory = FundingProjectDirectory();
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
    public void No_Code_Outside_Funding_References_Funding_Internals()
    {
        var srcDirectory = RepoDirectory("src");
        var violations = new List<string>();
        foreach (var file in SourceFiles(srcDirectory))
        {
            var relative = Path.GetRelativePath(srcDirectory, file);
            if (relative.StartsWith($"Modules{Path.DirectorySeparatorChar}AskVest.Modules.Funding{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"AskVest.Migrator{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var index = 0;
            const string marker = "AskVest.Modules.Funding.";
            while ((index = text.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
            {
                var tail = text[(index + marker.Length)..];
                if (!(tail.StartsWith("Contracts", StringComparison.Ordinal)
                    || tail.StartsWith(';')
                    || tail.StartsWith("FundingModuleExtensions", StringComparison.Ordinal)
                    || tail.StartsWith("FundingEndpointsExtensions", StringComparison.Ordinal)))
                {
                    violations.Add($"{relative} references Funding outside Contracts");
                    break;
                }

                index += marker.Length;
            }
        }

        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Assistant_Has_No_Funding_Reference()
    {
        var assistant = typeof(AskVest.Modules.Assistant.AssistantModuleExtensions).Assembly;
        assistant.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ShouldNotContain("AskVest.Modules.Funding");
        assistant.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("AskVest.", StringComparison.Ordinal))
            .ShouldAllBe(name => !name.Contains("Funding", StringComparison.Ordinal));
    }

    [Fact]
    public void Funding_Contains_No_Vendor_Country_Or_Chain_Specific_Code()
    {
        var projectDirectory = FundingProjectDirectory();
        var violations = FundingBoundaryGuard.FindSpecificViolations(projectDirectory);
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Funding_Guard_Catches_Synthetic_Violations()
    {
        FundingBoundaryGuard.FindSpecificViolationsInText(
                "src/Modules/AskVest.Modules.Funding/Services/Foo.cs",
                "var rail = \"paystack_transfer\";")
            .ShouldNotBeEmpty();
        FundingBoundaryGuard.FindSpecificViolationsInText(
                "src/Modules/AskVest.Modules.Funding/Services/Foo.cs",
                "var country = \"NG\";")
            .ShouldNotBeEmpty();
        FundingBoundaryGuard.FindSpecificViolationsInText(
                "src/Modules/AskVest.Modules.Funding/Services/Foo.cs",
                "var family = ChainFamily.Evm;")
            .ShouldNotBeEmpty();
        FundingBoundaryGuard.FindSpecificViolationsInText(
                "src/Modules/AskVest.Modules.Funding/Services/Foo.cs",
                "var method = methodOption.Currency;")
            .ShouldBeEmpty();
        FundingBoundaryGuard.FindSpecificViolationsInText(
                "src/Modules/AskVest.Modules.Funding/Services/Foo.cs",
                "var provider = fundingProvider;")
            .ShouldBeEmpty();
    }

    private static string FundingProjectDirectory()
        => RepoDirectory(Path.Combine("src", "Modules", "AskVest.Modules.Funding"));

    private static string RepoDirectory(string relative)
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", relative));

    private static IEnumerable<string> SourceFiles(string directory)
        => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}

internal static class FundingBoundaryGuard
{
    private static readonly string[] VendorMarkers =
    [
        "paystack",
        "flutterwave",
        "stripe",
        "checkout.com",
        "dlocal",
        "cellulant",
        "interswitch",
        "mtn",
        "orange money",
        "mpesa",
        "pegasus",
    ];

    private static readonly string[] ChainMarkers =
    [
        "ChainFamily",
        "EvmChain",
        "Nethereum",
        "IsValidEthereumAddress",
        "0x1234567890abcdef",
    ];

    private static readonly System.Text.RegularExpressions.Regex CountryLiteralPattern = new(
        @"""[A-Z]{2}""",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex EvmAddressPattern = new(
        @"0x[0-9a-fA-F]{40}",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static IReadOnlyList<string> FindSpecificViolations(string directory)
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
            violations.AddRange(FindSpecificViolationsInText(relative, File.ReadAllText(file))
                .Select(v => $"{relative}: {v}"));
        }

        return violations;
    }

    public static IReadOnlyList<string> FindSpecificViolationsInText(string file, string text)
    {
        var violations = new List<string>();
        var lowered = text.ToLowerInvariant();
        foreach (var marker in VendorMarkers)
        {
            if (lowered.Contains(marker, StringComparison.Ordinal))
            {
                violations.Add($"contains vendor marker '{marker}'");
            }
        }

        foreach (var marker in ChainMarkers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                violations.Add($"contains chain marker '{marker}'");
            }
        }

        if (CountryLiteralPattern.IsMatch(text))
        {
            violations.Add("contains an ISO country code string literal");
        }

        if (EvmAddressPattern.IsMatch(text))
        {
            violations.Add("contains an EVM-style address literal");
        }

        return violations;
    }
}
