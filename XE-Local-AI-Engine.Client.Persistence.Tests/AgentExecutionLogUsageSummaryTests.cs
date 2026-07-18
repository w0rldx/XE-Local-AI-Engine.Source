namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Aggregation round-trips for <see cref="AgentExecutionLogStore.SummarizeTokenUsageAsync" /> (BE-01): the set-based
///     GROUP BY over the run-envelope ledger sums tokens per (model, UTC day), excludes the memory-diagnostics producer,
///     counts missing token fields as zero, and honours the half-open date range — all executed server-side against real
///     SQLite so the arithmetic day-bucket and nullable SUM translations are proven.
/// </summary>
public sealed class AgentExecutionLogUsageSummaryTests : IDisposable
{
    private const long MillisecondsPerDay = 86_400_000L;
    private const long DayOneStart = MillisecondsPerDay;      // UTC day index 1
    private const long DayTwoStart = 2 * MillisecondsPerDay;  // UTC day index 2

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task SummarizeTokenUsageAsync_GroupsByModelAndDay_SumsTokensNewestDayFirst()
    {
        var databasePath = GetDatabasePath("usage-summary-group.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, TimeProvider.System);

        // Day 1: two "llama-x" runs + one "llama-y" run (llama-y reports no reasoning/total → those sum to 0).
        await AddEnvelopeAsync(context, "llama-x", DayOneStart + 10, prompt: 100, completion: 200, reasoning: 50, total: 350);
        await AddEnvelopeAsync(context, "llama-x", DayOneStart + 20, prompt: 10, completion: 20, reasoning: 5, total: 35);
        await AddEnvelopeAsync(context, "llama-y", DayOneStart + 30, prompt: 1, completion: 2, reasoning: null, total: null);
        // Day 2: one large "llama-x" run — must sort ahead of the day-1 buckets (newest day first).
        await AddEnvelopeAsync(context, "llama-x", DayTwoStart + 5, prompt: 1000, completion: 2000, reasoning: 500, total: 3500);

        var summary = await store.SummarizeTokenUsageAsync(fromEpochMsInclusive: null, toEpochMsExclusive: null);

        AssertEx.Equal(expected: 3, summary.Count);

        // Newest day first, then model name ascending → [day2/llama-x, day1/llama-x, day1/llama-y].
        var dayTwoX = summary[0];
        AssertEx.Equal("llama-x", dayTwoX.ModelName);
        AssertEx.Equal(DayTwoStart, dayTwoX.DayStartUtcMs);
        AssertEx.Equal(expected: 1, dayTwoX.RunCount);
        AssertEx.Equal(expected: 1000L, dayTwoX.PromptTokens);
        AssertEx.Equal(expected: 2000L, dayTwoX.CompletionTokens);
        AssertEx.Equal(expected: 500L, dayTwoX.ReasoningTokens);
        AssertEx.Equal(expected: 3500L, dayTwoX.TotalTokens);

        var dayOneX = summary[1];
        AssertEx.Equal("llama-x", dayOneX.ModelName);
        AssertEx.Equal(DayOneStart, dayOneX.DayStartUtcMs);
        AssertEx.Equal(expected: 2, dayOneX.RunCount);
        AssertEx.Equal(expected: 110L, dayOneX.PromptTokens);
        AssertEx.Equal(expected: 220L, dayOneX.CompletionTokens);
        AssertEx.Equal(expected: 55L, dayOneX.ReasoningTokens);
        AssertEx.Equal(expected: 385L, dayOneX.TotalTokens);

        var dayOneY = summary[2];
        AssertEx.Equal("llama-y", dayOneY.ModelName);
        AssertEx.Equal(DayOneStart, dayOneY.DayStartUtcMs);
        AssertEx.Equal(expected: 1, dayOneY.RunCount);
        AssertEx.Equal(expected: 1L, dayOneY.PromptTokens);
        AssertEx.Equal(expected: 2L, dayOneY.CompletionTokens);
        // Missing reasoning/total fields count as 0, not null.
        AssertEx.Equal(expected: 0L, dayOneY.ReasoningTokens);
        AssertEx.Equal(expected: 0L, dayOneY.TotalTokens);
    }

    [Test]
    public async Task SummarizeTokenUsageAsync_ExcludesMemoryDiagnosticsRows()
    {
        var databasePath = GetDatabasePath("usage-summary-exclude-memory.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, TimeProvider.System);

        // One run-envelope row (kind 1) and one adaptive-memory diagnostics row (kind 0) with tokens: only the envelope
        // may be summed. A leaking memory row would inflate the prompt total to 700.
        await AddEnvelopeAsync(context, "llama-x", DayOneStart + 10, prompt: 100, completion: 200, reasoning: 50, total: 350);
        _ = await store.AddAsync(new AgentExecutionLogInput(Guid.NewGuid(),
            ConversationId: Guid.NewGuid(),
            MessageId: null,
            "llama-x",
            "h",
            LatencyMs: 3L,
            Success: true,
            PromptTokens: 600,
            CompletionTokens: 700));

        var summary = await store.SummarizeTokenUsageAsync(fromEpochMsInclusive: null, toEpochMsExclusive: null);

        AssertEx.Equal(expected: 1, summary.Count);
        AssertEx.Equal(expected: 1, summary[0].RunCount);
        AssertEx.Equal(expected: 100L, summary[0].PromptTokens);
        AssertEx.Equal(expected: 200L, summary[0].CompletionTokens);
    }

    [Test]
    public async Task SummarizeTokenUsageAsync_EmptyRange_ReturnsEmpty()
    {
        var databasePath = GetDatabasePath("usage-summary-empty.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, TimeProvider.System);

        await AddEnvelopeAsync(context, "llama-x", DayOneStart + 10, prompt: 100, completion: 200, reasoning: 50, total: 350);

        // A range entirely after the only row's timestamp selects nothing.
        var summary = await store.SummarizeTokenUsageAsync(fromEpochMsInclusive: DayTwoStart, toEpochMsExclusive: null);

        AssertEx.Empty(summary);
    }

    [Test]
    public async Task SummarizeTokenUsageAsync_HonoursHalfOpenRange()
    {
        var databasePath = GetDatabasePath("usage-summary-range.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, TimeProvider.System);

        // A row exactly on the lower bound is included; a row exactly on the upper bound is excluded.
        await AddEnvelopeAsync(context, "llama-x", DayOneStart, prompt: 1, completion: 1, reasoning: 0, total: 2);          // included (>= from)
        await AddEnvelopeAsync(context, "llama-x", DayOneStart + 100, prompt: 10, completion: 10, reasoning: 0, total: 20); // included
        await AddEnvelopeAsync(context, "llama-x", DayTwoStart, prompt: 999, completion: 999, reasoning: 0, total: 1998);   // excluded (== to)

        var summary = await store.SummarizeTokenUsageAsync(fromEpochMsInclusive: DayOneStart, toEpochMsExclusive: DayTwoStart);

        AssertEx.Equal(expected: 1, summary.Count);
        AssertEx.Equal(DayOneStart, summary[0].DayStartUtcMs);
        AssertEx.Equal(expected: 2, summary[0].RunCount);
        AssertEx.Equal(expected: 11L, summary[0].PromptTokens);
        AssertEx.Equal(expected: 22L, summary[0].TotalTokens);
    }

    // Inserts a run-envelope row (kind 1) directly through the internal DbSet with an explicit token set, mirroring what
    // the terminalize persistence command writes. Keeps token fields controllable so the aggregation math is provable.
    private static async Task AddEnvelopeAsync(NodeChatDbContext context,
        string modelName,
        long createdAtUtc,
        int? prompt,
        int? completion,
        int? reasoning,
        int? total)
    {
        _ = context.AgentExecutionLogs.Add(new AgentExecutionLog
        {
            Id = Guid.NewGuid(),
            RecordKind = (int)AgentExecutionLogRecordKind.ChatRunEnvelope,
            SchemaVersion = AgentRunEnvelope.CurrentSchemaVersion,
            AgentDefinitionId = Guid.Empty,
            ConversationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            ModelName = modelName,
            ConfigHash = string.Empty,
            TerminalStatus = "completed",
            Success = true,
            PromptTokens = prompt,
            CompletionTokens = completion,
            ReasoningTokens = reasoning,
            TotalTokens = total,
            CreatedAtUtc = createdAtUtc
        });

        _ = await context.SaveChangesAsync();
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
