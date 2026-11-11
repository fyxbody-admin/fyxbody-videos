using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FyxBody.Videos.Data.Migrations
{
    /// <inheritdoc />
    public partial class RevokeCatalogGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "revoked_at",
                schema: "videos",
                table: "license_videos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_license_videos_revoked",
                schema: "videos",
                table: "license_videos",
                sql: "revoked_at IS NULL OR revoked_at >= created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_license_videos_revoked",
                schema: "videos",
                table: "license_videos");

            migrationBuilder.DropColumn(
                name: "revoked_at",
                schema: "videos",
                table: "license_videos");
        }
    }
}
