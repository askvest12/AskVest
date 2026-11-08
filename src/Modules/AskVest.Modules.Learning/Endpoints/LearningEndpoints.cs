using AskVest.Modules.Learning.Content;
using AskVest.Modules.Learning.Persistence.Entities;
using AskVest.Modules.Learning.Services;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Learning.Endpoints;

public static class LearningEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var learning = api.MapGroup("/learning").WithTags("Learning");

        learning.MapGet("/tracks", (HttpContext httpContext, LearningReadService reads,
                CancellationToken cancellationToken) =>
                ListTracksAsync(httpContext, reads, cancellationToken))
            .WithName("ListLearningTracks")
            .WithSummary("Learning tracks with their lessons in order and the caller's status.");

        learning.MapGet("/lessons/{lessonId}", (HttpContext httpContext, LearningReadService reads,
                string lessonId, CancellationToken cancellationToken) =>
                GetLessonAsync(httpContext, reads, lessonId, cancellationToken))
            .WithName("GetLearningLesson")
            .WithSummary("One lesson's body and quiz questions without answers or explanations.");
    }

    private static async Task<IResult> ListTracksAsync(
        HttpContext httpContext,
        LearningReadService reads,
        CancellationToken cancellationToken)
    {
        var progress = await reads.GetProgressAsync(CurrentUserId(httpContext), cancellationToken);
        return LearningResults.Ok(
            new
            {
                tracks = reads.Tracks.Select(track => new
                {
                    id = track.Id,
                    title = track.Title,
                    summary = track.Summary,
                    lessons = track.Lessons.Select(lesson => LessonSummary(lesson, progress)),
                }),
            },
            "Learning tracks loaded.",
            httpContext);
    }

    private static async Task<IResult> GetLessonAsync(
        HttpContext httpContext,
        LearningReadService reads,
        string lessonId,
        CancellationToken cancellationToken)
    {
        if (!reads.TryGetLesson(lessonId, out var lesson) || lesson is null)
        {
            return NotFound(httpContext);
        }

        var progress = await reads.GetProgressAsync(CurrentUserId(httpContext), cancellationToken);
        progress.TryGetValue(lessonId, out var row);
        reads.TrackIdByLesson.TryGetValue(lessonId, out var trackId);
        return LearningResults.Ok(
            new
            {
                lesson = LessonDetail(lesson, trackId, row),
            },
            "Lesson loaded.",
            httpContext);
    }

    private static object LessonSummary(LessonContent lesson, IReadOnlyDictionary<string, LessonProgress> progress)
    {
        progress.TryGetValue(lesson.Id, out var row);
        return new
        {
            id = lesson.Id,
            title = lesson.Title,
            summary = lesson.Summary,
            estimatedMinutes = lesson.EstimatedMinutes,
            requiredForReal = lesson.RequiredForReal,
            status = LearningReadService.StatusFor(row),
        };
    }

    private static object LessonDetail(LessonContent lesson, string? trackId, LessonProgress? progress)
    {
        return new
        {
            id = lesson.Id,
            trackId = trackId,
            title = lesson.Title,
            summary = lesson.Summary,
            estimatedMinutes = lesson.EstimatedMinutes,
            version = lesson.Version,
            requiredForReal = lesson.RequiredForReal,
            blocks = lesson.Blocks.Select(block => new
            {
                type = block.Type,
                text = block.Text,
                items = block.Items,
                kind = block.Kind,
            }),
            quiz = lesson.Quiz is null
                ? null
                : new
                {
                    passMarkPercent = LearningContentValidator.EffectivePassMark(lesson.Quiz),
                    questions = lesson.Quiz.Questions.Select(question => new
                    {
                        id = question.Id,
                        prompt = question.Prompt,
                        options = question.Options.Select(option => new
                        {
                            id = option.Id,
                            text = option.Text,
                        }),
                    }),
                },
            progress = new
            {
                status = LearningReadService.StatusFor(progress),
                attemptsCount = progress?.AttemptsCount ?? 0,
                bestScore = progress?.BestScore,
                completedVersion = progress?.CompletedVersion,
                completedAt = progress?.CompletedAt,
                lastAttemptAt = progress?.LastAttemptAt,
            },
        };
    }

    private static IResult NotFound(HttpContext httpContext)
        => LearningResults.Error(new Error(
            ErrorCodes.ResourceNotFound,
            "The lesson was not found.",
            ErrorType.NotFound), httpContext);

    private static Guid CurrentUserId(HttpContext httpContext)
        => Guid.Parse(httpContext.User.FindFirst("sub")!.Value);
}
