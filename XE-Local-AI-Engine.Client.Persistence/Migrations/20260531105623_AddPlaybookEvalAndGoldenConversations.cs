using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddPlaybookEvalAndGoldenConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "eval_result",
                table: "playbook_actions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "golden_conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_definition_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    input_turns = table.Column<byte[]>(type: "BLOB", nullable: false),
                    assertion = table.Column<byte[]>(type: "BLOB", nullable: true),
                    rubric = table.Column<byte[]>(type: "BLOB", nullable: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_golden_conversations", x => x.id);
                    table.ForeignKey(
                        name: "FK_golden_conversations_agent_definitions_agent_definition_id",
                        column: x => x.agent_definition_id,
                        principalTable: "agent_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_golden_conversations_agent_definition_id",
                table: "golden_conversations",
                column: "agent_definition_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "golden_conversations");

            migrationBuilder.DropColumn(
                name: "eval_result",
                table: "playbook_actions");
        }
    }
}
