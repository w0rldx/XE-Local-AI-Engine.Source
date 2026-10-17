using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddIntegrationGraphTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "target_agent_definition_id",
                table: "integration_triggers",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<Guid>(
                name: "target_graph_workflow_definition_id",
                table: "integration_triggers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "agent_definition_id",
                table: "integration_sessions",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<Guid>(
                name: "graph_workflow_run_id",
                table: "integration_executions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_integration_triggers_one_target",
                table: "integration_triggers",
                sql: "(target_kind = 'Agent' AND target_agent_definition_id IS NOT NULL AND target_graph_workflow_definition_id IS NULL) OR (target_kind = 'Graph' AND target_graph_workflow_definition_id IS NOT NULL AND target_agent_definition_id IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A graph-target trigger has no agent to fall back to, and the older schema cannot express it.
            migrationBuilder.Sql("DELETE FROM integration_triggers WHERE target_kind = 'Graph';");

            migrationBuilder.DropCheckConstraint(
                name: "ck_integration_triggers_one_target",
                table: "integration_triggers");

            migrationBuilder.DropColumn(
                name: "target_graph_workflow_definition_id",
                table: "integration_triggers");

            migrationBuilder.DropColumn(
                name: "graph_workflow_run_id",
                table: "integration_executions");

            migrationBuilder.AlterColumn<Guid>(
                name: "target_agent_definition_id",
                table: "integration_triggers",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "agent_definition_id",
                table: "integration_sessions",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
