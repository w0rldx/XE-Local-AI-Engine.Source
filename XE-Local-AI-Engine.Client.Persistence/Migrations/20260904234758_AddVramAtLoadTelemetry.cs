using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddVramAtLoadTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "vram_admitted_bytes",
                table: "dev_workflow_node_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "vram_free_at_load_bytes",
                table: "dev_workflow_node_runs",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "vram_admitted_bytes",
                table: "dev_workflow_node_runs");

            migrationBuilder.DropColumn(
                name: "vram_free_at_load_bytes",
                table: "dev_workflow_node_runs");
        }
    }
}
