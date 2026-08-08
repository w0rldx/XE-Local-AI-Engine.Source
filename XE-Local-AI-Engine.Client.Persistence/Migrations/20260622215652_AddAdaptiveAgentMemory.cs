using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddAdaptiveAgentMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "memory_scope",
                table: "playbook_actions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "memory_excluded",
                table: "conversations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "default_temporary_chat",
                table: "agent_definitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "memory_extraction_enabled",
                table: "agent_definitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "agent_execution_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_definition_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    message_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    model_name = table.Column<string>(type: "TEXT", nullable: false),
                    config_hash = table.Column<string>(type: "TEXT", nullable: false),
                    latency_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    prompt_tokens = table.Column<int>(type: "INTEGER", nullable: true),
                    completion_tokens = table.Column<int>(type: "INTEGER", nullable: true),
                    success = table.Column<bool>(type: "INTEGER", nullable: false),
                    error_class = table.Column<string>(type: "TEXT", nullable: true),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_execution_logs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_execution_logs_agent_definition_id_created_at_utc",
                table: "agent_execution_logs",
                columns: new[] { "agent_definition_id", "created_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_execution_logs");

            migrationBuilder.DropColumn(
                name: "memory_scope",
                table: "playbook_actions");

            migrationBuilder.DropColumn(
                name: "memory_excluded",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "default_temporary_chat",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "memory_extraction_enabled",
                table: "agent_definitions");
        }
    }
}
