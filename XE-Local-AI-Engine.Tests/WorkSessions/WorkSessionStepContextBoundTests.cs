namespace XE_Local_AI_Engine.Tests.WorkSessions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The step boundary's transcript bound. A session step replays every earlier step's state block, answer and
///     reasoning verbatim, and its own knowledge-base reads can spend 16k tokens on a single document — on 2026-08-24 a
///     27B model at a 65,536-token window went over at step 5. The bound folds the older turns before the send.
/// </summary>
public sealed class WorkSessionStepContextBoundTests
{
    [Test]
    public async Task Loop_WhenTheProjectedTranscriptExceedsTheBudget_ForcesCompactionWithASessionKeepWindow()
    {
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        var compaction = new RecordingCompactionService();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxStepsPerRun", "1"),
                ("WorkSessions:StepContextBudgetTokens", "200")),
            ConfigureAdditionalTestServices = WithFakes(services => stream = new FakeNodeChatStreamService(
                    services.GetRequiredService<INodeChatStreamCancellationRegistry>(),
                    services,
                    sessionId),
                publisher,
                compaction)
        };

        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        // Well past a 200-token budget: ~4,000 characters of completed history at roughly four characters per token.
        await SeedTranscriptAsync(factory.Services, session.ConversationId, turns: 4, contentChars: 1_000).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        _ = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Paused).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, fake.Requests.Count);
        var forced = compaction.Calls.Where(call => call.ConversationId == session.ConversationId && call.KeepVerbatim is not null).ToList();
        AssertEx.NotEmpty(forced, "An over-budget step boundary must fold the session conversation before it sends.");
        AssertEx.Equal(WorkSessionStepContextBound.SessionKeepVerbatim,
            forced[0].KeepVerbatim,
            "The forced fold keeps one step verbatim, not the configured chat window.");
    }

    [Test]
    public async Task Loop_WhenTheProjectedTranscriptFitsTheBudget_SendsWithoutCompactingAtTheBoundary()
    {
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        var compaction = new RecordingCompactionService();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxStepsPerRun", "1"),
                ("WorkSessions:StepContextBudgetTokens", "100000")),
            ConfigureAdditionalTestServices = WithFakes(services => stream = new FakeNodeChatStreamService(
                    services.GetRequiredService<INodeChatStreamCancellationRegistry>(),
                    services,
                    sessionId),
                publisher,
                compaction)
        };

        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        await SeedTranscriptAsync(factory.Services, session.ConversationId, turns: 4, contentChars: 1_000).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        _ = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Paused).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, fake.Requests.Count);
        AssertEx.Empty(compaction.Calls.Where(call => call.KeepVerbatim is not null).ToList(),
            "Under budget, the step boundary must not summarize — that is a local model call per step for nothing.");
    }

    [Test]
    public async Task StateBlock_AfterTheTranscriptIsFolded_StillCarriesEveryOpenTaskAndKeyFinding()
    {
        // The whole reason folding is safe: the state block is rebuilt from the database, not from what survived the
        // transcript. A step that sends after a forced fold must still see the plan and the findings.
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        var compaction = new RecordingCompactionService();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxStepsPerRun", "1"),
                ("WorkSessions:StepContextBudgetTokens", "200")),
            ConfigureAdditionalTestServices = WithFakes(services => stream = new FakeNodeChatStreamService(
                    services.GetRequiredService<INodeChatStreamCancellationRegistry>(),
                    services,
                    sessionId),
                publisher,
                compaction)
        };

        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        await SeedTranscriptAsync(factory.Services, session.ConversationId, turns: 4, contentChars: 1_000).ConfigureAwait(false);
        await SeedPlanAsync(factory.Services, sessionId).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        _ = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Paused).ConfigureAwait(false);

        AssertEx.NotEmpty(compaction.Calls.Where(call => call.KeepVerbatim is not null).ToList());
        var sent = fake.Requests[0].Content;
        AssertEx.Contains(sent, "Read the runtime wiki", message: "The open task survives the fold.");
        AssertEx.Contains(sent, "Still open after folding", message: "The blocked task survives the fold.");
        AssertEx.Contains(sent, "llama.cpp is the default runtime", message: "The recorded finding survives the fold.");
    }

    [Test]
    public void Project_CountsReasoningAndIgnoresWhatTheSynopsisAlreadyCovers()
    {
        var estimator = new HeuristicTokenEstimator();
        var messages = new List<NodeChatPersistedMessageDto>
        {
            Message(sequence: 0, "user", new string('a', 4_000)),
            Message(sequence: 1, "assistant", new string('b', 400), new string('c', 4_000)),
            Message(sequence: 2, "user", new string('d', 400))
        };

        var whole = WorkSessionStepContextBound.Project(Conversation(messages), estimator);
        var covered = WorkSessionStepContextBound.Project(Conversation(messages, "SYNOPSIS", coversToSequence: 1), estimator);
        var withoutReasoning = WorkSessionStepContextBound.Project(
            Conversation([messages[0], Message(sequence: 1, "assistant", new string('b', 400)), messages[2]]),
            estimator);

        AssertEx.True(whole > 1_800, $"~8,800 characters of history should project well past 1,800 tokens, projected {whole}.");
        AssertEx.True(covered < whole / 2, $"A synopsis covering the first two messages should more than halve the projection, {covered} vs {whole}.");
        AssertEx.True(whole - withoutReasoning > 800,
            $"Replayed reasoning is real input and must be counted; dropping 4,000 characters of it changed the projection by {whole - withoutReasoning}.");
    }

    [Test]
    public void Project_WhenAMessageIsNotCompleted_LeavesItOut()
    {
        var estimator = new HeuristicTokenEstimator();
        var completed = Message(sequence: 0, "user", new string('a', 4_000));
        var streaming = completed with
        {
            MessageId = Guid.NewGuid(),
            Sequence = 1,
            Status = NodeChatMessageStatusValues.Streaming
        };

        var withStreaming = WorkSessionStepContextBound.Project(Conversation([completed, streaming]), estimator);
        var withoutStreaming = WorkSessionStepContextBound.Project(Conversation([completed]), estimator);

        AssertEx.Equal(withoutStreaming, withStreaming, "The send path drops non-completed messages, so the projection must too.");
    }

    private static Action<IServiceCollection> WithFakes(Func<IServiceProvider, INodeChatStreamService> streamFactory,
        RecordingWorkSessionEventPublisher publisher,
        RecordingCompactionService compaction) =>
        services =>
        {
            WorkSessionTestSupport.WithFakes(streamFactory, publisher)(services);
            services.RemoveAll<IConversationCompactionService>();
            services.AddSingleton<IConversationCompactionService>(compaction);
        };

    private static FakeNodeChatStreamService ResolveStream(TestServerWebAppFactory factory, ref FakeNodeChatStreamService? stream)
    {
        _ = factory.Services.GetRequiredService<INodeChatStreamService>();
        return AssertEx.NotNull(stream, "The fake stream service must have been constructed from the container.");
    }

    /// <summary>Persists <paramref name="turns" /> completed user/assistant exchanges so the projection has something to measure.</summary>
    private static async Task SeedTranscriptAsync(IServiceProvider services, Guid conversationId, int turns, int contentChars)
    {
        await using var scope = services.CreateAsyncScope();
        var persistence = scope.ServiceProvider.GetRequiredService<INodeChatPersistenceService>();
        for (var turn = 0; turn < turns; turn++)
        {
            var messageId = Guid.NewGuid();
            var requestId = Guid.NewGuid();
            _ = await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversationId,
                        Guid.NewGuid(),
                        new string('u', contentChars),
                        CreatedAtUtc: turn))
                .ConfigureAwait(false);
            _ = await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversationId, messageId, requestId, CreatedAtUtc: turn))
                                 .ConfigureAwait(false);
            _ = await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(new NodeChatMessageCorrelation(conversationId, messageId, requestId),
                        NodeChatMessageStatusValues.Completed,
                        UpdatedAtUtc: turn,
                        new string('a', contentChars),
                        new string('r', contentChars)))
                .ConfigureAwait(false);
        }
    }

    /// <summary>Seeds the durable state the folded transcript must not take with it.</summary>
    private static async Task SeedPlanAsync(IServiceProvider services, Guid sessionId)
    {
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();
        _ = await store.ApplyPlanAsync(new ApplyWorkPlanCommand(sessionId,
                    WorkSessionVersions.Any,
                    Guid.NewGuid(),
                    AgentWorkSessionTaskOrigin.Agent,
                    [
                        new WorkPlanTaskChange(Guid.NewGuid(), WorkPlanTaskOperation.Add, Title: "Read the runtime wiki", Status: AgentWorkSessionTaskStatus.Active),
                        new WorkPlanTaskChange(Guid.NewGuid(), WorkPlanTaskOperation.Add, Title: "Still open after folding", Status: AgentWorkSessionTaskStatus.Planned)
                    ]))
            .ConfigureAwait(false);
        _ = await store.AppendFindingAsync(new AppendWorkSessionFindingCommand(sessionId,
                    Guid.NewGuid(),
                    WorkSessionVersions.Any,
                    Guid.NewGuid(),
                    AgentWorkSessionFindingKind.Finding,
                    "llama.cpp is the default runtime"))
            .ConfigureAwait(false);
    }

    private static NodeChatConversationDto Conversation(IReadOnlyList<NodeChatPersistedMessageDto> messages, string? summary = null, int? coversToSequence = null) =>
        new(ConversationId: Guid.NewGuid(),
            Title: null,
            UserId: null,
            CreatedAtUtc: 0,
            LastSeenUtc: 0,
            Purged: false,
            Messages: messages,
            CompactionSummary: summary,
            CompactionSummaryCoversToSequence: coversToSequence);

    private static NodeChatPersistedMessageDto Message(int sequence, string role, string content, string? reasoning = null) =>
        new(Guid.NewGuid(),
            ConversationId: Guid.NewGuid(),
            RequestId: null,
            sequence,
            role,
            content,
            reasoning,
            NodeChatMessageStatusValues.Completed,
            CreatedAtUtc: sequence,
            UpdatedAtUtc: sequence,
            Model: null,
            Error: null,
            MetadataJson: null);

    /// <summary>Records every compaction the loop asks for, including the keep window it asked with.</summary>
    private sealed class RecordingCompactionService : IConversationCompactionService
    {
        public List<(Guid ConversationId, int? KeepVerbatim)> Calls { get; } = [];

        public Task<ConversationCompactionResult> CompactAsync(Guid conversationId,
            string? requestedModel,
            int? recentMessagesToKeepVerbatim,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((conversationId, recentMessagesToKeepVerbatim));
            return Task.FromResult(new ConversationCompactionResult(ConversationCompactionOutcome.NothingToCompact));
        }
    }
}
