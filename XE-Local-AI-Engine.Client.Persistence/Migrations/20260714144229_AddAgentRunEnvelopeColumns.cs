using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddAgentRunEnvelopeColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "content_chunk_count",
                table: "agent_execution_logs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "invocation_id",
                table: "agent_execution_logs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "reasoning_chunk_count",
                table: "agent_execution_logs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "record_kind",
                table: "agent_execution_logs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "request_id",
                table: "agent_execution_logs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "schema_version",
                table: "agent_execution_logs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "terminal_status",
                table: "agent_execution_logs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "trace_id",
                table: "agent_execution_logs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "content_chunk_count",
                table: "agent_execution_logs");

            migrationBuilder.DropColumn(
                name: "invocation_id",
                table: "agent_execution_logs");

            migrationBuilder.DropColumn(
                name: "reasoning_chunk_count",
                table: "agent_execution_logs");

            migrationBuilder.DropColumn(
                name: "record_kind",
                table: "agent_execution_logs");

            migrationBuilder.DropColumn(
                name: "request_id",
                table: "agent_execution_logs");

            migrationBuilder.DropColumn(
                name: "schema_version",
                table: "agent_execution_logs");

            migrationBuilder.DropColumn(
                name: "terminal_status",
                table: "agent_execution_logs");

            migrationBuilder.DropColumn(
                name: "trace_id",
                table: "agent_execution_logs");
        }
    }
}
