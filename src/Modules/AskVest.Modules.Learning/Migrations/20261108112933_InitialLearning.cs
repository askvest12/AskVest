using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.Learning.Migrations
{
    /// <inheritdoc />
    public partial class InitialLearning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "learning");

            // The history table must exist before provisioning grants SELECT on it;
            // EF creates it lazily at the first history insert, which is too late.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS learning.__ef_migrations_history (
                    "MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL,
                    CONSTRAINT "__ef_migrations_history_pkey" PRIMARY KEY ("MigrationId")
                );
                """);

            AskVest.Platform.SchemaAccess.ProvisionSchema(migrationBuilder, "learning", "__ef_migrations_history");

            migrationBuilder.CreateTable(
                name: "learning_days",
                schema: "learning",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    first_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_learning_days", x => new { x.user_id, x.day });
                });

            migrationBuilder.CreateTable(
                name: "lesson_progress",
                schema: "learning",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lesson_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    completed_version = table.Column<int>(type: "integer", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    best_score = table.Column<int>(type: "integer", nullable: true),
                    attempts_count = table.Column<int>(type: "integer", nullable: false),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lesson_progress", x => new { x.user_id, x.lesson_id });
                    table.CheckConstraint("ck_lesson_progress_attempts", "attempts_count >= 0 AND (best_score IS NULL OR (best_score >= 0 AND best_score <= 100))");
                    table.CheckConstraint("ck_lesson_progress_completed", "(status = 'completed') = (completed_version IS NOT NULL AND completed_at IS NOT NULL)");
                    table.CheckConstraint("ck_lesson_progress_status", "status IN ('attempted', 'completed')");
                });

            migrationBuilder.CreateTable(
                name: "quiz_attempts",
                schema: "learning",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lesson_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    lesson_version = table.Column<int>(type: "integer", nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    passed = table.Column<bool>(type: "boolean", nullable: false),
                    answers = table.Column<string>(type: "jsonb", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quiz_attempts", x => x.id);
                    table.CheckConstraint("ck_quiz_attempts_score", "score >= 0 AND score <= 100");
                });

            migrationBuilder.CreateIndex(
                name: "ix_lesson_progress_user_id_status",
                schema: "learning",
                table: "lesson_progress",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_quiz_attempts_user_id_idempotency_key",
                schema: "learning",
                table: "quiz_attempts",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quiz_attempts_user_id_submitted_at",
                schema: "learning",
                table: "quiz_attempts",
                columns: new[] { "user_id", "submitted_at" });

            // quiz_attempts is append-only for the application role: the rows are
            // the evidence of comprehension.
            AskVest.Platform.SchemaAccess.RestrictToAppendOnly(migrationBuilder, "learning", "quiz_attempts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "learning_days",
                schema: "learning");

            migrationBuilder.DropTable(
                name: "lesson_progress",
                schema: "learning");

            migrationBuilder.DropTable(
                name: "quiz_attempts",
                schema: "learning");
        }
    }
}
