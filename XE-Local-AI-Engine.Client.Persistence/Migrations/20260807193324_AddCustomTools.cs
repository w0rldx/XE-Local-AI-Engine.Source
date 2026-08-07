using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddCustomTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "custom_tools",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    description = table.Column<byte[]>(type: "BLOB", nullable: false),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    mode = table.Column<int>(type: "INTEGER", nullable: false),
                    parameters_json = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    config_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    acknowledged = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_tools", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_custom_tools_name",
                table: "custom_tools",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "custom_tools");
        }
    }
}
