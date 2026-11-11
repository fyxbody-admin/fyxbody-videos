using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FyxBody.Videos.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialVideoLicensing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "videos");

            migrationBuilder.CreateTable(
                name: "partners",
                schema: "videos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_partners_id", x => x.id);
                    table.CheckConstraint("ck_partners_code", "code ~ '^[a-z0-9][a-z0-9_-]*$' AND length(btrim(name)) > 0");
                    table.CheckConstraint("ck_partners_status", "status IN ('active','suspended','revoked')");
                });

            migrationBuilder.CreateTable(
                name: "videos",
                schema: "videos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_exercise_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_videos_id", x => x.id);
                    table.CheckConstraint("ck_videos_dates", "updated_at >= created_at");
                    table.CheckConstraint("ck_videos_identity", "length(btrim(external_exercise_id)) > 0 AND length(btrim(title)) > 0");
                    table.CheckConstraint("ck_videos_status", "status IN ('draft','active','archived')");
                });

            migrationBuilder.CreateTable(
                name: "licenses",
                schema: "videos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    partner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    monthly_session_quota = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_licenses_id", x => x.id);
                    table.UniqueConstraint("ak_licenses_id_partner_id", x => new { x.id, x.partner_id });
                    table.CheckConstraint("ck_licenses_dates", "ends_at > starts_at");
                    table.CheckConstraint("ck_licenses_name", "length(btrim(name)) > 0");
                    table.CheckConstraint("ck_licenses_quota", "monthly_session_quota >= 0");
                    table.CheckConstraint("ck_licenses_status", "status IN ('active','suspended','revoked')");
                    table.ForeignKey(
                        name: "fk_licenses_partner_id",
                        column: x => x.partner_id,
                        principalSchema: "videos",
                        principalTable: "partners",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "partner_credentials",
                schema: "videos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    partner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_partner_credentials_id", x => x.id);
                    table.CheckConstraint("ck_partner_credentials_dates", "(expires_at IS NULL OR expires_at > created_at) AND (revoked_at IS NULL OR revoked_at >= created_at)");
                    table.CheckConstraint("ck_partner_credentials_hash", "length(btrim(secret_hash)) >= 32 AND length(btrim(key_id)) > 0");
                    table.ForeignKey(
                        name: "fk_partner_credentials_partner_id",
                        column: x => x.partner_id,
                        principalSchema: "videos",
                        principalTable: "partners",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "video_assets",
                schema: "videos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    video_id = table.Column<Guid>(type: "uuid", nullable: false),
                    language = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    rendition = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    blob_container = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    blob_name = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_video_assets_id", x => x.id);
                    table.UniqueConstraint("ak_video_assets_id_video_id", x => new { x.id, x.video_id });
                    table.CheckConstraint("ck_video_assets_blob", "length(btrim(blob_container)) > 0 AND length(btrim(blob_name)) > 0");
                    table.CheckConstraint("ck_video_assets_language", "language ~ '^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$' AND length(btrim(rendition)) > 0");
                    table.CheckConstraint("ck_video_assets_sizes", "size_bytes >= 0 AND duration_ms >= 0");
                    table.CheckConstraint("ck_video_assets_status", "status IN ('pending','ready','disabled')");
                    table.ForeignKey(
                        name: "fk_video_assets_video_id",
                        column: x => x.video_id,
                        principalSchema: "videos",
                        principalTable: "videos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "license_videos",
                schema: "videos",
                columns: table => new
                {
                    license_id = table.Column<Guid>(type: "uuid", nullable: false),
                    video_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_license_videos_license_id_video_id", x => new { x.license_id, x.video_id });
                    table.ForeignKey(
                        name: "fk_license_videos_license_id",
                        column: x => x.license_id,
                        principalSchema: "videos",
                        principalTable: "licenses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_license_videos_video_id",
                        column: x => x.video_id,
                        principalSchema: "videos",
                        principalTable: "videos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "usage_monthly",
                schema: "videos",
                columns: table => new
                {
                    partner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    license_id = table.Column<Guid>(type: "uuid", nullable: false),
                    month = table.Column<DateOnly>(type: "date", nullable: false),
                    sessions_issued = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_monthly_partner_id_license_id_month", x => new { x.partner_id, x.license_id, x.month });
                    table.CheckConstraint("ck_usage_monthly_count", "sessions_issued >= 0");
                    table.CheckConstraint("ck_usage_monthly_month", "extract(day from month) = 1");
                    table.ForeignKey(
                        name: "fk_usage_monthly_license_id_partner_id",
                        columns: x => new { x.license_id, x.partner_id },
                        principalSchema: "videos",
                        principalTable: "licenses",
                        principalColumns: new[] { "id", "partner_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "playback_sessions",
                schema: "videos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    partner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    license_id = table.Column<Guid>(type: "uuid", nullable: false),
                    video_id = table.Column<Guid>(type: "uuid", nullable: false),
                    video_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    usage_month = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_playback_sessions_id", x => x.id);
                    table.CheckConstraint("ck_playback_sessions_dates", "expires_at > created_at");
                    table.CheckConstraint("ck_playback_sessions_month", "extract(day from usage_month) = 1 AND usage_month = date_trunc('month', created_at AT TIME ZONE 'UTC')::date");
                    table.CheckConstraint("ck_playback_sessions_request", "length(btrim(idempotency_key)) > 0 AND request_hash ~ '^[a-f0-9]{64}$'");
                    table.CheckConstraint("ck_playback_sessions_status", "status IN ('issued','revoked','expired')");
                    table.ForeignKey(
                        name: "fk_playback_sessions_license_id_partner_id",
                        columns: x => new { x.license_id, x.partner_id },
                        principalSchema: "videos",
                        principalTable: "licenses",
                        principalColumns: new[] { "id", "partner_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_playback_sessions_license_id_video_id",
                        columns: x => new { x.license_id, x.video_id },
                        principalSchema: "videos",
                        principalTable: "license_videos",
                        principalColumns: new[] { "license_id", "video_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_playback_sessions_video_asset_id_video_id",
                        columns: x => new { x.video_asset_id, x.video_id },
                        principalSchema: "videos",
                        principalTable: "video_assets",
                        principalColumns: new[] { "id", "video_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "playback_events",
                schema: "videos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    playback_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    position_ms = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_playback_events_id", x => x.id);
                    table.CheckConstraint("ck_playback_events_key", "length(btrim(event_key)) > 0");
                    table.CheckConstraint("ck_playback_events_kind", "kind IN ('started','progress','completed','failed')");
                    table.CheckConstraint("ck_playback_events_position", "position_ms >= 0");
                    table.ForeignKey(
                        name: "fk_playback_events_playback_session_id",
                        column: x => x.playback_session_id,
                        principalSchema: "videos",
                        principalTable: "playback_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_license_videos_video_id",
                schema: "videos",
                table: "license_videos",
                column: "video_id");

            migrationBuilder.CreateIndex(
                name: "ix_licenses_partner_id_status_starts_at",
                schema: "videos",
                table: "licenses",
                columns: new[] { "partner_id", "status", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ix_partner_credentials_key_id",
                schema: "videos",
                table: "partner_credentials",
                column: "key_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_partner_credentials_partner_id",
                schema: "videos",
                table: "partner_credentials",
                column: "partner_id");

            migrationBuilder.CreateIndex(
                name: "ix_partners_code",
                schema: "videos",
                table: "partners",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_playback_events_created_at",
                schema: "videos",
                table: "playback_events",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_playback_events_playback_session_id_event_key",
                schema: "videos",
                table: "playback_events",
                columns: new[] { "playback_session_id", "event_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_playback_sessions_expires_at",
                schema: "videos",
                table: "playback_sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_playback_sessions_license_id_partner_id",
                schema: "videos",
                table: "playback_sessions",
                columns: new[] { "license_id", "partner_id" });

            migrationBuilder.CreateIndex(
                name: "ix_playback_sessions_license_id_video_id",
                schema: "videos",
                table: "playback_sessions",
                columns: new[] { "license_id", "video_id" });

            migrationBuilder.CreateIndex(
                name: "ix_playback_sessions_partner_id_created_at",
                schema: "videos",
                table: "playback_sessions",
                columns: new[] { "partner_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_playback_sessions_partner_id_idempotency_key",
                schema: "videos",
                table: "playback_sessions",
                columns: new[] { "partner_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_playback_sessions_video_asset_id_video_id",
                schema: "videos",
                table: "playback_sessions",
                columns: new[] { "video_asset_id", "video_id" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_monthly_license_id_partner_id",
                schema: "videos",
                table: "usage_monthly",
                columns: new[] { "license_id", "partner_id" });

            migrationBuilder.CreateIndex(
                name: "ix_video_assets_blob_container_blob_name",
                schema: "videos",
                table: "video_assets",
                columns: new[] { "blob_container", "blob_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_video_assets_video_id_language_rendition",
                schema: "videos",
                table: "video_assets",
                columns: new[] { "video_id", "language", "rendition" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_videos_external_exercise_id",
                schema: "videos",
                table: "videos",
                column: "external_exercise_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "partner_credentials",
                schema: "videos");

            migrationBuilder.DropTable(
                name: "playback_events",
                schema: "videos");

            migrationBuilder.DropTable(
                name: "usage_monthly",
                schema: "videos");

            migrationBuilder.DropTable(
                name: "playback_sessions",
                schema: "videos");

            migrationBuilder.DropTable(
                name: "license_videos",
                schema: "videos");

            migrationBuilder.DropTable(
                name: "video_assets",
                schema: "videos");

            migrationBuilder.DropTable(
                name: "licenses",
                schema: "videos");

            migrationBuilder.DropTable(
                name: "videos",
                schema: "videos");

            migrationBuilder.DropTable(
                name: "partners",
                schema: "videos");
        }
    }
}
