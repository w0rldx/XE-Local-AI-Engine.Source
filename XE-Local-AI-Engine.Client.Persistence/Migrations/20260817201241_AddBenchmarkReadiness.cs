using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <summary>
    ///     Everything the benchmark-readiness work added to the two benchmark tables, in one migration.
    ///     <para>
    ///         <c>benchmark_runs</c> gains: <c>primary_stop_reason</c> (the provider's own finish reason, which is the
    ///         only thing that tells a run cut off at the token budget apart from one that finished — both are
    ///         <c>Succeeded</c>); the throughput split <c>ttft_ms</c>/<c>prompt_tokens</c>/<c>prompt_ms</c>/
    ///         <c>generation_tokens</c>/<c>generation_ms</c>/<c>cached_prompt_tokens</c> that one blended
    ///         <c>tokens_per_second</c> used to conflate, plus <c>segment_count</c> for how many provider requests
    ///         those sums are made of; the repeat group <c>repeat_group_id</c>/<c>repeat_index</c>/<c>is_warmup</c>;
    ///         and <c>invocation_timeout_seconds</c>, the generation budget frozen onto the run.
    ///         <c>benchmark_projects</c> gains the operator-tunable <c>max_output_tokens</c> and
    ///         <c>invocation_timeout_seconds</c>, each bounded by a check constraint.
    ///     </para>
    ///     <para>
    ///         Every column is nullable except <c>is_warmup</c>, which defaults to false: a run measured before any of
    ///         this existed was a single, non-warm-up run with no measured split, and that is exactly what NULL and
    ///         false read as. Nothing is backfilled with an invented measurement.
    ///     </para>
    ///     <para>
    ///         This was five separate migrations on the feature branch. Their timestamps interleaved with develop's
    ///         training migrations, so each side's target model was missing the other's columns and every SQLite
    ///         drop-column rebuild silently dropped the other side's work on rollback. Regenerating them as one
    ///         migration at the end of the merged chain is what makes the chain reversible.
    ///     </para>
    /// </summary>
    public partial class AddBenchmarkReadiness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "cached_prompt_tokens",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "generation_ms",
                table: "benchmark_runs",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "generation_tokens",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "invocation_timeout_seconds",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_warmup",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "primary_stop_reason",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "prompt_ms",
                table: "benchmark_runs",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "prompt_tokens",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "repeat_group_id",
                table: "benchmark_runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "repeat_index",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "segment_count",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ttft_ms",
                table: "benchmark_runs",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "invocation_timeout_seconds",
                table: "benchmark_projects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_output_tokens",
                table: "benchmark_projects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_runs_repeat_group_id",
                table: "benchmark_runs",
                column: "repeat_group_id");

            migrationBuilder.AddCheckConstraint(
                name: "CK_benchmark_projects_invocation_timeout",
                table: "benchmark_projects",
                sql: "invocation_timeout_seconds IS NULL OR (invocation_timeout_seconds >= 60 AND invocation_timeout_seconds <= 7200)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_benchmark_projects_max_output_tokens",
                table: "benchmark_projects",
                sql: "max_output_tokens IS NULL OR (max_output_tokens > 0 AND max_output_tokens < context_tokens)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_benchmark_runs_repeat_group_id",
                table: "benchmark_runs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_benchmark_projects_invocation_timeout",
                table: "benchmark_projects");

            migrationBuilder.DropCheckConstraint(
                name: "CK_benchmark_projects_max_output_tokens",
                table: "benchmark_projects");

            migrationBuilder.DropColumn(
                name: "cached_prompt_tokens",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "generation_ms",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "generation_tokens",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "invocation_timeout_seconds",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "is_warmup",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "primary_stop_reason",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "prompt_ms",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "prompt_tokens",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "repeat_group_id",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "repeat_index",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "segment_count",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "ttft_ms",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "invocation_timeout_seconds",
                table: "benchmark_projects");

            migrationBuilder.DropColumn(
                name: "max_output_tokens",
                table: "benchmark_projects");
        }
    }
}
