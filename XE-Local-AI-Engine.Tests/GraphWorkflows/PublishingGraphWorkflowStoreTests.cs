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
[Category(TestCategories.Unit)]
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

            await probe.Invoke(store);

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

        _ = await store.TransitionNodeRunAsync(NodeRunTransition(GraphWorkflowNodeRunStatus.WaitingForApproval));

        await publisher.Received(1).PublishAsync(RunId, Sequence, GraphWorkflowChangeKind.Gate, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AReadAnnouncesNothing()
    {
        foreach (var read in Reads())
        {
            var (store, publisher, _) = Create();

            await read(store);

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

            await write(store);

            AssertEx.Empty(publisher.ReceivedCalls());
        }
    }

    /// <summary>
    ///     A conditional write that matched no row committed nothing, so there is nothing to announce — and announcing
    ///     it anyway would tell every watching client to re-read a run that has not changed.
    /// </summary>
    [Test]
    public async Task ADecisionThatMatchedNoRow_AnnouncesNothing()
    {
        var inner = Substitute.For<IGraphWorkflowStore>();
        inner.DecideNodeRunAsync(Arg.Any<DecideGraphWorkflowNodeRunCommand>(), Arg.Any<CancellationToken>()).Returns((GraphWorkflowMutationResult?)null);
        var publisher = Substitute.For<IGraphWorkflowEventPublisher>();
        var store = new PublishingGraphWorkflowStore(inner, publisher, NullLogger<PublishingGraphWorkflowStore>.Instance);

        AssertEx.Null(await store.DecideNodeRunAsync(Decision()), "the decline travels to the caller untouched.");
        AssertEx.Empty(publisher.ReceivedCalls());
    }

    /// <summary>A publish stamp another tick already wrote matched no row, and announces nothing for the same reason.</summary>
    [Test]
    public async Task APublishStampThatMatchedNoRow_AnnouncesNothing()
    {
        var inner = Substitute.For<IGraphWorkflowStore>();
        inner.MarkNodeRunPublishedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((GraphWorkflowMutationResult?)null);
        var publisher = Substitute.For<IGraphWorkflowEventPublisher>();
        var store = new PublishingGraphWorkflowStore(inner, publisher, NullLogger<PublishingGraphWorkflowStore>.Instance);

        AssertEx.Null(await store.MarkNodeRunPublishedAsync(RunId, NodeRunId, Guid.NewGuid()));
        AssertEx.Empty(publisher.ReceivedCalls());
    }

    /// <summary>A steer the store declined committed nothing, and announces nothing.</summary>
    [Test]
    public async Task ASteerThatWasDeclined_AnnouncesNothing()
    {
        var inner = Substitute.For<IGraphWorkflowStore>();
        inner.AppendNodeRunSteeringAsync(Arg.Any<AppendGraphWorkflowSteeringCommand>(), Arg.Any<CancellationToken>()).Returns((GraphWorkflowMutationResult?)null);
        var publisher = Substitute.For<IGraphWorkflowEventPublisher>();
        var store = new PublishingGraphWorkflowStore(inner, publisher, NullLogger<PublishingGraphWorkflowStore>.Instance);

        AssertEx.Null(await store.AppendNodeRunSteeringAsync(Steer()));
        AssertEx.Empty(publisher.ReceivedCalls());
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

        var result = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand
        {
            RunId = RunId,
            ExpectedVersion = GraphWorkflowVersions.Any,
            TargetStatus = GraphWorkflowRunStatus.Running
        });

        AssertEx.Equal(Sequence, result.Sequence, "the commit's own watermark still reaches the caller.");
        await inner.Received(1).TransitionRunAsync(Arg.Any<TransitionGraphWorkflowRunCommand>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Every mutation the store can commit, and the kind the client reacts to.</summary>
    private static IReadOnlyList<Probe> Probes() =>
    [
        new()
        {
            Method = nameof(IGraphWorkflowStore.TransitionRunAsync),
            Kind = GraphWorkflowChangeKind.Run,
            Invoke = store => store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand
            {
                RunId = RunId,
                ExpectedVersion = GraphWorkflowVersions.Any,
                TargetStatus = GraphWorkflowRunStatus.Running
            })
        },
        new()
        {
            Method = nameof(IGraphWorkflowStore.AppendEventAsync),
            Kind = GraphWorkflowChangeKind.Run,
            Invoke = store => store.AppendEventAsync(new AppendGraphWorkflowEventCommand
            {
                RunId = RunId,
                ExpectedVersion = GraphWorkflowVersions.Any,
                EventType = GraphWorkflowEventTypes.NodeInterrupted
            })
        },
        new()
        {
            Method = nameof(IGraphWorkflowStore.TransitionNodeRunAsync),
            Kind = GraphWorkflowChangeKind.Node,
            Invoke = store => store.TransitionNodeRunAsync(NodeRunTransition(GraphWorkflowNodeRunStatus.Running))
        },
        new()
        {
            Method = nameof(IGraphWorkflowStore.TransitionNodeRunAsync),
            Kind = GraphWorkflowChangeKind.Gate,
            Invoke = store => store.TransitionNodeRunAsync(NodeRunTransition(GraphWorkflowNodeRunStatus.WaitingForApproval))
        },

        // An answered pause is the change a watching client most needs told: it is what removes the badge asking for
        // a person, so it announces a gate rather than an ordinary node repaint.
        new()
        {
            Method = nameof(IGraphWorkflowStore.DecideNodeRunAsync),
            Kind = GraphWorkflowChangeKind.Gate,
            Invoke = store => store.DecideNodeRunAsync(Decision())
        },

        // A result published into the run's chat conversation: the node's row changed, so the client repaints it.
        new()
        {
            Method = nameof(IGraphWorkflowStore.MarkNodeRunPublishedAsync),
            Kind = GraphWorkflowChangeKind.Node,
            Invoke = store => store.MarkNodeRunPublishedAsync(RunId, NodeRunId, Guid.NewGuid())
        },

        // A steer the row now lists, and one a tick judged too late: both repaint the node.
        new()
        {
            Method = nameof(IGraphWorkflowStore.AppendNodeRunSteeringAsync),
            Kind = GraphWorkflowChangeKind.Node,
            Invoke = store => store.AppendNodeRunSteeringAsync(Steer())
        },
        new()
        {
            Method = nameof(IGraphWorkflowStore.IgnoreNodeRunSteeringAsync),
            Kind = GraphWorkflowChangeKind.Node,
            Invoke = store => store.IgnoreNodeRunSteeringAsync(RunId, NodeRunId, Guid.NewGuid())
        }
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
        store => store.ListInterruptedNodeRunsAsync(),
        store => store.FindNodeRunByDecisionOperationAsync(RunId, Guid.NewGuid()),
        store => store.ListRunsByConversationAsync(Guid.NewGuid(), limit: 5),
        store => store.FindConversationDecisionAsync(Guid.NewGuid(), Guid.NewGuid()),
        store => store.ListUnpublishedNodeRunsAsync(RunId)
    ];

    private static IReadOnlyList<Func<IGraphWorkflowStore, Task>> SilentWrites() =>
    [
        store => store.CreateDefinitionAsync(new CreateGraphWorkflowDefinitionCommand
        {
            DefinitionId = Guid.NewGuid(),
            Name = "graph",
            GraphJson = "{}",
            NodeCount = 3
        }),
        store => store.UpdateDefinitionAsync(new UpdateGraphWorkflowDefinitionCommand
        {
            DefinitionId = Guid.NewGuid(),
            ExpectedVersion = 1,
            Name = "renamed"
        }),
        store => store.DeleteDefinitionAsync(Guid.NewGuid()),
        store => store.StartRunAsync(new StartGraphWorkflowRunCommand
        {
            RunId = RunId,
            RequestId = Guid.NewGuid(),
            DefinitionId = Guid.NewGuid(),
            DefinitionVersion = 1,
            GraphHash = "graph-hash",
            GraphJson = "{}",
            InputJson = null,
            NodeRuns =
            [
                new GraphWorkflowNodeRunSeed
                {
                    NodeRunId = NodeRunId,
                    NodeKey = "draft",
                    Kind = GraphWorkflowNodeKind.Agent
                }
            ]
        }),
        store => store.ReconcileNonTerminalNodeRunsAsync("the node restarted", [])
    ];

    private static TransitionGraphWorkflowNodeRunCommand NodeRunTransition(GraphWorkflowNodeRunStatus target) =>
        new()
        {
            RunId = RunId,
            NodeRunId = NodeRunId,
            ExpectedVersion = GraphWorkflowVersions.Any,
            TargetStatus = target
        };

    private static AppendGraphWorkflowSteeringCommand Steer() =>
        new()
        {
            RunId = RunId,
            NodeRunId = NodeRunId,
            OperationId = Guid.NewGuid(),
            Message = "focus on the tests",
            SteeredBySubject = null,
            MaxEntries = 5
        };

    private static DecideGraphWorkflowNodeRunCommand Decision() =>
        new()
        {
            RunId = RunId,
            NodeRunId = NodeRunId,
            ExpectedVersion = GraphWorkflowVersions.Any,
            OperationId = Guid.NewGuid(),
            Decision = GraphWorkflowDecisionKind.Approve,
            DecidedBySubject = "operator@localhost",
            OutputJson = """{"status":"succeeded","output":{"decision":"Approve"}}"""
        };

    private static (IGraphWorkflowStore Store, IGraphWorkflowEventPublisher Publisher, IGraphWorkflowStore Inner) Create()
    {
        var inner = Substitute.For<IGraphWorkflowStore>();
        var result = new GraphWorkflowMutationResult
        {
            RunId = RunId,
            Sequence = Sequence
        };
        inner.TransitionRunAsync(Arg.Any<TransitionGraphWorkflowRunCommand>(), Arg.Any<CancellationToken>()).Returns(result);
        inner.TransitionNodeRunAsync(Arg.Any<TransitionGraphWorkflowNodeRunCommand>(), Arg.Any<CancellationToken>()).Returns(result);
        inner.AppendEventAsync(Arg.Any<AppendGraphWorkflowEventCommand>(), Arg.Any<CancellationToken>()).Returns(result);
        inner.DecideNodeRunAsync(Arg.Any<DecideGraphWorkflowNodeRunCommand>(), Arg.Any<CancellationToken>()).Returns(result);
        inner.MarkNodeRunPublishedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(result);
        inner.AppendNodeRunSteeringAsync(Arg.Any<AppendGraphWorkflowSteeringCommand>(), Arg.Any<CancellationToken>()).Returns(result);
        inner.IgnoreNodeRunSteeringAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(result);

        var publisher = Substitute.For<IGraphWorkflowEventPublisher>();
        var store = new PublishingGraphWorkflowStore(inner, publisher, NullLogger<PublishingGraphWorkflowStore>.Instance);
        return (store, publisher, inner);
    }

    private sealed record Probe
    {
        public required string Method { get; init; }

        public required GraphWorkflowChangeKind Kind { get; init; }

        public required Func<IGraphWorkflowStore, Task> Invoke { get; init; }
    }
}
