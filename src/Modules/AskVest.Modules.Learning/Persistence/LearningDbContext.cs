using AskVest.Modules.Learning.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Learning.Persistence;

public class LearningDbContext(DbContextOptions<LearningDbContext> options) : DbContext(options)
{
    public const string Schema = "learning";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<LessonProgress> LessonProgress => Set<LessonProgress>();

    public DbSet<QuizAttempt> QuizAttempts => Set<QuizAttempt>();

    public DbSet<LearningDay> LearningDays => Set<LearningDay>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<LessonProgress>(entity =>
        {
            entity.ToTable("lesson_progress");
            entity.HasKey(progress => new { progress.UserId, progress.LessonId });
            entity.Property(progress => progress.LessonId).HasMaxLength(64).IsRequired();
            entity.Property(progress => progress.Status).HasMaxLength(12).IsRequired();
            entity.Property(progress => progress.UpdatedAt).IsRequired();
            entity.Property(progress => progress.Xmin).IsRowVersion();
            entity.HasIndex(progress => new { progress.UserId, progress.Status });
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_lesson_progress_status",
                "status IN ('attempted', 'completed')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_lesson_progress_attempts",
                "attempts_count >= 0 AND (best_score IS NULL OR (best_score >= 0 AND best_score <= 100))"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_lesson_progress_completed",
                "(status = 'completed') = (completed_version IS NOT NULL AND completed_at IS NOT NULL)"));
        });

        modelBuilder.Entity<QuizAttempt>(entity =>
        {
            entity.ToTable("quiz_attempts");
            entity.HasKey(attempt => attempt.Id);
            entity.Property(attempt => attempt.LessonId).HasMaxLength(64).IsRequired();
            entity.Property(attempt => attempt.Answers).HasColumnType("jsonb").IsRequired();
            entity.Property(attempt => attempt.SubmittedAt).IsRequired();
            entity.Property(attempt => attempt.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.HasIndex(attempt => new { attempt.UserId, attempt.IdempotencyKey }).IsUnique();
            entity.HasIndex(attempt => new { attempt.UserId, attempt.SubmittedAt });
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_quiz_attempts_score",
                "score >= 0 AND score <= 100"));
        });

        modelBuilder.Entity<LearningDay>(entity =>
        {
            entity.ToTable("learning_days");
            entity.HasKey(day => new { day.UserId, day.Day });
            entity.Property(day => day.FirstActivityAt).IsRequired();
        });
    }
}
