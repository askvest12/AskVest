using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.MarketData.Migrations
{
    /// <inheritdoc />
    public partial class AddLatestPriceUnmappedMarker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "unmapped_at",
                schema: "marketdata",
                table: "latest_prices",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "unmapped_at",
                schema: "marketdata",
                table: "latest_prices");
        }
    }
}
