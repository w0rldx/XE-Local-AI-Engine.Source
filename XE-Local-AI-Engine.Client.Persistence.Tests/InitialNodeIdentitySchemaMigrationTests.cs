namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>InitialNodeIdentitySchema</c> creates the node's identity database — the ASP.NET Identity tables plus this
///     node's own refresh-token table. It runs against a SEPARATE migrations-history table
///     (<c>__EFMigrationsHistory_Identity</c>) from the chat context, and nothing else in this suite had ever exercised
///     that context, so a break in it would have surfaced only at first launch.
/// </summary>
public sealed class InitialNodeIdentitySchemaMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_CreatesIdentityTablesAndRefreshTokens()
    {
        await using var probe = await MigrationSchemaProbe.MigrateIdentityAsync("initial-node-identity.sqlite").ConfigureAwait(false);

        foreach (var table in new[] { "AspNetRoles", "AspNetUsers", "AspNetRoleClaims", "AspNetUserClaims", "AspNetUserLogins", "AspNetUserRoles", "AspNetUserTokens", "node_refresh_tokens" })
        {
            AssertEx.True(await probe.TableExistsAsync(table).ConfigureAwait(false), $"{table} must exist.");
        }

        // The node's own columns on the Identity user, mapped to snake_case; the rest are Identity's own.
        AssertEx.True((await probe.ColumnsAsync("AspNetUsers").ConfigureAwait(false)).IsSupersetOf(new[]
        {
            "Id",
            "setup_completed",
            "created_at_utc",
            "NormalizedUserName",
            "PasswordHash",
            "SecurityStamp"
        }), "AspNetUsers must carry the node's own columns alongside Identity's.");

        AssertEx.True((await probe.ColumnsAsync("node_refresh_tokens").ConfigureAwait(false)).IsSupersetOf(new[]
        {
            "id",
            "user_id",
            "token_hash",
            "expires_at_utc",
            "created_at_utc",
            "revoked_at_utc"
        }), "node_refresh_tokens must expose the mapped columns.");

        AssertEx.True(await probe.ForeignKeyExistsAsync("node_refresh_tokens", "user_id", "AspNetUsers").ConfigureAwait(false),
            "A refresh token must be foreign-keyed to its user.");

        // Only the HASH is indexed and unique: lookup is by digest, never by a stored plaintext token.
        AssertEx.True(await probe.IndexExistsAsync("node_refresh_tokens",
                "IX_node_refresh_tokens_token_hash",
                unique: true,
                "token_hash").ConfigureAwait(false),
            "The refresh-token digest must be uniquely indexed.");

        AssertEx.True(await probe.IndexExistsAsync("AspNetUsers", "UserNameIndex", unique: true, "NormalizedUserName").ConfigureAwait(false),
            "Identity's unique username index must survive the snake_case remapping.");
    }
}
