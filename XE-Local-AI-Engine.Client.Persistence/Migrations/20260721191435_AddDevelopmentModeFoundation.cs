using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDevelopmentModeFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "development_projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    objective = table.Column<byte[]>(type: "BLOB", nullable: false),
                    repository_identity_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    base_branch = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    egress_policy = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    coder_model_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    reviewer_model_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    max_tokens = table.Column<int>(type: "INTEGER", nullable: true),
                    max_duration_seconds = table.Column<int>(type: "INTEGER", nullable: true),
                    configuration_version = table.Column<int>(type: "INTEGER", nullable: false),
                    trusted_repository_acknowledged = table.Column<bool>(type: "INTEGER", nullable: false),
                    trusted_repository_policy_version = table.Column<int>(type: "INTEGER", nullable: true),
                    trusted_repository_acknowledged_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_projects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "development_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    title = table.Column<byte[]>(type: "BLOB", nullable: false),
                    requirements = table.Column<byte[]>(type: "BLOB", nullable: false),
                    acceptance_criteria_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    current_review_round = table.Column<int>(type: "INTEGER", nullable: false),
                    max_review_rounds = table.Column<int>(type: "INTEGER", nullable: false),
                    blocked_reason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    blocked_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    approved_subject_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_tasks", x => x.id);
                    table.ForeignKey(
                        name: "FK_development_tasks_development_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "development_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "development_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    predecessor_attempt_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    role = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    model_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    provider = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    ended_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    terminal_reason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    input_tokens = table.Column<long>(type: "INTEGER", nullable: true),
                    output_tokens = table.Column<long>(type: "INTEGER", nullable: true),
                    start_operation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_attempts", x => x.id);
                    table.ForeignKey(
                        name: "FK_development_attempts_development_attempts_predecessor_attempt_id",
                        column: x => x.predecessor_attempt_id,
                        principalTable: "development_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_development_attempts_development_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "development_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "development_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    attempt_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    content_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    managed_reference = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    content_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    byte_count = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    base_commit = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    subject_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    changed_files_manifest_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    input_artifact_ids_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    command_profile_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    is_valid = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_artifacts", x => x.id);
                    table.ForeignKey(
                        name: "FK_development_artifacts_development_attempts_attempt_id",
                        column: x => x.attempt_id,
                        principalTable: "development_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_development_artifacts_development_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "development_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_development_artifacts_development_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "development_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "development_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    attempt_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    event_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    occurred_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    detail_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    operation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    operation_phase = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    outcome = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    result_metadata_json = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_development_events_development_attempts_attempt_id",
                        column: x => x.attempt_id,
                        principalTable: "development_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_development_events_development_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "development_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_development_events_development_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "development_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_development_artifacts_attempt_id",
                table: "development_artifacts",
                column: "attempt_id");

            migrationBuilder.CreateIndex(
                name: "IX_development_artifacts_project_id",
                table: "development_artifacts",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_development_artifacts_task_kind_valid",
                table: "development_artifacts",
                columns: new[] { "task_id", "kind", "is_valid" });

            migrationBuilder.CreateIndex(
                name: "ix_development_attempts_predecessor_attempt_id",
                table: "development_attempts",
                column: "predecessor_attempt_id");

            migrationBuilder.CreateIndex(
                name: "ux_development_attempts_one_active_per_task",
                table: "development_attempts",
                column: "task_id",
                unique: true,
                filter: "status IN ('Pending', 'Running')");

            migrationBuilder.CreateIndex(
                name: "ux_development_attempts_start_operation_id",
                table: "development_attempts",
                column: "start_operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_development_events_attempt_id",
                table: "development_events",
                column: "attempt_id");

            migrationBuilder.CreateIndex(
                name: "ix_development_events_task_id",
                table: "development_events",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "ux_development_events_operation_phase",
                table: "development_events",
                columns: new[] { "project_id", "operation_id", "operation_phase" },
                unique: true,
                filter: "operation_id IS NOT NULL AND operation_phase IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_development_events_project_sequence",
                table: "development_events",
                columns: new[] { "project_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_development_projects_repository_identity_hash",
                table: "development_projects",
                column: "repository_identity_hash");

            migrationBuilder.CreateIndex(
                name: "ux_development_tasks_project_id",
                table: "development_tasks",
                column: "project_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "development_artifacts");

            migrationBuilder.DropTable(
                name: "development_events");

            migrationBuilder.DropTable(
                name: "development_attempts");

            migrationBuilder.DropTable(
                name: "development_tasks");

            migrationBuilder.DropTable(
                name: "development_projects");
        }
    }
}
