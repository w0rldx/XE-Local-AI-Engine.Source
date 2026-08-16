using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <summary>
    ///     Retires the 1–5 judge. Everything about a judging now lives on <c>benchmark_judge_attempts</c> — status,
    ///     result, score, timestamps and the whole launch-evidence block — because a run is judged many times and a
    ///     column can only remember the last one. The project's judge columns go with it: the policy revision it points
    ///     at owns the model, the context and the prompt/schema versions, all inside the policy hash.
    ///     <para>
    ///         Deliberately a SECOND migration on top of <c>AddBenchmarkJudgePolicies</c> rather than an edit of it:
    ///         removing and re-adding a migration on this repository has been observed to roll the model snapshot back
    ///         past an unrelated sibling.
    ///     </para>
    /// </summary>
    public partial class RemoveBenchmarkRunJudgeColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_benchmark_projects_judge_context_tokens",
                table: "benchmark_projects");

            migrationBuilder.DropColumn(
                name: "judge_completed_at_utc",
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
                name: "judge_error_message",
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
                name: "judge_launch_executable_sha256",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_launch_has_aux_assets",
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
                name: "judge_result_json",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_started_at_utc",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_status",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_variant",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_context_tokens",
                table: "benchmark_projects");

            migrationBuilder.DropColumn(
                name: "judge_enabled",
                table: "benchmark_projects");

            migrationBuilder.DropColumn(
                name: "judge_model_name",
                table: "benchmark_projects");

            migrationBuilder.DropColumn(
                name: "judge_output_schema_version",
                table: "benchmark_projects");

            migrationBuilder.DropColumn(
                name: "judge_prompt_version",
                table: "benchmark_projects");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "judge_completed_at_utc",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

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
                name: "judge_error_message",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 1024,
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
                name: "judge_launch_executable_sha256",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "judge_launch_has_aux_assets",
                table: "benchmark_runs",
                type: "INTEGER",
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

            migrationBuilder.AddColumn<byte[]>(
                name: "judge_result_json",
                table: "benchmark_runs",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "judge_started_at_utc",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "judge_status",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "judge_variant",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "judge_context_tokens",
                table: "benchmark_projects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "judge_enabled",
                table: "benchmark_projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "judge_model_name",
                table: "benchmark_projects",
                type: "TEXT",
                maxLength: 255,
                nullable: true,
                collation: "NOCASE");

            migrationBuilder.AddColumn<int>(
                name: "judge_output_schema_version",
                table: "benchmark_projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "judge_prompt_version",
                table: "benchmark_projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_benchmark_projects_judge_context_tokens",
                table: "benchmark_projects",
                sql: "judge_context_tokens IS NULL OR judge_context_tokens > 0");
        }
    }
}
