using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddGraphWorkflowNodeRunDevelopmentTask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "development_task_id",
                table: "graph_workflow_node_runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_graph_workflow_node_runs_development_task",
                table: "graph_workflow_node_runs",
                column: "development_task_id",
                filter: "\"development_task_id\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_graph_workflow_node_runs_development_task",
                table: "graph_workflow_node_runs");

            migrationBuilder.DropColumn(
                name: "development_task_id",
                table: "graph_workflow_node_runs");
        }
    }
}
