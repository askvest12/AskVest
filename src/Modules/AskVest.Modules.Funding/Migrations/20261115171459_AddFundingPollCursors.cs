using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.Funding.Migrations
{
    /// <inheritdoc />
    public partial class AddFundingPollCursors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "poll_cursors",
                schema: "funding",
                columns: table => new
                {
                    provider_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    cursor = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_poll_cursors", x => x.provider_key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "poll_cursors",
                schema: "funding");
        }
    }
}
