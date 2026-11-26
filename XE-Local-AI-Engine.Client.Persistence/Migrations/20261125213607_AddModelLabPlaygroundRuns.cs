using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddModelLabPlaygroundRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_lab_playground_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    template_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    trace_blob_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    trace_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    trace_byte_count = table.Column<long>(type: "INTEGER", nullable: false),
                    result_json = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_lab_playground_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_model_lab_playground_runs_created",
                table: "model_lab_playground_runs",
                column: "created_at_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_lab_playground_runs");
        }
    }
}
