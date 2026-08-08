using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddPlaybookActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "playbook_enabled",
                table: "agent_definitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "playbook_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_definition_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    state = table.Column<int>(type: "INTEGER", nullable: false),
                    source = table.Column<int>(type: "INTEGER", nullable: false),
                    trigger_condition = table.Column<byte[]>(type: "BLOB", nullable: true),
                    behavior = table.Column<byte[]>(type: "BLOB", nullable: false),
                    scope = table.Column<string>(type: "TEXT", nullable: true),
                    priority = table.Column<int>(type: "INTEGER", nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playbook_actions", x => x.id);
                    table.ForeignKey(
                        name: "FK_playbook_actions_agent_definitions_agent_definition_id",
                        column: x => x.agent_definition_id,
                        principalTable: "agent_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_playbook_actions_agent_definition_id",
                table: "playbook_actions",
                column: "agent_definition_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "playbook_actions");

            migrationBuilder.DropColumn(
                name: "playbook_enabled",
                table: "agent_definitions");
        }
    }
}
