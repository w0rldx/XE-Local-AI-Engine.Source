using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowConcurrentRefreshSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_node_refresh_tokens_user_id",
                table: "node_refresh_tokens");

            migrationBuilder.AddColumn<string>(
                name: "replaced_by_token_id",
                table: "node_refresh_tokens",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_node_refresh_tokens_user_id",
                table: "node_refresh_tokens",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_node_refresh_tokens_user_id",
                table: "node_refresh_tokens");

            migrationBuilder.DropColumn(
                name: "replaced_by_token_id",
                table: "node_refresh_tokens");

            // The restored one-live-token-per-user index cannot hold concurrent sessions, so a rollback signs every session out first.
            migrationBuilder.Sql("UPDATE node_refresh_tokens SET revoked_at_utc = strftime('%Y-%m-%d %H:%M:%f', 'now') WHERE revoked_at_utc IS NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_node_refresh_tokens_user_id",
                table: "node_refresh_tokens",
                column: "user_id",
                unique: true,
                filter: "\"revoked_at_utc\" IS NULL");
        }
    }
}
