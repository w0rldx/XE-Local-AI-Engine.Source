using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddConversationState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "conversation_state",
                table: "conversations",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "conversation_state_covers_to_sequence",
                table: "conversations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "conversation_state_updated_at_utc",
                table: "conversations",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "conversation_state",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "conversation_state_covers_to_sequence",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "conversation_state_updated_at_utc",
                table: "conversations");
        }
    }
}
