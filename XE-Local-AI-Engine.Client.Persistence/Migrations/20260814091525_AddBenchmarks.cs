using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddBenchmarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "benchmark_projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    core_task_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    context_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    agent_definition_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    judge_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    judge_model_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true, collation: "NOCASE"),
                    judge_context_tokens = table.Column<int>(type: "INTEGER", nullable: true),
                    judge_prompt_version = table.Column<int>(type: "INTEGER", nullable: false),
                    judge_output_schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_benchmark_projects", x => x.id);
                    table.CheckConstraint("CK_benchmark_projects_context_tokens", "context_tokens > 0");
                    table.CheckConstraint("CK_benchmark_projects_judge_context_tokens", "judge_context_tokens IS NULL OR judge_context_tokens > 0");
                });

            migrationBuilder.CreateTable(
                name: "benchmark_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    runtime_snapshot_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    primary_model_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false, collation: "NOCASE"),
                    primary_model_origin = table.Column<string>(type: "TEXT", nullable: true),
                    model_content_fingerprint = table.Column<string>(type: "TEXT", maxLength: 67, nullable: false),
                    agent_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    agent_version = table.Column<long>(type: "INTEGER", nullable: false),
                    requested_context_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    primary_status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    effective_context_tokens = table.Column<int>(type: "INTEGER", nullable: true),
                    duration_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    total_tokens = table.Column<int>(type: "INTEGER", nullable: true),
                    tokens_per_second = table.Column<double>(type: "REAL", nullable: true),
                    output_parts_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    last_stream_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    user_score = table.Column<int>(type: "INTEGER", nullable: true),
                    judge_status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    judge_result_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    primary_error_message = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    judge_error_message = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    primary_completed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    judge_started_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    judge_completed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_benchmark_runs", x => x.id);
                    table.CheckConstraint("CK_benchmark_runs_model_origin", "primary_model_origin IS NULL OR primary_model_origin IN ('huggingface', 'imported')");
                    table.CheckConstraint("CK_benchmark_runs_requested_context", "requested_context_tokens > 0");
                    table.CheckConstraint("CK_benchmark_runs_user_score", "user_score IS NULL OR (user_score >= 1 AND user_score <= 5)");
                    table.ForeignKey(
                        name: "FK_benchmark_runs_benchmark_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "benchmark_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "benchmark_work_items",
                columns: table => new
                {
                    queue_sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    enqueued_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    finished_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    error_message = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_benchmark_work_items", x => x.queue_sequence);
                    table.CheckConstraint("CK_benchmark_work_items_attempt", "attempt = 1");
                    table.ForeignKey(
                        name: "FK_benchmark_work_items_benchmark_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "benchmark_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_projects_agent_definition_id",
                table: "benchmark_projects",
                column: "agent_definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_runs_project_created_at",
                table: "benchmark_runs",
                columns: new[] { "project_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_work_items_status_sequence",
                table: "benchmark_work_items",
                columns: new[] { "status", "queue_sequence" });

            migrationBuilder.CreateIndex(
                name: "ux_benchmark_work_items_run_kind",
                table: "benchmark_work_items",
                columns: new[] { "run_id", "kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "benchmark_work_items");

            migrationBuilder.DropTable(
                name: "benchmark_runs");

            migrationBuilder.DropTable(
                name: "benchmark_projects");
        }
    }
}
