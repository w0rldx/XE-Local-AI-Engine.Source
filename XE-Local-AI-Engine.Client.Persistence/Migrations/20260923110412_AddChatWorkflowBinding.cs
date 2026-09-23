using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddChatWorkflowBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "conversation_id",
                table: "graph_workflow_runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "trigger_message_id",
                table: "graph_workflow_runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "published_message_id",
                table: "graph_workflow_node_runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_graph_workflow_runs_conversation_created",
                table: "graph_workflow_runs",
                columns: new[] { "conversation_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_graph_workflow_runs_live_conversation",
                table: "graph_workflow_runs",
                column: "conversation_id",
                unique: true,
                filter: "\"conversation_id\" IS NOT NULL AND \"status\" IN ('Pending', 'Running', 'WaitingForApproval', 'Cancelling')");

            migrationBuilder.AddForeignKey(
                name: "FK_graph_workflow_runs_conversations_conversation_id",
                table: "graph_workflow_runs",
                column: "conversation_id",
                principalTable: "conversations",
                principalColumn: "conversation_id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_graph_workflow_runs_conversations_conversation_id",
                table: "graph_workflow_runs");

            migrationBuilder.DropIndex(
                name: "ix_graph_workflow_runs_conversation_created",
                table: "graph_workflow_runs");

            migrationBuilder.DropIndex(
                name: "ux_graph_workflow_runs_live_conversation",
                table: "graph_workflow_runs");

            migrationBuilder.DropColumn(
                name: "conversation_id",
                table: "graph_workflow_runs");

            migrationBuilder.DropColumn(
                name: "trigger_message_id",
                table: "graph_workflow_runs");

            migrationBuilder.DropColumn(
                name: "published_message_id",
                table: "graph_workflow_node_runs");
        }
    }
}
