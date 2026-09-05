namespace XE_Local_AI_Engine.Tests.WorkSessions;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The checkpoint composer. The prose half comes from the EXISTING compaction service — the same call that bounds
///     the owned conversation's raw history — and every one of its no-op outcomes is non-fatal, because a node with no
///     installed local chat model still has to be able to checkpoint and be resumed.
/// </summary>
public sealed class WorkSessionCheckpointTests
{
    [Test]
    public async Task Compose_WritesTheStructuredStateFromTheSessionsRows()
    {
        var compaction = new StubCompactionService(new ConversationCompactionResult(ConversationCompactionOutcome.Compacted, "Three documents read, two open questions."));
        await using var factory = NewFactory(compaction);
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var (activeTaskId, decisionId) = await SeedContentAsync(factory.Services, sessionId).ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<WorkSessionCheckpointComposer>().ComposeAsync(sessionId).ConfigureAwait(false);

        var checkpoint = (await WorkSessionTestSupport.ReadCheckpointsAsync(factory.Services, sessionId).ConfigureAwait(false)).Single();
        // The mutation result carries the EVENT's watermark, and the store allocates the checkpoint row's first, so the
        // event always sits one above it. That is what a hub subscriber is told about; it re-reads each feed from that
        // feed's own watermark.
        AssertEx.True(checkpoint.Sequence < result.Sequence, "The checkpoint row is stamped before the event that announces it.");
        AssertEx.Equal("Three documents read, two open questions.", checkpoint.Summary);

        var state = AssertEx.NotNull(JsonSerializer.Deserialize<WorkSessionCheckpointState>(checkpoint.StateJson), "The structured half is always written.");
        AssertEx.Equal(activeTaskId, state.CurrentTaskId);
        AssertEx.Equal("Read the ADR", state.NextAction);
        AssertEx.Contains(state.OpenTaskIds, activeTaskId);
        AssertEx.Equal(decisionId, state.KeyFindingIds[0], "Decisions and open questions come first: they are what a resumed session must not re-litigate.");

        AssertEx.Equal(checkpoint.Id,
            (await WorkSessionTestSupport.ReadSessionAsync(factory.Services, sessionId).ConfigureAwait(false)).LastCheckpointId,
            "The session points at its latest checkpoint.");
    }

    [Test]
    public async Task Compose_ForAShortSession_FoldsWithTheSessionKeepWindow()
    {
        // The configured chat window keeps eight messages — four whole steps — verbatim, so a session that checkpoints
        // before its fourth step has nothing OUTSIDE the window to fold: compaction answered NothingToCompact and the
        // checkpoint's prose half stayed null, on exactly the sessions whose checkpoint is the only record of them.
        var compaction = new StubCompactionService(new ConversationCompactionResult(ConversationCompactionOutcome.NothingToCompact))
        {
            ResultByKeepVerbatim = keep => keep == ConversationStepContextBound.SessionKeepVerbatim
                ? new ConversationCompactionResult(ConversationCompactionOutcome.Compacted, "Two steps in, one document read.")
                : new ConversationCompactionResult(ConversationCompactionOutcome.NothingToCompact)
        };

        await using var factory = NewFactory(compaction);
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);

        await ComposeAsync(factory, sessionId).ConfigureAwait(false);

