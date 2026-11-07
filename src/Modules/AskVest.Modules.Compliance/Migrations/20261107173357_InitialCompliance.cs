using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.Compliance.Migrations
{
    /// <inheritdoc />
    public partial class InitialCompliance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "compliance");

            // The history table must exist before ProvisionSchema grants SELECT on it;
            // EF creates it lazily at the first history insert, which is too late.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS compliance.__ef_migrations_history (
                    "MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL,
                    CONSTRAINT "__ef_migrations_history_pkey" PRIMARY KEY ("MigrationId")
                );
                """);

            AskVest.Platform.SchemaAccess.ProvisionSchema(migrationBuilder, "compliance", "__ef_migrations_history");

            migrationBuilder.CreateTable(
                name: "compliance_events",
                schema: "compliance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_compliance_events", x => x.id);
                    table.CheckConstraint("ck_compliance_events_event_type", "event_type IN ('ConsentAccepted', 'CountrySet', 'CountryChanged', 'KycStarted', 'KycApproved', 'KycRejected', 'ScreeningClear', 'ScreeningHit', 'WebhookRejected')");
                });

            migrationBuilder.CreateTable(
                name: "compliance_profiles",
                schema: "compliance",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    declared_country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    kyc_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    kyc_provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    provider_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    verified_country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    attempts_used = table.Column<int>(type: "integer", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejection_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    screening_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    screened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_compliance_profiles", x => x.user_id);
                    table.CheckConstraint("ck_compliance_profiles_kyc_status", "kyc_status IN ('NotStarted', 'InReview', 'Approved', 'Rejected')");
                    table.CheckConstraint("ck_compliance_profiles_screening_status", "screening_status IN ('NotRun', 'Clear', 'Hit')");
                });

            migrationBuilder.CreateTable(
                name: "consent_records",
                schema: "compliance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consent_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "kyc_webhook_events",
                schema: "compliance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    event_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    body_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kyc_webhook_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_compliance_events_user_id_occurred_at",
                schema: "compliance",
                table: "compliance_events",
                columns: new[] { "user_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_consent_records_user_id_document_type_version",
                schema: "compliance",
                table: "consent_records",
                columns: new[] { "user_id", "document_type", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_kyc_webhook_events_provider_event_id",
                schema: "compliance",
                table: "kyc_webhook_events",
                columns: new[] { "provider", "event_id" },
                unique: true);

            AskVest.Platform.SchemaAccess.RestrictToAppendOnly(migrationBuilder, "compliance", "compliance_events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "compliance_events",
                schema: "compliance");

            migrationBuilder.DropTable(
                name: "compliance_profiles",
                schema: "compliance");

            migrationBuilder.DropTable(
                name: "consent_records",
                schema: "compliance");

            migrationBuilder.DropTable(
                name: "kyc_webhook_events",
                schema: "compliance");
        }
    }
}
