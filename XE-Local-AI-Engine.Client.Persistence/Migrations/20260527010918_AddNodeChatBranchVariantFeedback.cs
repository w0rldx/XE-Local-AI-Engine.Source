using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeChatBranchVariantFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "parent_message_id",
                table: "messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "variant_group_id",
                table: "messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "branch_of_conversation_id",
                table: "conversations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "message_feedback",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    rating = table.Column<string>(type: "TEXT", nullable: false),
                    comment = table.Column<string>(type: "TEXT", nullable: true),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_feedback", x => x.message_id);
                    table.ForeignKey(
                        name: "FK_message_feedback_messages_message_id",
                        column: x => x.message_id,
                        principalTable: "messages",
                        principalColumn: "message_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_messages_parent_message_id",
                table: "messages",
                column: "parent_message_id");

            migrationBuilder.CreateIndex(
                name: "IX_messages_variant_group_id",
                table: "messages",
                column: "variant_group_id");

            migrationBuilder.CreateIndex(
                name: "IX_message_feedback_conversation_id",
                table: "message_feedback",
                column: "conversation_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_feedback");

            migrationBuilder.DropIndex(
                name: "IX_messages_parent_message_id",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "IX_messages_variant_group_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "parent_message_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "variant_group_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "branch_of_conversation_id",
                table: "conversations");
        }
    }
}