        AssertEx.Equal<int?>(ConversationStepContextBound.SessionKeepVerbatim,
            compaction.LastKeepVerbatim,
            "The checkpoint folds with the session window, not the configured chat default.");
        AssertEx.Equal("Two steps in, one document read.",
            (await WorkSessionTestSupport.ReadCheckpointsAsync(factory.Services, sessionId).ConfigureAwait(false)).Single().Summary);
    }

    [Test]
    [Arguments(ConversationCompactionOutcome.NothingToCompact)]
    [Arguments(ConversationCompactionOutcome.NoLocalModel)]
    [Arguments(ConversationCompactionOutcome.SummarizerReturnedNothing)]
    public async Task Compose_WhenCompactionIsANoOp_StillCheckpointsAndKeepsThePriorSummary(ConversationCompactionOutcome outcome)
    {
        var compaction = new StubCompactionService(new ConversationCompactionResult(ConversationCompactionOutcome.Compacted, "First pass."));
        await using var factory = NewFactory(compaction);
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);

        await ComposeAsync(factory, sessionId).ConfigureAwait(false);

        compaction.Result = new ConversationCompactionResult(outcome);
        await ComposeAsync(factory, sessionId).ConfigureAwait(false);

        var checkpoints = await WorkSessionTestSupport.ReadCheckpointsAsync(factory.Services, sessionId).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, checkpoints.Count, $"A {outcome} compaction must not stop the structured checkpoint.");
        AssertEx.Equal("First pass.", checkpoints[^1].Summary, "The prior synopsis is kept rather than replaced with a placeholder.");
    }

    [Test]
    public async Task Compose_WhenTheNodeNeverSummarized_LeavesTheSummaryNull()
    {
        // Nullable end to end on purpose: a node with no local model produces no synopsis, and a placeholder would be a
        // lie the resumed session would then read as fact.
        await using var factory = NewFactory(new StubCompactionService(new ConversationCompactionResult(ConversationCompactionOutcome.NoLocalModel)));
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<WorkSessionCheckpointComposer>().ComposeAsync(sessionId).ConfigureAwait(false);

        AssertEx.Null((await WorkSessionTestSupport.ReadCheckpointsAsync(factory.Services, sessionId).ConfigureAwait(false)).Single().Summary);
    }

    [Test]
    public async Task AfterACheckpoint_TheStateBlockCarriesItsSummary()
    {
        await using var factory = NewFactory(new StubCompactionService(new ConversationCompactionResult(ConversationCompactionOutcome.Compacted, "Where the work stands.")));
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<WorkSessionCheckpointComposer>().ComposeAsync(sessionId).ConfigureAwait(false);

        var store = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();
        var state = new WorkSessionState(await store.GetAsync(sessionId).ConfigureAwait(false),
            await store.ListTasksAsync(sessionId).ConfigureAwait(false),
            await store.ListFindingsAsync(sessionId).ConfigureAwait(false),
            await store.ListArtifactsAsync(sessionId).ConfigureAwait(false),
            await store.GetLatestCheckpointAsync(sessionId).ConfigureAwait(false));

        AssertEx.Contains(WorkSessionStateBlockComposer.Compose(state, step: 6, maxStepsPerRun: 25), "Where the work stands.");
    }

    private static async Task ComposeAsync(TestServerWebAppFactory factory, Guid sessionId)
    {
        // A fresh scope per checkpoint, mirroring the supervisor: a DbContext reused across two writes would carry a
        // stale row version into the second one.
        await using var scope = factory.Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<WorkSessionCheckpointComposer>().ComposeAsync(sessionId).ConfigureAwait(false);
    }

    private static TestServerWebAppFactory NewFactory(IConversationCompactionService compaction) =>
        new()
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(),
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IConversationCompactionService>();
                services.AddSingleton<IConversationCompactionService>(compaction);
            }
        };

    private static async Task<(Guid ActiveTaskId, Guid DecisionId)> SeedContentAsync(IServiceProvider services, Guid sessionId)
    {
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();

        var activeTaskId = Guid.NewGuid();
        var doneTaskId = Guid.NewGuid();
        _ = await store.ApplyPlanAsync(new ApplyWorkPlanCommand(sessionId,
                           WorkSessionVersions.Any,
                           Guid.NewGuid(),
                           AgentWorkSessionTaskOrigin.Agent,
                           [
                               new WorkPlanTaskChange(activeTaskId, WorkPlanTaskOperation.Add, Title: "Read the ADR", Status: AgentWorkSessionTaskStatus.Active),
                               new WorkPlanTaskChange(doneTaskId, WorkPlanTaskOperation.Add, Title: "Already finished", Status: AgentWorkSessionTaskStatus.Done)
                           ]))
                       .ConfigureAwait(false);

        _ = await store.AppendFindingAsync(new AppendWorkSessionFindingCommand(sessionId,
                           Guid.NewGuid(),
                           WorkSessionVersions.Any,
                           Guid.NewGuid(),
                           AgentWorkSessionFindingKind.Finding,
                           "A plain fact."))
                       .ConfigureAwait(false);

        var decisionId = Guid.NewGuid();
        _ = await store.AppendFindingAsync(new AppendWorkSessionFindingCommand(sessionId,
                           decisionId,
                           WorkSessionVersions.Any,
                           Guid.NewGuid(),
                           AgentWorkSessionFindingKind.Decision,
                           "Chose the process sandbox."))
                       .ConfigureAwait(false);

        return (activeTaskId, decisionId);
    }

    private sealed class StubCompactionService(ConversationCompactionResult result) : IConversationCompactionService
    {
        public ConversationCompactionResult Result { get; set; } = result;

        /// <summary>Set to answer per keep window — what the short-session case needs to tell the two windows apart.</summary>
        public Func<int?, ConversationCompactionResult>? ResultByKeepVerbatim { get; set; }

        public int? LastKeepVerbatim { get; private set; }

        public Task<ConversationCompactionResult> CompactAsync(Guid conversationId,
            string? requestedModel,
            int? recentMessagesToKeepVerbatim,
            CancellationToken cancellationToken = default)
        {
            LastKeepVerbatim = recentMessagesToKeepVerbatim;
            return Task.FromResult(ResultByKeepVerbatim is null ? Result : ResultByKeepVerbatim(recentMessagesToKeepVerbatim));
        }
    }
}
