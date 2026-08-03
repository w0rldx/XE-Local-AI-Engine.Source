using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <summary>
    ///     Switches the inbound-MCP bearer credential from reversibly-stored plaintext to a one-way SHA-256 digest.
    ///     <para>
    ///         <b>This migration FORCES REGENERATION: any existing key stops working.</b> That is the deliberate choice
    ///         over converting in place. Conversion would have to decrypt the stored plaintext before hashing it, which
    ///         needs the node encryption key — a runtime secret an EF migration has no access to — so it could not live
    ///         here at all. There is at most one key on one machine, and regenerating it is a Node Settings button, so
    ///         the conversion machinery would cost more than the reconfiguration it saves.
    ///     </para>
    ///     <para>
    ///         The DELETE is load-bearing, not tidiness. A surviving row would carry ciphertext sealed under the old AAD
    ///         column name (<c>mcp_api_key_material</c>); the materialization interceptor now authenticates against
    ///         <c>mcp_api_key_hash</c>, so the AEAD tag would not verify and every read of the table would throw. The
    ///         row must go, not merely be renamed around.
    ///     </para>
    /// </summary>
    public partial class HashMcpServerApiKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the credential BEFORE the rename: the old value is encrypted plaintext under the old AAD and is
            // unusable — and unreadable — once the column changes meaning.
            migrationBuilder.Sql("DELETE FROM mcp_server_api_keys;");

            migrationBuilder.RenameColumn(
                name: "material",
                table: "mcp_server_api_keys",
                newName: "key_hash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Symmetric: a digest cannot be turned back into the key it came from, so rolling back also leaves no
            // credential behind. The operator regenerates on the old build exactly as they do on the new one.
            migrationBuilder.Sql("DELETE FROM mcp_server_api_keys;");

            migrationBuilder.RenameColumn(
                name: "key_hash",
                table: "mcp_server_api_keys",
                newName: "material");
        }
    }
}
