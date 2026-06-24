using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTutorialState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "tutorial_state",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "tutorial_state",
                table: "AspNetUsers");
        }
    }
}
