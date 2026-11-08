using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.Practice.Migrations
{
    /// <inheritdoc />
    public partial class InitialPractice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "practice");

            // The history table must exist before provisioning grants SELECT on it;
            // EF creates it lazily at the first history insert, which is too late.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS practice.__ef_migrations_history (
                    "MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL,
                    CONSTRAINT "__ef_migrations_history_pkey" PRIMARY KEY ("MigrationId")
                );
                """);

            AskVest.Platform.SchemaAccess.ProvisionSchema(migrationBuilder, "practice", "__ef_migrations_history");

            migrationBuilder.CreateTable(
                name: "practice_accounts",
                schema: "practice",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reset_count = table.Column<int>(type: "integer", nullable: false),
                    last_reset_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    starting_credits_used = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_practice_accounts", x => x.user_id);
                    table.CheckConstraint("ck_practice_accounts_reset_count", "reset_count >= 0");
                    table.CheckConstraint("ck_practice_accounts_starting_credits", "starting_credits_used >= 0");
                });

            migrationBuilder.CreateTable(
                name: "practice_events",
                schema: "practice",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_practice_events", x => x.id);
                    table.CheckConstraint("ck_practice_events_type", "event_type IN ('account_started', 'account_reset', 'order_filled', 'order_cancelled', 'stop_triggered')");
                });

            migrationBuilder.CreateTable(
                name: "practice_instruments",
                schema: "practice",
                columns: table => new
                {
                    instrument_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    asset_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_practice_instruments", x => x.instrument_id);
                });

            migrationBuilder.CreateTable(
                name: "practice_orders",
                schema: "practice",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    side = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    type = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    state = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    requested_amount = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: true),
                    requested_quantity = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: true),
                    sell_all = table.Column<bool>(type: "boolean", nullable: false),
                    stop_price = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: true),
                    filled_quantity = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: true),
                    fill_price = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: true),
                    cash_amount = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: true),
                    fee = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: true),
                    price_as_of = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    price_label = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    price_session = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    hold_journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    fill_journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    filled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_practice_orders", x => x.id);
                    table.CheckConstraint("ck_practice_orders_amounts", "(requested_amount IS NULL OR requested_amount > 0) AND (requested_quantity IS NULL OR requested_quantity > 0) AND (stop_price IS NULL OR stop_price > 0) AND (filled_quantity IS NULL OR filled_quantity > 0) AND (fill_price IS NULL OR fill_price > 0) AND (cash_amount IS NULL OR cash_amount >= 0) AND (fee IS NULL OR fee >= 0)");
                    table.CheckConstraint("ck_practice_orders_side", "side IN ('buy', 'sell')");
                    table.CheckConstraint("ck_practice_orders_state", "state IN ('open', 'filled', 'cancelled')");
                    table.CheckConstraint("ck_practice_orders_stop_shape", "(type = 'market') = (stop_price IS NULL)");
                    table.CheckConstraint("ck_practice_orders_type", "type IN ('market', 'stop_loss')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_practice_events_user_id_occurred_at",
                schema: "practice",
                table: "practice_events",
                columns: new[] { "user_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_practice_instruments_asset_code",
                schema: "practice",
                table: "practice_instruments",
                column: "asset_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_practice_orders_state_type",
                schema: "practice",
                table: "practice_orders",
                columns: new[] { "state", "type" });

            migrationBuilder.CreateIndex(
                name: "ix_practice_orders_user_id_idempotency_key",
                schema: "practice",
                table: "practice_orders",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_practice_orders_user_id_state",
                schema: "practice",
                table: "practice_orders",
                columns: new[] { "user_id", "state" });

            AskVest.Platform.SchemaAccess.RestrictToAppendOnly(migrationBuilder, "practice", "practice_events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "practice_accounts",
                schema: "practice");

            migrationBuilder.DropTable(
                name: "practice_events",
                schema: "practice");

            migrationBuilder.DropTable(
                name: "practice_instruments",
                schema: "practice");

            migrationBuilder.DropTable(
                name: "practice_orders",
                schema: "practice");
        }
    }
}
