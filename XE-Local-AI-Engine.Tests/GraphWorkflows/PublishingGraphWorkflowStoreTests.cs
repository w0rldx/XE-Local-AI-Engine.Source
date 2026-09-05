namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The notification seam. It wraps the store rather than living at each call site in the runtime because a missed
///     call site is a pane that silently stops updating and no test would notice — which is why the coverage assertion
///     below is the point of the design rather than decoration: a mutation added to the store interface fails this
///     file until it is announced.
/// </summary>
public sealed class PublishingGraphWorkflowStoreTests
{
    private const long Sequence = 12;

    private static readonly Guid RunId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid NodeRunId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    /// <summary>
    ///     The claim the decorator exists to make: nothing can commit without announcing it. Asserted against the
    ///     INTERFACE rather than a hand-picked few, so a mutation added later cannot quietly ship unannounced.
    /// </summary>
    [Test]
    public void TheProbes_CoverEveryMutationTheStoreDeclares()
    {
        var declared = typeof(IGraphWorkflowStore).GetMethods()
                                                  .Where(static method => method.ReturnType == typeof(Task<GraphWorkflowMutationResult>))
                                                  .Select(static method => method.Name)
                                                  .Distinct(StringComparer.Ordinal)
                                                  .OrderBy(static name => name, StringComparer.Ordinal);
        var probed = Probes().Select(static probe => probe.Method).Distinct(StringComparer.Ordinal).OrderBy(static name => name, StringComparer.Ordinal);

        AssertEx.Equal(string.Join(Environment.NewLine, declared),
            string.Join(Environment.NewLine, probed),
            "Every store mutation must be exercised below: an unannounced one is a view that silently stops updating.");
    }

    [Test]
    public async Task EveryMutation_AnnouncesItsCommitWithTheKindTheClientReactsTo()
    {
        foreach (var probe in Probes())
        {
            var (store, publisher, _) = Create();

            await probe.Invoke(store).ConfigureAwait(false);

            await publisher.Received(1).PublishAsync(RunId, Sequence, probe.Kind, Arg.Any<CancellationToken>());
            AssertEx.Equal(expected: 1,
                publisher.ReceivedCalls().Count(),
                $"{probe.Method} → {probe.Kind} must announce its commit exactly once, with the watermark that commit allocated.");
        }
    }

