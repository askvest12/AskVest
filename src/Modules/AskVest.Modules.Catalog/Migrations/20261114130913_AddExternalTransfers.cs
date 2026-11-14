using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "external_transfers",
                schema: "catalog",
                table: "tokens",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "external_transfers",
                schema: "catalog",
                table: "tokens");
        }
    }
}
