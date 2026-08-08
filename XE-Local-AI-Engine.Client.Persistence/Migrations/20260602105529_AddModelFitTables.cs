using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddModelFitTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "approved_utility_images",
                columns: table => new
                {
                    approved_image_id = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    purpose = table.Column<int>(type: "INTEGER", nullable: false),
                    image_reference = table.Column<string>(type: "TEXT", nullable: false),
                    source_url = table.Column<string>(type: "TEXT", nullable: true),
                    upstream_version = table.Column<string>(type: "TEXT", nullable: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    deprecated_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    replacement_approved_image_id = table.Column<string>(type: "TEXT", nullable: true),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    last_used_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    last_successful_run_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    diagnostics_json = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approved_utility_images", x => x.approved_image_id);
                });

            migrationBuilder.CreateTable(
                name: "model_fit_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    approved_image_id = table.Column<string>(type: "TEXT", nullable: false),
                    operation = table.Column<int>(type: "INTEGER", nullable: false),
                    use_case = table.Column<string>(type: "TEXT", nullable: true),
                    provider_name = table.Column<string>(type: "TEXT", nullable: false),
                    model_name = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    duration_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    exit_code = table.Column<int>(type: "INTEGER", nullable: true),
                    raw_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    stderr_excerpt = table.Column<byte[]>(type: "BLOB", nullable: true),
                    diagnostics_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    is_latest_successful = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_by_run_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_fit_snapshots", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "model_fit_benchmarks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    model_name = table.Column<string>(type: "TEXT", nullable: false),
                    provider_name = table.Column<string>(type: "TEXT", nullable: false),
                    tokens_per_second = table.Column<double>(type: "REAL", nullable: true),
                    ttft_ms = table.Column<double>(type: "REAL", nullable: true),
                    total_latency_ms = table.Column<double>(type: "REAL", nullable: true),
                    runs = table.Column<int>(type: "INTEGER", nullable: true),
                    raw_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    diagnostics_json = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_fit_benchmarks", x => x.id);
                    table.ForeignKey(
                        name: "FK_model_fit_benchmarks_model_fit_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "model_fit_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "model_fit_recommendations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    rank = table.Column<int>(type: "INTEGER", nullable: false),
                    model_name = table.Column<string>(type: "TEXT", nullable: false),
                    provider_model_name = table.Column<string>(type: "TEXT", nullable: true),
                    score = table.Column<double>(type: "REAL", nullable: false),
                    fit_level = table.Column<string>(type: "TEXT", nullable: true),
                    run_mode = table.Column<string>(type: "TEXT", nullable: true),
                    quantization = table.Column<string>(type: "TEXT", nullable: true),
                    estimated_tokens_per_second = table.Column<double>(type: "REAL", nullable: true),
                    required_ram_mb = table.Column<double>(type: "REAL", nullable: true),
                    required_vram_mb = table.Column<double>(type: "REAL", nullable: true),
                    context_tokens = table.Column<int>(type: "INTEGER", nullable: true),
                    is_installed = table.Column<bool>(type: "INTEGER", nullable: false),
                    pull_model_name = table.Column<string>(type: "TEXT", nullable: true),
                    diagnostics_json = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_fit_recommendations", x => x.id);
                    table.ForeignKey(
                        name: "FK_model_fit_recommendations_model_fit_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "model_fit_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_model_fit_benchmarks_snapshot_id",
                table: "model_fit_benchmarks",
                column: "snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_model_fit_recommendations_snapshot_id_rank",
                table: "model_fit_recommendations",
                columns: new[] { "snapshot_id", "rank" });

            migrationBuilder.CreateIndex(
                name: "IX_model_fit_snapshots_operation_use_case_provider_name_model_name_is_latest_successful",
                table: "model_fit_snapshots",
                columns: new[] { "operation", "use_case", "provider_name", "model_name", "is_latest_successful" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approved_utility_images");

            migrationBuilder.DropTable(
                name: "model_fit_benchmarks");

            migrationBuilder.DropTable(
                name: "model_fit_recommendations");

            migrationBuilder.DropTable(
                name: "model_fit_snapshots");
        }
    }
}
