using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.MarketData.Migrations
{
    /// <inheritdoc />
    public partial class InitialMarketData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "marketdata");

            // The history table must exist before provisioning grants SELECT on it;
            // EF creates it lazily at the first history insert, which is too late.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS marketdata.__ef_migrations_history (
                    "MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL,
                    CONSTRAINT "__ef_migrations_history_pkey" PRIMARY KEY ("MigrationId")
                );
                """);

            AskVest.Platform.SchemaAccess.ProvisionSchema(migrationBuilder, "marketdata", "__ef_migrations_history");

            migrationBuilder.CreateTable(
                name: "candles",
                schema: "marketdata",
                columns: table => new
                {
                    subject_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    subject_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    interval = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    open_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    open = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    high = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    low = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    close = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    volume = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    adjusted = table.Column<bool>(type: "boolean", nullable: false),
                    partial = table.Column<bool>(type: "boolean", nullable: false),
                    provider_key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_candles", x => new { x.subject_type, x.subject_id, x.interval, x.open_time });
                    table.CheckConstraint("ck_candles_high_low", "high >= low");
                    table.CheckConstraint("ck_candles_interval", "interval IN ('15m', '1h', '1d')");
                    table.CheckConstraint("ck_candles_ohlc", "open > 0 AND high > 0 AND low > 0 AND close > 0");
                    table.CheckConstraint("ck_candles_subject_type", "subject_type = 'instrument'");
                    table.CheckConstraint("ck_candles_volume", "volume >= 0");
                });

            migrationBuilder.CreateTable(
                name: "latest_prices",
                schema: "marketdata",
                columns: table => new
                {
                    subject_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    subject_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    price = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    as_of = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    session = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    previous_close = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: true),
                    provider_key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    delay_minutes = table.Column<int>(type: "integer", nullable: false),
                    suspect_price = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: true),
                    suspect_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_latest_prices", x => new { x.subject_type, x.subject_id });
                    table.CheckConstraint("ck_latest_prices_delay", "delay_minutes >= 0");
                    table.CheckConstraint("ck_latest_prices_previous_close", "previous_close IS NULL OR previous_close > 0");
                    table.CheckConstraint("ck_latest_prices_price", "price > 0");
                    table.CheckConstraint("ck_latest_prices_session", "session IN ('pre', 'regular', 'post', 'closed', 'halted', 'unknown')");
                    table.CheckConstraint("ck_latest_prices_subject_type", "subject_type = 'instrument'");
                    table.CheckConstraint("ck_latest_prices_suspect_price", "suspect_price IS NULL OR suspect_price > 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_latest_prices_as_of",
                schema: "marketdata",
                table: "latest_prices",
                column: "as_of");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "candles",
                schema: "marketdata");

            migrationBuilder.DropTable(
                name: "latest_prices",
                schema: "marketdata");
        }
    }
}
