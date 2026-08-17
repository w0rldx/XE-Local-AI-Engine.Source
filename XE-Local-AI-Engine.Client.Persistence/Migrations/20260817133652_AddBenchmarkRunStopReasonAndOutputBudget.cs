using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <summary>
    ///     Two additive, plaintext columns. <c>benchmark_runs.primary_stop_reason</c> records WHY the primary
    ///     generation stopped (the provider's own <c>finish_reason</c>), so a run cut off at the token budget is
    ///     distinguishable from one that finished — both are <c>Succeeded</c>, and only the persisted reason can tell
    ///     them apart. <c>benchmark_projects.max_output_tokens</c> is the optional per-run output budget frozen into
    ///     every run's sampling; the check constraint keeps it inside the project's own context window, since a budget
    ///     at or above it could never be honoured.
    /// </summary>
    public partial class AddBenchmarkRunStopReasonAndOutputBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "primary_stop_reason",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_output_tokens",
                table: "benchmark_projects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_benchmark_projects_max_output_tokens",
                table: "benchmark_projects",
                sql: "max_output_tokens IS NULL OR (max_output_tokens > 0 AND max_output_tokens < context_tokens)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_benchmark_projects_max_output_tokens",
                table: "benchmark_projects");

            migrationBuilder.DropColumn(
                name: "primary_stop_reason",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "max_output_tokens",
                table: "benchmark_projects");
        }
    }
}
