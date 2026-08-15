using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddTrainingEvaluation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "training_evaluation_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    training_run_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    comparison_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    model_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    model_content_fingerprint = table.Column<string>(type: "TEXT", maxLength: 67, nullable: true),
                    dataset_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    dataset_content_fingerprint = table.Column<string>(type: "TEXT", maxLength: 67, nullable: false),
                    membership_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    results_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    total_count = table.Column<int>(type: "INTEGER", nullable: false),
                    scored_count = table.Column<int>(type: "INTEGER", nullable: false),
                    passed_count = table.Column<int>(type: "INTEGER", nullable: false),
                    per_kind_json = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    error_message = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_training_evaluation_runs", x => x.id);
                    table.CheckConstraint("CK_training_evaluation_runs_counts", "total_count >= 0 AND scored_count >= 0 AND passed_count >= 0 AND scored_count <= total_count AND passed_count <= scored_count");
                    table.ForeignKey(
                        name: "FK_training_evaluation_runs_training_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "training_datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_training_evaluation_runs_training_runs_training_run_id",
                        column: x => x.training_run_id,
                        principalTable: "training_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "training_comparison_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    base_evaluation_run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tuned_evaluation_run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    base_benchmark_run_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    tuned_benchmark_run_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    training_run_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    deltas_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_training_comparison_reports", x => x.id);
                    table.ForeignKey(
                        name: "FK_training_comparison_reports_training_evaluation_runs_base_evaluation_run_id",
                        column: x => x.base_evaluation_run_id,
                        principalTable: "training_evaluation_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_training_comparison_reports_training_evaluation_runs_tuned_evaluation_run_id",
                        column: x => x.tuned_evaluation_run_id,
                        principalTable: "training_evaluation_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_training_comparison_reports_base_evaluation_run_id",
                table: "training_comparison_reports",
                column: "base_evaluation_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_comparison_reports_training_run",
                table: "training_comparison_reports",
                column: "training_run_id");

            migrationBuilder.CreateIndex(
                name: "IX_training_comparison_reports_tuned_evaluation_run_id",
                table: "training_comparison_reports",
                column: "tuned_evaluation_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_evaluation_runs_comparison",
                table: "training_evaluation_runs",
                column: "comparison_id");

            migrationBuilder.CreateIndex(
                name: "IX_training_evaluation_runs_dataset_id",
                table: "training_evaluation_runs",
                column: "dataset_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_evaluation_runs_status",
                table: "training_evaluation_runs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_training_evaluation_runs_training_run",
                table: "training_evaluation_runs",
                column: "training_run_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "training_comparison_reports");

            migrationBuilder.DropTable(
                name: "training_evaluation_runs");
        }
    }
}
