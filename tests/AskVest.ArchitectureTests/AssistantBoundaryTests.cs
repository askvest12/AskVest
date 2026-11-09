using System.Reflection;
using Shouldly;
using Xunit;

namespace AskVest.ArchitectureTests;

public sealed class AssistantModuleBoundaryTests
{
    private static readonly Assembly Assistant = typeof(AskVest.Modules.Assistant.AssistantModuleExtensions).Assembly;

    [Fact]
    public void Assistant_References_Only_Allowed_AskVest_Assemblies()
    {
        var askVestReferences = Assistant.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("AskVest.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        askVestReferences.ShouldBeSubsetOf(
            [
                "AskVest.SharedKernel",
                "AskVest.Platform",
                "AskVest.Modules.Catalog",
                "AskVest.Modules.MarketData",
                "AskVest.Modules.Portfolio",
                "AskVest.Modules.Learning",
                "AskVest.Modules.Compliance",
                "AskVest.Modules.Identity",
                "AskVest.Modules.Ledger",
            ]);
        Assistant.GetReferencedAssemblies()
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
    public void Assistant_Public_Surface_Stays_Within_Module_Namespaces()
    {
        var publicTypes = Assistant.GetExportedTypes()
            .Where(t => t.Namespace?.StartsWith("AskVest.Modules.Assistant", StringComparison.Ordinal) == true)
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
            || name.EndsWith("AssistantModuleExtensions")
            || name.EndsWith("AssistantEndpointsExtensions"));
    }

