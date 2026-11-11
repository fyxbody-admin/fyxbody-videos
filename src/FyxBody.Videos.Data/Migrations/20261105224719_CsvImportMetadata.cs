using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FyxBody.Videos.Data.Migrations
{
    /// <inheritdoc />
    public partial class CsvImportMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reference",
                schema: "videos",
                table: "video_import_jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requested_title",
                schema: "videos",
                table: "video_import_jobs",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_video_import_jobs_reference",
                schema: "videos",
                table: "video_import_jobs",
                column: "reference",
                unique: true,
                filter: "status IN ('queued','running') AND reference IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_video_import_jobs_metadata",
                schema: "videos",
                table: "video_import_jobs",
                sql: "(reference IS NULL OR length(btrim(reference)) BETWEEN 1 AND 128) AND (requested_title IS NULL OR length(btrim(requested_title)) BETWEEN 1 AND 300)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_video_import_jobs_reference",
                schema: "videos",
                table: "video_import_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_video_import_jobs_metadata",
                schema: "videos",
                table: "video_import_jobs");

            migrationBuilder.DropColumn(
                name: "reference",
                schema: "videos",
                table: "video_import_jobs");

            migrationBuilder.DropColumn(
                name: "requested_title",
                schema: "videos",
                table: "video_import_jobs");
        }
    }
}
