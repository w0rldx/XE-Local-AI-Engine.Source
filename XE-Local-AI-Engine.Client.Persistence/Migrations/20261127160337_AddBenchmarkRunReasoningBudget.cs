using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddBenchmarkRunReasoningBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "applied_reasoning_budget_tokens",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "reasoning_budget_tokens",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "reasoning_tokens",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            // Every existing run froze its project's budget (no launch override existed), and a project with runs refuses
            // edits, so the project's value is exactly the run's. The two measurements stay NULL: nothing measured them.
            migrationBuilder.Sql("""
                UPDATE benchmark_runs
                SET reasoning_budget_tokens = (SELECT reasoning_budget_tokens FROM benchmark_projects WHERE benchmark_projects.id = benchmark_runs.project_id);
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_benchmark_runs_reasoning_budget_tokens",
                table: "benchmark_runs",
                sql: "reasoning_budget_tokens IS NULL OR reasoning_budget_tokens >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refuses, rather than drops, what the older schema cannot express: a run-specific budget (a launch override)
            // or a measurement. Dropping them is silent, and a later Up would relabel every such run with its project's
            // budget while its snapshot still replays the override. SQLite allows RAISE only inside a trigger.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE benchmark_run_reasoning_rollback_guard (lossy INTEGER NOT NULL);
                CREATE TEMP TRIGGER benchmark_run_reasoning_rollback_refusal BEFORE INSERT ON benchmark_run_reasoning_rollback_guard
                WHEN NEW.lossy
                BEGIN
                    SELECT RAISE(ABORT, 'Cannot roll back AddBenchmarkRunReasoningBudget: a benchmark run carries a launch reasoning-budget override or a measured reasoning value the older schema cannot hold. Restore the database snapshot taken before the upgrade instead.');
                END;
                INSERT INTO benchmark_run_reasoning_rollback_guard (lossy)
                SELECT EXISTS (
                    SELECT 1 FROM benchmark_runs
                    WHERE reasoning_tokens IS NOT NULL
                       OR applied_reasoning_budget_tokens IS NOT NULL
                       OR reasoning_budget_tokens IS NOT (SELECT reasoning_budget_tokens FROM benchmark_projects WHERE benchmark_projects.id = benchmark_runs.project_id));
                DROP TABLE benchmark_run_reasoning_rollback_guard;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_benchmark_runs_reasoning_budget_tokens",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "applied_reasoning_budget_tokens",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "reasoning_budget_tokens",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "reasoning_tokens",
                table: "benchmark_runs");
        }
    }
}
