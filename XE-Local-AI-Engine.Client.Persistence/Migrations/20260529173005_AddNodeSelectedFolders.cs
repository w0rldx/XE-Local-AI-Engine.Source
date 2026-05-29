using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddNodeSelectedFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "selected_folders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    alias = table.Column<string>(type: "TEXT", nullable: false),
                    host_path = table.Column<byte[]>(type: "BLOB", nullable: false),
                    mode = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_selected_folders", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_selected_folders_alias",
                table: "selected_folders",
                column: "alias",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "selected_folders");
        }
    }
}
