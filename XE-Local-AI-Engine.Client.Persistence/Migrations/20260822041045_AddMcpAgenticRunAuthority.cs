using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddMcpAgenticRunAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_agentic_auto_approve",
                table: "mcp_agent_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "requesting_key_prefix",
                table: "mcp_agent_runs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_mcp_agent_runs_agentic_authority",
                table: "mcp_agent_runs",
                sql: "(is_agentic_auto_approve = 0 AND requesting_key_prefix IS NULL) OR (is_agentic_auto_approve = 1 AND requesting_key_prefix IS NOT NULL AND length(requesting_key_prefix) BETWEEN 1 AND 32 AND requesting_key_prefix NOT GLOB '*[^A-Za-z0-9_-]*')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_mcp_agent_runs_agentic_authority",
                table: "mcp_agent_runs");

            migrationBuilder.DropColumn(
                name: "is_agentic_auto_approve",
                table: "mcp_agent_runs");

            migrationBuilder.DropColumn(
                name: "requesting_key_prefix",
                table: "mcp_agent_runs");
        }
    }
}
