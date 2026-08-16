using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddBenchmarkRunLaunchReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "judge_effective_backend",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "judge_effective_launch_identity",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "judge_environment_facts_hash",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "judge_environment_facts_json",
                table: "benchmark_runs",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "judge_flash_attention_mode",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "judge_intended_executable_sha256",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "judge_intended_launch_identity",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "judge_kv_auto_reason",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "judge_kv_cache_type",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "judge_kv_cache_type_source",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "judge_launch_kv_cache_type_source",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "judge_launch_receipt_json",
                table: "benchmark_runs",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "judge_placement_offloaded",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "judge_placement_total",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "judge_receipt_hash",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "judge_variant",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primary_effective_backend",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primary_effective_launch_identity",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primary_environment_facts_hash",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "primary_environment_facts_json",
                table: "benchmark_runs",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primary_flash_attention_mode",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primary_intended_executable_sha256",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primary_intended_launch_identity",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primary_kv_auto_reason",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primary_kv_cache_type",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primary_kv_cache_type_source",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primary_launch_kv_cache_type_source",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "primary_launch_receipt_json",
                table: "benchmark_runs",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "primary_placement_offloaded",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "primary_placement_total",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primary_receipt_hash",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primary_variant",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_runs_project_primary_kv_cache_type",
                table: "benchmark_runs",
                columns: new[] { "project_id", "primary_kv_cache_type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_benchmark_runs_project_primary_kv_cache_type",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_effective_backend",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_effective_launch_identity",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_environment_facts_hash",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_environment_facts_json",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_flash_attention_mode",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_intended_executable_sha256",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_intended_launch_identity",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_kv_auto_reason",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_kv_cache_type",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_kv_cache_type_source",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_launch_kv_cache_type_source",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_launch_receipt_json",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_placement_offloaded",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_placement_total",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_receipt_hash",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_variant",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "primary_effective_backend",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "primary_effective_launch_identity",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "primary_environment_facts_hash",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "primary_environment_facts_json",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "primary_flash_attention_mode",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "primary_intended_executable_sha256",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "primary_intended_launch_identity",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "primary_kv_auto_reason",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "primary_kv_cache_type",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "primary_kv_cache_type_source",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "primary_launch_kv_cache_type_source",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "primary_launch_receipt_json",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "primary_placement_offloaded",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "primary_placement_total",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "primary_receipt_hash",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "primary_variant",
                table: "benchmark_runs");
        }
    }
}
