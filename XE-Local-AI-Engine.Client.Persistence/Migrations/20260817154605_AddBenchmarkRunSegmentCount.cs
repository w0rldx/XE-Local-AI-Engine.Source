using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <summary>
    ///     One additive, nullable, plaintext column. <c>benchmark_runs.segment_count</c> records how many provider
    ///     requests a turn made, i.e. how many timing readings the pp/tg sums are made of. A tool-calling turn is
    ///     several llama-server requests, each prefilling again, so a run showing 1720 generated tokens against a usage
    ///     total of 4349 is only explicable once the request count is visible. Existing rows stay NULL.
    /// </summary>
    public partial class AddBenchmarkRunSegmentCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "segment_count",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "segment_count",
                table: "benchmark_runs");
        }
    }
}
