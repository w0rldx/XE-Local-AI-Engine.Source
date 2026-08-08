#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations;

using Microsoft.EntityFrameworkCore.Migrations;

/// <inheritdoc />
public partial class InitialNodeChatSchema : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("conversations",
            table => new
            {
                conversation_id = table.Column<Guid>("TEXT", nullable: false),
                title = table.Column<string>("TEXT", nullable: true),
                user_id = table.Column<string>("TEXT", nullable: true),
                created_at_utc = table.Column<long>("INTEGER", nullable: false),
                last_seen_utc = table.Column<long>("INTEGER", nullable: false),
                purged = table.Column<bool>("INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_conversations", x => x.conversation_id);
            });

        migrationBuilder.CreateTable("purged_tombstones",
            table => new
            {
                conversation_id = table.Column<Guid>("TEXT", nullable: false),
                purged_at_utc = table.Column<long>("INTEGER", nullable: false),
                acked_at_utc = table.Column<long>("INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_purged_tombstones", x => x.conversation_id);
            });

        migrationBuilder.CreateTable("messages",
            table => new
            {
                message_id = table.Column<Guid>("TEXT", nullable: false),
                conversation_id = table.Column<Guid>("TEXT", nullable: false),
                sequence = table.Column<int>("INTEGER", nullable: false),
                role = table.Column<string>("TEXT", nullable: false),
                content = table.Column<byte[]>("BLOB", nullable: false),
                metadata_json = table.Column<byte[]>("BLOB", nullable: true),
                created_at_utc = table.Column<long>("INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_messages", x => x.message_id);
                table.ForeignKey("FK_messages_conversations_conversation_id",
                    x => x.conversation_id,
                    "conversations",
                    "conversation_id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable("tool_events",
            table => new
            {
                tool_call_id = table.Column<Guid>("TEXT", nullable: false),
                conversation_id = table.Column<Guid>("TEXT", nullable: false),
                tool_name = table.Column<string>("TEXT", nullable: false),
                plaintext_args = table.Column<byte[]>("BLOB", nullable: true),
                plaintext_result = table.Column<byte[]>("BLOB", nullable: true),
                status = table.Column<string>("TEXT", nullable: false),
                created_at_utc = table.Column<long>("INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tool_events", x => x.tool_call_id);
                table.ForeignKey("FK_tool_events_conversations_conversation_id",
                    x => x.conversation_id,
                    "conversations",
                    "conversation_id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_messages_conversation_id",
            "messages",
            "conversation_id");

        migrationBuilder.CreateIndex("IX_tool_events_conversation_id",
            "tool_events",
            "conversation_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("messages");

        migrationBuilder.DropTable("purged_tombstones");

        migrationBuilder.DropTable("tool_events");

        migrationBuilder.DropTable("conversations");
    }
}
