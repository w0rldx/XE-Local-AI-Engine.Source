namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The notification seam. It wraps the store rather than living at each call site in the runtime because a missed
///     call site is a pane that silently stops updating, and no test would notice — so these assert that the wrapper
///     announces every mutation, with the kind the client reacts to.
/// </summary>
public sealed class PublishingDevWorkflowStoreTests
{
    private static readonly Guid RunId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid NodeRunId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Test]
    public async Task ARunTransition_AnnouncesTheRunWithTheWatermarkItsCommitAllocated()
    {
        var (store, inner, publisher) = Create();
        inner.TransitionRunAsync(Arg.Any<TransitionDevWorkflowRunCommand>(), Arg.Any<CancellationToken>()).Returns(Result(sequence: 12));

        _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(RunId, DevWorkflowVersions.Any, DevWorkflowRunStatus.Running))
                       .ConfigureAwait(false);

        await publisher.Received(1).PublishAsync(RunId, 12, DevWorkflowChangeKind.Run, Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A node run entering a human wait is the one status move a client does more than repaint for, so it is the
    ///     one that carries the gate kind.
    /// </summary>
    [Test]
    [Arguments(DevWorkflowNodeRunStatus.WaitingForApproval, DevWorkflowChangeKind.Gate)]
    [Arguments(DevWorkflowNodeRunStatus.Blocked, DevWorkflowChangeKind.Gate)]
    [Arguments(DevWorkflowNodeRunStatus.Running, DevWorkflowChangeKind.Node)]
    [Arguments(DevWorkflowNodeRunStatus.Succeeded, DevWorkflowChangeKind.Node)]
    public async Task ANodeRunTransition_AnnouncesTheKindTheClientReactsTo(DevWorkflowNodeRunStatus target, DevWorkflowChangeKind expected)
    {
        var (store, inner, publisher) = Create();
        inner.TransitionNodeRunAsync(Arg.Any<TransitionDevWorkflowNodeRunCommand>(), Arg.Any<CancellationToken>()).Returns(Result(sequence: 7));

        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(RunId, NodeRunId, DevWorkflowVersions.Any, target)).ConfigureAwait(false);

        await publisher.Received(1).PublishAsync(RunId, 7, expected, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnArtifactAppend_AnnouncesTheArtifactFeed()
    {
        var (store, inner, publisher) = Create();
        inner.AppendArtifactAsync(Arg.Any<AppendDevWorkflowArtifactCommand>(), Arg.Any<CancellationToken>()).Returns(Result(sequence: 3));

        _ = await store.AppendArtifactAsync(new AppendDevWorkflowArtifactCommand(RunId,
                            Guid.NewGuid(),
                            NodeRunId,
                            DevWorkflowVersions.Any,
                            Guid.NewGuid(),
                            DevWorkflowArtifactKind.Plan,
                            "plan.md",
                            "text/markdown",
                            "sha",
                            SizeBytes: 4,
                            "reference"))
                       .ConfigureAwait(false);

        await publisher.Received(1).PublishAsync(RunId, 3, DevWorkflowChangeKind.Artifact, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ARecordedDecision_AnnouncesTheGate()
    {
        var (store, inner, publisher) = Create();
        inner.RecordDecisionAsync(Arg.Any<RecordDevWorkflowDecisionCommand>(), Arg.Any<CancellationToken>()).Returns(Result(sequence: 21));

        _ = await store.RecordDecisionAsync(new RecordDevWorkflowDecisionCommand(RunId,
                            Guid.NewGuid(),
                            NodeRunId,
                            DevWorkflowVersions.Any,
                            Guid.NewGuid(),
                            DevWorkflowDecisionKind.Approve))
                       .ConfigureAwait(false);

        await publisher.Received(1).PublishAsync(RunId, 21, DevWorkflowChangeKind.Gate, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AReadAnnouncesNothing()
    {
        var (store, inner, publisher) = Create();
        inner.ListNodeRunsAsync(RunId, Arg.Any<CancellationToken>()).Returns([]);

        _ = await store.ListNodeRunsAsync(RunId).ConfigureAwait(false);

        AssertEx.Empty(publisher.ReceivedCalls());
    }

    private static (IDevWorkflowStore Store, IDevWorkflowStore Inner, IDevWorkflowEventPublisher Publisher) Create()
    {
        var inner = Substitute.For<IDevWorkflowStore>();
        var publisher = Substitute.For<IDevWorkflowEventPublisher>();
        return (new PublishingDevWorkflowStore(inner, publisher), inner, publisher);
    }

    private static DevWorkflowMutationResult Result(long sequence) =>
        new(RunId, sequence, Version: 2, DevWorkflowRunStatus.Running, GraphRevision: 0);
}
