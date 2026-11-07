using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            // The history table must exist before ProvisionReadOnlySchema grants SELECT on it;
            // EF creates it lazily at the first history insert, which is too late.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS catalog.__ef_migrations_history (
                    "MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL,
                    CONSTRAINT "__ef_migrations_history_pkey" PRIMARY KEY ("MigrationId")
                );
                """);

            AskVest.Platform.SchemaAccess.ProvisionReadOnlySchema(migrationBuilder, "catalog", "__ef_migrations_history");

            migrationBuilder.CreateTable(
                name: "catalog_events",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    subject_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    subject_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_fields = table.Column<List<string>>(type: "text[]", nullable: false),
                    data_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_events", x => x.id);
                    table.CheckConstraint("ck_catalog_events_type", "event_type IN ('imported', 'listed', 'suspended', 'relisted', 'delisted', 'updated')");
                });

            migrationBuilder.CreateTable(
                name: "chains",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    family = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    native_asset_symbol = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    native_decimals = table.Column<int>(type: "integer", nullable: false),
                    explorer_link_template = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    required_confirmations = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chains", x => x.id);
                    table.CheckConstraint("ck_chains_confirmations", "required_confirmations >= 0");
                    table.CheckConstraint("ck_chains_native_decimals", "native_decimals BETWEEN 0 AND 36");
                    table.CheckConstraint("ck_chains_status", "status IN ('enabled', 'disabled')");
                });

            migrationBuilder.CreateTable(
                name: "instruments",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    isin = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    listing_exchange = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    country_of_listing = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    quote_currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    sector = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    short_description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    logo_link = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instruments", x => x.id);
                    table.CheckConstraint("ck_instruments_kind", "kind IN ('stock', 'etf')");
                    table.CheckConstraint("ck_instruments_status", "status IN ('active', 'delisted')");
                });

            migrationBuilder.CreateTable(
                name: "issuers",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    legal_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    jurisdiction = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    website = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    regulatory_status = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    legal_document_links = table.Column<List<string>>(type: "text[]", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_issuers", x => x.id);
                    table.CheckConstraint("ck_issuers_status", "status IN ('approved', 'suspended')");
                });

            migrationBuilder.CreateTable(
                name: "tokens",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issuer_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    chain_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    standard = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    canonical_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    caip19 = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    decimals = table.Column<int>(type: "integer", nullable: false),
                    ledger_asset_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    gateway_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    schedule_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    schedule_time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    schedule_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    claim_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    claim_description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    backing_description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    custodian_description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    redemption_description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    dividend_policy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    dividend_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    voting_rights = table.Column<bool>(type: "boolean", nullable: false),
                    voting_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    transfer_restrictions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    issuer_fees = table.Column<List<string>>(type: "text[]", nullable: false),
                    no_issuer_fees = table.Column<bool>(type: "boolean", nullable: false),
                    risk_statements = table.Column<List<string>>(type: "text[]", nullable: false),
                    disclosure_document_link = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewer_label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ever_listed = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tokens", x => x.id);
                    table.CheckConstraint("ck_tokens_decimals", "decimals BETWEEN 0 AND 18");
                    table.CheckConstraint("ck_tokens_issuer_fees", "no_issuer_fees OR cardinality(issuer_fees) > 0");
                    table.CheckConstraint("ck_tokens_risk_statements", "cardinality(risk_statements) >= 3");
                    table.CheckConstraint("ck_tokens_status", "status IN ('draft', 'listed', 'suspended', 'delisted')");
                    table.ForeignKey(
                        name: "fk_tokens_chains_chain_id",
                        column: x => x.chain_id,
                        principalSchema: "catalog",
                        principalTable: "chains",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tokens_instruments_instrument_id",
                        column: x => x.instrument_id,
                        principalSchema: "catalog",
                        principalTable: "instruments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tokens_issuers_issuer_id",
                        column: x => x.issuer_id,
                        principalSchema: "catalog",
                        principalTable: "issuers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "token_countries",
                schema: "catalog",
                columns: table => new
                {
                    token_id = table.Column<Guid>(type: "uuid", nullable: false),
                    country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_token_countries", x => new { x.token_id, x.country_code });
                    table.CheckConstraint("ck_token_countries_code", "country_code ~ '^[A-Z]{2}$'");
                    table.ForeignKey(
                        name: "fk_token_countries_tokens_token_id",
                        column: x => x.token_id,
                        principalSchema: "catalog",
                        principalTable: "tokens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_events_occurred_at",
                schema: "catalog",
                table: "catalog_events",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_instruments_status",
                schema: "catalog",
                table: "instruments",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_tokens_caip19",
                schema: "catalog",
                table: "tokens",
                column: "caip19",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tokens_chain_id",
                schema: "catalog",
                table: "tokens",
                column: "chain_id");

            migrationBuilder.CreateIndex(
                name: "ix_tokens_instrument_id_status",
                schema: "catalog",
                table: "tokens",
                columns: new[] { "instrument_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_tokens_issuer_id",
                schema: "catalog",
                table: "tokens",
                column: "issuer_id");

            migrationBuilder.CreateIndex(
                name: "ix_tokens_ledger_asset_code",
                schema: "catalog",
                table: "tokens",
                column: "ledger_asset_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tokens_status",
                schema: "catalog",
                table: "tokens",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalog_events",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "token_countries",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "tokens",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "chains",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "instruments",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "issuers",
                schema: "catalog");
        }
    }
}
