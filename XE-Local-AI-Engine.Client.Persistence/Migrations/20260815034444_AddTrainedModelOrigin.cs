using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddTrainedModelOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_benchmark_runs_model_origin",
                table: "benchmark_runs");

            migrationBuilder.AddCheckConstraint(
                name: "CK_benchmark_runs_model_origin",
                table: "benchmark_runs",
                sql: "primary_model_origin IS NULL OR primary_model_origin IN ('huggingface', 'imported', 'trained')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_benchmark_runs_model_origin",
                table: "benchmark_runs");

            migrationBuilder.AddCheckConstraint(
                name: "CK_benchmark_runs_model_origin",
                table: "benchmark_runs",
                sql: "primary_model_origin IS NULL OR primary_model_origin IN ('huggingface', 'imported')");
        }
    }
}
