namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using static XE_Local_AI_Engine.Tests.Chat.NodeChatStreamServiceTestSupport;

[Category(TestCategories.Unit)]
public sealed class NodeChatStreamServiceKnowledgeTests
{
    [Test]
    public async Task SendMessageAsync_WhenPlainChatWithKnowledgeBase_InjectsFencedContextAndRecordsSources()
    {
        // An opt-in plain-chat send retrieves KB hits, inlines them as ONE fenced untrusted context
        // block, and records their provenance as the turn's sources on the terminal row.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        NodeChatTerminalizeMessageRequest? terminalRequest = null;
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, request => terminalRequest = request);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ContextCapturingInvocationRunner(dispatcher);
        var scopeFactory = CreateKnowledgeScopeFactory(KnowledgeHit("Runbook", "Restart the service with the eject command.", score: 0.91));

        var service = CreateServiceWithScopeFactory(persistence, runner, dispatcher, scopeFactory);

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "how do I restart the service?",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           UseKnowledgeBase: true)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.Contains(runner.CapturedContext, message => message.Content.Contains(KnowledgeChatContextComposer.Preamble, StringComparison.Ordinal));
        AssertEx.Contains(runner.CapturedContext, message => message.Content.Contains("Restart the service with the eject command.", StringComparison.Ordinal));
        var terminal = AssertEx.NotNull(terminalRequest);
        var sources = AssertEx.NotNull(terminal.Sources);
        AssertEx.Equal(1, sources.Count);
        AssertEx.Equal("Runbook", sources[0].Title);
    }

    [Test]
    public async Task SendMessageAsync_WhenTheRunsSearchToolReturnsHits_MergesThemAfterThePreTurnSources()
    {
        // The agent tool path persists its sources too: hits recorded inside the run reach the terminal row after the
        // pre-turn grounding, with a chunk both paths returned listed once.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        NodeChatTerminalizeMessageRequest? terminalRequest = null;
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, request => terminalRequest = request);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var preTurnHit = KnowledgeHit("Runbook", "Restart the service with the eject command.", score: 0.91);
        var toolHit = KnowledgeHit("Manual", "The eject command lives in the toolbar.", score: 0.5);
        var runner = new ContextCapturingInvocationRunner(dispatcher)
        {
            ToolHits = [toolHit, preTurnHit]
        };
        var service = CreateServiceWithScopeFactory(persistence, runner, dispatcher, CreateKnowledgeScopeFactory(preTurnHit));

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "how do I restart the service?",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           UseKnowledgeBase: true)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        var sources = AssertEx.NotNull(AssertEx.NotNull(terminalRequest).Sources);
        AssertEx.Equal($"{preTurnHit.ChunkId},{toolHit.ChunkId}", string.Join(',', sources.Select(static source => source.ChunkId)));
        AssertEx.Empty(TurnKnowledgeSourceCollector.Snapshot(), "the turn's scope must not leak into the caller's flow");
    }

    [Test]
    public async Task SendMessageAsync_WhenKnowledgeBaseRetrievalEmpty_ProceedsWithoutContextOrSources()
    {
        // Degrade gracefully: no matching chunks means no KB context and no sources, but the turn still runs.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        NodeChatTerminalizeMessageRequest? terminalRequest = null;
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, request => terminalRequest = request);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ContextCapturingInvocationRunner(dispatcher);
        var scopeFactory = CreateKnowledgeScopeFactory();

        var service = CreateServiceWithScopeFactory(persistence, runner, dispatcher, scopeFactory);

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "an unrelated question",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           UseKnowledgeBase: true)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events even with no KB matches.");
        AssertEx.False(runner.CapturedContext.Any(message => message.Content.Contains(KnowledgeChatContextComposer.Preamble, StringComparison.Ordinal)),
            "no KB context block must be composed when retrieval returns nothing.");
        AssertEx.Null(AssertEx.NotNull(terminalRequest).Sources);
    }

    /// <summary>
    ///     On a node with no embedding model the upload cannot index, so a grounded turn must say that grounding is
    ///     unavailable rather than run silently, where the 4B claims it has no access to documents.
    /// </summary>
    [Test]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task SendMessageAsync_WhenKnowledgeIsRequested_NoticesOnlyAMissingEmbeddingModel(bool embedderInstalled, bool expectNotice)
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ContextCapturingInvocationRunner(dispatcher);
        var scopeFactory = CreateKnowledgeScopeFactory();
        var embeddingResolver = Substitute.For<IEmbeddingModelResolver>();
        embeddingResolver.ResolveAsync(Arg.Any<ILocalModelProvider>(), Arg.Any<CancellationToken>())
                         .Returns(new EmbeddingModelResolution
                         {
                             Name = "nomic-embed-text",
                             IsConfident = embedderInstalled
                         });
        var builder = new ChatTurnContextBuilder(Substitute.For<IConversationUploadedFileStore>(),
            CreateFenceSeedProvider(),
            scopeFactory,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            NullLogger<ChatTurnContextBuilder>.Instance,
            embeddingResolver,
            Substitute.For<ILocalModelProviderResolver>(),
            Options.Create(new KnowledgeBaseOptions()));

        var service = CreateServiceWithScopeFactory(persistence, runner, dispatcher, scopeFactory, turnContextBuilder: builder);

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "what does the runbook say?",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           UseKnowledgeBase: true)))
        {
            events.Add(streamEvent);
        }

        AssertEx.Equal(expectNotice ? 1 : 0, events.Count(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantNotice
                                                                         && streamEvent.NoticeKind == nameof(TurnNoticeKind.KnowledgeUnavailable)));
    }

    /// <summary>
    ///     A send queued behind a benchmark measurement says so; a benchmark poll's hold with the slot free does
    ///     not, because the benchmark queue takes that hold on every poll, even with nothing to claim.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SendMessageAsync_DuringABenchmark_NamesItOnlyWhenAMeasurementHoldsTheSlot(bool measurementHoldsTheSlot)
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ContextCapturingInvocationRunner(dispatcher);
        var gate = new GpuWorkGate();
        using var benchmark = AssertEx.NotNull(gate.TryBeginShared(GpuWorkKind.Benchmark));
        if (measurementHoldsTheSlot)
        {
            // A benchmark measurement runs under a conversation id no conversation has.
            var measurementConversationId = Guid.NewGuid();
            _ = persistence.GetConversationKindAsync(measurementConversationId, Arg.Any<CancellationToken>()).Returns((string?)null);
            _ = await dispatcher.ReportInvocationAssignedAsync(RuntimePackageBuilder.Valid().WithConversationId(measurementConversationId).Build());
        }

        var service = CreateServiceWithScopeFactory(persistence, runner, dispatcher, CreateKnowledgeScopeFactory(), gpuWorkGate: gate);

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)))
        {
            events.Add(streamEvent);
        }

        var notices = events.Where(static streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantNotice
                                                         && streamEvent.NoticeKind == nameof(TurnNoticeKind.QueuedBehindGpuWork))
                            .ToArray();
        AssertEx.Equal(measurementHoldsTheSlot ? 1 : 0, notices.Length);
        if (measurementHoldsTheSlot)
        {
            AssertEx.Equal("This message was queued behind a running benchmark.", notices[0].NoticeMessage);
        }
    }
}
