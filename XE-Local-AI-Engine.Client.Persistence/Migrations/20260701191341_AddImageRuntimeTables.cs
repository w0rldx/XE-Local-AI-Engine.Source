using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddImageRuntimeTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "image_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    model_name = table.Column<string>(type: "TEXT", nullable: false),
                    prompt = table.Column<byte[]>(type: "BLOB", nullable: false),
                    negative_prompt = table.Column<byte[]>(type: "BLOB", nullable: true),
                    seed = table.Column<long>(type: "INTEGER", nullable: false),
                    width = table.Column<int>(type: "INTEGER", nullable: false),
                    height = table.Column<int>(type: "INTEGER", nullable: false),
                    steps = table.Column<int>(type: "INTEGER", nullable: false),
                    sampler = table.Column<string>(type: "TEXT", nullable: false),
                    cfg_scale = table.Column<double>(type: "REAL", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    duration_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    image_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    sanitized_error = table.Column<string>(type: "TEXT", nullable: true),
                    cancellation_requested_at_utc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_image_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "image_model_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    machine_key = table.Column<string>(type: "TEXT", nullable: false),
                    model_name = table.Column<string>(type: "TEXT", nullable: false),
                    backend = table.Column<string>(type: "TEXT", nullable: false),
                    default_steps = table.Column<int>(type: "INTEGER", nullable: false),
                    default_sampler = table.Column<string>(type: "TEXT", nullable: false),
                    default_cfg = table.Column<double>(type: "REAL", nullable: false),
                    default_width = table.Column<int>(type: "INTEGER", nullable: false),
                    default_height = table.Column<int>(type: "INTEGER", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_image_model_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "generated_images",
                columns: table => new
                {
                    image_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    job_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    mime_type = table.Column<string>(type: "TEXT", nullable: false),
                    width = table.Column<int>(type: "INTEGER", nullable: false),
                    height = table.Column<int>(type: "INTEGER", nullable: false),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    storage_path = table.Column<string>(type: "TEXT", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generated_images", x => x.image_id);
                    table.ForeignKey(
                        name: "FK_generated_images_image_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "image_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_generated_images_job_id",
                table: "generated_images",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "IX_image_jobs_created_at_utc",
                table: "image_jobs",
                column: "created_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_image_jobs_status",
                table: "image_jobs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_image_model_profiles_machine_key_model_name_backend",
                table: "image_model_profiles",
                columns: new[] { "machine_key", "model_name", "backend" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_image_model_profiles_status",
                table: "image_model_profiles",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "generated_images");

            migrationBuilder.DropTable(
                name: "image_model_profiles");

            migrationBuilder.DropTable(
                name: "image_jobs");
        }
    }
}
