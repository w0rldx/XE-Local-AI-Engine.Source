namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddAgentExecutionLogProvider</c> records which provider served each turn. Older rows backfill to
///     <c>unknown</c>, not today's default provider, which would credit it with historical latency and tokens and
///     skew per-provider comparisons.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddAgentExecutionLogProviderMigrationTests
{
    private const string PreProviderMigrationId = "20260718023348_DropApprovedUtilityImages";
    private const string ThisMigrationId = "20260718143054_AddAgentExecutionLogProvider";

    [Test]
    public async Task Migrate_OverHistoricalLogs_AttributesThemToAnUnknownProvider()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("execution-log-provider.sqlite", PreProviderMigrationId);

        var logId = Guid.NewGuid().ToString();
        await probe.ExecuteAsync("""
                                 INSERT INTO agent_execution_logs
                                     (id, agent_definition_id, model_name, config_hash, latency_ms, success, created_at_utc)
                                 VALUES ($id, $agent_definition_id, 'qwen3.5:0.8b', 'hash', 42, 1, 1234);
                                 """,
            command =>
            {
                command.Parameters.AddWithValue("$id", logId);
                command.Parameters.AddWithValue("$agent_definition_id", Guid.NewGuid().ToString());
            });

        await probe.MigrateToAsync(ThisMigrationId);

        var provider = await probe.ScalarAsync("SELECT provider FROM agent_execution_logs WHERE id = $id;",
            command => command.Parameters.AddWithValue("$id", logId));

        AssertEx.Equal("unknown", AssertEx.NotNull(provider as string, "The provider column must be non-null for historical rows."),
            "A pre-migration turn must be attributed to 'unknown', never to the provider configured at migration time.");

        AssertEx.Equal("'unknown'", await probe.ColumnDefaultAsync("agent_execution_logs", "provider"));
    }
}