    /// <summary>A node run entering a human wait is the one status move a client does more than repaint for.</summary>
    [Test]
    public async Task ANodeRunEnteringAHumanWait_AnnouncesAGate()
    {
        var (store, publisher, _) = Create();

        _ = await store.TransitionNodeRunAsync(NodeRunTransition(GraphWorkflowNodeRunStatus.WaitingForApproval)).ConfigureAwait(false);

        await publisher.Received(1).PublishAsync(RunId, Sequence, GraphWorkflowChangeKind.Gate, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AReadAnnouncesNothing()
    {
        foreach (var read in Reads())
        {
            var (store, publisher, _) = Create();

            await read(store).ConfigureAwait(false);

            AssertEx.Empty(publisher.ReceivedCalls());
        }
    }

    /// <summary>
    ///     The three writes that deliberately announce nothing: nobody subscribes to a definition, nothing is watching
    ///     a run that does not exist yet, and startup recovery runs before any client can connect.
    /// </summary>
    [Test]
    public async Task AWriteNobodyIsWatchingAnnouncesNothing()
    {
        foreach (var write in SilentWrites())
        {
            var (store, publisher, _) = Create();

            await write(store).ConfigureAwait(false);

            AssertEx.Empty(publisher.ReceivedCalls());
        }
    }

    /// <summary>
    ///     The write is already committed when the announcement is attempted, so failing the caller over a notification
    ///     would turn a late repaint into a lost transition.
    /// </summary>
    [Test]
    public async Task AFailedAnnouncementDoesNotFailTheCommittedWrite()
    {
        var (store, publisher, inner) = Create();
        publisher.PublishAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<GraphWorkflowChangeKind>(), Arg.Any<CancellationToken>())
                 .ThrowsAsyncForAnyArgs(new InvalidOperationException("the hub is gone"));

        var result = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(RunId, GraphWorkflowVersions.Any, GraphWorkflowRunStatus.Running))
                                .ConfigureAwait(false);

        AssertEx.Equal(Sequence, result.Sequence, "the commit's own watermark still reaches the caller.");
        await inner.Received(1).TransitionRunAsync(Arg.Any<TransitionGraphWorkflowRunCommand>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Every mutation the store can commit, and the kind the client reacts to.</summary>
    private static IReadOnlyList<Probe> Probes() =>
    [
        new(nameof(IGraphWorkflowStore.TransitionRunAsync),
            GraphWorkflowChangeKind.Run,
            store => store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(RunId, GraphWorkflowVersions.Any, GraphWorkflowRunStatus.Running))),
        new(nameof(IGraphWorkflowStore.AppendEventAsync),
            GraphWorkflowChangeKind.Run,
            store => store.AppendEventAsync(new AppendGraphWorkflowEventCommand(RunId, GraphWorkflowVersions.Any, GraphWorkflowEventTypes.NodeInterrupted))),
        new(nameof(IGraphWorkflowStore.TransitionNodeRunAsync),
            GraphWorkflowChangeKind.Node,
            store => store.TransitionNodeRunAsync(NodeRunTransition(GraphWorkflowNodeRunStatus.Running))),
        new(nameof(IGraphWorkflowStore.TransitionNodeRunAsync),
            GraphWorkflowChangeKind.Gate,
            store => store.TransitionNodeRunAsync(NodeRunTransition(GraphWorkflowNodeRunStatus.WaitingForApproval)))
    ];

    private static IReadOnlyList<Func<IGraphWorkflowStore, Task>> Reads() =>
    [
        store => store.ListDefinitionsAsync(),
        store => store.GetDefinitionAsync(Guid.NewGuid()),
        store => store.FindRunByRequestAsync(Guid.NewGuid()),
        store => store.GetRunAsync(RunId),
        store => store.ListRunsAsync(),
        store => store.CountActiveRunsAsync(probeLimit: 4),
        store => store.ListNodeRunsAsync(RunId),
        store => store.GetNodeRunAsync(RunId, "draft"),
        store => store.ListEventsAsync(RunId),
        store => store.ListInterruptedNodeRunsAsync()
    ];

    private static IReadOnlyList<Func<IGraphWorkflowStore, Task>> SilentWrites() =>
    [
        store => store.CreateDefinitionAsync(new CreateGraphWorkflowDefinitionCommand(Guid.NewGuid(), "graph", "{}", NodeCount: 3)),
        store => store.UpdateDefinitionAsync(new UpdateGraphWorkflowDefinitionCommand(Guid.NewGuid(), ExpectedVersion: 1, "renamed")),
        store => store.DeleteDefinitionAsync(Guid.NewGuid()),
        store => store.StartRunAsync(new StartGraphWorkflowRunCommand(RunId,
            RequestId: Guid.NewGuid(),
            DefinitionId: Guid.NewGuid(),
            DefinitionVersion: 1,
            "graph-hash",
            "{}",
            InputJson: null,
            [new GraphWorkflowNodeRunSeed(NodeRunId, "draft", GraphWorkflowNodeKind.Agent)])),
        store => store.ReconcileNonTerminalNodeRunsAsync("the node restarted", [])
    ];

    private static TransitionGraphWorkflowNodeRunCommand NodeRunTransition(GraphWorkflowNodeRunStatus target) =>
        new(RunId, NodeRunId, GraphWorkflowVersions.Any, target);

    private static (IGraphWorkflowStore Store, IGraphWorkflowEventPublisher Publisher, IGraphWorkflowStore Inner) Create()
    {
        var inner = Substitute.For<IGraphWorkflowStore>();
        var result = new GraphWorkflowMutationResult(RunId, Sequence);
        inner.TransitionRunAsync(Arg.Any<TransitionGraphWorkflowRunCommand>(), Arg.Any<CancellationToken>()).Returns(result);
        inner.TransitionNodeRunAsync(Arg.Any<TransitionGraphWorkflowNodeRunCommand>(), Arg.Any<CancellationToken>()).Returns(result);
        inner.AppendEventAsync(Arg.Any<AppendGraphWorkflowEventCommand>(), Arg.Any<CancellationToken>()).Returns(result);

        var publisher = Substitute.For<IGraphWorkflowEventPublisher>();
        var store = new PublishingGraphWorkflowStore(inner, publisher, NullLogger<PublishingGraphWorkflowStore>.Instance);
        return (store, publisher, inner);
    }

    private sealed record Probe(string Method, GraphWorkflowChangeKind Kind, Func<IGraphWorkflowStore, Task> Invoke);
}
