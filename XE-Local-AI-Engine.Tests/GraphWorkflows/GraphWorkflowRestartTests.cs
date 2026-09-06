namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Killing the engine while a run is in flight, one test per row of the run-engine plan's reconciler table.
///     <para>
///         The set a restart judges is exactly <c>Queued ∪ Running</c>. Everything here is ultimately about that
///         sentence: a queued row was never dispatched, a running inline row is a pure function of rows the crash did
///         not change, a running agent turn died with the process, and a pause is a durable human wait that a restart
///         must leave exactly where it found it.
///     </para>
///     <para>
///         Every test carries the keyed <c>[NotInParallel]</c> below, because startup recovery sweeps every in-flight
///         node run in the DATABASE rather than one run's — which on this class's shared host is every sibling test's
///         staging as well. The key serializes this class alone; other classes hold their own host and their own file.
///     </para>
/// </summary>
public sealed class GraphWorkflowRestartTests
{
    private const string RecoveryKey = nameof(GraphWorkflowRestartTests);

    [ClassDataSource<GraphWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowHostFixture Host { get; init; }

    /// <summary>
    ///     A queued row was admitted and never dispatched, so nothing about it failed: it collapses back to
    ///     <c>Pending</c> unspent, and the first tick after the restart runs it as though the crash had not happened.
    /// </summary>
    [Test]
    [NotInParallel(RecoveryKey)]
    public async Task AnInterruptedQueuedNodeRun_CollapsesToPendingUnspentAndRunsOnTheFirstTick()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await InFlightWorkNodeAsync(harness, GraphWorkflowNodeRunStatus.Queued).ConfigureAwait(false);

        await RestartAsync(harness).ConfigureAwait(false);

        var collapsed = await harness.ReadNodeRunAsync(runId, "work").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Pending, collapsed.Status);
        AssertEx.Equal(expected: 1, collapsed.Attempt, "a row that was never dispatched did not fail, so it is not an attempt.");
        AssertEx.Equal(GraphWorkflowFailureClass.None, collapsed.FailureClass, "a re-dispatchable row must not carry the reason it stopped.");
        AssertEx.Contains(await harness.ReadEventTrailAsync(runId).ConfigureAwait(false), "node.interrupted");

        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "work").ConfigureAwait(false)).Status,
            "the replacement dispatcher re-dispatches it on its first tick.");
    }

    /// <summary>
    ///     An inline node run is a pure function of rows the crash did not change, so re-running it costs nothing and
    ///     answers the same. It collapses to <c>Pending</c> exactly as a queued row does.
    /// </summary>
    [Test]
    [NotInParallel(RecoveryKey)]
    public async Task AnInterruptedRunningInlineNodeRun_CollapsesToPendingAndTheFirstTickRunsIt()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await InFlightWorkNodeAsync(harness, GraphWorkflowNodeRunStatus.Running).ConfigureAwait(false);

        await RestartAsync(harness).ConfigureAwait(false);

        var collapsed = await harness.ReadNodeRunAsync(runId, "work").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Pending, collapsed.Status);
        AssertEx.Equal(expected: 1, collapsed.Attempt, "an inline node re-derives its answer, so the restart costs it no attempt.");
        AssertEx.Null(collapsed.StartedAtUtc, "or a reader sees the row running since the attempt the host died on.");

        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "work").ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     The one row a restart cannot resume: an agent turn was an in-process task with no durable handle, so its
    ///     partial output died with the host and the invocation slot went with it. It is FAILED rather than collapsed —
    ///     and failed with the plain <c>Interrupted</c> class, because the reconciler knows nothing about retry.
    /// </summary>
    [Test]
    [NotInParallel(RecoveryKey)]
    public async Task AnInterruptedRunningAgentNodeRun_IsFailedInterruptedRatherThanResumed()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await RunningAgentNodeAsync(harness, GraphWorkflowGraphs.InlineWithAgent).ConfigureAwait(false);

        await RestartAsync(harness).ConfigureAwait(false);

        var failed = await harness.ReadNodeRunAsync(runId, "analyze").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, failed.Status, "never resume a provider stream: there is nothing left to resume.");
        AssertEx.Equal(GraphWorkflowFailureClass.Interrupted, failed.FailureClass);
        AssertEx.Equal(expected: 1, failed.Attempt, "the reconciler never re-attempts; the dispatcher's retry stage decides that.");
        AssertEx.Contains(await harness.ReadEventTrailAsync(runId).ConfigureAwait(false),
            "node.interrupted, node.failed",
            message: "the collapse and the verdict that repairs it are both on the log, in that order.");
    }

    /// <summary>
    ///     And the other half of that ruling: the interrupted verdict is re-attempted by the dispatcher's retry stage
    ///     rather than by a second mechanism inside recovery. A work node gets three attempts by default, so the first
    ///     tick after the restart spends the second one.
    /// </summary>
    [Test]
    [NotInParallel(RecoveryKey)]
    public async Task AnInterruptedAgentWithAnAttemptLeft_IsReAttemptedByTheDispatchersRetryStage()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await RunningAgentNodeAsync(harness, GraphWorkflowGraphs.InlineWithAgent).ConfigureAwait(false);

        await RestartAsync(harness).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        // The second attempt then fails ValidationFailed, because this build registers no Agent lane — the absent case
        // the dispatcher documents, and the one the agent executor removes. What is under test is that the attempt was
        // spent at all, which is the retry stage consuming a verdict recovery wrote and knows nothing further about.
        AssertEx.Equal(expected: 2, (await harness.ReadNodeRunAsync(runId, "analyze").ConfigureAwait(false)).Attempt);
        var retried = (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Single(static entry => entry.EventType == "node.retried");
        AssertEx.Contains(retried.DetailJson, "Interrupted", message: "the row cleared the failure, so the event is the only place the interruption survives.");
    }

    /// <summary>
    ///     The test that stops a reconciler from destroying every pause on the node at boot. <c>WaitingForApproval</c>
    ///     is non-terminal and is NOT in the interrupted set: it is a durable human wait, not work a host death
    ///     invalidated.
    /// </summary>
    [Test]
    [NotInParallel(RecoveryKey)]
    public async Task ANodeRunWaitingForApproval_ComesBackFromARestartUntouched()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await InFlightWorkNodeAsync(harness, GraphWorkflowNodeRunStatus.Running).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "work", GraphWorkflowNodeRunStatus.WaitingForApproval).ConfigureAwait(false);
        var paused = await harness.ReadNodeRunAsync(runId, "work").ConfigureAwait(false);

        await RestartAsync(harness).ConfigureAwait(false);

        var afterwards = await harness.ReadNodeRunAsync(runId, "work").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.WaitingForApproval, afterwards.Status);
        AssertEx.Equal(paused.Attempt, afterwards.Attempt);
        AssertEx.Equal(paused.UpdatedAtUtc, afterwards.UpdatedAtUtc, "untouched means the row was not written at all, not that it landed back where it was.");
        AssertEx.Empty((await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Where(static entry => entry.EventType == "node.interrupted"));
    }

    /// <summary>
    ///     The reconciler touches no RUN row: the dispatcher recomputes the run from the node runs recovery left behind,
    ///     on the sweep it runs immediately at startup.
    ///     <para>
    ///         The run's watermark and version DO advance, and necessarily so — the <c>node.interrupted</c> events are
    ///         entries in the run's own append-only log, and every entry takes the next sequence off the run row. What
    ///         "touches no run row" means, and what this asserts, is that recovery decides nothing about the run's
    ///         STATUS or its outcome.
    ///     </para>
    /// </summary>
    [Test]
    [NotInParallel(RecoveryKey)]
    public async Task AStartupRecovery_DecidesNothingAboutTheRunItself()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await InFlightWorkNodeAsync(harness, GraphWorkflowNodeRunStatus.Running).ConfigureAwait(false);
        var before = await harness.ReadRunAsync(runId).ConfigureAwait(false);

        await RestartAsync(harness).ConfigureAwait(false);

        var afterwards = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(before.Status, afterwards.Status, "runs auto-resume: a restart is not an operator decision to make about every live run.");
        AssertEx.Equal(before.FailureClass, afterwards.FailureClass);
        AssertEx.Equal(before.CompletedAtUtc, afterwards.CompletedAtUtc);
        AssertEx.Null(afterwards.OutputJson, "and writes no result for a run it did not decide had one.");
    }

    /// <summary>
    ///     A run with no attempt left does not loop: the interrupted verdict stands, the branch behind it cascades, and
    ///     the run reaches a terminal status rather than being re-offered for ever.
    /// </summary>
    [Test]
    [NotInParallel(RecoveryKey)]
    public async Task ARunWhoseAgentHasNoAttemptLeft_SettlesFailedInsteadOfLooping()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await RunningAgentNodeAsync(harness, GraphWorkflowGraphs.InlineWithSingleAttemptAgent).ConfigureAwait(false);

        await RestartAsync(harness).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowRunStatus.Failed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        var analyze = await harness.ReadNodeRunAsync(runId, "analyze").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowFailureClass.Interrupted, analyze.FailureClass, "the node's own budget refused the retry, so the class recovery wrote stands.");
        AssertEx.Equal(expected: 1, analyze.Attempt);
        AssertEx.Empty((await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Where(static entry => entry.EventType == "node.retried"));
    }

    /// <summary>
    ///     The restart's own crash window: the host dies while recovering, between collapsing the stranded rows and
    ///     writing what each of them costs. Nothing may be left half-repaired — a row reading as an ordinary
    ///     <c>Pending</c> would be re-run on the next boot with nothing accounted for, for ever.
    /// </summary>
    [Test]
    [NotInParallel(RecoveryKey)]
    public async Task ARecoveryThatDiesBeforeItCommits_LeavesTheRowsForTheNextBootToRepairExactlyOnce()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await InFlightWorkNodeAsync(harness, GraphWorkflowNodeRunStatus.Running).ConfigureAwait(false);

        await FailRecoveryAsync(harness).ConfigureAwait(false);
        await FailRecoveryAsync(harness).ConfigureAwait(false);

        var stranded = await harness.ReadNodeRunAsync(runId, "work").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Running, stranded.Status, "a recovery that did not commit leaves the row exactly as the dead host left it.");
        AssertEx.Empty((await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Where(static entry => entry.EventType == "node.interrupted"),
            "and writes no event either: the collapse and its verdicts are one transaction.");

        await RestartAsync(harness).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Pending, (await harness.ReadNodeRunAsync(runId, "work").ConfigureAwait(false)).Status);
        AssertEx.Equal(expected: 1,
            (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count(static entry => entry.EventType == "node.interrupted"),
            "one interruption, one interrupted event, however many boots died trying to record it.");
    }

    /// <summary>
    ///     A row that keeps moving under recovery is settled by the LAST pass rather than left in flight. The settlement
    ///     travels on that pass and on no earlier one: passing it sooner would fail rows a second read would have
    ///     judged, and never passing it would strand a row the dispatcher polls neither of.
    /// </summary>
    [Test]
    [NotInParallel(RecoveryKey)]
    public async Task ANodeRunThatKeepsMovingUnderRecovery_IsSettledByTheLastPassAndNoEarlierOne()
    {
        var store = new DriftingGraphWorkflowStore();

        await NewReconciler(enabled: true).RecoverAsync(store, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 3, store.Settlements.Count, "recovery is bounded: a writer it cannot outrace must not keep it from the dispatcher.");
        AssertEx.Null(store.Settlements[0], "an unjudged row on an early pass is read again rather than failed off stale evidence.");
        AssertEx.Null(store.Settlements[1]);
        var settlement = AssertEx.NotNull(store.Settlements[2], "the last pass settles what is left; there is no Blocked state in v1 to park it in.");
        AssertEx.Equal(GraphWorkflowFailureClass.Interrupted, settlement.FailureClass);
        AssertEx.Contains(settlement.SanitizedReason, "could not settle");
    }

    /// <summary>
    ///     A disabled node reads nothing at all. The guard sits before the scope, so recovery on a node whose feature is
    ///     off does not even reach the container — which is what this scope factory, which throws if asked, pins.
    /// </summary>
    [Test]
    [NotInParallel(RecoveryKey)]
    public async Task ADisabledNode_OpensNoScopeAndReadsNoRowAtStartup()
    {
        await NewReconciler(enabled: false).StartAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    ///     The registration order the whole design rests on: hosted services start in the order they were registered, so
    ///     the reconciler has to be registered before the dispatcher or the pumps begin admitting rows nothing judged.
    /// </summary>
    [Test]
    public void TheModule_RegistersItsReconcilerAheadOfTheDispatcher()
    {
        // Fully qualified: this class's fixture property is also called Host.
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(settings: null);
        _ = builder.AddNodeGraphWorkflows(new ConfigurationBuilder().Build());

        var hosted = builder.Services.Where(static descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList();
        AssertEx.Equal(expected: 2, hosted.Count, "this module owns exactly two hosted services: recovery, and the loop.");
        AssertEx.Equal(typeof(GraphWorkflowStartupReconciler),
            hosted[0].ImplementationType,
            "startup recovery must finish before the dispatcher's pumps begin admitting node runs it has not judged.");
    }

    /// <summary>
    ///     A restart, in the order the composition root registers it: recovery makes the stranded node runs judgeable
    ///     again, then a fresh dispatcher takes over. The reconciler is constructed by hand because the test host strips
    ///     every hosted service, which is why the registration ORDER is pinned by its own test above rather than here.
    /// </summary>
    private static async Task RestartAsync(GraphWorkflowHarness harness)
    {
        await new GraphWorkflowStartupReconciler(harness.Services.GetRequiredService<IServiceScopeFactory>(),
                  Options.Create(harness.CurrentOptions()),
                  harness.Services.GetRequiredService<ILogger<GraphWorkflowStartupReconciler>>())
              .StartAsync(CancellationToken.None)
              .ConfigureAwait(false);

        _ = harness.CreateReplacementDispatcher();
    }

    /// <summary>
    ///     A startup recovery that dies before it commits — the one window a restart has, between collapsing the rows
    ///     the dead host stranded and writing what each of them costs.
    ///     <para>
    ///         The transaction is made to fail by handing it a repair under a version the run cannot have, rather than
    ///         by killing a process: what the test using this asserts is the transaction BOUNDARY, and the cause of the
    ///         failure is not what makes that true.
    ///     </para>
    /// </summary>
    private static async Task FailRecoveryAsync(GraphWorkflowHarness harness)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>();
        var interrupted = await store.ListInterruptedNodeRunsAsync().ConfigureAwait(false);
        AssertEx.NotEmpty(interrupted, "there was no in-flight node run for the failed recovery to have died on.");

        _ = await AssertEx.ThrowsAsync<GraphWorkflowInvalidTransitionException>(() => store.ReconcileNonTerminalNodeRunsAsync("the host restarted",
                          [
                              .. interrupted.Select(static row => new GraphWorkflowNodeRunVerdict(row.NodeRunId,
                                  row.Status,
                                  row.Attempt,
                                  [
                                      new TransitionGraphWorkflowNodeRunCommand(row.RunId,
                                          row.NodeRunId,
                                          long.MaxValue,
                                          GraphWorkflowNodeRunStatus.Pending)
                                  ]))
                          ]))
                          .ConfigureAwait(false);
    }

    private static GraphWorkflowStartupReconciler NewReconciler(bool enabled) =>
        new(new ThrowingServiceScopeFactory(),
            Options.Create(new GraphWorkflowOptions
            {
                Enabled = enabled
            }),
            NullLogger<GraphWorkflowStartupReconciler>.Instance);

    /// <summary>A run ticked far enough that its inline work node is in flight, the way a host death would leave it.</summary>
    private static async Task<Guid> InFlightWorkNodeAsync(GraphWorkflowHarness harness, GraphWorkflowNodeRunStatus status)
    {
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineRetryable).ConfigureAwait(false);

        // Out of Pending, then Start — after which the work node is Pending and nothing has dispatched it yet.
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "work", status).ConfigureAwait(false);
        return runId;
    }

    /// <summary>
    ///     The same, for an <c>Agent</c> node. It is stood <c>Running</c> through the store rather than by a tick,
    ///     because this build registers no Agent lane and a tick would fail the row before a restart could judge it.
    /// </summary>
    private static async Task<Guid> RunningAgentNodeAsync(GraphWorkflowHarness harness, string graphJson)
    {
        var runId = await harness.StartRunAsync(graphJson).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "analyze", GraphWorkflowNodeRunStatus.Running).ConfigureAwait(false);
        return runId;
    }
}

