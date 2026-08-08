using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class RepairAndUniqueMessageSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing databases may already carry duplicate (conversation_id, sequence) pairs from the pre-lock race
            // (two concurrent sends/regenerations that read the same MAX(sequence)+1). Deterministically renumber every
            // conversation's messages to a contiguous, gap-free 0-based order BEFORE creating the unique index, or the
            // CreateIndex below would fail on those rows. The ordering preserves the existing sequence order and breaks
            // ties by created-at then message id, so well-formed data is left in the same order and only genuine
            // collisions are separated. No index exists yet, so the transient renumbering cannot violate a constraint.
            // On a clean or empty database this updates zero rows.
            migrationBuilder.Sql("""
                                 WITH ordered AS (
                                     SELECT message_id,
                                            ROW_NUMBER() OVER (
                                                PARTITION BY conversation_id
                                                ORDER BY sequence, created_at_utc, message_id) - 1 AS new_sequence
                                     FROM messages)
                                 UPDATE messages
                                 SET sequence = (SELECT new_sequence FROM ordered WHERE ordered.message_id = messages.message_id)
                                 WHERE EXISTS (SELECT 1 FROM ordered WHERE ordered.message_id = messages.message_id
                                                 AND ordered.new_sequence <> messages.sequence);
                                 """);

            migrationBuilder.DropIndex(
                name: "IX_messages_conversation_id",
                table: "messages");

            migrationBuilder.CreateIndex(
                name: "IX_messages_conversation_id_sequence",
                table: "messages",
                columns: new[] { "conversation_id", "sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_messages_conversation_id_sequence",
                table: "messages");

            migrationBuilder.CreateIndex(
                name: "IX_messages_conversation_id",
                table: "messages",
                column: "conversation_id");
        }
    }
}
