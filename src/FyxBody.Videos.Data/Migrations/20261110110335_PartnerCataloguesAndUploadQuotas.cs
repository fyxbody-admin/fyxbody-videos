using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FyxBody.Videos.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartnerCataloguesAndUploadQuotas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "owner_partner_id",
                schema: "videos",
                table: "videos",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "partner_reference",
                schema: "videos",
                table: "videos",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "upload_idempotency_key",
                schema: "videos",
                table: "videos",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "upload_license_id",
                schema: "videos",
                table: "videos",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "upload_request_hash",
                schema: "videos",
                table: "videos",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "parent_partner_id",
                schema: "videos",
                table: "partners",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "allow_catalogue_delegation",
                schema: "videos",
                table: "licenses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "allow_partner_uploads",
                schema: "videos",
                table: "licenses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "allow_subpartner_management",
                schema: "videos",
                table: "licenses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "upload_quota_limit",
                schema: "videos",
                table: "licenses",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "upload_quota_mode",
                schema: "videos",
                table: "licenses",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "unlimited");

            migrationBuilder.AddColumn<Guid>(
                name: "source_license_id",
                schema: "videos",
                table: "license_videos",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_videos_owner_partner_id_upload_idempotency_key",
                schema: "videos",
                table: "videos",
                columns: new[] { "owner_partner_id", "upload_idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_videos_upload_license_id_owner_partner_id",
                schema: "videos",
                table: "videos",
                columns: new[] { "upload_license_id", "owner_partner_id" });

            migrationBuilder.CreateIndex(
                name: "ix_partners_parent_partner_id",
                schema: "videos",
                table: "partners",
                column: "parent_partner_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_licenses_upload_quota",
                schema: "videos",
                table: "licenses",
                sql: "(upload_quota_mode = 'unlimited' AND upload_quota_limit IS NULL) OR (upload_quota_mode IN ('storage','videos') AND upload_quota_limit IS NOT NULL AND upload_quota_limit > 0)");

            migrationBuilder.CreateIndex(
                name: "ix_license_videos_source_license_id",
                schema: "videos",
                table: "license_videos",
                column: "source_license_id");

            migrationBuilder.AddForeignKey(
                name: "fk_license_videos_source_license_id",
                schema: "videos",
                table: "license_videos",
                column: "source_license_id",
                principalSchema: "videos",
                principalTable: "licenses",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_partners_parent_partner_id",
                schema: "videos",
                table: "partners",
                column: "parent_partner_id",
                principalSchema: "videos",
                principalTable: "partners",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_videos_owner_partner_id",
                schema: "videos",
                table: "videos",
                column: "owner_partner_id",
                principalSchema: "videos",
                principalTable: "partners",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_videos_upload_license_id_owner_partner_id",
                schema: "videos",
                table: "videos",
                columns: new[] { "upload_license_id", "owner_partner_id" },
                principalSchema: "videos",
                principalTable: "licenses",
                principalColumns: new[] { "id", "partner_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_license_videos_source_license_id",
                schema: "videos",
                table: "license_videos");

            migrationBuilder.DropForeignKey(
                name: "fk_partners_parent_partner_id",
                schema: "videos",
                table: "partners");

            migrationBuilder.DropForeignKey(
                name: "fk_videos_owner_partner_id",
                schema: "videos",
                table: "videos");

            migrationBuilder.DropForeignKey(
                name: "fk_videos_upload_license_id_owner_partner_id",
                schema: "videos",
                table: "videos");

            migrationBuilder.DropIndex(
                name: "ix_videos_owner_partner_id_upload_idempotency_key",
                schema: "videos",
                table: "videos");

            migrationBuilder.DropIndex(
                name: "ix_videos_upload_license_id_owner_partner_id",
                schema: "videos",
                table: "videos");

            migrationBuilder.DropIndex(
                name: "ix_partners_parent_partner_id",
                schema: "videos",
                table: "partners");

            migrationBuilder.DropCheckConstraint(
                name: "ck_licenses_upload_quota",
                schema: "videos",
                table: "licenses");

            migrationBuilder.DropIndex(
                name: "ix_license_videos_source_license_id",
                schema: "videos",
                table: "license_videos");

            migrationBuilder.DropColumn(
                name: "owner_partner_id",
                schema: "videos",
                table: "videos");

            migrationBuilder.DropColumn(
                name: "partner_reference",
                schema: "videos",
                table: "videos");

            migrationBuilder.DropColumn(
                name: "upload_idempotency_key",
                schema: "videos",
                table: "videos");

            migrationBuilder.DropColumn(
                name: "upload_license_id",
                schema: "videos",
                table: "videos");

            migrationBuilder.DropColumn(
                name: "upload_request_hash",
                schema: "videos",
                table: "videos");

            migrationBuilder.DropColumn(
                name: "parent_partner_id",
                schema: "videos",
                table: "partners");

            migrationBuilder.DropColumn(
                name: "allow_catalogue_delegation",
                schema: "videos",
                table: "licenses");

            migrationBuilder.DropColumn(
                name: "allow_partner_uploads",
                schema: "videos",
                table: "licenses");

            migrationBuilder.DropColumn(
                name: "allow_subpartner_management",
                schema: "videos",
                table: "licenses");

            migrationBuilder.DropColumn(
                name: "upload_quota_limit",
                schema: "videos",
                table: "licenses");

            migrationBuilder.DropColumn(
                name: "upload_quota_mode",
                schema: "videos",
                table: "licenses");

            migrationBuilder.DropColumn(
                name: "source_license_id",
                schema: "videos",
                table: "license_videos");
        }
    }
}
