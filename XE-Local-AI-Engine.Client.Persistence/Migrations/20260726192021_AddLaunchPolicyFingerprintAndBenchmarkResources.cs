using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddLaunchPolicyFingerprintAndBenchmarkResources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "external_pressure_detected",
                table: "model_fit_benchmarks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "global_free_vram_after_bytes",
                table: "model_fit_benchmarks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "global_free_vram_load_bytes",
                table: "model_fit_benchmarks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "launch_policy_fingerprint",
                table: "model_fit_benchmarks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "launch_policy_fingerprint_version",
                table: "model_fit_benchmarks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "minimum_global_free_vram_bytes",
                table: "model_fit_benchmarks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "minimum_process_budget_vram_bytes",
                table: "model_fit_benchmarks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "peak_process_ram_bytes",
                table: "model_fit_benchmarks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "process_budget_vram_after_bytes",
                table: "model_fit_benchmarks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "process_budget_vram_load_bytes",
                table: "model_fit_benchmarks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "launch_policy_fingerprint",
                table: "inference_profiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "launch_policy_fingerprint_version",
                table: "inference_profiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "global_free_vram_at_freeze_bytes",
                table: "inference_profiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "process_budget_vram_at_freeze_bytes",
                table: "inference_profiles",
                type: "INTEGER",
                nullable: true);

            // Existing rows predate the versioned identity. They are not assumed equivalent to policy v1: demote them
            // and clear their freeze justification so only a fresh explore + benchmark can make them replayable.
            migrationBuilder.Sql(
                """
                UPDATE inference_profiles
                SET status = 2,
                    benchmark_snapshot_id = NULL,
                    free_vram_at_freeze_bytes = NULL
                WHERE launch_policy_fingerprint IS NULL
                   OR launch_policy_fingerprint_version IS NULL;
                """);

            migrationBuilder.DropColumn(
                name: "free_vram_at_freeze_bytes",
                table: "inference_profiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "external_pressure_detected",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "global_free_vram_after_bytes",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "global_free_vram_load_bytes",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "launch_policy_fingerprint",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "launch_policy_fingerprint_version",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "minimum_global_free_vram_bytes",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "minimum_process_budget_vram_bytes",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "peak_process_ram_bytes",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "process_budget_vram_after_bytes",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "process_budget_vram_load_bytes",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "launch_policy_fingerprint",
                table: "inference_profiles");

            migrationBuilder.DropColumn(
                name: "launch_policy_fingerprint_version",
                table: "inference_profiles");

            migrationBuilder.DropColumn(
                name: "global_free_vram_at_freeze_bytes",
                table: "inference_profiles");

            migrationBuilder.DropColumn(
                name: "process_budget_vram_at_freeze_bytes",
                table: "inference_profiles");

            migrationBuilder.AddColumn<long>(
                name: "free_vram_at_freeze_bytes",
                table: "inference_profiles",
                type: "INTEGER",
                nullable: true);
        }
    }
}
