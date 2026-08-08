using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddAgentSkillImportProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "content_sha256",
                table: "agent_skills",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "frontmatter_json",
                table: "agent_skills",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "imported_at_utc",
                table: "agent_skills",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "origin",
                table: "agent_skills",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "source_uri",
                table: "agent_skills",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agent_skill_resources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    skill_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    description = table.Column<string>(type: "TEXT", nullable: false),
                    media_type = table.Column<string>(type: "TEXT", nullable: false),
                    content = table.Column<byte[]>(type: "BLOB", nullable: false),
                    size_bytes = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_skill_resources", x => x.id);
                    table.ForeignKey(
                        name: "FK_agent_skill_resources_agent_skills_skill_id",
                        column: x => x.skill_id,
                        principalTable: "agent_skills",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_skill_resources_skill_id_name",
                table: "agent_skill_resources",
                columns: new[] { "skill_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_skill_resources");

            migrationBuilder.DropColumn(
                name: "content_sha256",
                table: "agent_skills");

            migrationBuilder.DropColumn(
                name: "frontmatter_json",
                table: "agent_skills");

            migrationBuilder.DropColumn(
                name: "imported_at_utc",
                table: "agent_skills");

            migrationBuilder.DropColumn(
                name: "origin",
                table: "agent_skills");

            migrationBuilder.DropColumn(
                name: "source_uri",
                table: "agent_skills");
        }
    }
}
