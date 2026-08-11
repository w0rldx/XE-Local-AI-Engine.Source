using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddModelLaunchArguments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_launch_arguments",
                columns: table => new
                {
                    model_name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    raw_arguments = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_launch_arguments", x => x.model_name);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_launch_arguments");
        }
    }
}
