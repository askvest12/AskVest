using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class BackfillExternalTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rows created before the column existed carry the empty string,
            // which the policy converter cannot parse. They default to the
            // conservative not_supported value the importer uses.
            migrationBuilder.Sql(
                "UPDATE catalog.tokens SET external_transfers = 'not_supported' WHERE external_transfers = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only data fix: reverting would restore values the
            // policy converter cannot read.
        }
    }
}
