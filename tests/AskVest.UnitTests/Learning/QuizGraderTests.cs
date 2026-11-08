using AskVest.Modules.Learning.Content;
using AskVest.Modules.Learning.Domain;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Learning;

public sealed class QuizGraderTests
{
    private static QuizContent Quiz(int passMark = 80, int questionCount = 3)
        => new(
            passMark,
            Enumerable.Range(1, questionCount)
                .Select(index => new QuestionContent(
                    $"q{index}",
                    $"Prompt {index}",
                    [new OptionContent("a", "A"), new OptionContent("b", "B")],
                    "b",
                    $"Why {index}"))
                .ToList());

    private static Dictionary<string, string> Answers(int questionCount, string option = "b")
        => Enumerable.Range(1, questionCount)
            .ToDictionary(index => $"q{index}", _ => option, StringComparer.Ordinal);

    [Fact]
    public void All_Correct_Scores_100_And_Passes()
    {
        var result = QuizGrader.Grade(Quiz(), Answers(3)).Value;
        result.Score.ShouldBe(100);
        result.Passed.ShouldBeTrue();
        result.Questions.ShouldAllBe(question => question.Correct);
    }

    [Fact]
    public void All_Wrong_Scores_Zero_And_Fails()
    {
        var result = QuizGrader.Grade(Quiz(), Answers(3, "a")).Value;
        result.Score.ShouldBe(0);
        result.Passed.ShouldBeFalse();
    }

    [Fact]
    public void Score_Rounds_Down_To_Whole_Percent()
    {
        var quiz = Quiz(passMark: 66, questionCount: 3);
        var answers = Answers(3);
        answers["q3"] = "a";
        var result = QuizGrader.Grade(quiz, answers).Value;
        result.Score.ShouldBe(66);
        result.Passed.ShouldBeTrue();
    }

    [Fact]
    public void Exact_Pass_Mark_Boundary_Passes()
    {
        var quiz = Quiz(passMark: 67, questionCount: 3);
        var answers = Answers(3);
        answers["q3"] = "a";
        var result = QuizGrader.Grade(quiz, answers).Value;
        result.Score.ShouldBe(66);
        result.Passed.ShouldBeFalse();
    }

    [Fact]
    public void Default_Pass_Mark_Applies_When_Unset()
    {
        var quiz = new QuizContent(null, Quiz().Questions);
        var answers = Answers(3);
        answers["q3"] = "a";
        var result = QuizGrader.Grade(quiz, answers).Value;
        result.Score.ShouldBe(66);
        result.Passed.ShouldBeFalse();
    }

    [Fact]
    public void Missing_Answer_Is_Rejected()
    {
        var answers = Answers(3);
        answers.Remove("q2");
        var result = QuizGrader.Grade(Quiz(), answers);
        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(SharedKernel.ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void Unknown_Question_Answer_Is_Rejected()
    {
        var answers = Answers(3);
        answers["q9"] = "a";
        answers.Remove("q3");
        var result = QuizGrader.Grade(Quiz(), answers);
        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Unknown_Option_Is_Rejected()
    {
        var answers = Answers(3);
        answers["q1"] = "z";
        var result = QuizGrader.Grade(Quiz(), answers);
        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Extra_Answer_Is_Rejected()
    {
        var answers = Answers(4);
        var result = QuizGrader.Grade(Quiz(questionCount: 3), answers);
        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Per_Question_Results_Carry_Explanations()
    {
        var answers = Answers(3);
        answers["q2"] = "a";
        var result = QuizGrader.Grade(Quiz(), answers).Value;
        result.Questions[1].Correct.ShouldBeFalse();
        result.Questions[1].Explanation.ShouldBe("Why 2");
        result.Questions[1].CorrectOptionId.ShouldBe("b");
    }
}
