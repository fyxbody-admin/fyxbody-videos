using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FyxBody.Videos.Data.Migrations
{
    /// <inheritdoc />
    public partial class YouTubeImportJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "video_import_jobs",
                schema: "videos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    youtube_id = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                    source_url = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    stage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    progress_percent = table.Column<int>(type: "integer", nullable: false),
                    video_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_heights = table.Column<int[]>(type: "integer[]", nullable: false),
                    completed_heights = table.Column<int[]>(type: "integer[]", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_video_import_jobs_id", x => x.id);
                    table.CheckConstraint("ck_video_import_jobs_dates", "updated_at >= created_at");
                    table.CheckConstraint("ck_video_import_jobs_heights", "cardinality(requested_heights) BETWEEN 1 AND 4 AND requested_heights <@ ARRAY[1080,720,480,360] AND cardinality(completed_heights) <= 4 AND 0 < ALL(completed_heights)");
                    table.CheckConstraint("ck_video_import_jobs_identity", "youtube_id ~ '^[A-Za-z0-9_-]{11}$' AND source_url = 'https://www.youtube.com/watch?v=' || youtube_id");
                    table.CheckConstraint("ck_video_import_jobs_lease", "(status = 'running' AND lease_id IS NOT NULL AND lease_expires_at IS NOT NULL) OR (status <> 'running' AND lease_id IS NULL AND lease_expires_at IS NULL)");
                    table.CheckConstraint("ck_video_import_jobs_progress", "progress_percent BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_video_import_jobs_stage", "stage IN ('queued','probing','downloading','transcoding','uploading','completed','failed')");
                    table.CheckConstraint("ck_video_import_jobs_status", "status IN ('queued','running','completed','failed')");
                    table.ForeignKey(
                        name: "fk_video_import_jobs_video_id",
                        column: x => x.video_id,
                        principalSchema: "videos",
                        principalTable: "videos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_video_import_jobs_lease_expires_at",
                schema: "videos",
                table: "video_import_jobs",
                column: "lease_expires_at",
                filter: "status = 'running'");

            migrationBuilder.CreateIndex(
                name: "ix_video_import_jobs_status_created_at",
                schema: "videos",
                table: "video_import_jobs",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_video_import_jobs_video_id",
                schema: "videos",
                table: "video_import_jobs",
                column: "video_id");

            migrationBuilder.CreateIndex(
                name: "ix_video_import_jobs_youtube_id",
                schema: "videos",
                table: "video_import_jobs",
                column: "youtube_id",
                unique: true,
                filter: "status IN ('queued','running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "video_import_jobs",
                schema: "videos");
        }
    }
}
