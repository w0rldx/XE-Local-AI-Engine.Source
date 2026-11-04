using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddModelLabExperiments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_lab_experiments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    repo_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    revision = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    base_artifact_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    installed_model_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    prompt = table.Column<byte[]>(type: "BLOB", nullable: false),
                    license_confirmation_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    result_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    error_message = table.Column<byte[]>(type: "BLOB", nullable: true),
                    launch_receipt_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    trace_blob_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    trace_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    trace_byte_count = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_lab_experiments", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_model_lab_experiments_created",
                table: "model_lab_experiments",
                column: "created_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_model_lab_experiments_status",
                table: "model_lab_experiments",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_lab_experiments");
        }
    }
}
