using System.Reflection;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace AskVest.ArchitectureTests;

public sealed class LedgerModuleBoundaryTests
{
    private static readonly Assembly Ledger = typeof(AskVest.Modules.Ledger.LedgerModuleExtensions).Assembly;

    [Fact]
    public void Ledger_References_Only_SharedKernel_Platform_And_Module_Contracts()
    {
        var askVestReferences = Ledger.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("AskVest.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        askVestReferences.ShouldBeSubsetOf(
            ["AskVest.SharedKernel", "AskVest.Platform", "AskVest.Modules.Identity", "AskVest.Modules.Compliance"]);
        Ledger.GetReferencedAssemblies()
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
    public void Ledger_Public_Surface_Stays_Within_Module_Namespaces()
    {
        var publicTypes = Ledger.GetExportedTypes()
            .Where(t => t.Namespace?.StartsWith("AskVest.Modules.Ledger", StringComparison.Ordinal) == true)
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
            || name.EndsWith("LedgerModuleExtensions")
            || name.EndsWith("LedgerEndpointsExtensions"));
    }

    [Fact]
    public void Ledger_Uses_Other_Modules_Only_Through_Contracts()
    {
        var projectDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Modules", "AskVest.Modules.Ledger"));
        var violations = LedgerBoundaryGuard.FindContractViolations(projectDirectory, "AskVest.Modules.Identity.")
            .Concat(LedgerBoundaryGuard.FindContractViolations(projectDirectory, "AskVest.Modules.Compliance."))
            .ToList();
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void No_Code_Outside_Ledger_References_Ledger_Internals()
    {
        var srcDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        var violations = LedgerBoundaryGuard.FindExternalReferenceViolations(srcDirectory)
            .Where(v => !v.StartsWith("AskVest.Migrator.", StringComparison.Ordinal))
            .ToList();
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Guard_Catches_A_Synthetic_Ledger_Internal_Reference()
    {
        const string violating = "var db = services.AddDbContext<AskVest.Modules.Ledger.Persistence.LedgerDbContext>();";
        LedgerBoundaryGuard.IsAllowedLedgerReference("Persistence.LedgerDbContext>();").ShouldBeFalse();
        LedgerBoundaryGuard.IsAllowedLedgerReference("Contracts.ILedger").ShouldBeTrue();
        LedgerBoundaryGuard.IsAllowedLedgerReference(";").ShouldBeTrue();
        LedgerBoundaryGuard.ExtractReferenceTail(violating).ShouldBe("Persistence.LedgerDbContext>();");
    }
}

public sealed class LedgerHygieneGuardTests
{
    [Fact]
    public void Outbox_Payloads_Carry_Ids_Only()
    {
        var srcDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        var violations = LedgerBoundaryGuard.FindOutboxPayloadViolations(srcDirectory);
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Log_Statements_Never_Carry_Amounts_Or_Balances()
    {
        var srcDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        var violations = LedgerBoundaryGuard.FindLogAmountViolations(srcDirectory);
        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void Hygiene_Guard_Catches_Synthetic_Violations()
    {
        LedgerBoundaryGuard.FindOutboxPayloadViolationsInText(
                "public sealed record JournalPostedPayload(Guid JournalId, decimal Amount, string AccountId);")
            .ShouldNotBeEmpty();
        LedgerBoundaryGuard.FindOutboxPayloadViolationsInText(
                "public sealed record JournalPostedPayload(Guid JournalId, string Book, string ReasonCode);")
            .ShouldBeEmpty();
        LedgerBoundaryGuard.FindLogAmountViolationsInText(
                "_logger.LogInformation(\"Posted journal {JournalId} amount {Amount}\", journalId, amount);")
            .ShouldNotBeEmpty();
        LedgerBoundaryGuard.FindLogAmountViolationsInText(
                "_logger.LogInformation(\"Posted journal {JournalId}\", journalId);")
            .ShouldBeEmpty();
    }
}

internal static class LedgerBoundaryGuard
{
    private const string Marker = "AskVest.Modules.Ledger.";

    private static readonly Regex LogCall = new(
        @"\.Log(Trace|Debug|Information|Warning|Error|Critical)\s*\(|\bLog\.(Trace|Debug|Information|Warning|Error|Critical)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex BannedLogTerm = new(
        @"\b(amounts?|balances?|account[_\s]?ids?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BannedPayloadMember = new(
        @"\b[Aa]mounts?\b|\b[Bb]alances?\b",
        RegexOptions.Compiled);

    public static bool IsAllowedLedgerReference(string tail)
    {
        return tail.StartsWith("Contracts", StringComparison.Ordinal)
            || tail.StartsWith(';')
            || tail.StartsWith("LedgerModuleExtensions", StringComparison.Ordinal)
            || tail.StartsWith("LedgerEndpointsExtensions", StringComparison.Ordinal);
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
            if (relative.StartsWith($"Modules{Path.DirectorySeparatorChar}AskVest.Modules.Ledger{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"AskVest.Migrator{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var index = 0;
            while ((index = text.IndexOf(Marker, index, StringComparison.Ordinal)) >= 0)
            {
                var tail = text[(index + Marker.Length)..];
                if (!IsAllowedLedgerReference(tail))
                {
                    violations.Add($"{relative} references Ledger outside Contracts");
                    break;
                }

                index += Marker.Length;
            }
        }

        return violations;
    }

    public static IReadOnlyList<string> FindOutboxPayloadViolations(string directory)
    {
        var violations = new List<string>();
        foreach (var file in SourceFiles(directory))
        {
            var relative = Path.GetRelativePath(directory, file);
            if (!relative.Contains("Outbox", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            violations.AddRange(FindOutboxPayloadViolationsInText(File.ReadAllText(file))
                .Select(v => $"{relative}: {v}"));
        }

        return violations;
    }

    public static IReadOnlyList<string> FindOutboxPayloadViolationsInText(string text)
    {
        var violations = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*'))
            {
                continue;
            }

            var hit = BannedPayloadMember.Match(line);
            if (hit.Success)
            {
                violations.Add($"payload member '{hit.Value}'");
            }
        }

        return violations;
    }

    public static IReadOnlyList<string> FindLogAmountViolations(string directory)
    {
        var violations = new List<string>();
        foreach (var file in SourceFiles(directory))
        {
            var relative = Path.GetRelativePath(directory, file);
            violations.AddRange(FindLogAmountViolationsInText(File.ReadAllText(file))
                .Select(v => $"{relative}: {v}"));
        }

        return violations;
    }

    public static IReadOnlyList<string> FindLogAmountViolationsInText(string text)
    {
        var violations = new List<string>();
        foreach (Match match in LogCall.Matches(text))
        {
            var end = text.IndexOf(';', match.Index);
            var statement = end < 0 ? text[match.Index..] : text[match.Index..end];
            var hit = BannedLogTerm.Match(statement);
            if (hit.Success)
            {
                violations.Add($"log statement names '{hit.Value}'");
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
