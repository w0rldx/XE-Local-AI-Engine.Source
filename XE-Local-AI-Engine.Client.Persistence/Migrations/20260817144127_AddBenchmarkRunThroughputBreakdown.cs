using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <summary>
    ///     Six additive, nullable, plaintext columns on <c>benchmark_runs</c>. They separate what one blended
    ///     <c>tokens_per_second</c> used to conflate: <c>ttft_ms</c> is how long the caller waited for the first token,
    ///     <c>prompt_tokens</c>/<c>prompt_ms</c> are prompt processing (pp) and
    ///     <c>generation_tokens</c>/<c>generation_ms</c> are decoding (tg), as the runtime itself timed them.
    ///     <c>cached_prompt_tokens</c> records how much of the prefill came from the KV cache, which is what tells a
    ///     cold pp measurement apart from a warm one. Existing rows stay NULL — a run measured before these columns
    ///     existed must not be given a split nobody measured — and <c>tokens_per_second</c>/<c>duration_ms</c>/
    ///     <c>total_tokens</c> keep their meaning for every existing reader.
    /// </summary>
    public partial class AddBenchmarkRunThroughputBreakdown : Migration
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

            migrationBuilder.AddColumn<double>(
                name: "ttft_ms",
                table: "benchmark_runs",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
                name: "prompt_ms",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "prompt_tokens",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "ttft_ms",
                table: "benchmark_runs");
        }
    }
}