/// <summary>
///     A store whose one in-flight row never stops moving: each read reports a different attempt, so every verdict is
///     composed from evidence the next commit no longer recognises and nothing is ever reconciled. It stands in for the
///     writer recovery cannot outrace, and what it records is the only thing this seam decides — how many passes there
///     are, and which of them carries the settlement.
///     <para>
///         That a mismatched verdict is skipped rather than applied is the STORE's rule, pinned by its own suite in
///         <c>Client.Persistence.Tests</c>; re-deciding it here would be a second copy of it.
///     </para>
/// </summary>
internal sealed class DriftingGraphWorkflowStore : IGraphWorkflowStore
{
    private readonly GraphWorkflowReconciledNodeRun _stranded = new(Guid.NewGuid(),
        Guid.NewGuid(),
        "work",
        GraphWorkflowNodeKind.Agent,
        GraphWorkflowNodeRunStatus.Running,
        Attempt: 1);

    private int _reads;

    /// <summary>The settlement each reconcile pass carried, in order — <see langword="null" /> for a pass that is not the last.</summary>
    public List<GraphWorkflowUnjudgedNodeRunSettlement?> Settlements { get; } = [];

    public Task<IReadOnlyList<GraphWorkflowReconciledNodeRun>> ListInterruptedNodeRunsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GraphWorkflowReconciledNodeRun>>([
            _stranded with
            {
                Attempt = _stranded.Attempt + ++_reads
            }
        ]);

