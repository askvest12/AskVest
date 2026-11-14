using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.Wallets.Migrations
{
    /// <inheritdoc />
    public partial class InitialWallets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "wallets");

            // The history table must exist before ProvisionSchema grants SELECT on it;
            // EF creates it lazily at the first history insert, which is too late.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS wallets.__ef_migrations_history (
                    "MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL,
                    CONSTRAINT "__ef_migrations_history_pkey" PRIMARY KEY ("MigrationId")
                );
                """);

            AskVest.Platform.SchemaAccess.ProvisionSchema(migrationBuilder, "wallets", "__ef_migrations_history");

            migrationBuilder.CreateTable(
                name: "custody_pauses",
                schema: "wallets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    chain_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    consecutive_mismatches = table.Column<int>(type: "integer", nullable: false),
                    paused_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_custody_pauses", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "deposits",
                schema: "wallets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_deposit_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chain_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    token_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    sender_address = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    settlement_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    detail_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    credit_journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    credited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deposits", x => x.id);
                    table.CheckConstraint("ck_deposits_quantity", "quantity > 0");
                    table.CheckConstraint("ck_deposits_state", "state IN ('pending', 'credited', 'quarantined', 'ignored')");
                });

            migrationBuilder.CreateTable(
                name: "wallet_addresses",
                schema: "wallets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chain_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    address = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    memo = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    provider_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallet_addresses", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "wallet_events",
                schema: "wallets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    from_state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    to_state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    detail_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallet_events", x => x.id);
                    table.CheckConstraint("ck_wallet_events_type", "event_type IN ('deposit_credited', 'withdrawal_address_added', 'withdrawal_state_changed')");
                });

            migrationBuilder.CreateTable(
                name: "withdrawal_addresses",
                schema: "wallets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chain_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    canonical_address = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    screening_result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    screening_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    active_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_withdrawal_addresses", x => x.id);
                    table.CheckConstraint("ck_withdrawal_addresses_state", "state IN ('cooling_off', 'active', 'removed')");
                });

            migrationBuilder.CreateTable(
                name: "withdrawals",
                schema: "wallets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_id = table.Column<Guid>(type: "uuid", nullable: false),
                    address_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_chain_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    destination_address = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    gross_quantity = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    fee = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    net_quantity = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    terms_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    preview_used = table.Column<bool>(type: "boolean", nullable: false),
                    hold_journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    settle_journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    release_journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    settlement_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    detail_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_status_check_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_withdrawals", x => x.id);
                    table.CheckConstraint("ck_withdrawals_amounts", "gross_quantity > 0 AND fee >= 0 AND net_quantity > 0");
                    table.CheckConstraint("ck_withdrawals_state", "state IN ('previewed', 'requested', 'submitting', 'submitted', 'completed', 'failed', 'cancelled', 'needs_reconciliation')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_custody_pauses_chain_id_token_id",
                schema: "wallets",
                table: "custody_pauses",
                columns: new[] { "chain_id", "token_id" });

            migrationBuilder.CreateIndex(
                name: "ix_deposits_chain_id_state",
                schema: "wallets",
                table: "deposits",
                columns: new[] { "chain_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_deposits_provider_key_provider_deposit_id",
                schema: "wallets",
                table: "deposits",
                columns: new[] { "provider_key", "provider_deposit_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deposits_user_id_detected_at",
                schema: "wallets",
                table: "deposits",
                columns: new[] { "user_id", "detected_at" });

            migrationBuilder.CreateIndex(
                name: "ix_wallet_addresses_chain_id_address_memo",
                schema: "wallets",
                table: "wallet_addresses",
                columns: new[] { "chain_id", "address", "memo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_wallet_addresses_user_id_chain_id",
                schema: "wallets",
                table: "wallet_addresses",
                columns: new[] { "user_id", "chain_id" });

            migrationBuilder.CreateIndex(
                name: "ix_wallet_events_subject_id",
                schema: "wallets",
                table: "wallet_events",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "ix_wallet_events_user_id_occurred_at",
                schema: "wallets",
                table: "wallet_events",
                columns: new[] { "user_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_withdrawal_addresses_user_id_chain_id_canonical_address",
                schema: "wallets",
                table: "withdrawal_addresses",
                columns: new[] { "user_id", "chain_id", "canonical_address" });

            migrationBuilder.CreateIndex(
                name: "ix_withdrawal_addresses_user_id_state",
                schema: "wallets",
                table: "withdrawal_addresses",
                columns: new[] { "user_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_withdrawals_state_updated_at",
                schema: "wallets",
                table: "withdrawals",
                columns: new[] { "state", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_withdrawals_user_id_idempotency_key",
                schema: "wallets",
                table: "withdrawals",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_withdrawals_user_id_state",
                schema: "wallets",
                table: "withdrawals",
                columns: new[] { "user_id", "state" });

            AskVest.Platform.SchemaAccess.RestrictToAppendOnly(migrationBuilder, "wallets", "wallet_events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "custody_pauses",
                schema: "wallets");

            migrationBuilder.DropTable(
                name: "deposits",
                schema: "wallets");

            migrationBuilder.DropTable(
                name: "wallet_addresses",
                schema: "wallets");

            migrationBuilder.DropTable(
                name: "wallet_events",
                schema: "wallets");

            migrationBuilder.DropTable(
                name: "withdrawal_addresses",
                schema: "wallets");

            migrationBuilder.DropTable(
                name: "withdrawals",
                schema: "wallets");
        }
    }
}
