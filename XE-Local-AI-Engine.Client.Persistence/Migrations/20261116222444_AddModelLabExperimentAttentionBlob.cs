using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddModelLabExperimentAttentionBlob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "attention_blob_id",
                table: "model_lab_experiments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "attention_byte_count",
                table: "model_lab_experiments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "attention_sha256",
                table: "model_lab_experiments",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "attention_blob_id",
                table: "model_lab_experiments");

            migrationBuilder.DropColumn(
                name: "attention_byte_count",
                table: "model_lab_experiments");

            migrationBuilder.DropColumn(
                name: "attention_sha256",
                table: "model_lab_experiments");
        }
    }
}
