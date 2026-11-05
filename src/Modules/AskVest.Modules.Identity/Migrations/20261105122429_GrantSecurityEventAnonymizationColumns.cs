using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.Identity.Migrations
{
    /// <inheritdoc />
    public partial class GrantSecurityEventAnonymizationColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("GRANT UPDATE (ip, user_agent) ON TABLE identity.security_events TO askvest_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE UPDATE (ip, user_agent) ON TABLE identity.security_events FROM askvest_app;");
        }
    }
}
