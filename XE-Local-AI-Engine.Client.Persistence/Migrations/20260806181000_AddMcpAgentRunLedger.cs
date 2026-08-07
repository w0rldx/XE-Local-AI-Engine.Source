namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb;

using Microsoft.EntityFrameworkCore.Migrations;

public sealed partial class AddMcpAgentRunLedger : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "mcp_agent_run_ledger",
            columns: table => new
            {
                id = table.Column<int>(type: "INTEGER", nullable: false),
                accounting_version = table.Column<int>(type: "INTEGER", nullable: false),
                nonterminal_run_count = table.Column<long>(type: "INTEGER", nullable: false),
                queued_run_count = table.Column<long>(type: "INTEGER", nullable: false),
                running_run_count = table.Column<long>(type: "INTEGER", nullable: false),
                identity_count = table.Column<long>(type: "INTEGER", nullable: false),
                active_payload_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                tombstone_logical_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_mcp_agent_run_ledger", x => x.id);
                table.CheckConstraint("CK_mcp_agent_run_ledger_singleton", "id = 1");
                table.CheckConstraint("CK_mcp_agent_run_ledger_accounting_version", "accounting_version = 1");
                table.CheckConstraint("CK_mcp_agent_run_ledger_nonnegative",
                    "nonterminal_run_count >= 0 AND queued_run_count >= 0 AND running_run_count >= 0 AND nonterminal_run_count = queued_run_count + running_run_count AND identity_count >= 0 AND active_payload_bytes >= 0 AND tombstone_logical_bytes >= 0");
            });

        migrationBuilder.CreateTable(
            name: "mcp_agent_runs",
            columns: table => new
            {
                request_id = table.Column<Guid>(type: "TEXT", nullable: false),
                request_fingerprint = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: false),
                accounting_version = table.Column<int>(type: "INTEGER", nullable: false),
                status = table.Column<int>(type: "INTEGER", nullable: false),
                version = table.Column<long>(type: "INTEGER", nullable: false),
                claim_token = table.Column<Guid>(type: "TEXT", nullable: true),
                stop_reason = table.Column<int>(type: "INTEGER", nullable: false),
                stop_requested_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                agent_definition_id = table.Column<Guid>(type: "TEXT", nullable: true),
                agent_definition_version = table.Column<long>(type: "INTEGER", nullable: true),
                model_id = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                model_override_id = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                workspace_id = table.Column<Guid>(type: "TEXT", nullable: true),
                binding_fingerprint = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: true),
                task_payload = table.Column<byte[]>(type: "BLOB", nullable: true),
                instructions_payload = table.Column<byte[]>(type: "BLOB", nullable: true),
                result_payload = table.Column<byte[]>(type: "BLOB", nullable: true),
                display_payload = table.Column<byte[]>(type: "BLOB", nullable: true),
                failure_code = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                reserved_active_payload_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                active_payload_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                tombstone_logical_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                claimed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                completed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                payload_expires_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                compacted_at_utc = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_mcp_agent_runs", x => x.request_id);
                table.CheckConstraint("CK_mcp_agent_runs_accounting_version", "accounting_version = 1");
                table.CheckConstraint("CK_mcp_agent_runs_nonnegative",
                    "version >= 0 AND reserved_active_payload_bytes >= 0 AND active_payload_bytes >= 0 AND tombstone_logical_bytes >= 0");
            });

        migrationBuilder.Sql("""
            INSERT INTO mcp_agent_run_ledger (
                id, accounting_version, nonterminal_run_count, queued_run_count, running_run_count,
                identity_count, active_payload_bytes,
                tombstone_logical_bytes, updated_at_utc)
            VALUES (1, 1, 0, 0, 0, 0, 0, 0, 0);
            """);

        migrationBuilder.CreateIndex(
            name: "IX_mcp_agent_runs_payload_expires_at_utc",
            table: "mcp_agent_runs",
            column: "payload_expires_at_utc");

        migrationBuilder.CreateIndex(
            name: "IX_mcp_agent_runs_status_created_at_utc",
            table: "mcp_agent_runs",
            columns: new[] { "status", "created_at_utc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "mcp_agent_run_ledger");
        migrationBuilder.DropTable(name: "mcp_agent_runs");
    }
}
