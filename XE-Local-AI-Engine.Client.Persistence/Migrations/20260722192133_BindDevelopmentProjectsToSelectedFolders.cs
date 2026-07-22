using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BindDevelopmentProjectsToSelectedFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "selected_folder_id",
                table: "development_projects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_development_projects_selected_folder_id",
                table: "development_projects",
                column: "selected_folder_id");

            migrationBuilder.AddForeignKey(
                name: "FK_development_projects_selected_folders_selected_folder_id",
                table: "development_projects",
                column: "selected_folder_id",
                principalTable: "selected_folders",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_development_projects_selected_folders_selected_folder_id",
                table: "development_projects");

            migrationBuilder.DropIndex(
                name: "ix_development_projects_selected_folder_id",
                table: "development_projects");

            migrationBuilder.DropColumn(
                name: "selected_folder_id",
                table: "development_projects");
        }
    }
}
