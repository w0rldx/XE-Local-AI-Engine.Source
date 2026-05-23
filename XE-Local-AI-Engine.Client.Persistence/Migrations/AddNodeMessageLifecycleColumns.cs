using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeMessageLifecycleColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "error",
                table: "messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "request_id",
                table: "messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "messages",
                type: "TEXT",
                nullable: false,
                defaultValue: "completed");

            migrationBuilder.AddColumn<long>(
                name: "updated_at_utc",
                table: "messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.Sql("UPDATE messages SET updated_at_utc = created_at_utc WHERE updated_at_utc = 0;");

            migrationBuilder.CreateIndex(
                name: "IX_messages_request_id",
                table: "messages",
                column: "request_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_messages_request_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "error",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "request_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "status",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "updated_at_utc",
                table: "messages");
        }
    }
}
