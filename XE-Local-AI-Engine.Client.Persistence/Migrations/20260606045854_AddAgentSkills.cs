using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddAgentSkills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "allowed_skill_ids_json",
                table: "agent_definitions",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "agent_skills",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    description = table.Column<byte[]>(type: "BLOB", nullable: false),
                    body = table.Column<byte[]>(type: "BLOB", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    version = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_skills", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_skills_name",
                table: "agent_skills",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_skills");

            migrationBuilder.DropColumn(
                name: "allowed_skill_ids_json",
                table: "agent_definitions");
        }
    }
}
