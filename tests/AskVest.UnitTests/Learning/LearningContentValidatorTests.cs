using AskVest.Modules.Learning.Content;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Learning;

public sealed class LearningContentValidatorTests
{
    [Fact]
    public void Valid_Content_Has_No_Problems()
    {
        LearningContentValidator.Validate(Sample(), Banned()).ShouldBeEmpty();
    }

    [Fact]
    public void Empty_Content_Is_Allowed()
    {
        LearningContentValidator.Validate(new ContentFile([]), Banned()).ShouldBeEmpty();
    }

    [Fact]
    public void Duplicate_Lesson_Id_Across_Tracks_Is_Reported()
    {
        var content = new ContentFile(
        [
            Track("t1", Lesson("same")),
            Track("t2", Lesson("same")),
        ]);
        LearningContentValidator.Validate(content, Banned())
            .ShouldContain(problem => problem.Contains("duplicate lesson id 'same'"));
    }

    [Fact]
    public void Duplicate_Track_Id_Is_Reported()
    {
        var content = new ContentFile([Track("t1", Lesson("l1")), Track("t1", Lesson("l2"))]);
        LearningContentValidator.Validate(content, Banned())
            .ShouldContain(problem => problem.Contains("duplicate track id 't1'"));
    }

    [Fact]
    public void Duplicate_Option_Id_Is_Reported()
    {
        var lesson = Lesson("l1", quiz: Quiz(
            Question("q1", [Option("a", "one"), Option("a", "two")], "a", "why")));
        var problems = LearningContentValidator.Validate(Content(lesson), Banned());
        problems.ShouldContain(problem => problem.Contains("duplicate option id 'a'"));
    }

    [Fact]
    public void Question_Without_Matching_Correct_Option_Is_Reported()
    {
        var lesson = Lesson("l1", quiz: Quiz(
            Question("q1", [Option("a", "one"), Option("b", "two")], "zzz", "why")));
        var problems = LearningContentValidator.Validate(Content(lesson), Banned());
        problems.ShouldContain(problem => problem.Contains("correctOptionId 'zzz' does not name an option"));
    }

    [Fact]
    public void Missing_Explanation_Is_Reported()
    {
        var lesson = Lesson("l1", quiz: Quiz(
            Question("q1", [Option("a", "one"), Option("b", "two")], "a", " ")));
        var problems = LearningContentValidator.Validate(Content(lesson), Banned());
        problems.ShouldContain(problem => problem.Contains("explanation: text is required"));
    }

    [Fact]
    public void Single_Option_Question_Is_Reported()
    {
        var lesson = Lesson("l1", quiz: Quiz(
            Question("q1", [Option("a", "only")], "a", "why")));
        var problems = LearningContentValidator.Validate(Content(lesson), Banned());
        problems.ShouldContain(problem => problem.Contains("between 2 and 5 options"));
    }

    [Fact]
    public void Required_Lesson_Without_Quiz_Is_Reported()
    {
        var lesson = Lesson("l1", requiredForReal: true, includeQuiz: false);
        var problems = LearningContentValidator.Validate(Content(lesson), Banned());
        problems.ShouldContain(problem => problem.Contains("requiredForReal lesson must have a quiz"));
    }

    [Fact]
    public void Banned_Phrase_Is_Reported_With_Location()
    {
        var lesson = Lesson("l1", summary: "This stock is guaranteed to rise.");
        var problems = LearningContentValidator.Validate(Content(lesson), Banned());
        problems.ShouldContain(problem =>
            problem.Contains("lesson 'l1' summary") && problem.Contains("banned phrase 'guaranteed'"));
    }

    [Fact]
    public void Over_Long_Text_Is_Reported()
    {
        var lesson = Lesson("l1", title: new string('x', 121));
        var problems = LearningContentValidator.Validate(Content(lesson), Banned());
        problems.ShouldContain(problem => problem.Contains("title: text is longer than 120"));
    }

    [Fact]
    public void Bad_Pass_Mark_Is_Reported()
    {
        var lesson = Lesson("l1", quiz: new QuizContent(20, Quiz().Questions));
        var problems = LearningContentValidator.Validate(Content(lesson), Banned());
        problems.ShouldContain(problem => problem.Contains("passMarkPercent must be between 50 and 100"));
    }

