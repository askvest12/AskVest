using System.Reflection;
using System.Text.RegularExpressions;
using AskVest.SharedKernel;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests;

public sealed class ErrorCodesTests
{
    private static readonly Regex CodePattern = new(
        @"^[a-z]+(_[a-z]+)*(\.[a-z]+(_[a-z]+)*)+$",
        RegexOptions.Compiled);

    private static IEnumerable<(string Name, string Code)> AllCodes()
    {
        foreach (var field in typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is string code)
            {
                yield return (field.Name, code);
            }
        }
    }

    [Fact]
    public void EveryCode_MatchesPattern()
    {
        foreach (var (name, code) in AllCodes())
        {
            CodePattern.IsMatch(code).ShouldBeTrue($"{name} = '{code}' does not match the error code pattern.");
        }
    }

    [Fact]
    public void EveryCode_IsUnique()
    {
        var codes = AllCodes().Select(x => x.Code).ToList();
        codes.Distinct().Count().ShouldBe(codes.Count);
        codes.ShouldAllBe(code => code.Equals(code.ToLowerInvariant(), StringComparison.Ordinal));
    }

    [Fact]
    public void EveryCode_AppearsInApiStandardsDoc()
    {
        var docPath = FindRepoFile("docs/design/01-api-standards.md");
        var doc = File.ReadAllText(docPath);
        var documented = Regex.Matches(doc, @"^\| `([a-z_.]+)` \|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        foreach (var (_, code) in AllCodes())
        {
            documented.ShouldContain(code);
        }
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relativePath} by walking up from {AppContext.BaseDirectory}.");
    }
}
