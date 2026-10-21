using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddBenchmarkRunAgentDefinition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "agent_definition_id",
                table: "benchmark_runs",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // SQLite adds a NOT NULL column only with a default; the backfill then names every existing run's agent: the
            // project's, which is what it was frozen against (a freeze always read the project's agent before variants).
            migrationBuilder.Sql("""
                UPDATE benchmark_runs
                SET agent_definition_id = (SELECT agent_definition_id FROM benchmark_projects WHERE benchmark_projects.id = benchmark_runs.project_id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "agent_definition_id",
                table: "benchmark_runs");
        }
    }
}
