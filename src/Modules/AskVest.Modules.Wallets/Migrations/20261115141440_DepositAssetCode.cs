using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.Wallets.Migrations
{
    /// <inheritdoc />
    public partial class DepositAssetCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "asset_code",
                schema: "wallets",
                table: "deposits",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "asset_code",
                schema: "wallets",
                table: "deposits");
        }
    }
}
