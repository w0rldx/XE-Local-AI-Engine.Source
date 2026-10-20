using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddBenchmarkReviewsArtifactsToolTiming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "tool_call_count",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "tool_time_ms",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "requires_review",
                table: "benchmark_projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "benchmark_run_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    judge_attempt_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    criterion_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    media_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    byte_count = table.Column<long>(type: "INTEGER", nullable: false),
                    content_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_benchmark_run_artifacts", x => x.id);
                    table.CheckConstraint("CK_benchmark_run_artifacts_byte_count", "byte_count >= 0");
                    table.CheckConstraint("CK_benchmark_run_artifacts_kind", "kind IN ('pythonTestsStdout', 'pythonTestsStderr')");
                    table.ForeignKey(
                        name: "FK_benchmark_run_artifacts_benchmark_judge_attempts_judge_attempt_id",
                        column: x => x.judge_attempt_id,
                        principalTable: "benchmark_judge_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_benchmark_run_artifacts_benchmark_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "benchmark_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "benchmark_run_reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    score = table.Column<int>(type: "INTEGER", nullable: true),
                    criteria_scores_json = table.Column<string>(type: "TEXT", nullable: true),
                    policy_revision_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    notes_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_benchmark_run_reviews", x => x.id);
                    table.CheckConstraint("CK_benchmark_run_reviews_approved_score", "state = 'rejected' OR score IS NOT NULL");
                    table.CheckConstraint("CK_benchmark_run_reviews_score", "score IS NULL OR (score >= 0 AND score <= 100)");
                    table.CheckConstraint("CK_benchmark_run_reviews_state", "state IN ('approved', 'rejected')");
                    table.ForeignKey(
                        name: "FK_benchmark_run_reviews_benchmark_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "benchmark_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_run_artifacts_run_id",
                table: "benchmark_run_artifacts",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ux_benchmark_run_artifacts_attempt_criterion_kind",
                table: "benchmark_run_artifacts",
                columns: new[] { "judge_attempt_id", "criterion_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_run_reviews_policy_revision_id",
                table: "benchmark_run_reviews",
                column: "policy_revision_id");

            migrationBuilder.CreateIndex(
                name: "ux_benchmark_run_reviews_run_id",
                table: "benchmark_run_reviews",
                column: "run_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "benchmark_run_artifacts");

            migrationBuilder.DropTable(
                name: "benchmark_run_reviews");

            migrationBuilder.DropColumn(
                name: "tool_call_count",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "tool_time_ms",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "requires_review",
                table: "benchmark_projects");
        }
    }
}
