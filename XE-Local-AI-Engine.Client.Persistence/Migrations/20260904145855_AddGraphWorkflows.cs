using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddGraphWorkflows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "graph_workflow_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    graph_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    graph_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    node_count = table.Column<int>(type: "INTEGER", nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_graph_workflow_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "graph_workflow_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    request_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    definition_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    definition_version = table.Column<int>(type: "INTEGER", nullable: false),
                    graph_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    failure_class = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    graph_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    input_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    output_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    seq = table.Column<long>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    cancel_requested_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_graph_workflow_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "graph_workflow_node_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    node_key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    pending_decision_kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    decision_operation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    decided_by_subject = table.Column<byte[]>(type: "BLOB", nullable: true),
                    failure_class = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    error = table.Column<byte[]>(type: "BLOB", nullable: true),
                    input_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    output_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    invocation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_graph_workflow_node_runs", x => x.id);
                    table.ForeignKey(
                        name: "FK_graph_workflow_node_runs_graph_workflow_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "graph_workflow_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "graph_workflow_run_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    seq = table.Column<long>(type: "INTEGER", nullable: false),
                    event_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    node_key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    detail_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_graph_workflow_run_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_graph_workflow_run_events_graph_workflow_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "graph_workflow_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_graph_workflow_definitions_name",
                table: "graph_workflow_definitions",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_graph_workflow_node_runs_status",
                table: "graph_workflow_node_runs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_graph_workflow_node_runs_decision_operation",
                table: "graph_workflow_node_runs",
                columns: new[] { "run_id", "decision_operation_id" },
                unique: true,
                filter: "\"decision_operation_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_graph_workflow_node_runs_run_node",
                table: "graph_workflow_node_runs",
                columns: new[] { "run_id", "node_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_graph_workflow_run_events_run_seq",
                table: "graph_workflow_run_events",
                columns: new[] { "run_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_graph_workflow_runs_definition",
                table: "graph_workflow_runs",
                column: "definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_graph_workflow_runs_status_created",
                table: "graph_workflow_runs",
                columns: new[] { "status", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_graph_workflow_runs_request_id",
                table: "graph_workflow_runs",
                column: "request_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "graph_workflow_definitions");

            migrationBuilder.DropTable(
                name: "graph_workflow_node_runs");

            migrationBuilder.DropTable(
                name: "graph_workflow_run_events");

            migrationBuilder.DropTable(
                name: "graph_workflow_runs");
        }
    }
}
