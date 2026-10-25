using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeAuthSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "session_id",
                table: "node_refresh_tokens",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "node_auth_sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_used_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    client_label = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    revoked_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_node_auth_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_node_auth_sessions_AspNetUsers_user_id",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_node_refresh_tokens_session_id",
                table: "node_refresh_tokens",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "IX_node_auth_sessions_user_id",
                table: "node_auth_sessions",
                column: "user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_node_refresh_tokens_node_auth_sessions_session_id",
                table: "node_refresh_tokens",
                column: "session_id",
                principalTable: "node_auth_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_node_refresh_tokens_node_auth_sessions_session_id",
                table: "node_refresh_tokens");

            migrationBuilder.DropTable(
                name: "node_auth_sessions");

            migrationBuilder.DropIndex(
                name: "IX_node_refresh_tokens_session_id",
                table: "node_refresh_tokens");

            migrationBuilder.DropColumn(
                name: "session_id",
                table: "node_refresh_tokens");
        }
    }
}
