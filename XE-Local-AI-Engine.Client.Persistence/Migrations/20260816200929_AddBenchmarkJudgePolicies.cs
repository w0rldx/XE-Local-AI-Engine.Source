using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <summary>
    ///     Judging becomes repeatable: a project points at an immutable, hashed judge policy revision, and every
    ///     judging of a run is its own attempt row rather than a set of columns overwritten in place. Work items gain
    ///     the attempt they judge, so a run can hold one judge item per attempt instead of exactly one forever, and
    ///     the operator score widens from 1..5 to 0..100.
    ///     <para>
    ///         <b>Up deletes every existing benchmark row</b> (work items, then runs, then projects). This is an
    ///         explicit operator decision: the feature is declared unused, only development boxes hold any
    ///         rows, and the alternative — a fail-fast precondition — would brick node startup on such a box with no
    ///         UI path left to clear it. Down restores the previous empty schema and no data.
    ///     </para>
    /// </summary>
    public partial class AddBenchmarkJudgePolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Child first: this database does not enforce foreign keys, so the order is the integrity.
            migrationBuilder.Sql("DELETE FROM benchmark_work_items;");
            migrationBuilder.Sql("DELETE FROM benchmark_runs;");
            migrationBuilder.Sql("DELETE FROM benchmark_projects;");

            migrationBuilder.DropIndex(
                name: "ux_benchmark_work_items_run_kind",
                table: "benchmark_work_items");

            migrationBuilder.DropCheckConstraint(
                name: "CK_benchmark_runs_user_score",
                table: "benchmark_runs");

            migrationBuilder.AddColumn<Guid>(
                name: "judge_attempt_id",
                table: "benchmark_work_items",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "current_judge_attempt_id",
                table: "benchmark_runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "current_judge_policy_revision_id",
                table: "benchmark_projects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "benchmark_judge_policy_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    policy_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    policy_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    reference_execution_key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    cohort_generation = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_benchmark_judge_policy_revisions", x => x.id);
                    table.CheckConstraint("CK_benchmark_judge_policy_revisions_cohort_generation", "cohort_generation > 0");
                    table.CheckConstraint("CK_benchmark_judge_policy_revisions_revision", "revision > 0");
                    table.ForeignKey(
                        name: "FK_benchmark_judge_policy_revisions_benchmark_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "benchmark_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "benchmark_judge_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    policy_revision_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    cohort_generation = table.Column<int>(type: "INTEGER", nullable: false),
                    judge_runtime_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    judge_execution_key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    result_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    score = table.Column<int>(type: "INTEGER", nullable: true),
                    error_message = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    launch_receipt_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    environment_facts_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    variant = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    kv_cache_type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    kv_cache_type_source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    kv_auto_reason = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    flash_attention_mode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    intended_launch_identity = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    intended_executable_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    receipt_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    environment_facts_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    effective_launch_identity = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    effective_backend = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    placement_offloaded = table.Column<int>(type: "INTEGER", nullable: true),
                    placement_total = table.Column<int>(type: "INTEGER", nullable: true),
                    launch_executable_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    launch_has_aux_assets = table.Column<bool>(type: "INTEGER", nullable: true),
                    launch_kv_cache_type_source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    enqueued_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_benchmark_judge_attempts", x => x.id);
                    table.CheckConstraint("CK_benchmark_judge_attempts_cohort_generation", "cohort_generation > 0");
                    table.CheckConstraint("CK_benchmark_judge_attempts_score", "score IS NULL OR (score >= 0 AND score <= 100)");
                    table.CheckConstraint("CK_benchmark_judge_attempts_sequence", "sequence > 0");
                    table.CheckConstraint("CK_benchmark_judge_attempts_status", "status IN ('Queued', 'Running', 'Succeeded', 'Failed', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_benchmark_judge_attempts_benchmark_judge_policy_revisions_policy_revision_id",
                        column: x => x.policy_revision_id,
                        principalTable: "benchmark_judge_policy_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_benchmark_judge_attempts_benchmark_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "benchmark_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_benchmark_work_items_judge_attempt",
                table: "benchmark_work_items",
                column: "judge_attempt_id",
                unique: true,
                filter: "kind = 'Judge'");

            migrationBuilder.CreateIndex(
                name: "ux_benchmark_work_items_primary_run",
                table: "benchmark_work_items",
                column: "run_id",
                unique: true,
                filter: "kind = 'Primary'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_benchmark_work_items_judge_attempt",
                table: "benchmark_work_items",
                sql: "(kind = 'Primary' AND judge_attempt_id IS NULL) OR (kind = 'Judge' AND judge_attempt_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_benchmark_runs_user_score",
                table: "benchmark_runs",
                sql: "user_score IS NULL OR (user_score >= 0 AND user_score <= 100)");

            migrationBuilder.CreateIndex(
                name: "IX_benchmark_judge_attempts_policy_revision_id",
                table: "benchmark_judge_attempts",
                column: "policy_revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_judge_attempts_run_execution_key",
                table: "benchmark_judge_attempts",
                columns: new[] { "run_id", "judge_execution_key" });

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_judge_attempts_run_status",
                table: "benchmark_judge_attempts",
                columns: new[] { "run_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_benchmark_judge_attempts_run_sequence",
                table: "benchmark_judge_attempts",
                columns: new[] { "run_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_benchmark_judge_policy_revisions_project_policy_hash",
                table: "benchmark_judge_policy_revisions",
                columns: new[] { "project_id", "policy_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_benchmark_judge_policy_revisions_project_revision",
                table: "benchmark_judge_policy_revisions",
                columns: new[] { "project_id", "revision" },
                unique: true);
        }

        /// <inheritdoc />
        /// <remarks>Schema only — the rows Up deleted are not restored.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "benchmark_judge_attempts");

            migrationBuilder.DropTable(
                name: "benchmark_judge_policy_revisions");

            migrationBuilder.DropIndex(
                name: "ux_benchmark_work_items_judge_attempt",
                table: "benchmark_work_items");

            migrationBuilder.DropIndex(
                name: "ux_benchmark_work_items_primary_run",
                table: "benchmark_work_items");

            migrationBuilder.DropCheckConstraint(
                name: "CK_benchmark_work_items_judge_attempt",
                table: "benchmark_work_items");

            migrationBuilder.DropCheckConstraint(
                name: "CK_benchmark_runs_user_score",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "judge_attempt_id",
                table: "benchmark_work_items");

            migrationBuilder.DropColumn(
                name: "current_judge_attempt_id",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "current_judge_policy_revision_id",
                table: "benchmark_projects");

            migrationBuilder.CreateIndex(
                name: "ux_benchmark_work_items_run_kind",
                table: "benchmark_work_items",
                columns: new[] { "run_id", "kind" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_benchmark_runs_user_score",
                table: "benchmark_runs",
                sql: "user_score IS NULL OR (user_score >= 1 AND user_score <= 5)");
        }
    }
}
