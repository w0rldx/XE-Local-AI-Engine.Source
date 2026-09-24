namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AllowConcurrentRefreshSessions</c> lets one user hold a live refresh token per signed-in client and records which
///     token rotation issued in a revoked one's place — the link the reuse grace follows instead of matching timestamps.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AllowConcurrentRefreshSessionsMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_AddsTheSuccessorLinkAndDropsTheOneLiveTokenPerUserRule()
    {
        await using var probe = await MigrationSchemaProbe.FromIdentityTemplateAsync("concurrent-refresh-sessions.sqlite");

        AssertEx.True((await probe.ColumnsAsync("node_refresh_tokens")).Contains("replaced_by_token_id"),
            "node_refresh_tokens must carry the rotation successor link.");

        // Still indexed for revoke-all by user, but no longer unique: a second sign-in must not collide with the first.
        AssertEx.True(await probe.IndexExistsAsync("node_refresh_tokens", "IX_node_refresh_tokens_user_id", unique: false, "user_id"),
            "The user_id index must survive as a plain, non-unique index.");
    }

    [Test]
    public async Task WhenRolledBack_WithTwoLiveSessions_RevokesThemAndRestoresTheUniqueIndex()
    {
        await using var probe = await MigrationSchemaProbe.FromIdentityTemplateAsync("concurrent-refresh-sessions-down.sqlite");
        await probe.ExecuteAsync(
            """
            INSERT INTO AspNetUsers (Id, AccessFailedCount, EmailConfirmed, LockoutEnabled, PhoneNumberConfirmed, TwoFactorEnabled, setup_completed, created_at_utc)
            VALUES ('u1', 0, 0, 0, 0, 0, 1, '2026-09-23 00:00:00');
            INSERT INTO node_refresh_tokens (id, user_id, token_hash, expires_at_utc, created_at_utc)
            VALUES ('t1', 'u1', 'h1', '2099-01-01 00:00:00', '2026-09-23 00:00:00'),
                   ('t2', 'u1', 'h2', '2099-01-01 00:00:00', '2026-09-23 00:00:00');
            """);

        await probe.MigrateIdentityToAsync("20260624184036_AddTutorialState");

        AssertEx.Equal(0L, await probe.ScalarAsync("SELECT COUNT(*) FROM node_refresh_tokens WHERE revoked_at_utc IS NULL;"),
            "A rollback must sign every session out before the one-live-token index returns.");
        AssertEx.True(await probe.IndexExistsAsync("node_refresh_tokens", "IX_node_refresh_tokens_user_id", unique: true, "user_id"),
            "The rollback restores the unique user_id index.");
    }
}
