using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations
{
    /// <summary>
    ///     This schema change ships whole. Three new tables (fidelity attempts, pairwise
    ///     comparisons, the Bradley-Terry fit), the run's plaintext fidelity projection, the project's fidelity
    ///     settings, the revision's comparison-set version, and the two new work-item id columns — plus the REWRITTEN
    ///     work-item CHECK covering all four kinds.
    ///     <para>
    ///         Splitting it would open a window in which a work item of a kind the old CHECK forbids is written and
    ///         the freeze fails, so every change lands together before the first Fidelity or Comparison row exists.
    ///     </para>
    ///     <para>
    ///         The CHECK rewrite is a SQLite table rebuild (SQLite cannot ALTER a constraint). The three
    ///         <c>COALESCE(task_case_id, x'00')</c> unique indexes are raw SQL below rather than model-builder
    ///         indexes: <c>HasIndex()</c> takes columns, not expressions, so EF would emit an index on the bare
    ///         nullable column — and SQLite lets a unique index repeat NULLs, which is precisely the hole the
    ///         COALESCE closes.
    ///     </para>
    /// </summary>
    public partial class AddBenchmarkP2Discrimination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_benchmark_work_items_judge_attempt",
                table: "benchmark_work_items");

            migrationBuilder.AddColumn<Guid>(
                name: "comparison_id",
                table: "benchmark_work_items",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "fidelity_attempt_id",
                table: "benchmark_work_items",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "fidelity_attempt_id",
                table: "benchmark_runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fidelity_error_message",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fidelity_status",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kld_base_fingerprint",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 67,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kld_base_logits_digest",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 67,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "kld_mean",
                table: "benchmark_runs",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "kld_p99",
                table: "benchmark_runs",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "perplexity_chunks",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "perplexity_context_tokens",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "perplexity_corpus_id",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "perplexity_mean",
                table: "benchmark_runs",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "perplexity_std_err",
                table: "benchmark_runs",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "top_token_agreement",
                table: "benchmark_runs",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "fidelity_chunks",
                table: "benchmark_projects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "fidelity_enabled",
                table: "benchmark_projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "fidelity_kld_base_fingerprint",
                table: "benchmark_projects",
                type: "TEXT",
                maxLength: 67,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fidelity_kld_base_model_name",
                table: "benchmark_projects",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "fidelity_kld_enabled",
                table: "benchmark_projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "comparison_set_version",
                table: "benchmark_judge_policy_revisions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "benchmark_comparisons",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    policy_revision_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    cohort_generation = table.Column<int>(type: "INTEGER", nullable: false),
                    task_case_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    task_input_hash = table.Column<string>(type: "TEXT", maxLength: 67, nullable: false),
                    run_a_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_b_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    order = table.Column<int>(type: "INTEGER", nullable: false),
                    attempt_sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    judge_runtime_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    judge_execution_key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    verdict = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    answer_a_truncated = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    answer_b_truncated = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    result_json = table.Column<byte[]>(type: "BLOB", nullable: true),
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
                    table.PrimaryKey("PK_benchmark_comparisons", x => x.id);
                    table.CheckConstraint("CK_benchmark_comparisons_cohort_generation", "cohort_generation > 0");
                    table.CheckConstraint("CK_benchmark_comparisons_pair_order", "run_a_id < run_b_id AND \"order\" IN (0, 1)");
                    table.CheckConstraint("CK_benchmark_comparisons_sequence", "sequence > 0 AND attempt_sequence > 0");
                    table.CheckConstraint("CK_benchmark_comparisons_status", "status IN ('Queued', 'Running', 'Succeeded', 'Failed', 'Cancelled')");
                    table.CheckConstraint("CK_benchmark_comparisons_verdict", "verdict IS NULL OR verdict IN ('a', 'b', 'tie')");
                    table.ForeignKey(
                        name: "FK_benchmark_comparisons_benchmark_judge_policy_revisions_policy_revision_id",
                        column: x => x.policy_revision_id,
                        principalTable: "benchmark_judge_policy_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_benchmark_comparisons_benchmark_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "benchmark_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "benchmark_fidelity_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    perplexity_mean = table.Column<double>(type: "REAL", nullable: true),
                    perplexity_std_err = table.Column<double>(type: "REAL", nullable: true),
                    perplexity_chunks = table.Column<int>(type: "INTEGER", nullable: true),
                    perplexity_context_tokens = table.Column<int>(type: "INTEGER", nullable: true),
                    corpus_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    kld_mean = table.Column<double>(type: "REAL", nullable: true),
                    kld_p99 = table.Column<double>(type: "REAL", nullable: true),
                    top_token_agreement = table.Column<double>(type: "REAL", nullable: true),
                    base_model_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    base_model_content_fingerprint = table.Column<string>(type: "TEXT", maxLength: 67, nullable: true),
                    base_logits_digest = table.Column<string>(type: "TEXT", maxLength: 67, nullable: true),
                    receipt_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    error_message = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    enqueued_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_benchmark_fidelity_attempts", x => x.id);
                    table.CheckConstraint("CK_benchmark_fidelity_attempts_kind", "kind IN ('ppl', 'kld')");
                    table.CheckConstraint("CK_benchmark_fidelity_attempts_sequence", "sequence > 0");
                    table.CheckConstraint("CK_benchmark_fidelity_attempts_status", "status IN ('Queued', 'Running', 'Succeeded', 'Failed', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_benchmark_fidelity_attempts_benchmark_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "benchmark_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "benchmark_pairwise_fits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    policy_revision_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    cohort_generation = table.Column<int>(type: "INTEGER", nullable: false),
                    task_case_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    fit_key = table.Column<string>(type: "TEXT", maxLength: 67, nullable: false),
                    judge_execution_key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    comparison_set_version = table.Column<int>(type: "INTEGER", nullable: false),
                    fitted_set_json = table.Column<string>(type: "TEXT", nullable: false),
                    scores_json = table.Column<string>(type: "TEXT", nullable: false),
                    iterations = table.Column<int>(type: "INTEGER", nullable: false),
                    bootstrap_replicates = table.Column<int>(type: "INTEGER", nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_benchmark_pairwise_fits", x => x.id);
                    table.CheckConstraint("CK_benchmark_pairwise_fits_cohort_generation", "cohort_generation > 0");
                    table.CheckConstraint("CK_benchmark_pairwise_fits_iterations", "iterations > 0 AND bootstrap_replicates > 0");
                    table.ForeignKey(
                        name: "FK_benchmark_pairwise_fits_benchmark_judge_policy_revisions_policy_revision_id",
                        column: x => x.policy_revision_id,
                        principalTable: "benchmark_judge_policy_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_benchmark_pairwise_fits_benchmark_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "benchmark_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_benchmark_work_items_comparison",
                table: "benchmark_work_items",
                column: "comparison_id",
                unique: true,
                filter: "kind = 'Comparison'");

            migrationBuilder.CreateIndex(
                name: "ux_benchmark_work_items_fidelity",
                table: "benchmark_work_items",
                column: "fidelity_attempt_id",
                unique: true,
                filter: "kind = 'Fidelity'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_benchmark_work_items_judge_attempt",
                table: "benchmark_work_items",
                sql: "(kind = 'Primary' AND judge_attempt_id IS NULL AND comparison_id IS NULL AND fidelity_attempt_id IS NULL) OR (kind = 'Judge' AND judge_attempt_id IS NOT NULL AND comparison_id IS NULL AND fidelity_attempt_id IS NULL) OR (kind = 'Fidelity' AND judge_attempt_id IS NULL AND comparison_id IS NULL AND fidelity_attempt_id IS NOT NULL) OR (kind = 'Comparison' AND judge_attempt_id IS NULL AND comparison_id IS NOT NULL AND fidelity_attempt_id IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_benchmark_comparisons_policy_revision_id",
                table: "benchmark_comparisons",
                column: "policy_revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_comparisons_project_generation",
                table: "benchmark_comparisons",
                columns: new[] { "project_id", "cohort_generation" });

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_fidelity_attempts_run_status",
                table: "benchmark_fidelity_attempts",
                columns: new[] { "run_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_benchmark_fidelity_attempts_run_sequence",
                table: "benchmark_fidelity_attempts",
                columns: new[] { "run_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_benchmark_pairwise_fits_policy_revision_id",
                table: "benchmark_pairwise_fits",
                column: "policy_revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_pairwise_fits_project",
                table: "benchmark_pairwise_fits",
                columns: new[] { "project_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ux_benchmark_pairwise_fits_key",
                table: "benchmark_pairwise_fits",
                column: "fit_key",
                unique: true);

            // Expression indexes: not expressible through HasIndex(), so they are written here and NOT declared in
            // OnModelCreating, so EF can never generate a competing bare-column index beside them.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_benchmark_comparisons_slot_attempt
                  ON benchmark_comparisons (policy_revision_id, cohort_generation,
                                            COALESCE(task_case_id, x'00'), run_a_id, run_b_id, "order",
                                            attempt_sequence);
                """);
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_benchmark_comparisons_slot_live
                  ON benchmark_comparisons (policy_revision_id, cohort_generation,
                                            COALESCE(task_case_id, x'00'), run_a_id, run_b_id, "order")
                  WHERE status IN ('Queued', 'Running', 'Succeeded');
                """);
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_benchmark_pairwise_fits_active
                  ON benchmark_pairwise_fits (policy_revision_id, cohort_generation,
                                              COALESCE(task_case_id, x'00'))
                  WHERE is_active = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_benchmark_pairwise_fits_active;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_benchmark_comparisons_slot_live;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_benchmark_comparisons_slot_attempt;");

            migrationBuilder.DropTable(
                name: "benchmark_comparisons");

            migrationBuilder.DropTable(
                name: "benchmark_fidelity_attempts");

            migrationBuilder.DropTable(
                name: "benchmark_pairwise_fits");

            migrationBuilder.DropIndex(
                name: "ux_benchmark_work_items_comparison",
                table: "benchmark_work_items");

            migrationBuilder.DropIndex(
                name: "ux_benchmark_work_items_fidelity",
                table: "benchmark_work_items");

            migrationBuilder.DropCheckConstraint(
                name: "CK_benchmark_work_items_judge_attempt",
                table: "benchmark_work_items");

            migrationBuilder.DropColumn(
                name: "comparison_id",
                table: "benchmark_work_items");

            migrationBuilder.DropColumn(
                name: "fidelity_attempt_id",
                table: "benchmark_work_items");

            migrationBuilder.DropColumn(
                name: "fidelity_attempt_id",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "fidelity_error_message",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "fidelity_status",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "kld_base_fingerprint",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "kld_base_logits_digest",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "kld_mean",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "kld_p99",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "perplexity_chunks",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "perplexity_context_tokens",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "perplexity_corpus_id",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "perplexity_mean",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "perplexity_std_err",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "top_token_agreement",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "fidelity_chunks",
                table: "benchmark_projects");

            migrationBuilder.DropColumn(
                name: "fidelity_enabled",
                table: "benchmark_projects");

            migrationBuilder.DropColumn(
                name: "fidelity_kld_base_fingerprint",
                table: "benchmark_projects");

            migrationBuilder.DropColumn(
                name: "fidelity_kld_base_model_name",
                table: "benchmark_projects");

            migrationBuilder.DropColumn(
                name: "fidelity_kld_enabled",
                table: "benchmark_projects");

            migrationBuilder.DropColumn(
                name: "comparison_set_version",
                table: "benchmark_judge_policy_revisions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_benchmark_work_items_judge_attempt",
                table: "benchmark_work_items",
                sql: "(kind = 'Primary' AND judge_attempt_id IS NULL) OR (kind = 'Judge' AND judge_attempt_id IS NOT NULL)");
        }
    }
}