    [Fact]
    public void Missing_Pass_Mark_Uses_Default_And_Passes()
    {
        var lesson = Lesson("l1", quiz: new QuizContent(null, Quiz().Questions));
        LearningContentValidator.Validate(Content(lesson), Banned()).ShouldBeEmpty();
        LearningContentValidator.EffectivePassMark(lesson.Quiz!).ShouldBe(80);
    }

    [Fact]
    public void Unknown_Block_Type_Is_Reported()
    {
        var lesson = Lesson("l1", blocks: [new BlockContent("video", "text", null, null)]);
        var problems = LearningContentValidator.Validate(Content(lesson), Banned());
        problems.ShouldContain(problem => problem.Contains("unknown block type 'video'"));
    }

    [Fact]
    public void Paragraph_With_Items_Is_Reported()
    {
        var lesson = Lesson("l1", blocks:
            [new BlockContent("paragraph", "text", ["item"], null)]);
        var problems = LearningContentValidator.Validate(Content(lesson), Banned());
        problems.ShouldContain(problem => problem.Contains("items: not allowed for this block shape"));
    }

    [Fact]
    public void Callout_With_Bad_Kind_Is_Reported()
    {
        var lesson = Lesson("l1", blocks:
            [new BlockContent("callout", "text", null, "danger")]);
        var problems = LearningContentValidator.Validate(Content(lesson), Banned());
        problems.ShouldContain(problem => problem.Contains("callout kind must be one of"));
    }

    [Fact]
    public void Bullets_Without_Items_Are_Reported()
    {
        var lesson = Lesson("l1", blocks: [new BlockContent("bullets", null, [], null)]);
        var problems = LearningContentValidator.Validate(Content(lesson), Banned());
        problems.ShouldContain(problem => problem.Contains("bullets need at least one item"));
    }

    [Fact]
    public void Bad_Id_Shape_Is_Reported()
    {
        var lesson = Lesson("Bad_Id!");
        var problems = LearningContentValidator.Validate(Content(lesson), Banned());
        problems.ShouldContain(problem => problem.Contains("id must be lowercase"));
    }

    [Fact]
    public void Bad_Version_And_Minutes_Are_Reported()
    {
        var lesson = Lesson("l1", estimatedMinutes: 0, version: 0);
        var problems = LearningContentValidator.Validate(Content(lesson), Banned());
        problems.ShouldContain(problem => problem.Contains("estimatedMinutes"));
        problems.ShouldContain(problem => problem.Contains("version must be at least 1"));
    }

    [Fact]
    public void All_Problems_Are_Reported_Together()
    {
        var lesson = Lesson("Bad", summary: "guaranteed", blocks: [], requiredForReal: true, includeQuiz: false);
        var problems = LearningContentValidator.Validate(Content(lesson), Banned());
        problems.Count.ShouldBeGreaterThanOrEqualTo(4);
    }

    private static IReadOnlyList<string> Banned() => ["guaranteed"];

    private static ContentFile Sample() => Content(Lesson("l1"));

    private static ContentFile Content(LessonContent lesson)
        => new([Track("t1", lesson)]);

    private static TrackContent Track(string id, params LessonContent[] lessons)
        => new(id, "Track " + id, "Summary", lessons);

    private static LessonContent Lesson(
        string id,
        string? title = null,
        string? summary = null,
        int estimatedMinutes = 4,
        int version = 1,
        bool requiredForReal = false,
        IReadOnlyList<BlockContent>? blocks = null,
        QuizContent? quiz = null,
        bool includeQuiz = true)
        => new(
            id,
            title ?? "Title " + id,
            summary ?? "Summary",
            estimatedMinutes,
            version,
            requiredForReal,
            blocks ?? [new BlockContent("paragraph", "Body text", null, null)],
            includeQuiz ? quiz ?? Quiz() : null);

    private static QuizContent Quiz(params QuestionContent[] questions)
        => new(80, questions.Length == 0
            ? [Question("q1", [Option("a", "one"), Option("b", "two")], "a", "why")]
            : questions);

    private static QuestionContent Question(
        string id,
        IReadOnlyList<OptionContent> options,
        string correct,
        string explanation)
        => new(id, "Prompt " + id, options, correct, explanation);

    private static OptionContent Option(string id, string text) => new(id, text);
}
