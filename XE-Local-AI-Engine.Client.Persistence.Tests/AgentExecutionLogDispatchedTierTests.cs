namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     The read side of the two adaptive-effort dispatch columns: a run-envelope row carrying them projects them
///     through <c>ListRunEnvelopesAsync</c>, and a turn that authored a concrete effort projects nulls — which is what
///     makes <c>authored_effort IS NULL</c> the before-population of the measurement. The memory-diagnostics
///     projection (kind 0) is deliberately NOT widened, and that is pinned here too: these columns are always null on
///     that kind, so adding permanently-null fields to its projection would misinform every reader of that endpoint.
/// </summary>
public sealed class AgentExecutionLogDispatchedTierTests : IDisposable
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
    public async Task Envelope_WhenDispatched_StoresTierAndAuthoredEffort()
    {
        await using var context = await CreateDatabaseAsync("envelope-dispatch-stores.sqlite").ConfigureAwait(false);
        _ = context.AgentExecutionLogs.Add(EnvelopeRow(dispatchedTier: "fast", authoredEffort: "auto"));
        _ = await context.SaveChangesAsync().ConfigureAwait(false);
        var store = new AgentExecutionLogStore(context, new FixedTimeProvider(FixedNow));

        var envelope = (await store.ListRunEnvelopesAsync(conversationId: null, limit: 10).ConfigureAwait(false)).Single();

        AssertEx.Equal("fast", envelope.DispatchedTier);
        AssertEx.Equal("auto", envelope.AuthoredEffort);
        AssertEx.Equal(AgentRunEnvelope.CurrentSchemaVersion, envelope.SchemaVersion);
    }

    [Test]
    public async Task Envelope_WhenNotAuto_StoresNulls()
    {
        await using var context = await CreateDatabaseAsync("envelope-dispatch-nulls.sqlite").ConfigureAwait(false);
        _ = context.AgentExecutionLogs.Add(EnvelopeRow(dispatchedTier: null, authoredEffort: null));
        _ = await context.SaveChangesAsync().ConfigureAwait(false);
        var store = new AgentExecutionLogStore(context, new FixedTimeProvider(FixedNow));

        var envelope = (await store.ListRunEnvelopesAsync(conversationId: null, limit: 10).ConfigureAwait(false)).Single();

        AssertEx.Null(envelope.DispatchedTier);
        AssertEx.Null(envelope.AuthoredEffort);
    }

    [Test]
    [Arguments("fast")]
    [Arguments("normal")]
    [Arguments("deep")]
    public async Task ListRunEnvelopesAsync_ProjectsDispatchedTierAndAuthoredEffort(string tier)
    {
        // Every label of the closed vocabulary survives the round trip: the measurement groups by this column, so a
        // label that did not reach it would silently collapse a whole tier's rows into "not measured".
        await using var context = await CreateDatabaseAsync($"envelope-dispatch-{tier}.sqlite").ConfigureAwait(false);
        _ = context.AgentExecutionLogs.Add(EnvelopeRow(tier, authoredEffort: "auto"));
        _ = await context.SaveChangesAsync().ConfigureAwait(false);
        var store = new AgentExecutionLogStore(context, new FixedTimeProvider(FixedNow));

        var envelope = (await store.ListRunEnvelopesAsync(conversationId: null, limit: 10).ConfigureAwait(false)).Single();

        AssertEx.Equal(tier, envelope.DispatchedTier);
    }

    [Test]
    public async Task ListByAgentAsync_ForMemoryRows_IsUnchanged()
    {
        // The kind-0 diagnostics projection was deliberately left alone: these columns never carry a value on a memory
        // row, so widening that record would add two permanently-null fields to a different read view.
        await using var context = await CreateDatabaseAsync("dispatch-memory-projection.sqlite").ConfigureAwait(false);
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

    private static AgentExecutionLog EnvelopeRow(string? dispatchedTier, string? authoredEffort)
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
            DispatchedTier = dispatchedTier,
            AuthoredEffort = authoredEffort
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
