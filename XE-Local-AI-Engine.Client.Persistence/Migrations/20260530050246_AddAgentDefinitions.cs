using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddAgentDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "agent_definition_id",
                table: "conversations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agent_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<byte[]>(type: "BLOB", nullable: true),
                    instructions = table.Column<byte[]>(type: "BLOB", nullable: false),
                    model_profile = table.Column<string>(type: "TEXT", nullable: true),
                    reasoning_effort = table.Column<string>(type: "TEXT", nullable: true),
                    kind = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    allowed_tool_names_json = table.Column<string>(type: "TEXT", nullable: false),
                    tool_approvals_json = table.Column<string>(type: "TEXT", nullable: false),
                    orchestration_topology_json = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_definitions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_definitions_name",
                table: "agent_definitions",
                column: "name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "agent_definition_id",
                table: "conversations");
        }
    }
}
