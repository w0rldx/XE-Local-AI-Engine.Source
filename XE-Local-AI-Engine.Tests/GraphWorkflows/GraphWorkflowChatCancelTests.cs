namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Chat;

/// <summary>
///     The conversation-delete cancel losing a version race: the run moved between the run service's read and its write.
///     Substitutes, because that window cannot be opened deterministically on the real store.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class GraphWorkflowChatCancelTests
{
    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly Guid RunId = Guid.NewGuid();

    [Test]
    public async Task CancelBoundRun_AfterAVersionBump_ReReadsAndCancels()
    {
        var (service, runs) = Create(GraphWorkflowRunStatus.Running, GraphWorkflowRunStatus.Running);
        runs.CancelAsync(RunId, Arg.Any<CancellationToken>())
            .Returns(_ => throw new GraphWorkflowInvalidTransitionException("stale version"), _ => Task.FromResult(Detail()));

        await service.CancelBoundRunAsync(ConversationId);

        _ = await runs.Received(2).CancelAsync(RunId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CancelBoundRun_WhenTheRunIsAlreadyCancellingOnTheReRead_StopsAsking()
    {
        var (service, runs) = Create(GraphWorkflowRunStatus.Running, GraphWorkflowRunStatus.Cancelling);
        runs.CancelAsync(RunId, Arg.Any<CancellationToken>()).ThrowsAsync(new GraphWorkflowInvalidTransitionException("stale version"));

        await service.CancelBoundRunAsync(ConversationId);

        _ = await runs.Received(1).CancelAsync(RunId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CancelBoundRun_ThatKeepsLosing_GivesUpAfterThreeAttempts()
    {
        var (service, runs) = Create(GraphWorkflowRunStatus.Running);
        runs.CancelAsync(RunId, Arg.Any<CancellationToken>()).ThrowsAsync(new GraphWorkflowInvalidTransitionException("stale version"));

        await service.CancelBoundRunAsync(ConversationId);

        _ = await runs.Received(3).CancelAsync(RunId, Arg.Any<CancellationToken>());
    }

    private static (GraphWorkflowChatService Service, IGraphWorkflowRunService Runs) Create(GraphWorkflowRunStatus first, params GraphWorkflowRunStatus[] then)
    {
        var store = Substitute.For<IGraphWorkflowStore>();
        store.ListRunsByConversationAsync(ConversationId, Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(Listed(first), [.. then.Select(Listed)]);
        var runs = Substitute.For<IGraphWorkflowRunService>();
        var service = new GraphWorkflowChatService(store,
            runs,
            Substitute.For<INodeChatPersistenceService>(),
            Substitute.For<INodeChatMutationGuard>(),
            Substitute.For<IInvocationResumeRegistry>(),
            Substitute.For<IConversationUploadedFileStore>(),
            Options.Create(new GraphWorkflowOptions()),
            Options.Create(new SecurityOptions()),
            TimeProvider.System);
        return (service, runs);
    }

    private static IReadOnlyList<GraphWorkflowRunSnapshot> Listed(GraphWorkflowRunStatus status) =>
    [
        new()
        {
            Id = RunId,
            RequestId = Guid.NewGuid(),
            DefinitionId = Guid.NewGuid(),
            DefinitionVersion = 1,
            GraphHash = "hash",
            Status = status,
            FailureClass = GraphWorkflowFailureClass.None,
            GraphJson = "{}",
            InputJson = null,
            OutputJson = null,
            Seq = 1,
            Version = 1,
            CancelRequestedAtUtc = null,
            StartedAtUtc = 1,
            CompletedAtUtc = null,
            CreatedAtUtc = 1,
            ConversationId = ConversationId
        }
    ];

    private static GraphWorkflowRunDetail Detail() =>
        new()
        {
            Run = Listed(GraphWorkflowRunStatus.Cancelling)[0],
            NodeRuns = []
        };
}
