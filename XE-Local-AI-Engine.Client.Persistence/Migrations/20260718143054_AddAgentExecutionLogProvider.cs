using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddAgentExecutionLogProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "provider",
                table: "agent_execution_logs",
                type: "TEXT",
                nullable: false,
                defaultValue: "unknown");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "provider",
                table: "agent_execution_logs");
        }
    }
}
