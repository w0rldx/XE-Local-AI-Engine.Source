using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddImageEditColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "edit_mode",
                table: "image_jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_image_id",
                table: "image_jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "strength",
                table: "image_jobs",
                type: "REAL",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "job_id",
                table: "generated_images",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.CreateIndex(
                name: "IX_image_jobs_source_image_id",
                table: "image_jobs",
                column: "source_image_id");

            migrationBuilder.AddForeignKey(
                name: "FK_image_jobs_generated_images_source_image_id",
                table: "image_jobs",
                column: "source_image_id",
                principalTable: "generated_images",
                principalColumn: "image_id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_image_jobs_generated_images_source_image_id",
                table: "image_jobs");

            migrationBuilder.DropIndex(
                name: "IX_image_jobs_source_image_id",
                table: "image_jobs");

            migrationBuilder.DropColumn(
                name: "edit_mode",
                table: "image_jobs");

            migrationBuilder.DropColumn(
                name: "source_image_id",
                table: "image_jobs");

            migrationBuilder.DropColumn(
                name: "strength",
                table: "image_jobs");

            // An upload has no job and no pre-migration meaning; restoring NOT NULL would give it a dangling
            // Guid.Empty job. Its blob under generated-images/uploads/ is left on disk by a rollback.
            migrationBuilder.Sql("DELETE FROM generated_images WHERE job_id IS NULL;");

            migrationBuilder.AlterColumn<Guid>(
                name: "job_id",
                table: "generated_images",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
