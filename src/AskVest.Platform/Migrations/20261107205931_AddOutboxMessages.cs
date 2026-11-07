using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Platform.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    available_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    last_error = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    dedupe_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                    table.CheckConstraint("ck_outbox_messages_status", "status IN ('pending', 'processed', 'dead')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_dedupe_key",
                schema: "platform",
                table: "outbox_messages",
                column: "dedupe_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_status_available_at",
                schema: "platform",
                table: "outbox_messages",
                columns: new[] { "status", "available_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "platform");
        }
    }
}
