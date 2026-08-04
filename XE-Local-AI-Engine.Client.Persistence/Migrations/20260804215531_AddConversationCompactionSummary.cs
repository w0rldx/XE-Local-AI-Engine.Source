using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddConversationCompactionSummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "compaction_summary",
                table: "conversations",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "compaction_summary_covers_to_sequence",
                table: "conversations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "compaction_summary_updated_at_utc",
                table: "conversations",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "compaction_summary",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "compaction_summary_covers_to_sequence",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "compaction_summary_updated_at_utc",
                table: "conversations");
        }
    }
}
