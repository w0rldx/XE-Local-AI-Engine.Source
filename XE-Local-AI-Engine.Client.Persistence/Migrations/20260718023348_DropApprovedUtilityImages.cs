using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class DropApprovedUtilityImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approved_utility_images");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "approved_utility_images",
                columns: table => new
                {
                    approved_image_id = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    deprecated_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    diagnostics_json = table.Column<string>(type: "TEXT", nullable: true),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    image_reference = table.Column<string>(type: "TEXT", nullable: false),
                    last_successful_run_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    last_used_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    purpose = table.Column<int>(type: "INTEGER", nullable: false),
                    replacement_approved_image_id = table.Column<string>(type: "TEXT", nullable: true),
                    source_url = table.Column<string>(type: "TEXT", nullable: true),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    upstream_version = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approved_utility_images", x => x.approved_image_id);
                });
        }
    }
}
