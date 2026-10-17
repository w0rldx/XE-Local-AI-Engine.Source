using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddGraphWorkflowRunOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "key_prefix",
                table: "graph_workflow_runs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "principal_id",
                table: "graph_workflow_runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "trigger_source",
                table: "graph_workflow_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "trigger_sources",
                table: "graph_workflow_definitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "key_prefix",
                table: "graph_workflow_runs");

            migrationBuilder.DropColumn(
                name: "principal_id",
                table: "graph_workflow_runs");

            migrationBuilder.DropColumn(
                name: "trigger_source",
                table: "graph_workflow_runs");

            migrationBuilder.DropColumn(
                name: "trigger_sources",
                table: "graph_workflow_definitions");
        }
    }
}
