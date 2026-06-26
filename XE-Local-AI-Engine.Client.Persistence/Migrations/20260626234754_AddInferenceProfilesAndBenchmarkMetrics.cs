using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddInferenceProfilesAndBenchmarkMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "backend",
                table: "model_fit_benchmarks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "cache_hit_rate",
                table: "model_fit_benchmarks",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ctx_size",
                table: "model_fit_benchmarks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kv_type",
                table: "model_fit_benchmarks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "llamacpp_build",
                table: "model_fit_benchmarks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "machine_key",
                table: "model_fit_benchmarks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "n_gpu_layers",
                table: "model_fit_benchmarks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "override_tensor",
                table: "model_fit_benchmarks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "pp_tokens_per_second",
                table: "model_fit_benchmarks",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "quant",
                table: "model_fit_benchmarks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tensor_split",
                table: "model_fit_benchmarks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "tool_loop_ms",
                table: "model_fit_benchmarks",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "vram_after_bytes",
                table: "model_fit_benchmarks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "vram_load_bytes",
                table: "model_fit_benchmarks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "inference_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    machine_key = table.Column<string>(type: "TEXT", nullable: false),
                    model_name = table.Column<string>(type: "TEXT", nullable: false),
                    role = table.Column<int>(type: "INTEGER", nullable: false),
                    backend = table.Column<string>(type: "TEXT", nullable: false),
                    llamacpp_build = table.Column<string>(type: "TEXT", nullable: false),
                    quant = table.Column<string>(type: "TEXT", nullable: false),
                    ctx_size = table.Column<int>(type: "INTEGER", nullable: false),
                    n_gpu_layers = table.Column<int>(type: "INTEGER", nullable: true),
                    tensor_split = table.Column<string>(type: "TEXT", nullable: true),
                    override_tensor = table.Column<string>(type: "TEXT", nullable: true),
                    kv_type_k = table.Column<string>(type: "TEXT", nullable: true),
                    kv_type_v = table.Column<string>(type: "TEXT", nullable: true),
                    flash_attn = table.Column<bool>(type: "INTEGER", nullable: false),
                    n_params = table.Column<long>(type: "INTEGER", nullable: true),
                    is_moe = table.Column<bool>(type: "INTEGER", nullable: false),
                    expert_count = table.Column<int>(type: "INTEGER", nullable: true),
                    free_vram_at_freeze_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    benchmark_snapshot_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inference_profiles", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inference_profiles_machine_key_model_name_role_backend",
                table: "inference_profiles",
                columns: new[] { "machine_key", "model_name", "role", "backend" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inference_profiles_status",
                table: "inference_profiles",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inference_profiles");

            migrationBuilder.DropColumn(
                name: "backend",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "cache_hit_rate",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "ctx_size",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "kv_type",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "llamacpp_build",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "machine_key",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "n_gpu_layers",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "override_tensor",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "pp_tokens_per_second",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "quant",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "tensor_split",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "tool_loop_ms",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "vram_after_bytes",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "vram_load_bytes",
                table: "model_fit_benchmarks");
        }
    }
}
