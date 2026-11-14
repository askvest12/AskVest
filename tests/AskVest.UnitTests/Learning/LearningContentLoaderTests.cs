using AskVest.Modules.Learning.Configuration;
using AskVest.Modules.Learning.Content;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Learning;

public sealed class LearningContentLoaderTests
{
    [Fact]
    public void Real_And_Sample_Folders_Pass_Validation()
    {
        var root = RepositoryRoot();
        var options = new LearningOptions
        {
            ContentPath = Path.Combine(root, "data", "learning"),
            SampleContentPath = Path.Combine(root, "data", "learning-sample"),
            LoadSampleContent = true,
        };
        var snapshot = LearningContentLoader.Load(options, new TestEnvironment("Development"));
        snapshot.Tracks.ShouldNotBeEmpty();
        snapshot.RequiredLessons().ShouldNotBeEmpty();
    }

    [Fact]
    public void Sample_Content_Is_Refused_In_Production()
    {
        var root = RepositoryRoot();
        var options = new LearningOptions
        {
            ContentPath = Path.Combine(root, "data", "learning"),
            SampleContentPath = Path.Combine(root, "data", "learning-sample"),
            LoadSampleContent = true,
        };
        var exception = Should.Throw<InvalidOperationException>(
            () => LearningContentLoader.Load(options, new TestEnvironment("Production")));
        exception.Message.ShouldContain("Production");
    }

    [Fact]
    public void Missing_Content_File_Stops_Load_With_The_Problem()
    {
        var options = new LearningOptions { ContentPath = Path.Combine(Path.GetTempPath(), Guid.CreateVersion7().ToString("N")) };
        var exception = Should.Throw<InvalidOperationException>(
            () => LearningContentLoader.Load(options, new TestEnvironment("Development")));
        exception.Message.ShouldContain("content.json");
    }

    [Fact]
    public void Invalid_Content_Stops_Load_Listing_All_Problems()
    {
        var directory = Directory.CreateTempSubdirectory("learning-content-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "content.json"), """
                {"tracks":[{"id":"T1","title":"x","summary":"y","lessons":[]}]}
                """);
            var options = new LearningOptions { ContentPath = directory };
            var exception = Should.Throw<InvalidOperationException>(
                () => LearningContentLoader.Load(options, new TestEnvironment("Development")));
            exception.Message.ShouldContain("id must be lowercase");
            exception.Message.ShouldContain("at least one lesson");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Banned_Phrase_In_Custom_List_Is_Enforced()
    {
        var directory = Directory.CreateTempSubdirectory("learning-content-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "content.json"), """
                {"tracks":[{"id":"t","title":"T","summary":"S","lessons":[{"id":"l","title":"L","summary":"S","estimatedMinutes":2,"version":1,"requiredForReal":false,"blocks":[{"type":"paragraph","text":"buy low"}],"quiz":null}]}]}
                """);
            var options = new LearningOptions
            {
                ContentPath = directory,
                BannedPhrases = { "buy low" },
            };
            var exception = Should.Throw<InvalidOperationException>(
                () => LearningContentLoader.Load(options, new TestEnvironment("Development")));
            exception.Message.ShouldContain("buy low");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("non-custodial")]
    [InlineData("self-custody")]
    [InlineData("self custody")]
    [InlineData("your keys")]
    public void Custody_Phrases_Are_In_The_Default_Banned_List(string phrase)
    {
        LearningContentLoader.DefaultBannedPhrases.ShouldContain(phrase);
    }

    [Fact]
    public void Default_Banned_Phrases_Are_Enforced_Without_Custom_List()
    {
        var directory = Directory.CreateTempSubdirectory("learning-content-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "content.json"), """
                {"tracks":[{"id":"t","title":"T","summary":"S","lessons":[{"id":"l","title":"L","summary":"S","estimatedMinutes":2,"version":1,"requiredForReal":false,"blocks":[{"type":"paragraph","text":"This is self-custody."}],"quiz":null}]}]}
                """);
            var options = new LearningOptions { ContentPath = directory };
            var exception = Should.Throw<InvalidOperationException>(
                () => LearningContentLoader.Load(options, new TestEnvironment("Development")));
            exception.Message.ShouldContain("self-custody");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AskVest.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "AskVest.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
