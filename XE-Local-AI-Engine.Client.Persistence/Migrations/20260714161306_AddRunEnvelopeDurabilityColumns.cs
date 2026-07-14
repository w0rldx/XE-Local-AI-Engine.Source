using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddRunEnvelopeDurabilityColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "reasoning_tokens",
                table: "agent_execution_logs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "started_at_utc",
                table: "agent_execution_logs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "total_tokens",
                table: "agent_execution_logs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_execution_logs_envelope_message_id",
                table: "agent_execution_logs",
                column: "message_id",
                unique: true,
                filter: "record_kind = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_agent_execution_logs_envelope_message_id",
                table: "agent_execution_logs");

            migrationBuilder.DropColumn(
                name: "reasoning_tokens",
                table: "agent_execution_logs");

            migrationBuilder.DropColumn(
                name: "started_at_utc",
                table: "agent_execution_logs");

            migrationBuilder.DropColumn(
                name: "total_tokens",
                table: "agent_execution_logs");
        }
    }
}
