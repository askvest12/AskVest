using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class ExternalTransfersDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The add-column migration left a database default of the empty
            // string, which the policy converter cannot read. Inserts that
            // omit the column now get the conservative value instead.
            migrationBuilder.Sql(
                "ALTER TABLE catalog.tokens ALTER COLUMN external_transfers SET DEFAULT 'not_supported';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE catalog.tokens ALTER COLUMN external_transfers SET DEFAULT '';");
        }
    }
}
