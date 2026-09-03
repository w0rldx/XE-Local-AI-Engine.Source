namespace XE_Local_AI_Engine.Client.Persistence.Tests.Integrations;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Round-trips for <see cref="AgentExecutionLogStore.AddIntegrationInvocationAsync" />: an integration execution's
///     terminal audit is a metadata-only kind-3 row that REUSES existing columns without a schema change — trigger name
///     → model_name, key prefix → provider, target agent id → config_hash — and stays invisible to every other read
///     view. Executed against real SQLite so the column mapping and the read-view filters are proven.
/// </summary>
public sealed class IntegrationAuditRecordKindTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly NullNodeSqliteKeyHolder _keyHolder = new();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _keyHolder.Dispose();
        SqliteFileProbe.ReleasePooledHandles();
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task AddIntegrationInvocationAsync_WritesKindThreeReusingColumns()
    {
        var invocationId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var agentDefinitionId = Guid.NewGuid();

        await using var context = await CreateSchemaAsync("integration-audit-row.sqlite").ConfigureAwait(false);
        var store = new AgentExecutionLogStore(context, new FixedTimeProvider(FixedNow));

        await store.AddIntegrationInvocationAsync(new IntegrationInvocationAuditInput(invocationId,
                       requestId,
                       "sensor-ingest",
                       "xeint_a1b2c3d4",
                       agentDefinitionId,
                       "completed",
                       "trace-7",
                       LatencyMs: 4_200L))
                   .ConfigureAwait(false);

        var row = await context.AgentExecutionLogs.AsNoTracking().SingleAsync().ConfigureAwait(false);

        AssertEx.Equal((int)AgentExecutionLogRecordKind.IntegrationInvocation, row.RecordKind);
        AssertEx.Equal(Guid.Empty, row.AgentDefinitionId, "Kind-3 rows share one retention bucket and appear in no per-agent view.");
        AssertEx.Equal(expected: 0, row.SchemaVersion);

        AssertEx.Equal("sensor-ingest", row.ModelName);
        AssertEx.Equal("xeint_a1b2c3d4", row.Provider);
        AssertEx.Equal(agentDefinitionId.ToString("N"), row.ConfigHash);
        AssertEx.Equal("completed", row.TerminalStatus);
        AssertEx.Equal("trace-7", row.TraceId);
        AssertEx.Equal(invocationId, row.InvocationId);
        AssertEx.Equal(requestId, row.RequestId);
        AssertEx.Equal(expected: 4_200L, row.LatencyMs);
        AssertEx.True(row.Success, "A completed terminal status persists Success=true.");
        AssertEx.Equal(FixedNow.ToUnixTimeMilliseconds(), row.CreatedAtUtc);

        AssertEx.Null(row.ConversationId,
            "ConversationId stays null so a conversation purge never reaches an audit row; these age out with the execution-log retention sweep.");
    }

    [Test]
    [Arguments("failed")]
    [Arguments("cancelled")]
    public async Task AddIntegrationInvocationAsync_NonCompletedTerminalStatusPersistsSuccessFalse(string terminalStatus)
    {
        await using var context = await CreateSchemaAsync($"integration-audit-{terminalStatus}.sqlite").ConfigureAwait(false);
        var store = new AgentExecutionLogStore(context, new FixedTimeProvider(FixedNow));

        await store.AddIntegrationInvocationAsync(new IntegrationInvocationAuditInput(Guid.NewGuid(),
                       Guid.NewGuid(),
                       "sensor-ingest",
                       "xeint_a1b2c3d4",
                       Guid.NewGuid(),
                       terminalStatus,
                       TraceId: null,
                       LatencyMs: 10L))
                   .ConfigureAwait(false);

        var row = await context.AgentExecutionLogs.AsNoTracking().SingleAsync().ConfigureAwait(false);
        AssertEx.Equal(terminalStatus, row.TerminalStatus);
        AssertEx.False(row.Success);
    }

    [Test]
    public async Task AddIntegrationInvocationAsync_RowIsInvisibleToEveryOtherReadView()
    {
        await using var context = await CreateSchemaAsync("integration-audit-exclusion.sqlite").ConfigureAwait(false);
        var store = new AgentExecutionLogStore(context, new FixedTimeProvider(FixedNow));

        await store.AddIntegrationInvocationAsync(new IntegrationInvocationAuditInput(Guid.NewGuid(),
                       Guid.NewGuid(),
                       "sensor-ingest",
                       "xeint_a1b2c3d4",
                       Guid.NewGuid(),
                       "completed",
                       TraceId: null,
                       LatencyMs: 10L))
                   .ConfigureAwait(false);

        // The diagnostics view filters kind 0, the run-envelope ledger kind 1, and the usage summary aggregates kind 1
        // only — so a kind-3 row surfaces in none of them, and every reader that forgets to filter is the bug this
        // catches.
        AssertEx.Empty(await store.ListByAgentAsync(Guid.Empty, limit: 50).ConfigureAwait(false));
        AssertEx.Empty(await store.ListRunEnvelopesAsync(conversationId: null, limit: 50).ConfigureAwait(false));
        AssertEx.Empty(await store.SummarizeTokenUsageAsync(fromEpochMsInclusive: null, toEpochMsExclusive: null).ConfigureAwait(false));

        // Still physically present: retention prunes it on the whole-table sweep like every other kind.
        AssertEx.Equal(expected: 1, await context.AgentExecutionLogs.CountAsync().ConfigureAwait(false));
    }

    private async Task<NodeChatDbContext> CreateSchemaAsync(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        var context = AgentDefinitionTestContextFactory.Create(Path.Combine(_rootPath, fileName), _keyHolder);
        _ = await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        return context;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
