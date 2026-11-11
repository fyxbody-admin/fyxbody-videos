using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FyxBody.Videos.Data.Migrations
{
    /// <inheritdoc />
    public partial class PortalUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "portal_users",
                schema: "videos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    normalized_username = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    partner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    must_change_password = table.Column<bool>(type: "boolean", nullable: false),
                    password_hash = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    security_stamp = table.Column<Guid>(type: "uuid", nullable: false),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false),
                    lockout_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_portal_users_id", x => x.id);
                    table.CheckConstraint("ck_portal_users_dates", "updated_at >= created_at");
                    table.CheckConstraint("ck_portal_users_failures", "failed_login_count >= 0 AND failed_login_count <= 5");
                    table.CheckConstraint("ck_portal_users_name", "length(btrim(display_name)) > 0");
                    table.CheckConstraint("ck_portal_users_password", "length(password_hash) >= 32 AND security_stamp <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_portal_users_role", "(role = 'admin' AND partner_id IS NULL) OR (role = 'partner' AND partner_id IS NOT NULL)");
                    table.CheckConstraint("ck_portal_users_username", "username ~ '^[A-Za-z0-9][A-Za-z0-9@._+-]{2,127}$' AND normalized_username = upper(username)");
                    table.ForeignKey(
                        name: "fk_portal_users_partner_id",
                        column: x => x.partner_id,
                        principalSchema: "videos",
                        principalTable: "partners",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_portal_users_normalized_username",
                schema: "videos",
                table: "portal_users",
                column: "normalized_username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_portal_users_partner_id",
                schema: "videos",
                table: "portal_users",
                column: "partner_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "portal_users",
                schema: "videos");
        }
    }
}
