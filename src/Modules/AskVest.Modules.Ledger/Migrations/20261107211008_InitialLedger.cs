using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.Ledger.Migrations
{
    /// <inheritdoc />
    public partial class InitialLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ledger");

            // The history table must exist before ProvisionSchema grants SELECT on it;
            // EF creates it lazily at the first history insert, which is too late.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS ledger.__ef_migrations_history (
                    "MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL,
                    CONSTRAINT "__ef_migrations_history_pkey" PRIMARY KEY ("MigrationId")
                );
                """);

            AskVest.Platform.SchemaAccess.ProvisionSchema(migrationBuilder, "ledger", "__ef_migrations_history");

            migrationBuilder.CreateTable(
                name: "account_balances",
                schema: "ledger",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    balance = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    allow_negative = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_balances", x => x.account_id);
                    table.CheckConstraint("ck_account_balances_negative", "allow_negative OR balance >= 0");
                });

            migrationBuilder.CreateTable(
                name: "accounts",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    book = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    owner_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    asset_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    allow_negative = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                    table.CheckConstraint("ck_accounts_allow_negative", "allow_negative = (owner_type = 'system')");
                    table.CheckConstraint("ck_accounts_book", "book IN ('practice', 'real')");
                    table.CheckConstraint("ck_accounts_issuance_book", "kind <> 'system_practice_issuance' OR book = 'practice'");
                    table.CheckConstraint("ck_accounts_kind", "kind IN ('user_available', 'user_held', 'system_clearing', 'system_fees', 'system_practice_issuance')");
                    table.CheckConstraint("ck_accounts_owner", "(owner_type = 'user' AND kind IN ('user_available', 'user_held') AND owner_id <> '00000000-0000-0000-0000-000000000000') OR (owner_type = 'system' AND kind IN ('system_clearing', 'system_fees', 'system_practice_issuance') AND owner_id = '00000000-0000-0000-0000-000000000000')");
                });

            migrationBuilder.CreateTable(
                name: "assets",
                schema: "ledger",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scale = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assets", x => x.code);
                    table.CheckConstraint("ck_assets_kind", "kind IN ('fiat', 'stablecoin', 'token')");
                    table.CheckConstraint("ck_assets_scale", "scale BETWEEN 0 AND 18");
                });

            migrationBuilder.CreateTable(
                name: "journal_entries",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    journal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    book = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(28,18)", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_entries", x => x.id);
                    table.CheckConstraint("ck_journal_entries_amount", "amount <> 0");
                });

            migrationBuilder.CreateTable(
                name: "journals",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    book = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reference_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reference_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content_hash = table.Column<string>(type: "char(64)", nullable: false),
                    reverses_journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journals", x => x.id);
                    table.CheckConstraint("ck_journals_book", "book IN ('practice', 'real')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounts_book_owner_type_owner_id_kind_asset_code",
                schema: "ledger",
                table: "accounts",
                columns: new[] { "book", "owner_type", "owner_id", "kind", "asset_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_accounts_id_asset_code",
                schema: "ledger",
                table: "accounts",
                columns: new[] { "id", "asset_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_accounts_id_book",
                schema: "ledger",
                table: "accounts",
                columns: new[] { "id", "book" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_journal_id_line_number",
                schema: "ledger",
                table: "journal_entries",
                columns: new[] { "journal_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_journals_id_book",
                schema: "ledger",
                table: "journals",
                columns: new[] { "id", "book" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_journals_idempotency_key",
                schema: "ledger",
                table: "journals",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_journals_reverses_journal_id",
                schema: "ledger",
                table: "journals",
                column: "reverses_journal_id",
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE ledger.accounts
                    ADD CONSTRAINT fk_accounts_asset_code FOREIGN KEY (asset_code) REFERENCES ledger.assets (code);
                ALTER TABLE ledger.account_balances
                    ADD CONSTRAINT fk_account_balances_account_id FOREIGN KEY (account_id) REFERENCES ledger.accounts (id);
                ALTER TABLE ledger.journals
                    ADD CONSTRAINT fk_journals_reverses_journal_id FOREIGN KEY (reverses_journal_id) REFERENCES ledger.journals (id);
                ALTER TABLE ledger.journal_entries
                    ADD CONSTRAINT fk_journal_entries_journal_book FOREIGN KEY (journal_id, book) REFERENCES ledger.journals (id, book),
                    ADD CONSTRAINT fk_journal_entries_account_book FOREIGN KEY (account_id, book) REFERENCES ledger.accounts (id, book),
                    ADD CONSTRAINT fk_journal_entries_account_asset FOREIGN KEY (account_id, asset_code) REFERENCES ledger.accounts (id, asset_code);
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION ledger.enforce_journal_zero_sum() RETURNS trigger
                LANGUAGE plpgsql AS $function$
                DECLARE
                    entry_count integer;
                BEGIN
                    SELECT count(*) INTO entry_count
                    FROM ledger.journal_entries
                    WHERE journal_id = NEW.journal_id;

                    IF entry_count < 2 THEN
                        RAISE EXCEPTION 'ledger journal % must have at least two entries', NEW.journal_id;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM ledger.journal_entries
                        WHERE journal_id = NEW.journal_id
                        GROUP BY asset_code
                        HAVING sum(amount) <> 0
                    ) THEN
                        RAISE EXCEPTION 'ledger journal % does not sum to zero per asset', NEW.journal_id;
                    END IF;

                    RETURN NULL;
                END;
                $function$;

                CREATE CONSTRAINT TRIGGER trg_journal_entries_zero_sum
                AFTER INSERT ON ledger.journal_entries
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION ledger.enforce_journal_zero_sum();
                """);

            migrationBuilder.InsertData(
                table: "assets",
                schema: "ledger",
                columns: new[] { "code", "kind", "scale", "created_at" },
                values: new object[,]
                {
                    { "USD", "fiat", 6, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
                    { "USDC", "stablecoin", 6, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
                });

            AskVest.Platform.SchemaAccess.RestrictToAppendOnly(migrationBuilder, "ledger", "journals");
            AskVest.Platform.SchemaAccess.RestrictToAppendOnly(migrationBuilder, "ledger", "journal_entries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_balances",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "accounts",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "assets",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "journal_entries",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "journals",
                schema: "ledger");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS ledger.enforce_journal_zero_sum();");
        }
    }
}
