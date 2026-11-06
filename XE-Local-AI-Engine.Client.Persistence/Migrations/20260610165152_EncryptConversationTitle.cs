using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class EncryptConversationTitle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Clear all existing plaintext titles before altering the column to BLOB.
            // Titles cannot be encrypted inside a migration (no access to the node key), so existing plaintext values
            // are NULLed here and stay NULL (the client shows them as untitled); no startup backfill re-derives them.
            migrationBuilder.Sql("UPDATE conversations SET title = NULL;");

            migrationBuilder.AlterColumn<byte[]>(
                name: "title",
                table: "conversations",
                type: "BLOB",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "title",
                table: "conversations",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(byte[]),
                oldType: "BLOB",
                oldNullable: true);
        }
    }
}
