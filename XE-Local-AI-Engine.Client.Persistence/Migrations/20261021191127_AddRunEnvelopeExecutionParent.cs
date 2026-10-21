using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddRunEnvelopeExecutionParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "parent_execution_id",
                table: "agent_execution_logs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "parent_execution_kind",
                table: "agent_execution_logs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "parent_execution_id",
                table: "agent_execution_logs");

            migrationBuilder.DropColumn(
                name: "parent_execution_kind",
                table: "agent_execution_logs");
        }
    }
}
