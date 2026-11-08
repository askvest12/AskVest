using AskVest.Modules.Learning.Content;
using AskVest.Modules.Learning.Domain;
using AskVest.Modules.Learning.Endpoints;
using AskVest.Modules.Learning.Persistence;
using AskVest.Modules.Learning.Persistence.Entities;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AskVest.Modules.Learning.Services;

/// <summary>
/// Quiz attempts and lesson completion. Every write happens inside one
/// transaction scope; a per user and lesson advisory lock serializes
/// concurrent attempts so counters stay exact.
/// </summary>
internal sealed class LearningCommandService(
    LearningDbContext db,
    LearningContentSnapshot snapshot,
    ITransactionScope scope,
    TimeProvider timeProvider,
    ILogger<LearningCommandService> logger)
{
    public async Task<Result<(QuizGrade Grade, LessonProgress Progress)>> SubmitQuizAttemptAsync(
        Guid userId,
        string lessonId,
        IReadOnlyList<QuizAnswerRequest>? requestAnswers,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!snapshot.TryGetLesson(lessonId, out var lesson) || lesson is null)
        {
            return NotFound();
        }

        if (lesson.Quiz is null)
        {
            return Invalid("This lesson has no quiz.");
        }

        if (!TryBuildAnswers(requestAnswers, out var answers, out var invalid))
        {
            return Result.Failure<(QuizGrade Grade, LessonProgress Progress)>(invalid!);
        }

        var grade = QuizGrader.Grade(lesson.Quiz, answers);
        if (grade.IsFailure)
        {
            return Result.Failure<(QuizGrade Grade, LessonProgress Progress)>(grade.Error!);
        }

        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await LearningScope.EnlistAsync(db, scope, cancellationToken);
            await LockAsync(userId, lessonId, cancellationToken);

            var now = timeProvider.GetUtcNow();
            var chosen = answers
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            db.QuizAttempts.Add(new QuizAttempt
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                LessonId = lessonId,
                LessonVersion = lesson.Version,
                Score = grade.Value.Score,
                Passed = grade.Value.Passed,
                Answers = System.Text.Json.JsonSerializer.Serialize(chosen),
                SubmittedAt = now,
                IdempotencyKey = idempotencyKey,
            });

            var progress = await UpsertProgressAsync(
                userId, lessonId, lesson.Version, grade.Value.Passed, grade.Value.Score, now, cancellationToken);
            if (grade.Value.Passed)
            {
                await RecordLearningDayAsync(userId, now, cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            if (ownsScope)
            {
                await scope.CommitAsync(cancellationToken);
            }

            var score = grade.Value.Score;
            var passed = grade.Value.Passed;
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Learning quiz attempt lesson={LessonId} score={Score} passed={Passed}",
                    lessonId, score, passed);
            }

            return Result.Success((grade.Value, progress));
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    public async Task<Result<LessonProgress>> CompleteLessonAsync(
        Guid userId,
        string lessonId,
        CancellationToken cancellationToken)
    {
        if (!snapshot.TryGetLesson(lessonId, out var lesson) || lesson is null)
        {
            return Result.Failure<LessonProgress>(NotFoundError());
        }

        if (lesson.Quiz is not null)
        {
            return Result.Failure<LessonProgress>(new Error(
                ErrorCodes.LearningQuizRequired,
                "This lesson has a quiz; pass it through quiz-attempts instead.",
                ErrorType.BusinessRule));
        }

        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await LearningScope.EnlistAsync(db, scope, cancellationToken);
            await LockAsync(userId, lessonId, cancellationToken);

            var now = timeProvider.GetUtcNow();
            var progress = await FindProgressAsync(userId, lessonId, cancellationToken)
                ?? new LessonProgress
                {
                    UserId = userId,
                    LessonId = lessonId,
                    AttemptsCount = 0,
                };
            MarkCompleted(progress, lesson.Version, now);
            progress.UpdatedAt = now;
            if (db.Entry(progress).State == EntityState.Detached)
            {
                db.LessonProgress.Add(progress);
            }

            await RecordLearningDayAsync(userId, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            if (ownsScope)
            {
                await scope.CommitAsync(cancellationToken);
            }

            return Result.Success(progress);
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    private async Task<LessonProgress> UpsertProgressAsync(
        Guid userId,
        string lessonId,
        int lessonVersion,
        bool passed,
        int score,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var progress = await FindProgressAsync(userId, lessonId, cancellationToken)
            ?? new LessonProgress
            {
                UserId = userId,
                LessonId = lessonId,
                AttemptsCount = 0,
            };
        progress.AttemptsCount++;
        progress.BestScore = progress.BestScore is null
            ? score
            : Math.Max(progress.BestScore.Value, score);
        progress.LastAttemptAt = now;
        progress.UpdatedAt = now;
        if (passed)
        {
            MarkCompleted(progress, lessonVersion, now);
        }
        else if (progress.Status != LessonProgress.StatusCompleted)
        {
            progress.Status = LessonProgress.StatusAttempted;
        }

        if (db.Entry(progress).State == EntityState.Detached)
        {
            db.LessonProgress.Add(progress);
        }

        return progress;
    }

    private static void MarkCompleted(LessonProgress progress, int lessonVersion, DateTimeOffset now)
    {
        if (progress.Status == LessonProgress.StatusCompleted && progress.CompletedVersion == lessonVersion)
        {
            return;
        }

        progress.Status = LessonProgress.StatusCompleted;
        progress.CompletedVersion = lessonVersion;
        progress.CompletedAt = now;
    }

    private async Task<LessonProgress?> FindProgressAsync(
        Guid userId, string lessonId, CancellationToken cancellationToken)
        => await db.LessonProgress.FindAsync([userId, lessonId], cancellationToken);

    private async Task LockAsync(Guid userId, string lessonId, CancellationToken cancellationToken)
    {
        // Serializes concurrent attempts on the same lesson so the progress
        // counters and the single completion stay exact.
        var key = $"learning:{userId:N}:{lessonId}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({key}))", cancellationToken);
    }

    private async Task RecordLearningDayAsync(
        Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO learning.learning_days (user_id, day, first_activity_at)
            VALUES ({userId}, {day}, {now})
            ON CONFLICT (user_id, day) DO NOTHING
            """,
            cancellationToken);
    }

    private async Task RollbackAsync(bool ownsScope, CancellationToken cancellationToken)
    {
        if (ownsScope)
        {
            await scope.RollbackAsync(cancellationToken);
        }
    }

    private static bool TryBuildAnswers(
        IReadOnlyList<QuizAnswerRequest>? requestAnswers,
        out IReadOnlyDictionary<string, string> answers,
        out Error? error)
    {
        answers = new Dictionary<string, string>(StringComparer.Ordinal);
        error = null;
        if (requestAnswers is null)
        {
            error = InvalidError("Answers are required.");
            return false;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var answer in requestAnswers)
        {
            if (string.IsNullOrEmpty(answer.QuestionId) || string.IsNullOrEmpty(answer.OptionId)
                || !map.TryAdd(answer.QuestionId, answer.OptionId))
            {
                error = InvalidError("Each question must be answered exactly once with a valid option.");
                return false;
            }
        }

        answers = map;
        return true;
    }

    private static Result<(QuizGrade, LessonProgress)> NotFound()
        => Result.Failure<(QuizGrade Grade, LessonProgress Progress)>(NotFoundError());

    private static Result<(QuizGrade, LessonProgress)> Invalid(string message)
        => Result.Failure<(QuizGrade Grade, LessonProgress Progress)>(InvalidError(message));

    private static Error NotFoundError()
        => new(ErrorCodes.ResourceNotFound, "The lesson was not found.", ErrorType.NotFound);

    private static Error InvalidError(string message)
        => new(ErrorCodes.ValidationFailed, message, ErrorType.Validation);
}
