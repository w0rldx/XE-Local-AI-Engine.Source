using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddMcpServerApiKeyScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "generation_id",
                table: "mcp_server_api_keys",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "scope",
                table: "mcp_server_api_keys",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_mcp_server_api_keys_scope",
                table: "mcp_server_api_keys",
                sql: "scope IN (0, 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_mcp_server_api_keys_scope",
                table: "mcp_server_api_keys");

            migrationBuilder.DropColumn(
                name: "generation_id",
                table: "mcp_server_api_keys");

            migrationBuilder.DropColumn(
                name: "scope",
                table: "mcp_server_api_keys");
        }
    }
}
