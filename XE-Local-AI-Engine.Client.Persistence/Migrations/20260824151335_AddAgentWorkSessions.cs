using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddAgentWorkSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_work_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    objective = table.Column<byte[]>(type: "BLOB", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    agent_definition_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    current_task_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    step_count = table.Column<int>(type: "INTEGER", nullable: false),
                    last_checkpoint_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    last_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    config_version = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_work_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agent_work_session_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    session_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    media_type = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    content_sha256 = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    is_valid = table.Column<bool>(type: "INTEGER", nullable: false),
                    managed_reference = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    created_step = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_work_session_artifacts", x => x.id);
                    table.ForeignKey(
                        name: "FK_agent_work_session_artifacts_agent_work_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "agent_work_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_work_session_checkpoints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    session_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    step = table.Column<int>(type: "INTEGER", nullable: false),
                    summary = table.Column<byte[]>(type: "BLOB", nullable: true),
                    state_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_work_session_checkpoints", x => x.id);
                    table.ForeignKey(
                        name: "FK_agent_work_session_checkpoints_agent_work_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "agent_work_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_work_session_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    session_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    step = table.Column<int>(type: "INTEGER", nullable: false),
                    event_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    detail_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    operation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    outcome = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    occurred_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_work_session_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_agent_work_session_events_agent_work_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "agent_work_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_work_session_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    session_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    parent_task_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    title = table.Column<byte[]>(type: "BLOB", nullable: false),
                    detail = table.Column<byte[]>(type: "BLOB", nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    blocked_reason = table.Column<byte[]>(type: "BLOB", nullable: true),
                    origin = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    created_step = table.Column<int>(type: "INTEGER", nullable: false),
                    updated_step = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_work_session_tasks", x => x.id);
                    table.ForeignKey(
                        name: "FK_agent_work_session_tasks_agent_work_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "agent_work_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_work_session_findings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    session_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    text = table.Column<byte[]>(type: "BLOB", nullable: false),
                    source_ref = table.Column<byte[]>(type: "BLOB", nullable: true),
                    created_step = table.Column<int>(type: "INTEGER", nullable: false),
                    superseded = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_work_session_findings", x => x.id);
                    table.ForeignKey(
                        name: "FK_agent_work_session_findings_agent_work_session_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "agent_work_session_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_work_session_findings_agent_work_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "agent_work_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_work_session_artifacts_session_sequence",
                table: "agent_work_session_artifacts",
                columns: new[] { "session_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ux_agent_work_session_artifacts_session_name",
                table: "agent_work_session_artifacts",
                columns: new[] { "session_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_work_session_checkpoints_session_sequence",
                table: "agent_work_session_checkpoints",
                columns: new[] { "session_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ux_agent_work_session_events_operation",
                table: "agent_work_session_events",
                columns: new[] { "session_id", "operation_id" },
                unique: true,
                filter: "operation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_agent_work_session_events_session_sequence",
                table: "agent_work_session_events",
                columns: new[] { "session_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_work_session_findings_session_kind_superseded",
                table: "agent_work_session_findings",
                columns: new[] { "session_id", "kind", "superseded" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_work_session_findings_session_sequence",
                table: "agent_work_session_findings",
                columns: new[] { "session_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_work_session_findings_task_id",
                table: "agent_work_session_findings",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_work_session_tasks_parent",
                table: "agent_work_session_tasks",
                column: "parent_task_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_work_session_tasks_session_sequence",
                table: "agent_work_session_tasks",
                columns: new[] { "session_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_work_sessions_status_updated",
                table: "agent_work_sessions",
                columns: new[] { "status", "updated_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_agent_work_sessions_conversation_id",
                table: "agent_work_sessions",
                column: "conversation_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_work_session_artifacts");

            migrationBuilder.DropTable(
                name: "agent_work_session_checkpoints");

            migrationBuilder.DropTable(
                name: "agent_work_session_events");

            migrationBuilder.DropTable(
                name: "agent_work_session_findings");

            migrationBuilder.DropTable(
                name: "agent_work_session_tasks");

            migrationBuilder.DropTable(
                name: "agent_work_sessions");
        }
    }
}
