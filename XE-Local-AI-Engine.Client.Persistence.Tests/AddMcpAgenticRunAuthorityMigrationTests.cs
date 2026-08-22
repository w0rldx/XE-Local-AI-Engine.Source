namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddMcpAgenticRunAuthorityMigrationTests
{
    private const string PreviousMigrationId = "20260822013858_AddMcpServerApiKeyScope";
    private const string ThisMigrationId = "20260822041045_AddMcpAgenticRunAuthority";

    [Test]
    public async Task Migrate_AddsDelegateDefaultAndRejectsInconsistentAuthority()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("mcp-agentic-run-authority.sqlite", PreviousMigrationId)
                                                          .ConfigureAwait(false);
        await probe.ExecuteAsync("""
                                 INSERT INTO mcp_agent_runs (
                                     request_id, request_fingerprint, accounting_version, status, version, stop_reason,
                                     reserved_active_payload_bytes, active_payload_bytes, tombstone_logical_bytes, created_at_utc)
                                 VALUES ('24fd2ec2-eab3-4dd4-b0d5-052414a61751', zeroblob(32), 1, 0, 0, 0, 0, 0, 0, 1234);
                                 """).ConfigureAwait(false);

        await probe.MigrateToAsync(ThisMigrationId).ConfigureAwait(false);

        AssertEx.Equal("0", await probe.ColumnDefaultAsync("mcp_agent_runs", "is_agentic_auto_approve").ConfigureAwait(false));
        AssertEx.Equal(expected: 1L,
            (await probe.LongsAsync("SELECT \"notnull\" FROM pragma_table_info('mcp_agent_runs') WHERE name = 'is_agentic_auto_approve';")
                        .ConfigureAwait(false)).Single());
        var tableSql = AssertEx.NotNull(Convert.ToString(await probe.ScalarAsync("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'mcp_agent_runs';").ConfigureAwait(false)));
        AssertEx.True(tableSql.Contains("CK_mcp_agent_runs_agentic_authority", StringComparison.Ordinal));
        AssertEx.Equal(expected: 0L,
            (await probe.LongsAsync("SELECT is_agentic_auto_approve FROM mcp_agent_runs WHERE request_id = '24fd2ec2-eab3-4dd4-b0d5-052414a61751';")
                        .ConfigureAwait(false)).Single(),
            "Durable runs accepted before scoped authority existed must backfill as delegate.");
        AssertEx.Equal(expected: 0L,
            (await probe.LongsAsync("SELECT status FROM mcp_agent_runs WHERE request_id = '24fd2ec2-eab3-4dd4-b0d5-052414a61751';")
                        .ConfigureAwait(false)).Single(),
            "A migrated queued delegate must remain queued for dispatcher restart execution.");
        AssertEx.Null(await probe.ScalarAsync("SELECT requesting_key_prefix FROM mcp_agent_runs WHERE request_id = '24fd2ec2-eab3-4dd4-b0d5-052414a61751';").ConfigureAwait(false));

        _ = await AssertEx.ThrowsAsync<SqliteException>(() => probe.ExecuteAsync("""
                                                                                 INSERT INTO mcp_agent_runs (
                                                                                     request_id, request_fingerprint, accounting_version, status, version, stop_reason,
                                                                                     is_agentic_auto_approve, requesting_key_prefix,
                                                                                     reserved_active_payload_bytes, active_payload_bytes, tombstone_logical_bytes, created_at_utc)
                                                                                 VALUES ('6b1f0f2a-6f2f-4c1f-9d3e-7a4c0b5e8d21', zeroblob(32), 1, 0, 0, 0, 1, NULL, 0, 0, 0, 1234);
                                                                                 """)).ConfigureAwait(false);
    }

    [Test]
    public async Task Migrate_RejectsAgenticPrefixesOutsideBoundedAsciiAlphabet()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("mcp-agentic-prefix-alphabet.sqlite", ThisMigrationId)
                                                          .ConfigureAwait(false);

        foreach (var (requestId, prefix) in new[]
                 {
                     ("b145aec2-f3e8-4634-b2b2-0c0b4bf5f188", "xemcp bad"),
                     ("d60fffb9-bcb7-44bd-8d25-f576d2438ff3", "xemcp.bad")
                 })
        {
            _ = await AssertEx.ThrowsAsync<SqliteException>(() => probe.ExecuteAsync("""
                                                                                     INSERT INTO mcp_agent_runs (
                                                                                         request_id, request_fingerprint, accounting_version, status, version, stop_reason,
                                                                                         is_agentic_auto_approve, requesting_key_prefix,
                                                                                         reserved_active_payload_bytes, active_payload_bytes, tombstone_logical_bytes, created_at_utc)
                                                                                     VALUES ($requestId, zeroblob(32), 1, 0, 0, 0, 1, $prefix, 0, 0, 0, 1234);
                                                                                     """, command =>
            {
                command.Parameters.AddWithValue("$requestId", requestId);
                command.Parameters.AddWithValue("$prefix", prefix);
            })).ConfigureAwait(false);
        }
    }
}
