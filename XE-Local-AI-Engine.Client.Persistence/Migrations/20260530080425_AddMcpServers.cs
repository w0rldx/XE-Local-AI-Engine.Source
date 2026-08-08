using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddMcpServers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mcp_servers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    description = table.Column<byte[]>(type: "BLOB", nullable: true),
                    transport_kind = table.Column<int>(type: "INTEGER", nullable: false),
                    command = table.Column<string>(type: "TEXT", nullable: true),
                    arguments = table.Column<byte[]>(type: "BLOB", nullable: true),
                    working_directory = table.Column<string>(type: "TEXT", nullable: true),
                    env = table.Column<byte[]>(type: "BLOB", nullable: true),
                    url = table.Column<string>(type: "TEXT", nullable: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcp_servers", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mcp_servers_name",
                table: "mcp_servers",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_servers");
        }
    }
}
