using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationUploadedFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conversation_uploaded_files",
                columns: table => new
                {
                    file_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    original_file_name = table.Column<byte[]>(type: "BLOB", nullable: false),
                    mime_type = table.Column<string>(type: "TEXT", nullable: false),
                    extension = table.Column<string>(type: "TEXT", nullable: false),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    extraction_status = table.Column<string>(type: "TEXT", nullable: false),
                    extracted_chars = table.Column<int>(type: "INTEGER", nullable: true),
                    storage_path = table.Column<string>(type: "TEXT", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_uploaded_files", x => x.file_id);
                    table.ForeignKey(
                        name: "FK_conversation_uploaded_files_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "conversation_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_uploaded_files_conversation_id",
                table: "conversation_uploaded_files",
                column: "conversation_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_uploaded_files");
        }
    }
}
