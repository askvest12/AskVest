using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Learning.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Learning;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class LearningSearchTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Search_Matches_Title_Summary_And_Body_Text()
    {
        await using var provider = LearningTestServices.CreateProvider(fixture, TimeProvider.System);
        var requirements = provider.GetRequiredService<ILearningRequirements>();

        var byTitle = await requirements.SearchLessonsAsync(
            "plain", 10, TestContext.Current.CancellationToken);
        byTitle.ShouldHaveSingleItem().LessonId.ShouldBe("plain-lesson");
        byTitle[0].Title.ShouldBe("Plain lesson");
        byTitle[0].Summary.ShouldBe("No quiz");

        var byBody = await requirements.SearchLessonsAsync(
            "carefully", 10, TestContext.Current.CancellationToken);
        byBody.ShouldHaveSingleItem().LessonId.ShouldBe("required-lesson");
        byBody[0].Excerpt.ShouldContain("carefully");

        var caseInsensitive = await requirements.SearchLessonsAsync(
            "QUIZ", 10, TestContext.Current.CancellationToken);
        caseInsensitive.Select(hit => hit.LessonId).ShouldContain("quiz-lesson");

        var limited = await requirements.SearchLessonsAsync(
            "lesson", 1, TestContext.Current.CancellationToken);
        limited.Count.ShouldBe(1);

        var empty = await requirements.SearchLessonsAsync(
            "   ", 10, TestContext.Current.CancellationToken);
        empty.ShouldBeEmpty();

        var noMatch = await requirements.SearchLessonsAsync(
            "zzzz-no-such-term", 10, TestContext.Current.CancellationToken);
        noMatch.ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_Never_Exposes_Quiz_Answers_Or_Explanations()
    {
        await using var provider = LearningTestServices.CreateProvider(fixture, TimeProvider.System);
        var requirements = provider.GetRequiredService<ILearningRequirements>();

        var hits = await requirements.SearchLessonsAsync(
            "right", 10, TestContext.Current.CancellationToken);
        hits.ShouldBeEmpty("quiz explanations must not be searchable");

        var all = await requirements.SearchLessonsAsync(
            "lesson", 10, TestContext.Current.CancellationToken);
        foreach (var hit in all)
        {
            hit.Excerpt.ShouldNotContain("correctOptionId");
            hit.Excerpt.ShouldNotContain("explanation");
        }
    }
}
