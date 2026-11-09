using AskVest.Modules.Catalog.Import;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Catalog;

public sealed class CatalogDataReaderTests
{
    [Fact]
    public void TryRead_Parses_The_Real_And_Sample_Data_Folders()
    {
        var root = RepoRoot();

        CatalogDataReader.TryRead(
                Path.Combine(root, "data", "catalog"), out var real, out var realProblems)
            .ShouldBeTrue(string.Join("; ", realProblems));
        real!.Chains.Chains.ShouldBeEmpty();
        real.Issuers.Issuers.ShouldBeEmpty();
        real.Instruments.Instruments.ShouldBeEmpty();

        CatalogDataReader.TryRead(
                Path.Combine(root, "data", "catalog-sample"), out var sample, out var sampleProblems)
            .ShouldBeTrue(string.Join("; ", sampleProblems));
        sample!.Chains.Chains.Count.ShouldBe(2);
        sample.Issuers.Issuers.Count.ShouldBe(2);
        sample.Instruments.Instruments.Count.ShouldBe(4);
        sample.Instruments.Instruments.Sum(i => i.Tokens.Count).ShouldBe(9);
    }

    [Fact]
    public void TryRead_Rejects_Unknown_Properties_And_Missing_Files()
    {
        var directory = Path.Combine(Path.GetTempPath(), "askvest-reader-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "chains.json"), """{"version":"t","chains":[],"extra":1}""");
        File.WriteAllText(Path.Combine(directory, "issuers.json"), """{"version":"t","issuers":[]}""");

        CatalogDataReader.TryRead(directory, out _, out var problems).ShouldBeFalse();
        problems.ShouldContain(p => p.Contains("unmapped") || p.Contains("chains.json"));
        problems.ShouldContain(p => p.Contains("instruments.json"));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AskVest.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
