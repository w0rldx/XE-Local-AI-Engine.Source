namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Round-trips for <see cref="AgentExecutionLogStore.AddApprovalDecisionAsync" />: a resolved tool-approval
///     decision is persisted as a metadata-only row (kind 2) that REUSES existing columns without a schema change — tool
///     name → model_name, category → config_hash, decision → terminal_status, source → provider — and stays invisible to
///     every other read view (the diagnostics view, the run-envelope ledger, and the usage summary each filter their own
///     kind). Executed against real SQLite so the column mapping and the read-view filters are proven.
/// </summary>
public sealed class AgentExecutionLogApprovalDecisionTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 18, 10, 30, 0, TimeSpan.Zero);

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task AddApprovalDecisionAsync_WritesApproveRow_ReusingColumns()
    {
        var row = await WriteAndReadSingleRowAsync("approve-row.sqlite",
            new ApprovalDecisionAuditInput(InvocationId: Guid.NewGuid(),
                ToolName: "spawn_subagent",
                Category: "Orchestration",
                Decision: ApprovalDecisions.Approve,
                Source: ApprovalDecisionSources.Local,
                LatencyMs: 4_200L));

        // Discriminator + agentless retention bucket + no schema version for this kind.
        AssertEx.Equal((int)AgentExecutionLogRecordKind.ApprovalDecision, row.RecordKind);
        AssertEx.Equal(Guid.Empty, row.AgentDefinitionId);
        AssertEx.Equal(expected: 0, row.SchemaVersion);

        // Reused columns carry the audit fields.
        AssertEx.Equal("spawn_subagent", row.ModelName);
        AssertEx.Equal("Orchestration", row.ConfigHash);
        AssertEx.Equal(ApprovalDecisions.Approve, row.TerminalStatus);
        AssertEx.Equal(ApprovalDecisionSources.Local, row.Provider);
        AssertEx.Equal(expected: 4_200L, row.LatencyMs);
        AssertEx.True(row.InvocationId is not null, "invocation id should be persisted");

        // Approve → Success true; the store stamps CreatedAtUtc from the injected TimeProvider.
        AssertEx.True(row.Success, "an approve decision persists Success=true");
        AssertEx.Equal(FixedNow.ToUnixTimeMilliseconds(), row.CreatedAtUtc);
    }

    [Test]
    public async Task AddApprovalDecisionAsync_WritesDenyRow_SuccessFalse()
    {
        var row = await WriteAndReadSingleRowAsync("deny-row.sqlite",
            new ApprovalDecisionAuditInput(InvocationId: Guid.NewGuid(),
                ToolName: "search_web",
                Category: "Network",
                Decision: ApprovalDecisions.Deny,
                Source: ApprovalDecisionSources.Hub,
                LatencyMs: 900L));

        AssertEx.Equal(ApprovalDecisions.Deny, row.TerminalStatus);
        AssertEx.Equal(ApprovalDecisionSources.Hub, row.Provider);
        AssertEx.Equal("Network", row.ConfigHash);
        AssertEx.False(row.Success, "a deny decision persists Success=false");
    }

    [Test]
    public async Task AddApprovalDecisionAsync_WritesTimeoutRow_SuccessFalse()
    {
        var row = await WriteAndReadSingleRowAsync("timeout-row.sqlite",
            new ApprovalDecisionAuditInput(InvocationId: Guid.NewGuid(),
                ToolName: "run_in_agent_home",
                Category: "WriteExecute",
                Decision: ApprovalDecisions.Timeout,
                Source: ApprovalDecisionSources.Local,
                LatencyMs: 300_000L));

        AssertEx.Equal(ApprovalDecisions.Timeout, row.TerminalStatus);
        AssertEx.False(row.Success, "a timeout decision persists Success=false");
    }

    [Test]
    public async Task AddApprovalDecisionAsync_RowIsInvisibleToOtherReadViews()
    {
        var databasePath = GetDatabasePath("approval-exclusion.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, new FixedTimeProvider(FixedNow));

        await store.AddApprovalDecisionAsync(new ApprovalDecisionAuditInput(InvocationId: Guid.NewGuid(),
            ToolName: "spawn_subagent",
            Category: "Orchestration",
            Decision: ApprovalDecisions.Approve,
            Source: ApprovalDecisionSources.Local,
            LatencyMs: 10L));

        // The diagnostics view filters kind 0 (and the row's agentless bucket), the run-envelope ledger filters kind 1,
        // and the usage summary aggregates kind 1 only — so a kind-2 approval row surfaces in none of them.
        AssertEx.Empty(await store.ListByAgentAsync(Guid.Empty, limit: 50));
        AssertEx.Empty(await store.ListRunEnvelopesAsync(conversationId: null, limit: 50));
        AssertEx.Empty(await store.SummarizeTokenUsageAsync(fromEpochMsInclusive: null, toEpochMsExclusive: null));

        // The row is still physically present (retention prunes it on the whole-table sweep like every other kind).
        var count = await context.AgentExecutionLogs.CountAsync();
        AssertEx.Equal(expected: 1, count);
    }

    private async Task<AgentExecutionLog> WriteAndReadSingleRowAsync(string fileName, ApprovalDecisionAuditInput input)
    {
        var databasePath = GetDatabasePath(fileName);
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, new FixedTimeProvider(FixedNow));

        await store.AddApprovalDecisionAsync(input);

        // Read the row back through the raw entity DbSet so the exact persisted columns can be asserted.
        return await context.AgentExecutionLogs.AsNoTracking().SingleAsync();
    }

    private static NodeChatDbContext CreateContext(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        return AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private static byte[] CreateKeyMaterial()
    {
        return Enumerable.Range(start: 0, count: 32).Select(static value => (byte)(value + 1)).ToArray();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }

    private sealed class FixedNodeSqliteKeyHolder(byte[] key) : INodeSqliteKeyHolder
    {
        private byte[]? _key = key;

        public ReadOnlyMemory<byte> Key
        {
            get
            {
                ObjectDisposedException.ThrowIf(_key is null, this);
                return _key;
            }
        }

        public void Dispose()
        {
            if (_key is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
    }
}
