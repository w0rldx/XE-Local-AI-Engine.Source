using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <summary>
    ///     The operator-tunable generation budget, plus the copy frozen onto each run at start. Two columns rather than
    ///     one because a run must replay with the budget it was started under, exactly like its context — the project
    ///     column is the setting, the run column is the measurement's own fact. Both nullable: null means the frozen
    ///     default, so every existing project and run keeps reading without a backfill.
    /// </summary>
    public partial class AddBenchmarkProjectInvocationTimeout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "invocation_timeout_seconds",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "invocation_timeout_seconds",
                table: "benchmark_projects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_benchmark_projects_invocation_timeout",
                table: "benchmark_projects",
                sql: "invocation_timeout_seconds IS NULL OR (invocation_timeout_seconds >= 60 AND invocation_timeout_seconds <= 7200)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_benchmark_projects_invocation_timeout",
                table: "benchmark_projects");

            migrationBuilder.DropColumn(
                name: "invocation_timeout_seconds",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "invocation_timeout_seconds",
                table: "benchmark_projects");
        }
    }
}
