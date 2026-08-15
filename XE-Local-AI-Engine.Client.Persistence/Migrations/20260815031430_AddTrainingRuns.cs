using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddTrainingRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "training_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    dataset_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    dataset_content_fingerprint = table.Column<string>(type: "TEXT", maxLength: 67, nullable: false),
                    dataset_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    freeze_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    base_artifact_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    linked_installed_model_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    linked_model_content_fingerprint = table.Column<string>(type: "TEXT", maxLength: 67, nullable: true),
                    options_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    license_confirmation_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    progress_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    log_tail = table.Column<byte[]>(type: "BLOB", nullable: true),
                    launch_receipt_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    error_message = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_training_runs", x => x.id);
                    table.CheckConstraint("CK_training_runs_dataset_revision", "dataset_revision >= 0");
                    table.ForeignKey(
                        name: "FK_training_runs_training_base_artifacts_base_artifact_id",
                        column: x => x.base_artifact_id,
                        principalTable: "training_base_artifacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_training_runs_training_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "training_datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "training_work_items",
                columns: table => new
                {
                    queue_sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    target_id = table.Column<Guid>(type: "TEXT", nullable: false),
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
                    table.PrimaryKey("PK_training_work_items", x => x.queue_sequence);
                    table.CheckConstraint("CK_training_work_items_attempt", "attempt = 1");
                });

            migrationBuilder.CreateTable(
                name: "training_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    smoke_state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    smoke_reason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    committed_model_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_training_artifacts", x => x.id);
                    table.CheckConstraint("CK_training_artifacts_size_bytes", "size_bytes >= 0");
                    table.ForeignKey(
                        name: "FK_training_artifacts_training_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "training_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_training_artifacts_run",
                table: "training_artifacts",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_runs_base_artifact",
                table: "training_runs",
                column: "base_artifact_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_runs_dataset",
                table: "training_runs",
                column: "dataset_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_runs_status",
                table: "training_runs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_training_work_items_status_sequence",
                table: "training_work_items",
                columns: new[] { "status", "queue_sequence" });

            migrationBuilder.CreateIndex(
                name: "ux_training_work_items_target_kind",
                table: "training_work_items",
                columns: new[] { "target_id", "kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "training_artifacts");

            migrationBuilder.DropTable(
                name: "training_work_items");

            migrationBuilder.DropTable(
                name: "training_runs");
        }
    }
}
