using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddPlaybookActionAnalysisColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "confidence",
                table: "playbook_actions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_feedback_ids",
                table: "playbook_actions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "confidence",
                table: "playbook_actions");

            migrationBuilder.DropColumn(
                name: "source_feedback_ids",
                table: "playbook_actions");
        }
    }
}
