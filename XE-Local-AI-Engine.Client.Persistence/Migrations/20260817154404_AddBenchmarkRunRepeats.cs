using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <summary>
    ///     Three additive, plaintext columns on <c>benchmark_runs</c> that turn a set of unrelated runs into a REPEAT
    ///     GROUP. <c>repeat_group_id</c> is shared by every run one launch request created and is NULL for a plain
    ///     single run, so nothing about the existing shape changes; <c>repeat_index</c> orders them (0 is the warm-up
    ///     when one was requested, measured repeats are 1..N); <c>is_warmup</c> marks the run that is deliberately not
    ///     ranked and not counted in a group's statistics — it exists to absorb the first-launch costs the runs after
    ///     it should not pay. Existing rows take <c>is_warmup = 0</c> and NULL for the other two: a run measured before
    ///     repeats existed was a single run, which is exactly what that reads as.
    /// </summary>
    public partial class AddBenchmarkRunRepeats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_warmup",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

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

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_runs_repeat_group_id",
                table: "benchmark_runs",
                column: "repeat_group_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_benchmark_runs_repeat_group_id",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "is_warmup",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "repeat_group_id",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "repeat_index",
                table: "benchmark_runs");
        }
    }
}
