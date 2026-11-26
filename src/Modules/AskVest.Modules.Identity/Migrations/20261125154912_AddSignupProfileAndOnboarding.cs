using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddSignupProfileAndOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "full_name",
                schema: "identity",
                table: "users",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "onboarding_profiles",
                schema: "identity",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    investing_familiarity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    goal_flags = table.Column<long>(type: "bigint", nullable: false),
                    preferred_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_onboarding_profiles", x => x.user_id);
                    table.CheckConstraint("ck_onboarding_profiles_familiarity", "investing_familiarity IS NULL OR investing_familiarity IN ('Newcomer', 'Some', 'Experienced')");
                    table.ForeignKey(
                        name: "fk_onboarding_profiles_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "onboarding_profiles",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "full_name",
                schema: "identity",
                table: "users");
        }
    }
}
