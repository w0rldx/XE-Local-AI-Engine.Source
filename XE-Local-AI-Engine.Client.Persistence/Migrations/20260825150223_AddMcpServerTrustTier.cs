using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <summary>
    ///     Adds the MCP trust tier. The default of 0 is <c>Sandboxed</c>, so every EXISTING stdio registration migrates
    ///     into the boundary rather than being grandfathered outside it. That is a deliberate, breaking default: a
    ///     server that genuinely needs host access stops connecting and says so on the settings page, where the
    ///     operator can grant <c>PrivilegedHost</c> explicitly. See <c>docs/security/mcp-trust-tiers.md</c>.
    /// </summary>
    public partial class AddMcpServerTrustTier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "trust_tier",
                table: "mcp_servers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_mcp_servers_trust_tier",
                table: "mcp_servers",
                sql: "trust_tier IN (0, 1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_mcp_servers_trust_tier",
                table: "mcp_servers");

            migrationBuilder.DropColumn(
                name: "trust_tier",
                table: "mcp_servers");
        }
    }
}
