using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddModelClassifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_classifications",
                columns: table => new
                {
                    model_name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    digest = table.Column<string>(type: "TEXT", nullable: true),
                    detected_kind = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    detected_capabilities_json = table.Column<string>(type: "TEXT", nullable: true),
                    override_kind = table.Column<int>(type: "INTEGER", nullable: true),
                    detected_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_classifications", x => x.model_name);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_classifications");
        }
    }
}
