using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AskVest.Modules.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPinLockRepeatCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "pin_lock_repeat_count",
                schema: "identity",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pin_lock_repeat_count",
                schema: "identity",
                table: "users");
        }
    }
}
