namespace XE_Local_AI_Engine.Tests.Chat;

using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     GPTAUD-07: a persistence-pump fault (a FlushDeltaAsync / TerminalizeAsync exception) must not leave the assistant
///     row streaming forever. <see cref="ChatInvocationStatePump.PumpAsync" /> now catches a non-cancellation fault,
///     idempotently terminalizes the row Failed, emits the Failed terminal SSE, and rethrows so the caller cancels the
///     run. The Failed terminalize rides the same NodeChatMessageTransitions atomic guard, so it can never overwrite a
///     terminal that already committed.
/// </summary>
public sealed class ChatInvocationStatePumpFaultTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task PumpAsync_WhenFlushFaultsMidStream_TerminalizesRowFailedAndRethrows()
    {
        await using var provider = await BuildProviderAsync("pump-fault-flush.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        var conversationId = Guid.NewGuid();
        var (correlation, assistantMessageId) = await SeedStreamingRowAsync(persistence, conversationId).ConfigureAwait(false);

        // The pump's first partial flush throws — the terminalize path stays real so the fault handler can persist Failed.
        var faultingPump = new FlushFailingPump(ChatPumpTestFactory.Create(persistence));
        var pump = new ChatInvocationStatePump(faultingPump, TimeProvider.System);

        var stateChannel = Channel.CreateUnbounded<InvocationState>();
        var eventChannel = Channel.CreateUnbounded<ChatStreamEvent>();
        stateChannel.Writer.TryWrite(new InvocationState
        {
            InvocationId = correlation.RequestId,
            ConversationId = conversationId,
            Status = InvocationStatus.Running,
            StreamedContent = "partial answer",
            StartedAt = DateTimeOffset.UtcNow,
            LastUpdatedAt = DateTimeOffset.UtcNow
        });

        var events = new List<ChatStreamEvent>();
        var collector = Task.Run(async () =>
        {
            await foreach (var streamEvent in eventChannel.Reader.ReadAllAsync())
            {
                events.Add(streamEvent);
            }
        });

        // The original flush fault propagates so the caller learns of it and cancels the run.
        await AssertEx.ThrowsAsync<InvalidOperationException>(async () => await pump.PumpAsync(stateChannel.Reader,
                eventChannel.Writer,
                correlation,
                "model-x",
                new NodeChatStreamSequence(),
                new NodeChatPartAccumulator(),
                onTerminal: null,
                CancellationToken.None))
            .ConfigureAwait(false);

        await collector.ConfigureAwait(false);

        // The row is terminalized Failed rather than left streaming until restart recovery.
        var conversation = AssertEx.NotNull(await persistence.GetConversationAsync(conversationId).ConfigureAwait(false));
        var row = conversation.Messages.Single(message => message.MessageId == assistantMessageId);
        AssertEx.Equal(NodeChatMessageStatusValues.Failed, row.Status);

        // The client is told the turn failed rather than seeing a silently dropped stream.
        AssertEx.Contains(events, streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantFailed);
    }

    [Test]
    public async Task PumpAsync_WhenFaultRacesACommittedCompletedTerminal_LeavesCompletedIntact()
    {
        await using var provider = await BuildProviderAsync("pump-fault-idempotent.sqlite").ConfigureAwait(false);
        var persistence = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());

        var conversationId = Guid.NewGuid();
        var (correlation, assistantMessageId) = await SeedStreamingRowAsync(persistence, conversationId).ConfigureAwait(false);

        // The row already reached a genuine Completed terminal before the fault handler runs.
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                NodeChatMessageStatusValues.Completed,
                NowMs(),
                "the real answer",
                Model: "model-x"))
            .ConfigureAwait(false);

        var faultingPump = new FlushFailingPump(ChatPumpTestFactory.Create(persistence));
        var pump = new ChatInvocationStatePump(faultingPump, TimeProvider.System);

        var stateChannel = Channel.CreateUnbounded<InvocationState>();
        var eventChannel = Channel.CreateUnbounded<ChatStreamEvent>();
        stateChannel.Writer.TryWrite(new InvocationState
        {
            InvocationId = correlation.RequestId,
            ConversationId = conversationId,
            Status = InvocationStatus.Running,
            StreamedContent = "partial answer",
            StartedAt = DateTimeOffset.UtcNow,
            LastUpdatedAt = DateTimeOffset.UtcNow
        });

        var events = new List<ChatStreamEvent>();
        var collector = Task.Run(async () =>
        {
            await foreach (var streamEvent in eventChannel.Reader.ReadAllAsync())
            {
                events.Add(streamEvent);
            }
        });

        await AssertEx.ThrowsAsync<InvalidOperationException>(async () => await pump.PumpAsync(stateChannel.Reader,
                eventChannel.Writer,
                correlation,
                "model-x",
                new NodeChatStreamSequence(),
                new NodeChatPartAccumulator(),
                onTerminal: null,
                CancellationToken.None))
            .ConfigureAwait(false);

        await collector.ConfigureAwait(false);

        // The fault-terminalize's Failed write is a no-op over the committed Completed row (the transition guard rejects
        // it), so the authoritative Completed content survives.
        var conversation = AssertEx.NotNull(await persistence.GetConversationAsync(conversationId).ConfigureAwait(false));
        var row = conversation.Messages.Single(message => message.MessageId == assistantMessageId);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, row.Status);
        AssertEx.Equal("the real answer", row.Content);

        // The emitted terminal reflects the winning (Completed) row, never a spurious Failed.
        AssertEx.True(events.All(streamEvent => streamEvent.Type != ChatStreamEventTypes.AssistantFailed), "No Failed terminal may be emitted over a committed Completed row.");
    }

    private static async Task<(NodeChatMessageCorrelation Correlation, Guid AssistantMessageId)> SeedStreamingRowAsync(NodeChatPersistenceService persistence, Guid conversationId)
    {
        await persistence.EnsureConversationAsync(new NodeChatEnsureConversationRequest(conversationId, "Pump fault", "node", CreatedAtUtc: 10, NodeChatOriginValues.Local)).ConfigureAwait(false);
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversationId, Guid.NewGuid(), "hello", CreatedAtUtc: 11)).ConfigureAwait(false);

        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversationId, assistantMessageId, requestId);
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversationId, assistantMessageId, requestId, CreatedAtUtc: 12, "model-x")).ConfigureAwait(false);
        await persistence.MarkAssistantQueuedAsync(correlation, NowMs()).ConfigureAwait(false);
        await persistence.MarkAssistantStreamingAsync(correlation, NowMs()).ConfigureAwait(false);
        return (correlation, assistantMessageId);
    }

    private static long NowMs()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private async Task<ServiceProvider> BuildProviderAsync(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, fileName);
        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<NodeChatPersistenceWriter>();

        var provider = services.BuildServiceProvider(true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

        return provider;
    }

    // A pump whose partial flush always throws; the terminalize legs delegate to a real pump so the fault handler's
    // idempotent Failed terminalize actually exercises persistence.
    private sealed class FlushFailingPump(INodeChatInvocationPump inner) : INodeChatInvocationPump
    {
        public Task<NodeChatPumpFlushResult> FlushDeltaAsync(NodeChatMessageCorrelation correlation,
            InvocationState state,
            NodeChatPumpCursor cursor,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Injected flush fault.");
        }

        public Task<NodeChatPumpTerminalResult> TerminalizeAsync(NodeChatMessageCorrelation correlation,
            InvocationState state,
            string? requestedModel,
            IReadOnlyList<NodeChatMessagePart>? parts = null,
            IReadOnlyList<NodeChatMessageSource>? sources = null)
        {
            return inner.TerminalizeAsync(correlation, state, requestedModel, parts, sources);
        }

        public Task<NodeChatPumpTerminalResult> TerminalizeInterruptedAsync(NodeChatMessageCorrelation correlation,
            NodeChatPumpCursor cursor,
            bool wasCancelled)
        {
            return inner.TerminalizeInterruptedAsync(correlation, cursor, wasCancelled);
        }
    }
}
