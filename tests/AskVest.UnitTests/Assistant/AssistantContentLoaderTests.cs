using AskVest.Modules.Assistant.Domain;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Assistant;

public sealed class AssistantContentLoaderTests
{
    [Fact]
    public void Shipped_Prompt_And_Evaluation_Set_Load()
    {
        var root = RepositoryRoot();
        var promptPath = Path.Combine(root, "src", "Modules", "AskVest.Modules.Assistant", "Content", "system-prompt.md");
        var (text, version) = AssistantContentLoader.LoadPrompt(promptPath, "prompt-1");
        version.ShouldBe("prompt-1");
        text.ShouldContain("research assistant");

        var cases = AssistantContentLoader.LoadEvaluationSet(
            Path.Combine(root, "src", "Modules", "AskVest.Modules.Assistant", "Content", "evaluation-set.json"));
        cases.ShouldNotBeEmpty();
        cases.Select(evaluationCase => evaluationCase.Id).Distinct().Count().ShouldBe(cases.Count);
    }

    [Fact]
    public void Missing_Prompt_File_Stops_Startup()
    {
        Should.Throw<InvalidOperationException>(
            () => AssistantContentLoader.LoadPrompt("/nonexistent/prompt.md", "prompt-1"));
    }

    [Fact]
    public void Prompt_Without_Version_Line_Stops_Startup()
    {
        var path = WriteTemp("prompt.md", "no version here\nbody\n");
        try
        {
            Should.Throw<InvalidOperationException>(() => AssistantContentLoader.LoadPrompt(path, "prompt-1"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Prompt_Version_Mismatch_Stops_Startup()
    {
        var path = WriteTemp("prompt.md", "version: other-9\nbody\n");
        try
        {
            var exception = Should.Throw<InvalidOperationException>(
                () => AssistantContentLoader.LoadPrompt(path, "prompt-1"));
            exception.Message.ShouldContain("other-9");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Evaluation_Set_With_Duplicate_Ids_Stops_Startup()
    {
        var path = WriteTemp(
            "eval.json",
            """{"version":1,"cases":[{"id":"a","message":"m","checks":["x"]},{"id":"a","message":"m2","checks":["x"]}]}""");
        try
        {
            Should.Throw<InvalidOperationException>(() => AssistantContentLoader.LoadEvaluationSet(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Evaluation_Set_With_No_Checks_Stops_Startup()
    {
        var path = WriteTemp(
            "eval.json",
            """{"version":1,"cases":[{"id":"a","message":"m","checks":[]}]}""");
        try
        {
            Should.Throw<InvalidOperationException>(() => AssistantContentLoader.LoadEvaluationSet(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTemp(string name, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"assistant-{Guid.CreateVersion7():N}-{name}");
        File.WriteAllText(path, content);
        return path;
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
}