    [Theory]
    [InlineData("AskVest.Modules.Catalog.")]
    [InlineData("AskVest.Modules.MarketData.")]
    [InlineData("AskVest.Modules.Portfolio.")]
    [InlineData("AskVest.Modules.Learning.")]
    [InlineData("AskVest.Modules.Compliance.")]
    [InlineData("AskVest.Modules.Identity.")]
    [InlineData("AskVest.Modules.Ledger.")]
    public void Assistant_Uses_Other_Modules_Only_Through_Contracts(string marker)
    {
        var violations = AssistantBoundaryGuard.FindContractViolations(AssistantProjectDirectory(), marker)
            .ToList();
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Assistant_Never_References_Ledger_Or_Practice()
    {
        var violations = AssistantBoundaryGuard.FindWritePathViolations(AssistantProjectDirectory())
            .ToList();
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Assistant_Makes_No_Outbound_Calls_Outside_The_Model_Client()
    {
        var violations = AssistantBoundaryGuard.FindOutboundViolations(AssistantProjectDirectory())
            .ToList();
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Every_Assistant_Route_Carries_The_Assistant_Policy()
    {
        var endpointsDirectory = Path.Combine(AssistantProjectDirectory(), "Endpoints");
        var violations = new List<string>();
        if (Directory.Exists(endpointsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(endpointsDirectory, "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                var hasRoute = text.Contains("MapPost(", StringComparison.Ordinal)
                    || text.Contains("MapGet(", StringComparison.Ordinal)
                    || text.Contains("MapDelete(", StringComparison.Ordinal)
                    || text.Contains("MapPut(", StringComparison.Ordinal);
                if (hasRoute && !text.Contains("CompliancePolicyNames.Assistant", StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetFileName(file)} maps routes without the Assistant policy");
                }
            }
        }

        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void No_Code_Outside_Assistant_References_Assistant_Internals()
    {
        var srcDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        var violations = AssistantBoundaryGuard.FindExternalReferenceViolations(srcDirectory)
            .Where(v => !v.StartsWith("AskVest.Migrator.", StringComparison.Ordinal))
            .ToList();
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Guard_Catches_Synthetic_Violations()
    {
        AssistantBoundaryGuard.IsAllowedAssistantReference("Contracts.ICatalog").ShouldBeTrue();
        AssistantBoundaryGuard.IsAllowedAssistantReference("Persistence.AssistantDbContext").ShouldBeFalse();
        AssistantBoundaryGuard.FindWritePathViolationsInText(
                "src/Modules/AskVest.Modules.Assistant/Tools/Foo.cs",
                "await ledger.PostJournalAsync(request);")
            .ShouldNotBeEmpty();
        AssistantBoundaryGuard.FindWritePathViolationsInText(
                "src/Modules/AskVest.Modules.Assistant/Tools/Foo.cs",
                "await practice.PlaceOrderAsync(request);")
            .ShouldNotBeEmpty();
        AssistantBoundaryGuard.FindWritePathViolationsInText(
                "src/Modules/AskVest.Modules.Assistant/Tools/Foo.cs",
                "var portfolio = await portfolio.GetPortfolioAsync(userId, Book.Practice);")
            .ShouldBeEmpty();
        AssistantBoundaryGuard.FindOutboundViolationsInText(
                "src/Modules/AskVest.Modules.Assistant/Tools/Foo.cs",
                "var client = new HttpClient();")
            .ShouldNotBeEmpty();
        AssistantBoundaryGuard.FindOutboundViolationsInText(
                "src/Modules/AskVest.Modules.Assistant/Tools/Foo.cs",
                "var url = \"https://example.com\";")
            .ShouldBeEmpty();
    }

    private static string AssistantProjectDirectory()
    {
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Modules", "AskVest.Modules.Assistant"));
    }
}

internal static class AssistantBoundaryGuard
{
    private const string Marker = "AskVest.Modules.Assistant.";

    private static readonly string[] ForbiddenModuleMarkers =
        ["AskVest.Modules.Practice.", "AskVest.Modules.Trading.", "AskVest.Modules.Funding.", "AskVest.Modules.Wallets."];

    private static readonly string[] WriteMethodMarkers =
        [
            "PostJournalAsync",
            "ReverseJournalAsync",
            "RegisterAssetAsync",
            "PlaceOrderAsync",
            "PlaceAsync",
            "CancelOrderAsync",
            "CancelAsync",
            "TriggerStopAsync",
            "AddWatchlistItemAsync",
            "RemoveWatchlistItemAsync",
            "SubmitAttemptAsync",
            "CompleteLessonAsync",
            "RecordConsentAsync",
            "PostAsync",
            "WithdrawAsync",
            "DepositAsync",
            "ExecuteQuoteAsync",
        ];

    private static readonly string[] OutboundMarkers =
        [
            "HttpClient",
            "HttpRequestMessage",
            "WebClient",
            "SocketsHttpHandler",
            "System.Net.Http",
            "System.Net.Sockets",
            "System.Net.WebRequest",
            "DownloadStringAsync",
            "DownloadDataAsync",
            "GetStreamAsync",
        ];

    public static bool IsAllowedAssistantReference(string tail)
    {
        return tail.StartsWith("Contracts", StringComparison.Ordinal)
            || tail.StartsWith(';')
            || tail.StartsWith("AssistantModuleExtensions", StringComparison.Ordinal)
            || tail.StartsWith("AssistantEndpointsExtensions", StringComparison.Ordinal);
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

    public static IReadOnlyList<string> FindWritePathViolations(string directory)
    {
        var violations = new List<string>();
        foreach (var file in SourceFiles(directory))
        {
            var relative = file.Replace('\\', '/');
            var text = File.ReadAllText(file);
            foreach (var marker in ForbiddenModuleMarkers)
            {
                if (text.Contains(marker, StringComparison.Ordinal))
                {
                    violations.Add($"{relative} references forbidden module {marker}");
                }
            }

            foreach (var method in WriteMethodMarkers)
            {
                if (text.Contains(method, StringComparison.Ordinal))
                {
                    violations.Add($"{relative} references write method {method}");
                }
            }
        }

        return violations;
    }

    public static IReadOnlyList<string> FindWritePathViolationsInText(string file, string text)
    {
        var violations = new List<string>();
        foreach (var marker in ForbiddenModuleMarkers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                violations.Add($"{file} references forbidden module {marker}");
            }
        }

        foreach (var method in WriteMethodMarkers)
        {
            if (text.Contains(method, StringComparison.Ordinal))
            {
                violations.Add($"{file} references write method {method}");
            }
        }

        return violations;
    }

    public static IReadOnlyList<string> FindOutboundViolations(string directory)
    {
        var violations = new List<string>();
        foreach (var file in SourceFiles(directory))
        {
            var relative = file.Replace('\\', '/');
            violations.AddRange(FindOutboundViolationsInText(relative, File.ReadAllText(file))
                .Select(v => $"{relative}: {v}"));
        }

        return violations;
    }

    public static IReadOnlyList<string> FindOutboundViolationsInText(string file, string text)
    {
        var violations = new List<string>();
        foreach (var marker in OutboundMarkers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                violations.Add($"{file} mentions outbound marker {marker}");
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
            if (relative.StartsWith($"Modules{Path.DirectorySeparatorChar}AskVest.Modules.Assistant{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"AskVest.Migrator{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var index = 0;
            while ((index = text.IndexOf(Marker, index, StringComparison.Ordinal)) >= 0)
            {
                var tail = text[(index + Marker.Length)..];
                if (!IsAllowedAssistantReference(tail))
                {
                    violations.Add($"{relative} references Assistant outside Contracts");
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
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
