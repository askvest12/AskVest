using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.Trading.Migrations
{
    /// <inheritdoc />
    public partial class InitialTrading : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "trading");

            // The history table must exist before ProvisionSchema grants SELECT on it;
            // EF creates it lazily at the first history insert, which is too late.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS trading.__ef_migrations_history (
                    "MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL,
                    CONSTRAINT "__ef_migrations_history_pkey" PRIMARY KEY ("MigrationId")
                );
                """);

            AskVest.Platform.SchemaAccess.ProvisionSchema(migrationBuilder, "trading", "__ef_migrations_history");

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "trading",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    side = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    settlement_asset = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    price = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    gross_amount = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    our_fee = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    gateway_fee = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    total = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    gateway_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    gateway_order_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    hold_journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    fill_journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    release_journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    settlement_chain_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    settlement_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    detail_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    executed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_status_check_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.CheckConstraint("ck_orders_amounts", "quantity > 0 AND price > 0 AND gross_amount > 0 AND our_fee >= 0 AND gateway_fee >= 0 AND total > 0");
                    table.CheckConstraint("ck_orders_side", "side IN ('buy', 'sell')");
                    table.CheckConstraint("ck_orders_state", "state IN ('pending_submission', 'submitting', 'submitted', 'executed', 'failed', 'cancelled', 'needs_reconciliation')");
                });

            migrationBuilder.CreateTable(
                name: "quotes",
                schema: "trading",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_id = table.Column<Guid>(type: "uuid", nullable: false),
                    side = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    price = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    gross_amount = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    our_fee = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    gateway_fee = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    total = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    settlement_asset = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    gateway_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    gateway_quote_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    warnings = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    terms_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quotes", x => x.id);
                    table.CheckConstraint("ck_quotes_amounts", "quantity > 0 AND price > 0 AND gross_amount >= 0 AND our_fee >= 0 AND gateway_fee >= 0 AND total > 0");
                    table.CheckConstraint("ck_quotes_side", "side IN ('buy', 'sell')");
                    table.CheckConstraint("ck_quotes_state", "state IN ('open', 'used')");
                });

            migrationBuilder.CreateTable(
                name: "trading_events",
                schema: "trading",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    from_state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    to_state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    detail_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trading_events", x => x.id);
                    table.CheckConstraint("ck_trading_events_type", "event_type IN ('order_placed', 'order_submitted', 'order_executed', 'order_failed', 'order_cancelled', 'order_needs_reconciliation', 'order_reconciliation_resolved')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_orders_quote_id",
                schema: "trading",
                table: "orders",
                column: "quote_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_state_updated_at",
                schema: "trading",
                table: "orders",
                columns: new[] { "state", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_user_id_idempotency_key",
                schema: "trading",
                table: "orders",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_user_id_state",
                schema: "trading",
                table: "orders",
                columns: new[] { "user_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_quotes_user_id_created_at",
                schema: "trading",
                table: "quotes",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_quotes_user_id_state",
                schema: "trading",
                table: "quotes",
                columns: new[] { "user_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_trading_events_order_id",
                schema: "trading",
                table: "trading_events",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_trading_events_user_id_occurred_at",
                schema: "trading",
                table: "trading_events",
                columns: new[] { "user_id", "occurred_at" });

            AskVest.Platform.SchemaAccess.RestrictToAppendOnly(migrationBuilder, "trading", "trading_events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "orders",
                schema: "trading");

            migrationBuilder.DropTable(
                name: "quotes",
                schema: "trading");

            migrationBuilder.DropTable(
                name: "trading_events",
                schema: "trading");
        }
    }
}
