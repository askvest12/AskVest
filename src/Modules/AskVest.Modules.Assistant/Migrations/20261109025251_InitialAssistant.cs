using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.Assistant.Migrations
{
    /// <inheritdoc />
    public partial class InitialAssistant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "assistant");

            // The history table must exist before provisioning grants SELECT on it;
            // EF creates it lazily at the first history insert, which is too late.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS assistant.__ef_migrations_history (
                    "MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL,
                    CONSTRAINT "__ef_migrations_history_pkey" PRIMARY KEY ("MigrationId")
                );
                """);

            AskVest.Platform.SchemaAccess.ProvisionSchema(migrationBuilder, "assistant", "__ef_migrations_history");

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "assistant",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    model_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    policy_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    tool_names = table.Column<string>(type: "jsonb", nullable: false),
                    citation_source_ids = table.Column<string>(type: "jsonb", nullable: false),
                    tool_result_hashes = table.Column<string>(type: "jsonb", nullable: false),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    latency_milliseconds = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "conversations",
                schema: "assistant",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                schema: "assistant",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    citations = table.Column<string>(type: "jsonb", nullable: false),
                    fact_cards = table.Column<string>(type: "jsonb", nullable: false),
                    actions = table.Column<string>(type: "jsonb", nullable: false),
                    policy_outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_user_id_created_at",
                schema: "assistant",
                table: "audit_events",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_conversations_user_id_updated_at",
                schema: "assistant",
                table: "conversations",
                columns: new[] { "user_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_messages_conversation_id_created_at",
                schema: "assistant",
                table: "messages",
                columns: new[] { "conversation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_messages_user_id_created_at",
                schema: "assistant",
                table: "messages",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_messages_user_id_idempotency_key",
                schema: "assistant",
                table: "messages",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true,
                filter: "\"idempotency_key\" IS NOT NULL");

            // Audit events are append only for the application role.
            AskVest.Platform.SchemaAccess.RestrictToAppendOnly(migrationBuilder, "assistant", "audit_events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "assistant");

            migrationBuilder.DropTable(
                name: "conversations",
                schema: "assistant");

            migrationBuilder.DropTable(
                name: "messages",
                schema: "assistant");
        }
    }
}
