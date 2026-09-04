using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddToolSchemaTokenTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
