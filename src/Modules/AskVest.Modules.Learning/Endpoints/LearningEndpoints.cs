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

        learning.MapPost("/lessons/{lessonId}/quiz-attempts", (HttpContext httpContext,
                LearningCommandService commands,
                string lessonId, QuizAttemptRequest request, CancellationToken cancellationToken) =>
                SubmitQuizAttemptAsync(httpContext, commands, lessonId, request, cancellationToken))
            .WithName("SubmitLearningQuizAttempt")
            .WithSummary("Submit graded answers for a lesson quiz.")
            .WithMetadata(new RequiresIdempotencyAttribute());

        learning.MapPost("/lessons/{lessonId}/complete", (HttpContext httpContext,
                LearningCommandService commands,
                string lessonId, CancellationToken cancellationToken) =>
                CompleteLessonAsync(httpContext, commands, lessonId, cancellationToken))
            .WithName("CompleteLearningLesson")
            .WithSummary("Mark a lesson without a quiz as complete.")
            .WithMetadata(new RequiresIdempotencyAttribute());

        learning.MapGet("/progress", (HttpContext httpContext, LearningProgressService progress,
                CancellationToken cancellationToken) =>
                GetProgressAsync(httpContext, progress, cancellationToken))
            .WithName("GetLearningProgress")
            .WithSummary("The caller's learning progress, streaks and required lessons.");
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

    private static async Task<IResult> SubmitQuizAttemptAsync(
        HttpContext httpContext,
        LearningCommandService commands,
        string lessonId,
        QuizAttemptRequest request,
        CancellationToken cancellationToken)
    {
        var result = await commands.SubmitQuizAttemptAsync(
            CurrentUserId(httpContext),
            lessonId,
            request.Answers,
            httpContext.Request.Headers["Idempotency-Key"].FirstOrDefault() ?? string.Empty,
            cancellationToken);
        if (result.IsFailure)
        {
            return LearningResults.Error(result.Error!, httpContext);
        }

        var (grade, progress) = result.Value;
        return LearningResults.Ok(
            new
            {
                score = grade.Score,
                passed = grade.Passed,
                attemptsCount = progress.AttemptsCount,
                lessonComplete = progress.Status == LessonProgress.StatusCompleted,
                questions = grade.Questions.Select(question => new
                {
                    questionId = question.QuestionId,
                    correct = question.Correct,
                    selectedOptionId = question.SelectedOptionId,
                    correctOptionId = question.CorrectOptionId,
                    explanation = question.Explanation,
                }),
            },
            "Quiz attempt graded.",
            httpContext);
    }

    private static async Task<IResult> CompleteLessonAsync(
        HttpContext httpContext,
        LearningCommandService commands,
        string lessonId,
        CancellationToken cancellationToken)
    {
        var result = await commands.CompleteLessonAsync(
            CurrentUserId(httpContext), lessonId, cancellationToken);
        if (result.IsFailure)
        {
            return LearningResults.Error(result.Error!, httpContext);
        }

        var progress = result.Value;
        return LearningResults.Ok(
            new
            {
                lessonId = progress.LessonId,
                status = progress.Status,
                completedVersion = progress.CompletedVersion,
                completedAt = progress.CompletedAt,
            },
            "Lesson completed.",
            httpContext);
    }

    private static async Task<IResult> GetProgressAsync(
        HttpContext httpContext,
        LearningProgressService progress,
        CancellationToken cancellationToken)
    {
        var view = await progress.GetAsync(CurrentUserId(httpContext), cancellationToken);
        return LearningResults.Ok(
            new
            {
                tracks = view.Tracks.Select(track => new
                {
                    id = track.Id,
                    title = track.Title,
                    totalLessons = track.TotalLessons,
                    completedLessons = track.CompletedLessons,
                    attemptedLessons = track.AttemptedLessons,
                }),
                streak = new
                {
                    current = view.CurrentStreak,
                    longest = view.LongestStreak,
                    todayCounted = view.TodayCounted,
                },
                requiredLessons = view.RequiredLessons.Select(lesson => new
                {
                    id = lesson.Id,
                    trackId = lesson.TrackId,
                    version = lesson.Version,
                    status = lesson.Status,
                    completedVersion = lesson.CompletedVersion,
                }),
            },
            "Learning progress loaded.",
            httpContext);
    }

    private static IResult NotFound(HttpContext httpContext)
        => LearningResults.Error(new Error(
            ErrorCodes.ResourceNotFound,
            "The lesson was not found.",
            ErrorType.NotFound), httpContext);

    private static Guid CurrentUserId(HttpContext httpContext)
        => Guid.Parse(httpContext.User.FindFirst("sub")!.Value);
}
