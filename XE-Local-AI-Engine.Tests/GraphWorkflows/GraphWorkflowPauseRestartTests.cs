namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     What a restart and a cancel do to a run parked on a person.
///     <para>
///         A pause adds no restart MECHANISM: the set startup recovery judges is exactly <c>Queued ∪ Running</c>, so a
///         waiting row is already untouched. These tests exist to fail loudly if that set is ever widened — a durable
///         human wait collapsed by a reboot is an answered gate silently un-answering itself.
///     </para>
///     <para>
///         Keyed <c>[NotInParallel]</c> for the same reason <c>GraphWorkflowRestartTests</c> is: startup recovery
///         sweeps every in-flight node run in the DATABASE, which on this class's shared host is every sibling test's
///         staging as well.
///     </para>
/// </summary>
public sealed class GraphWorkflowPauseRestartTests
{
    private const string RecoveryKey = nameof(GraphWorkflowPauseRestartTests);

    [ClassDataSource<GraphWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowHostFixture Host { get; init; }

    [Test]
    [NotInParallel(RecoveryKey)]
    public async Task ARunParkedOnAPause_SurvivesARestartAndStillRoutesItsAnswer()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness).ConfigureAwait(false);

        await RestartAsync(harness).ConfigureAwait(false);

        var survived = await harness.ReadNodeRunAsync(runId, "review").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.WaitingForApproval, survived.Status, "a durable human wait is not what a restart invalidates.");
        AssertEx.Equal<GraphWorkflowDecisionKind?>(GraphWorkflowDecisionKind.Approve, survived.PendingDecisionKind, "and it still says what it is waiting for.");
        AssertEx.Equal(expected: 1, survived.Attempt, "waiting is not an attempt a restart spends.");
        AssertEx.Equal(GraphWorkflowRunStatus.WaitingForApproval, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        _ = await harness.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowRunStatus.Completed,
            (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status,
            "the answer given after the restart routes exactly as one given before it would have.");
    }

    /// <summary>
    ///     <c>WaitingForApproval</c> is a live status, so the cancel drain settles it like any other live row — the
    ///     pause lane has nothing in flight to be asked to stop. An answer arriving afterwards is refused.
    /// </summary>
    [Test]
    [NotInParallel(RecoveryKey)]
    public async Task CancellingARunThatIsWaiting_CancelsThePauseAndRefusesALaterAnswer()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness).ConfigureAwait(false);

        await harness.CancelAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Cancelled, (await harness.ReadNodeRunAsync(runId, "review").ConfigureAwait(false)).Status);
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        _ = await AssertEx.ThrowsAsync<GraphWorkflowRunConflictException>(() => harness.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve))
                          .ConfigureAwait(false);
    }

    private static async Task<Guid> ParkedRunAsync(GraphWorkflowHarness harness)
    {
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.PauseTwoDecisions).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.WaitingForApproval,
            (await harness.ReadNodeRunAsync(runId, "review").ConfigureAwait(false)).Status,
            "the run was expected to park on its pause.");
        return runId;
    }

    /// <summary>Startup recovery, then the dispatcher a restart would build — the same simulation the restart suite uses.</summary>
    private static async Task RestartAsync(GraphWorkflowHarness harness)
    {
        await new GraphWorkflowStartupReconciler(harness.Services.GetRequiredService<IServiceScopeFactory>(),
                  Options.Create(harness.CurrentOptions()),
                  harness.Services.GetRequiredService<ILogger<GraphWorkflowStartupReconciler>>())
              .StartAsync(CancellationToken.None)
              .ConfigureAwait(false);

        _ = harness.CreateReplacementDispatcher();
    }
}
