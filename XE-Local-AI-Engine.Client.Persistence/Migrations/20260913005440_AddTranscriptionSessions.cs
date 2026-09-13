using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddTranscriptionSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "transcription_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    title = table.Column<byte[]>(type: "BLOB", nullable: true),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    source_kind = table.Column<int>(type: "INTEGER", nullable: false),
                    model_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    config_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    detected_language = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    duration_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    error_code = table.Column<byte[]>(type: "BLOB", nullable: true),
                    error_message = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transcription_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "transcript_segments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    session_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    seq = table.Column<long>(type: "INTEGER", nullable: false),
                    start_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    end_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    text = table.Column<byte[]>(type: "BLOB", nullable: false),
                    channel = table.Column<int>(type: "INTEGER", nullable: false),
                    confidence = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transcript_segments", x => x.id);
                    table.ForeignKey(
                        name: "FK_transcript_segments_transcription_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "transcription_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_transcript_segments_session_seq",
                table: "transcript_segments",
                columns: new[] { "session_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_transcription_sessions_created_at_utc",
                table: "transcription_sessions",
                column: "created_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_transcription_sessions_status",
                table: "transcription_sessions",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "transcript_segments");

            migrationBuilder.DropTable(
                name: "transcription_sessions");
        }
    }
}
