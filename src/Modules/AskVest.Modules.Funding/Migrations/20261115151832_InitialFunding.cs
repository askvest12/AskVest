using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.Funding.Migrations
{
    /// <inheritdoc />
    public partial class InitialFunding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "funding");

            // The history table must exist before ProvisionSchema grants SELECT on it;
            // EF creates it lazily at the first history insert, which is too late.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS funding.__ef_migrations_history (
                    "MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL,
                    CONSTRAINT "__ef_migrations_history_pkey" PRIMARY KEY ("MigrationId")
                );
                """);

            AskVest.Platform.SchemaAccess.ProvisionSchema(migrationBuilder, "funding", "__ef_migrations_history");

            migrationBuilder.CreateTable(
                name: "beneficiaries",
                schema: "funding",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    provider_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    masked_display = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    name_verified = table.Column<bool>(type: "boolean", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    active_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beneficiaries", x => x.id);
                    table.CheckConstraint("ck_beneficiaries_state", "state IN ('cooling_off', 'active', 'removed')");
                });

            migrationBuilder.CreateTable(
                name: "conversions",
                schema: "funding",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pair = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    source_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    target_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    source_amount = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    mid_rate = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    effective_rate = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    receive_amount = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    fee_amount = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    net_amount = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    terms_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversions", x => x.id);
                    table.CheckConstraint("ck_conversions_amounts", "source_amount > 0 AND receive_amount >= 0 AND fee_amount >= 0 AND net_amount >= 0");
                    table.CheckConstraint("ck_conversions_state", "state IN ('quoted', 'completed', 'expired')");
                });

            migrationBuilder.CreateTable(
                name: "deposits",
                schema: "funding",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_deposit_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    provider_fee = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    payer_verified = table.Column<bool>(type: "boolean", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    detail_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    credit_journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reversal_journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shortfall = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    credited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reversed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deposits", x => x.id);
                    table.CheckConstraint("ck_deposits_amount", "amount > 0 AND provider_fee >= 0 AND shortfall >= 0");
                    table.CheckConstraint("ck_deposits_state", "state IN ('pending', 'credited', 'quarantined', 'failed', 'reversed', 'ignored')");
                });

            migrationBuilder.CreateTable(
                name: "funding_accounts",
                schema: "funding",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    provider_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    instruction_json = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_funding_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "funding_events",
                schema: "funding",
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
                    table.PrimaryKey("pk_funding_events", x => x.id);
                    table.CheckConstraint("ck_funding_events_type", "event_type IN ('funding_account_created', 'deposit_state_changed', 'conversion_completed', 'beneficiary_state_changed', 'withdrawal_state_changed', 'restriction_added', 'restriction_cleared', 'pause_changed', 'operator_action')");
                });

            migrationBuilder.CreateTable(
                name: "funding_pauses",
                schema: "funding",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    paused_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consecutive_shortfalls = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_funding_pauses", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "funding_restrictions",
                schema: "funding",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cleared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_funding_restrictions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_events",
                schema: "funding",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    event_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    body_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "withdrawals",
                schema: "funding",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    beneficiary_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receive_amount = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    fee_amount = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    total_debited = table.Column<decimal>(type: "numeric(28,8)", nullable: false),
                    terms_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    preview_used = table.Column<bool>(type: "boolean", nullable: false),
                    hold_journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    settle_journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    release_journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    detail_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    review_decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_withdrawals", x => x.id);
                    table.CheckConstraint("ck_withdrawals_amounts", "receive_amount > 0 AND fee_amount >= 0 AND total_debited > 0");
                    table.CheckConstraint("ck_withdrawals_state", "state IN ('previewed', 'requested', 'under_review', 'submitting', 'submitted', 'completed', 'failed', 'cancelled', 'needs_reconciliation')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_beneficiaries_user_id_currency_state",
                schema: "funding",
                table: "beneficiaries",
                columns: new[] { "user_id", "currency", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_beneficiaries_user_id_provider_key_provider_reference",
                schema: "funding",
                table: "beneficiaries",
                columns: new[] { "user_id", "provider_key", "provider_reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_conversions_user_id_idempotency_key",
                schema: "funding",
                table: "conversions",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_conversions_user_id_state",
                schema: "funding",
                table: "conversions",
                columns: new[] { "user_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_deposits_provider_key_provider_deposit_id",
                schema: "funding",
                table: "deposits",
                columns: new[] { "provider_key", "provider_deposit_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deposits_state_created_at",
                schema: "funding",
                table: "deposits",
                columns: new[] { "state", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_deposits_user_id_created_at",
                schema: "funding",
                table: "deposits",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_funding_accounts_provider_reference",
                schema: "funding",
                table: "funding_accounts",
                column: "provider_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_funding_accounts_user_id_currency",
                schema: "funding",
                table: "funding_accounts",
                columns: new[] { "user_id", "currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_funding_events_subject_id",
                schema: "funding",
                table: "funding_events",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "ix_funding_events_user_id_occurred_at",
                schema: "funding",
                table: "funding_events",
                columns: new[] { "user_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_funding_pauses_currency",
                schema: "funding",
                table: "funding_pauses",
                column: "currency",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_funding_restrictions_user_id_cleared_at",
                schema: "funding",
                table: "funding_restrictions",
                columns: new[] { "user_id", "cleared_at" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_events_provider_key_event_id",
                schema: "funding",
                table: "webhook_events",
                columns: new[] { "provider_key", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_withdrawals_state_updated_at",
                schema: "funding",
                table: "withdrawals",
                columns: new[] { "state", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_withdrawals_user_id_idempotency_key",
                schema: "funding",
                table: "withdrawals",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_withdrawals_user_id_state",
                schema: "funding",
                table: "withdrawals",
                columns: new[] { "user_id", "state" });

            // funding_events is append only for the application role.
            AskVest.Platform.SchemaAccess.RestrictToAppendOnly(migrationBuilder, "funding", "funding_events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "beneficiaries",
                schema: "funding");

            migrationBuilder.DropTable(
                name: "conversions",
                schema: "funding");

            migrationBuilder.DropTable(
                name: "deposits",
                schema: "funding");

            migrationBuilder.DropTable(
                name: "funding_accounts",
                schema: "funding");

            migrationBuilder.DropTable(
                name: "funding_events",
                schema: "funding");

            migrationBuilder.DropTable(
                name: "funding_pauses",
                schema: "funding");

            migrationBuilder.DropTable(
                name: "funding_restrictions",
                schema: "funding");

            migrationBuilder.DropTable(
                name: "webhook_events",
                schema: "funding");

            migrationBuilder.DropTable(
                name: "withdrawals",
                schema: "funding");
        }
    }
}
