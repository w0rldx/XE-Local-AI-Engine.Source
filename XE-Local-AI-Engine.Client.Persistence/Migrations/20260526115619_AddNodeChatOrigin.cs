using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeChatOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "origin",
                table: "messages",
                type: "TEXT",
                nullable: false,
                defaultValue: "Local");

            migrationBuilder.AddColumn<string>(
                name: "origin",
                table: "conversations",
                type: "TEXT",
                nullable: false,
                defaultValue: "Local");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "origin",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "origin",
                table: "conversations");
        }
    }
}
