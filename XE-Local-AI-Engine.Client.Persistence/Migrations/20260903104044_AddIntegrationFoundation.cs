using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddIntegrationFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "conversations",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "chat");

            // Backfill AFTER the AddColumn, so every existing row already holds the 'chat' default and only the
            // work-session-owned transcripts move. Idempotent by construction: re-running sets rows to the value
            // they already hold. agent_work_sessions has existed since AddWorkSessions, well before this
            // migration, so no IF EXISTS guard is needed.
            migrationBuilder.Sql("""
                                 UPDATE conversations
                                 SET kind = 'work-session'
                                 WHERE conversation_id IN (SELECT conversation_id FROM agent_work_sessions);
                                 """);

            migrationBuilder.CreateTable(
                name: "integration_api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    principal_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    key_prefix = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    key_hash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    label = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    allowed_trigger_ids_json = table.Column<string>(type: "TEXT", nullable: true),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    last_used_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    revoked_at_utc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_api_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "integration_executions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    trigger_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    session_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    principal_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    request_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    request_fingerprint = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: false),
                    key_prefix = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    invocation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    received_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    ended_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    stop_requested_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    failure_category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    failure_summary = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    output_count = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    output_bytes = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    last_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_executions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "integration_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    trigger_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    principal_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_definition_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    last_activity_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    execution_count = table.Column<int>(type: "INTEGER", nullable: false),
                    last_sequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "integration_triggers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    target_kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    target_agent_definition_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    session_policy = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    accepted_input_kinds = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_triggers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "integration_execution_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    execution_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    event_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    detail_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    occurred_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_execution_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_integration_execution_events_integration_executions_execution_id",
                        column: x => x.execution_id,
                        principalTable: "integration_executions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_integration_api_keys_prefix",
                table: "integration_api_keys",
                column: "key_prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_integration_execution_events_execution_sequence",
                table: "integration_execution_events",
                columns: new[] { "execution_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_integration_executions_session",
                table: "integration_executions",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_integration_executions_status_received",
                table: "integration_executions",
                columns: new[] { "status", "received_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_integration_executions_trigger",
                table: "integration_executions",
                column: "trigger_id");

            migrationBuilder.CreateIndex(
                name: "ux_integration_executions_principal_request",
                table: "integration_executions",
                columns: new[] { "principal_id", "request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_integration_sessions_status_activity",
                table: "integration_sessions",
                columns: new[] { "status", "last_activity_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_integration_sessions_trigger",
                table: "integration_sessions",
                column: "trigger_id");

            migrationBuilder.CreateIndex(
                name: "ux_integration_sessions_conversation_id",
                table: "integration_sessions",
                column: "conversation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_integration_triggers_enabled",
                table: "integration_triggers",
                column: "enabled");

            migrationBuilder.CreateIndex(
                name: "ux_integration_triggers_name",
                table: "integration_triggers",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "integration_api_keys");

            migrationBuilder.DropTable(
                name: "integration_execution_events");

            migrationBuilder.DropTable(
                name: "integration_sessions");

            migrationBuilder.DropTable(
                name: "integration_triggers");

            migrationBuilder.DropTable(
                name: "integration_executions");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "conversations");
        }
    }
}
