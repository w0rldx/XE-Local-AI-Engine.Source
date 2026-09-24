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
[Category(TestCategories.Integration)]
public sealed class WorkSessionCheckpointTests
{
    [Test]
    public async Task Compose_WritesTheStructuredStateFromTheSessionsRows()
    {
        var compaction = new StubCompactionService(new ConversationCompactionResult
        {
            Outcome = ConversationCompactionOutcome.Compacted,
            Summary = "Three documents read, two open questions."
        });
        await using var factory = NewFactory(compaction);
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var (activeTaskId, decisionId) = await SeedContentAsync(factory.Services, sessionId);

        await using var scope = factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<WorkSessionCheckpointComposer>().ComposeAsync(sessionId);

        var checkpoint = (await WorkSessionTestSupport.ReadCheckpointsAsync(factory.Services, sessionId)).Single();
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
            (await WorkSessionTestSupport.ReadSessionAsync(factory.Services, sessionId)).LastCheckpointId,
            "The session points at its latest checkpoint.");
    }

    [Test]
    public async Task Compose_ForAShortSession_FoldsWithTheSessionKeepWindow()
    {
        // The configured chat window keeps eight messages — four whole steps — verbatim, so a session that checkpoints
        // before its fourth step has nothing OUTSIDE the window to fold: compaction answered NothingToCompact and the
        // checkpoint's prose half stayed null, on exactly the sessions whose checkpoint is the only record of them.
        var compaction = new StubCompactionService(new ConversationCompactionResult
        {
            Outcome = ConversationCompactionOutcome.NothingToCompact
        })
        {
            ResultByKeepVerbatim = keep => keep == ConversationStepContextBound.SessionKeepVerbatim
                ? new ConversationCompactionResult
                {
                    Outcome = ConversationCompactionOutcome.Compacted,
                    Summary = "Two steps in, one document read."
                }
                : new ConversationCompactionResult
                {
                    Outcome = ConversationCompactionOutcome.NothingToCompact
                }
        };

        await using var factory = NewFactory(compaction);
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);

        await ComposeAsync(factory, sessionId);

        AssertEx.Equal<int?>(ConversationStepContextBound.SessionKeepVerbatim,
            compaction.LastKeepVerbatim,
            "The checkpoint folds with the session window, not the configured chat default.");
        AssertEx.Equal("Two steps in, one document read.",
            (await WorkSessionTestSupport.ReadCheckpointsAsync(factory.Services, sessionId)).Single().Summary);
    }

    [Test]
    [Arguments(ConversationCompactionOutcome.NothingToCompact)]
    [Arguments(ConversationCompactionOutcome.NoLocalModel)]
    [Arguments(ConversationCompactionOutcome.SummarizerReturnedNothing)]
    [Arguments(ConversationCompactionOutcome.TimedOut)]
    public async Task Compose_WhenCompactionIsANoOp_StillCheckpointsAndKeepsThePriorSummary(ConversationCompactionOutcome outcome)
    {
        var compaction = new StubCompactionService(new ConversationCompactionResult
        {
            Outcome = ConversationCompactionOutcome.Compacted,
            Summary = "First pass."
        });
        await using var factory = NewFactory(compaction);
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);

        await ComposeAsync(factory, sessionId);

        compaction.Result = new ConversationCompactionResult
        {
            Outcome = outcome
        };
        await ComposeAsync(factory, sessionId);

        var checkpoints = await WorkSessionTestSupport.ReadCheckpointsAsync(factory.Services, sessionId);
        AssertEx.Equal(expected: 2, checkpoints.Count, $"A {outcome} compaction must not stop the structured checkpoint.");
        AssertEx.Equal("First pass.", checkpoints[^1].Summary, "The prior synopsis is kept rather than replaced with a placeholder.");
    }

    [Test]
    public async Task Compose_WhenTheNodeNeverSummarized_LeavesTheSummaryNull()
    {
        // Nullable end to end on purpose: a node with no local model produces no synopsis, and a placeholder would be a
        // lie the resumed session would then read as fact.
        await using var factory = NewFactory(new StubCompactionService(new ConversationCompactionResult
        {
            Outcome = ConversationCompactionOutcome.NoLocalModel
        }));
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);

        await using var scope = factory.Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<WorkSessionCheckpointComposer>().ComposeAsync(sessionId);

        AssertEx.Null((await WorkSessionTestSupport.ReadCheckpointsAsync(factory.Services, sessionId)).Single().Summary);
    }

    [Test]
    public async Task AfterACheckpoint_TheStateBlockCarriesItsSummary()
    {
        await using var factory = NewFactory(new StubCompactionService(new ConversationCompactionResult
        {
            Outcome = ConversationCompactionOutcome.Compacted,
            Summary = "Where the work stands."
        }));
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);

        await using var scope = factory.Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<WorkSessionCheckpointComposer>().ComposeAsync(sessionId);

        var store = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();
        var state = new WorkSessionState
        {
            Session = await store.GetAsync(sessionId),
            Tasks = await store.ListTasksAsync(sessionId),
            Findings = await store.ListFindingsAsync(sessionId),
            Artifacts = await store.ListArtifactsAsync(sessionId),
            LastCheckpoint = await store.GetLatestCheckpointAsync(sessionId)
        };

        AssertEx.Contains(WorkSessionStateBlockComposer.Compose(state, step: 6, maxStepsPerRun: 25), "Where the work stands.");
    }

    private static async Task ComposeAsync(TestServerWebAppFactory factory, Guid sessionId)
    {
        // A fresh scope per checkpoint, mirroring the supervisor: a DbContext reused across two writes would carry a
        // stale row version into the second one.
        await using var scope = factory.Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<WorkSessionCheckpointComposer>().ComposeAsync(sessionId);
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
        _ = await store.ApplyPlanAsync(new ApplyWorkPlanCommand
        {
            SessionId = sessionId,
            ExpectedVersion = WorkSessionVersions.Any,
            OperationId = Guid.NewGuid(),
            Origin = AgentWorkSessionTaskOrigin.Agent,
            Changes =
            [
                new WorkPlanTaskChange
                {
                    TaskId = activeTaskId,
                    Operation = WorkPlanTaskOperation.Add,
                    Title = "Read the ADR",
                    Status = AgentWorkSessionTaskStatus.Active
                },
                new WorkPlanTaskChange
                {
                    TaskId = doneTaskId,
                    Operation = WorkPlanTaskOperation.Add,
                    Title = "Already finished",
                    Status = AgentWorkSessionTaskStatus.Done
                }
            ]
        });

        _ = await store.AppendFindingAsync(new AppendWorkSessionFindingCommand
        {
            SessionId = sessionId,
            FindingId = Guid.NewGuid(),
            ExpectedVersion = WorkSessionVersions.Any,
            OperationId = Guid.NewGuid(),
            Kind = AgentWorkSessionFindingKind.Finding,
            Text = "A plain fact."
        });

        var decisionId = Guid.NewGuid();
        _ = await store.AppendFindingAsync(new AppendWorkSessionFindingCommand
        {
            SessionId = sessionId,
            FindingId = decisionId,
            ExpectedVersion = WorkSessionVersions.Any,
            OperationId = Guid.NewGuid(),
            Kind = AgentWorkSessionFindingKind.Decision,
            Text = "Chose the process sandbox."
        });

        return (activeTaskId, decisionId);
    }

    private sealed class StubCompactionService : IConversationCompactionService
    {
        public StubCompactionService(ConversationCompactionResult result)
        {
            Result = result;
        }

        public ConversationCompactionResult Result { get; set; }

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
