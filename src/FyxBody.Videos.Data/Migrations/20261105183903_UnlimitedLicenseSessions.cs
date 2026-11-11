using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FyxBody.Videos.Data.Migrations
{
    /// <inheritdoc />
    public partial class UnlimitedLicenseSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "unlimited_sessions",
                schema: "videos",
                table: "licenses",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "unlimited_sessions",
                schema: "videos",
                table: "licenses");
        }
    }
}
