namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary><c>AddNodeAuthSessions</c> adds the sign-in session table and links refresh tokens to it, keeping live tokens.</summary>
[Category(TestCategories.Integration)]
public sealed class AddNodeAuthSessionsMigrationTests
{
    [Test]
    public async Task Migrate_OverALiveRefreshToken_KeepsItLiveWithNoSession()
    {
        await using var probe = await MigrationSchemaProbe.FromIdentityTemplateAsync("auth-sessions.sqlite");
        await probe.MigrateIdentityToAsync("20260923211514_AllowConcurrentRefreshSessions");
        await probe.ExecuteAsync("""
                                 INSERT INTO AspNetUsers (Id, AccessFailedCount, EmailConfirmed, LockoutEnabled, PhoneNumberConfirmed, TwoFactorEnabled, setup_completed, created_at_utc)
                                 VALUES ('u1', 0, 0, 0, 0, 0, 1, '2026-10-25 00:00:00');
                                 INSERT INTO node_refresh_tokens (id, user_id, token_hash, expires_at_utc, created_at_utc)
                                 VALUES ('t1', 'u1', 'h1', '2099-01-01 00:00:00', '2026-10-25 00:00:00');
                                 """);

        await probe.MigrateIdentityToAsync("20261025160526_AddNodeAuthSessions");

        AssertEx.True(await probe.TableExistsAsync("node_auth_sessions"), "The session table must exist.");
        AssertEx.True(await probe.ForeignKeyExistsAsync("node_refresh_tokens", "session_id", "node_auth_sessions"),
            "A refresh token must point at its session.");
        AssertEx.True(await probe.IndexExistsAsync("node_refresh_tokens", "IX_node_refresh_tokens_session_id", unique: false, "session_id"),
            "Revoking a session finds its tokens through an index.");
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM node_refresh_tokens WHERE id = 't1' AND revoked_at_utc IS NULL AND session_id IS NULL;"),
            "A token issued before sessions existed stays live; its next refresh gives it a session.");
    }
}
