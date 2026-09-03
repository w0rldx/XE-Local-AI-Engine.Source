namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     The read side of the two tool-schema token columns: a run-envelope row carrying them projects them through
///     <c>ListRunEnvelopesAsync</c>, a row without them projects nulls, and the cumulative one survives a value above
///     <see cref="int.MaxValue" /> — the single assertion that fails if any link from column to record narrows back to
///     an <c>int</c>. The memory-diagnostics projection (kind 0) is deliberately NOT widened, and that is pinned too:
///     these columns are always null on that kind, and adding permanently-null fields to its projection would
///     misinform every reader of that endpoint.
/// </summary>
public sealed class AgentExecutionLogToolSchemaTokenTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 3, 10, 30, 0, TimeSpan.Zero);

    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }

        _keyHolder.Dispose();
    }

    [Test]
    public async Task ListRunEnvelopesAsync_ProjectsToolSchemaTokens()
    {
        await using var context = await CreateDatabaseAsync("envelope-tokens-project.sqlite").ConfigureAwait(false);
        _ = context.AgentExecutionLogs.Add(EnvelopeRow(toolSchemaTokens: 12_345L, maxToolSchemaTokens: 4_096));
        _ = await context.SaveChangesAsync().ConfigureAwait(false);
        var store = new AgentExecutionLogStore(context, new FixedTimeProvider(FixedNow));

        var envelope = (await store.ListRunEnvelopesAsync(conversationId: null, limit: 10).ConfigureAwait(false)).Single();

        AssertEx.Equal(expected: 12_345L, envelope.ToolSchemaTokens);
        AssertEx.Equal(expected: 4_096, envelope.MaxToolSchemaTokens);
    }

    [Test]
    public async Task ListRunEnvelopesAsync_WhenNotReported_ProjectsNulls()
    {
        await using var context = await CreateDatabaseAsync("envelope-tokens-null.sqlite").ConfigureAwait(false);
        _ = context.AgentExecutionLogs.Add(EnvelopeRow(toolSchemaTokens: null, maxToolSchemaTokens: null));
        _ = await context.SaveChangesAsync().ConfigureAwait(false);
        var store = new AgentExecutionLogStore(context, new FixedTimeProvider(FixedNow));

        var envelope = (await store.ListRunEnvelopesAsync(conversationId: null, limit: 10).ConfigureAwait(false)).Single();

        AssertEx.Null(envelope.ToolSchemaTokens);
        AssertEx.Null(envelope.MaxToolSchemaTokens);
    }

    [Test]
    public async Task ToolSchemaTokens_RoundTripsAValueAboveIntMaxValue()
    {
        // The producing counter is a long and a whole session's worth of these is summed downstream, so a narrowing
        // anywhere along column -> entity -> record would truncate silently. This value is exactly one past int.MaxValue.
        const long wideEstimate = (long)int.MaxValue + 1;
        await using var context = await CreateDatabaseAsync("envelope-tokens-wide.sqlite").ConfigureAwait(false);
        _ = context.AgentExecutionLogs.Add(EnvelopeRow(wideEstimate, maxToolSchemaTokens: 8));
        _ = await context.SaveChangesAsync().ConfigureAwait(false);
        var store = new AgentExecutionLogStore(context, new FixedTimeProvider(FixedNow));

        var envelope = (await store.ListRunEnvelopesAsync(conversationId: null, limit: 10).ConfigureAwait(false)).Single();

        AssertEx.Equal(wideEstimate, envelope.ToolSchemaTokens);
    }

    [Test]
    public async Task ListByAgentAsync_ForMemoryRows_IsUnchanged()
    {
        // The kind-0 diagnostics projection was deliberately left alone: these columns never carry a value on a memory
        // row, so widening that record would add two permanently-null fields to a different read view.
        await using var context = await CreateDatabaseAsync("memory-projection-unchanged.sqlite").ConfigureAwait(false);
        var store = new AgentExecutionLogStore(context, new FixedTimeProvider(FixedNow));
        var agentId = Guid.NewGuid();

        _ = await store.AddAsync(new AgentExecutionLogInput(agentId,
                             ConversationId: null,
                             MessageId: null,
                             ModelName: "llama-3.1",
                             ConfigHash: "hash",
                             LatencyMs: 42L,
                             PromptTokens: 10,
                             CompletionTokens: 20,
                             Success: true,
                             ErrorClass: null))
                         .ConfigureAwait(false);

        var rows = await store.ListByAgentAsync(agentId, limit: 10).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, rows.Count);
        AssertEx.Equal(expected: 10, rows[0].PromptTokens);
    }

    private static AgentExecutionLog EnvelopeRow(long? toolSchemaTokens, int? maxToolSchemaTokens)
    {
        return new AgentExecutionLog
        {
            Id = Guid.NewGuid(),
            RecordKind = (int)AgentExecutionLogRecordKind.ChatRunEnvelope,
            SchemaVersion = AgentRunEnvelope.CurrentSchemaVersion,
            AgentDefinitionId = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            InvocationId = Guid.NewGuid(),
            ModelName = "llama-3.1",
            ConfigHash = string.Empty,
            TerminalStatus = "completed",
            LatencyMs = 1_500L,
            Success = true,
            CreatedAtUtc = FixedNow.ToUnixTimeMilliseconds(),
            ToolSchemaTokens = toolSchemaTokens,
            MaxToolSchemaTokens = maxToolSchemaTokens
        };
    }

    private async Task<NodeChatDbContext> CreateDatabaseAsync(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        var context = AgentDefinitionTestContextFactory.Create(Path.Combine(_rootPath, fileName), _keyHolder);
        await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        return context;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }
}
