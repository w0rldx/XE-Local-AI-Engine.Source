using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <summary>
    ///     Adds the project-level thinking budget (<c>reasoning_budget_tokens</c>) the freeze pins onto every run's
    ///     sampling. Nullable with no default: a project created before this pinned nothing, and backfilling a number
    ///     would silently cap the reasoning of every run it freezes from then on. Bounded inside the project's own
    ///     context by a check constraint, because a budget at or above the window can never be honoured and would
    ///     masquerade as "no budget" — the same rule <c>max_output_tokens</c> already carries.
    /// </summary>
    public partial class AddBenchmarkProjectReasoningBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "reasoning_budget_tokens",
                table: "benchmark_projects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_benchmark_projects_reasoning_budget_tokens",
                table: "benchmark_projects",
                sql: "reasoning_budget_tokens IS NULL OR (reasoning_budget_tokens > 0 AND reasoning_budget_tokens < context_tokens)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_benchmark_projects_reasoning_budget_tokens",
                table: "benchmark_projects");

            migrationBuilder.DropColumn(
                name: "reasoning_budget_tokens",
                table: "benchmark_projects");
        }
    }
}
