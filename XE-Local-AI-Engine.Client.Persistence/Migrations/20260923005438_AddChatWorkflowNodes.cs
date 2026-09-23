using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddChatWorkflowNodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "graph_workflow_definitions",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Standard");

            migrationBuilder.CreateIndex(
                name: "ix_graph_workflow_definitions_kind",
                table: "graph_workflow_definitions",
                column: "kind");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_graph_workflow_definitions_kind",
                table: "graph_workflow_definitions");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "graph_workflow_definitions");
        }
    }
}
