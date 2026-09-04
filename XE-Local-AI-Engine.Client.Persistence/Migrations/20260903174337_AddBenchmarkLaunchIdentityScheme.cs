using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddBenchmarkLaunchIdentityScheme : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "primary_launch_identity_scheme",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "launch_identity_scheme",
                table: "benchmark_judge_attempts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "launch_identity_scheme",
                table: "benchmark_comparisons",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "primary_launch_identity_scheme",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "launch_identity_scheme",
                table: "benchmark_judge_attempts");

            migrationBuilder.DropColumn(
                name: "launch_identity_scheme",
                table: "benchmark_comparisons");
        }
    }
}
