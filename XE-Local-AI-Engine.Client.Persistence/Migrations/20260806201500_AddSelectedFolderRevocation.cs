using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddSelectedFolderRevocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_selected_folders_alias",
                table: "selected_folders");

            migrationBuilder.AddColumn<long>(
                name: "revoked_at_utc",
                table: "selected_folders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_selected_folders_alias",
                table: "selected_folders",
                column: "alias",
                unique: true,
                filter: "revoked_at_utc IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_selected_folders_alias",
                table: "selected_folders");

            migrationBuilder.Sql("DELETE FROM selected_folders WHERE revoked_at_utc IS NOT NULL;");

            migrationBuilder.DropColumn(
                name: "revoked_at_utc",
                table: "selected_folders");

            migrationBuilder.CreateIndex(
                name: "IX_selected_folders_alias",
                table: "selected_folders",
                column: "alias",
                unique: true);
        }
    }
}
