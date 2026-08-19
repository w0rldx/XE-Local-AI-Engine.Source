using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <summary>
    ///     The only secondary index on <c>conversations</c>, serving both variants of the conversation-list query:
    ///     <c>purged = 0 [AND archived = 0]</c> ordered by <c>is_pinned DESC, last_seen_utc DESC LIMIT n</c>. Pure
    ///     index addition — no column, data or constraint change, so <c>Down</c> is a plain drop.
    /// </summary>
    public partial class AddConversationListIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_conversations_list",
                table: "conversations",
                columns: new[] { "purged", "is_pinned", "last_seen_utc", "archived" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_conversations_list",
                table: "conversations");
        }
    }
}
