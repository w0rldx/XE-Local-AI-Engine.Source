using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <summary>
    ///     Records what a repeat group measures, and the two sampling inputs that make an answer-variance group
    ///     readable without decrypting a snapshot. <c>repeat_mode</c> defaults to <c>Throughput</c> because every run
    ///     recorded before this column existed WAS a throughput repeat — the frozen deterministic sampling made it one
    ///     — so the default is historically true rather than invented. <c>sampling_seed</c> and
    ///     <c>sampling_temperature</c> stay NULL on those rows instead: the values are knowable, but "not recorded" and
    ///     "recorded as 0" are different facts, and only the second belongs in a measurement table.
    /// </summary>
    public partial class AddBenchmarkRunRepeatMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "repeat_mode",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Throughput");

            migrationBuilder.AddColumn<string>(
                name: "sampling_seed",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "sampling_temperature",
                table: "benchmark_runs",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "repeat_mode",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "sampling_seed",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "sampling_temperature",
                table: "benchmark_runs");
        }
    }
}
