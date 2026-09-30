using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddMcpServerSlugSessionScopeHeaders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "headers",
                table: "mcp_servers",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "session_scope",
                table: "mcp_servers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "slug",
                table: "mcp_servers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_mcp_servers_slug",
                table: "mcp_servers",
                column: "slug",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_mcp_servers_session_scope",
                table: "mcp_servers",
                sql: "session_scope IN (0, 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_mcp_servers_slug",
                table: "mcp_servers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_mcp_servers_session_scope",
                table: "mcp_servers");

            migrationBuilder.DropColumn(
                name: "headers",
                table: "mcp_servers");

            migrationBuilder.DropColumn(
                name: "session_scope",
                table: "mcp_servers");

            migrationBuilder.DropColumn(
                name: "slug",
                table: "mcp_servers");
        }
    }
}
