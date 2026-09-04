using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddAiTrendsWave : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "agent_turn_ms",
                table: "dev_workflow_node_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "estimated_input_tokens",
                table: "dev_workflow_node_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "input_tokens",
                table: "dev_workflow_node_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "output_tokens",
                table: "dev_workflow_node_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "provider_calls",
                table: "dev_workflow_node_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "reasoning_tokens",
                table: "dev_workflow_node_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "route_json",
                table: "dev_workflow_node_runs",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "served_model_name",
                table: "dev_workflow_node_runs",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "tool_calls",
                table: "dev_workflow_node_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tool_names_json",
                table: "dev_workflow_node_runs",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "tool_schema_tokens",
                table: "dev_workflow_node_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "work_session_steps",
                table: "dev_workflow_node_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "primary_launch_identity_scheme",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "launch_identity_scheme",
                table: "benchmark_judge_attempts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "launch_identity_scheme",
                table: "benchmark_comparisons",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "authored_effort",
                table: "agent_execution_logs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "dispatched_tier",
                table: "agent_execution_logs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_tool_schema_tokens",
                table: "agent_execution_logs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "tool_schema_tokens",
                table: "agent_execution_logs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "disable_tool_relevance_filter",
                table: "agent_definitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "agent_turn_ms",
                table: "dev_workflow_node_runs");

            migrationBuilder.DropColumn(
                name: "estimated_input_tokens",
                table: "dev_workflow_node_runs");

            migrationBuilder.DropColumn(
                name: "input_tokens",
                table: "dev_workflow_node_runs");

            migrationBuilder.DropColumn(
                name: "output_tokens",
                table: "dev_workflow_node_runs");

            migrationBuilder.DropColumn(
                name: "provider_calls",
                table: "dev_workflow_node_runs");

            migrationBuilder.DropColumn(
                name: "reasoning_tokens",
                table: "dev_workflow_node_runs");

            migrationBuilder.DropColumn(
                name: "route_json",
                table: "dev_workflow_node_runs");

            migrationBuilder.DropColumn(
                name: "served_model_name",
                table: "dev_workflow_node_runs");

            migrationBuilder.DropColumn(
                name: "tool_calls",
                table: "dev_workflow_node_runs");

            migrationBuilder.DropColumn(
                name: "tool_names_json",
                table: "dev_workflow_node_runs");

            migrationBuilder.DropColumn(
                name: "tool_schema_tokens",
                table: "dev_workflow_node_runs");

            migrationBuilder.DropColumn(
                name: "work_session_steps",
                table: "dev_workflow_node_runs");

            migrationBuilder.DropColumn(
                name: "primary_launch_identity_scheme",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "launch_identity_scheme",
                table: "benchmark_judge_attempts");

            migrationBuilder.DropColumn(
                name: "launch_identity_scheme",
                table: "benchmark_comparisons");

            migrationBuilder.DropColumn(
                name: "authored_effort",
                table: "agent_execution_logs");

            migrationBuilder.DropColumn(
                name: "dispatched_tier",
                table: "agent_execution_logs");

            migrationBuilder.DropColumn(
                name: "max_tool_schema_tokens",
                table: "agent_execution_logs");

            migrationBuilder.DropColumn(
                name: "tool_schema_tokens",
                table: "agent_execution_logs");

            migrationBuilder.DropColumn(
                name: "disable_tool_relevance_filter",
                table: "agent_definitions");
        }
    }
}
