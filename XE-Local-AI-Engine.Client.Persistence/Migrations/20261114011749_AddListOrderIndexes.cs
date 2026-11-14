using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddListOrderIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_scheduled_job_runs_fire_time",
                table: "scheduled_job_runs",
                columns: new[] { "actual_fire_time_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_documents_created",
                table: "knowledge_documents",
                columns: new[] { "created_at_utc", "document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_integration_executions_received",
                table: "integration_executions",
                columns: new[] { "received_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_execution_logs_conversation_created",
                table: "agent_execution_logs",
                columns: new[] { "conversation_id", "created_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_scheduled_job_runs_fire_time",
                table: "scheduled_job_runs");

            migrationBuilder.DropIndex(
                name: "ix_knowledge_documents_created",
                table: "knowledge_documents");

            migrationBuilder.DropIndex(
                name: "ix_integration_executions_received",
                table: "integration_executions");

            migrationBuilder.DropIndex(
                name: "ix_agent_execution_logs_conversation_created",
                table: "agent_execution_logs");
        }
    }
}
