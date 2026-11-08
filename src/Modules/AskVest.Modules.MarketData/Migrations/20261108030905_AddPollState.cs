using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.MarketData.Migrations
{
    /// <inheritdoc />
    public partial class AddPollState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE marketdata.poll_state (
                    id smallint PRIMARY KEY CHECK (id = 1),
                    last_poll_completed_at timestamp with time zone NOT NULL
                );
                INSERT INTO marketdata.poll_state (id, last_poll_completed_at)
                VALUES (1, '1970-01-01T00:00:00Z');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE marketdata.poll_state;");
        }
    }
}