    public Task<IReadOnlyList<GraphWorkflowReconciledNodeRun>> ReconcileNonTerminalNodeRunsAsync(string sanitizedReason,
        IReadOnlyList<GraphWorkflowNodeRunVerdict> verdicts,
        GraphWorkflowUnjudgedNodeRunSettlement? unjudged = null,
        CancellationToken cancellationToken = default)
    {
        Settlements.Add(unjudged);
        return Task.FromResult<IReadOnlyList<GraphWorkflowReconciledNodeRun>>([]);
    }

    public Task<GraphWorkflowDefinitionSnapshot> CreateDefinitionAsync(CreateGraphWorkflowDefinitionCommand command, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<GraphWorkflowDefinitionSnapshot> UpdateDefinitionAsync(UpdateGraphWorkflowDefinitionCommand command, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<GraphWorkflowDefinitionSummary>> ListDefinitionsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<GraphWorkflowDefinitionSnapshot> GetDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DeleteDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<GraphWorkflowRunSnapshot> StartRunAsync(StartGraphWorkflowRunCommand command, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<GraphWorkflowRunSnapshot?> FindRunByRequestAsync(Guid requestId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<GraphWorkflowRunSnapshot> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<GraphWorkflowRunSnapshot>> ListRunsAsync(GraphWorkflowRunStatus? status = null, int limit = 50, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<int> CountActiveRunsAsync(int probeLimit, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<GraphWorkflowMutationResult> TransitionRunAsync(TransitionGraphWorkflowRunCommand command, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<GraphWorkflowNodeRunSnapshot>> ListNodeRunsAsync(Guid runId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<GraphWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid runId, string nodeKey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<GraphWorkflowMutationResult> TransitionNodeRunAsync(TransitionGraphWorkflowNodeRunCommand command, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<GraphWorkflowMutationResult> AppendEventAsync(AppendGraphWorkflowEventCommand command, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<GraphWorkflowMutationResult?> DecideNodeRunAsync(DecideGraphWorkflowNodeRunCommand command, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<GraphWorkflowNodeRunSnapshot?> FindNodeRunByDecisionOperationAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<GraphWorkflowRunEventSnapshot>> ListEventsAsync(Guid runId, long afterSeq = 0, int limit = 200, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>A scope factory that fails the test if anything asks it for a scope. The disabled node's guard is that nothing does.</summary>
internal sealed class ThrowingServiceScopeFactory : IServiceScopeFactory
{
    public IServiceScope CreateScope() =>
        throw new AssertionException("Startup recovery opened a service scope on a node whose feature is disabled.");
}
