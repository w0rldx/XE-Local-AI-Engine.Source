using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDevWorkflowFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dev_workflow_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    graph_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    graph_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    node_count = table.Column<int>(type: "INTEGER", nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    seed_slug = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    archived = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_workflow_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dev_workflow_work_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    request = table.Column<byte[]>(type: "BLOB", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    development_project_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_workflow_work_items", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dev_workflow_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    work_item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    definition_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    definition_version = table.Column<int>(type: "INTEGER", nullable: false),
                    definition_graph_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    graph_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    graph_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    last_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    failure_class = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    terminal_reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    ended_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_workflow_runs", x => x.id);
                    table.ForeignKey(
                        name: "FK_dev_workflow_runs_dev_workflow_work_items_work_item_id",
                        column: x => x.work_item_id,
                        principalTable: "dev_workflow_work_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dev_workflow_artifact_uses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    node_run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    artifact_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    recorded_sequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_workflow_artifact_uses", x => x.id);
                    table.ForeignKey(
                        name: "FK_dev_workflow_artifact_uses_dev_workflow_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "dev_workflow_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dev_workflow_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    lineage_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    producing_node_key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    produced_by_node_run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    media_type = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    content_sha256 = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    managed_reference = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    is_valid = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_stale = table.Column<bool>(type: "INTEGER", nullable: false),
                    stale_since_sequence = table.Column<long>(type: "INTEGER", nullable: true),
                    stale_because_artifact_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    stale_reason = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_workflow_artifacts", x => x.id);
                    table.ForeignKey(
                        name: "FK_dev_workflow_artifacts_dev_workflow_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "dev_workflow_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dev_workflow_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    node_run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    decision = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    comment = table.Column<byte[]>(type: "BLOB", nullable: true),
                    payload_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    decided_by_subject = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    operation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    decided_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_workflow_decisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_dev_workflow_decisions_dev_workflow_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "dev_workflow_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dev_workflow_node_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    node_key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    node_type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    max_attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    session_resumes = table.Column<int>(type: "INTEGER", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    queue_reason = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    pending_decision_kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    work_session_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    agent_definition_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    development_project_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    development_task_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    input_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    output_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    policy_resolution_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    materialized_from_node_run_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    materialization_index = table.Column<int>(type: "INTEGER", nullable: true),
                    failure_class = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    terminal_reason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    queued_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    ended_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_workflow_node_runs", x => x.id);
                    table.ForeignKey(
                        name: "FK_dev_workflow_node_runs_dev_workflow_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "dev_workflow_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dev_workflow_run_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    node_run_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    event_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    detail_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    operation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    outcome = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    occurred_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_workflow_run_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_dev_workflow_run_events_dev_workflow_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "dev_workflow_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dev_workflow_artifact_uses_artifact",
                table: "dev_workflow_artifact_uses",
                column: "artifact_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_workflow_artifact_uses_run_id",
                table: "dev_workflow_artifact_uses",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ux_dev_workflow_artifact_uses_node_artifact",
                table: "dev_workflow_artifact_uses",
                columns: new[] { "node_run_id", "artifact_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dev_workflow_artifacts_producer",
                table: "dev_workflow_artifacts",
                column: "produced_by_node_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_dev_workflow_artifacts_run_node_name",
                table: "dev_workflow_artifacts",
                columns: new[] { "run_id", "producing_node_key", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_dev_workflow_artifacts_run_sequence",
                table: "dev_workflow_artifacts",
                columns: new[] { "run_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ux_dev_workflow_artifacts_lineage_version",
                table: "dev_workflow_artifacts",
                columns: new[] { "lineage_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dev_workflow_decisions_run_sequence",
                table: "dev_workflow_decisions",
                columns: new[] { "run_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ux_dev_workflow_decisions_node_run_attempt",
                table: "dev_workflow_decisions",
                columns: new[] { "node_run_id", "attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_dev_workflow_decisions_operation",
                table: "dev_workflow_decisions",
                columns: new[] { "run_id", "operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dev_workflow_definitions_archived",
                table: "dev_workflow_definitions",
                columns: new[] { "archived", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_dev_workflow_definitions_name",
                table: "dev_workflow_definitions",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ux_dev_workflow_definitions_seed_slug",
                table: "dev_workflow_definitions",
                column: "seed_slug",
                unique: true,
                filter: "\"seed_slug\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_dev_workflow_node_runs_development_task",
                table: "dev_workflow_node_runs",
                column: "development_task_id");

            migrationBuilder.CreateIndex(
                name: "ix_dev_workflow_node_runs_materialized_from",
                table: "dev_workflow_node_runs",
                column: "materialized_from_node_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_dev_workflow_node_runs_run_sequence",
                table: "dev_workflow_node_runs",
                columns: new[] { "run_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_dev_workflow_node_runs_status",
                table: "dev_workflow_node_runs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_dev_workflow_node_runs_run_node",
                table: "dev_workflow_node_runs",
                columns: new[] { "run_id", "node_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_dev_workflow_node_runs_work_session",
                table: "dev_workflow_node_runs",
                column: "work_session_id",
                unique: true,
                filter: "\"work_session_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_dev_workflow_run_events_node_run",
                table: "dev_workflow_run_events",
                column: "node_run_id");

            migrationBuilder.CreateIndex(
                name: "ux_dev_workflow_run_events_operation",
                table: "dev_workflow_run_events",
                columns: new[] { "run_id", "operation_id" },
                unique: true,
                filter: "\"operation_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_dev_workflow_run_events_run_sequence",
                table: "dev_workflow_run_events",
                columns: new[] { "run_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dev_workflow_runs_definition",
                table: "dev_workflow_runs",
                column: "definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_dev_workflow_runs_status_updated",
                table: "dev_workflow_runs",
                columns: new[] { "status", "updated_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_dev_workflow_runs_work_item",
                table: "dev_workflow_runs",
                columns: new[] { "work_item_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_dev_workflow_runs_live_per_work_item",
                table: "dev_workflow_runs",
                column: "work_item_id",
                unique: true,
                filter: "\"status\" IN ('Pending','Running','Pausing','Paused','WaitingForApproval','Cancelling')");

            migrationBuilder.CreateIndex(
                name: "ix_dev_workflow_work_items_project",
                table: "dev_workflow_work_items",
                column: "development_project_id");

            migrationBuilder.CreateIndex(
                name: "ix_dev_workflow_work_items_status_updated",
                table: "dev_workflow_work_items",
                columns: new[] { "status", "updated_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dev_workflow_artifact_uses");

            migrationBuilder.DropTable(
                name: "dev_workflow_artifacts");

            migrationBuilder.DropTable(
                name: "dev_workflow_decisions");

            migrationBuilder.DropTable(
                name: "dev_workflow_definitions");

            migrationBuilder.DropTable(
                name: "dev_workflow_node_runs");

            migrationBuilder.DropTable(
                name: "dev_workflow_run_events");

            migrationBuilder.DropTable(
                name: "dev_workflow_runs");

            migrationBuilder.DropTable(
                name: "dev_workflow_work_items");
        }
    }
}
