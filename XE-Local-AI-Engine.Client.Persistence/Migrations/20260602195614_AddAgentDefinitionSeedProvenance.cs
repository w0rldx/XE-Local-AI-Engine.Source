using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddAgentDefinitionSeedProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "seed_slug",
                table: "agent_definitions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "source",
                table: "agent_definitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_agent_definitions_seed_slug",
                table: "agent_definitions",
                column: "seed_slug",
                unique: true,
                filter: "\"seed_slug\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_agent_definitions_seed_slug",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "seed_slug",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "source",
                table: "agent_definitions");
        }
    }
}
