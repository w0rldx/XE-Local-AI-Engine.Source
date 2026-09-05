namespace XE_Local_AI_Engine.Tests.Hubs;

using Microsoft.AspNetCore.SignalR;
using NSubstitute;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class GraphWorkflowEventPublisherTests
{
    [Test]
    [Arguments(GraphWorkflowChangeKind.Run, "run")]
    [Arguments(GraphWorkflowChangeKind.Node, "node")]
    [Arguments(GraphWorkflowChangeKind.Gate, "gate")]
    public async Task PublishAsync_SendsTheChangeToTheRunGroupWithALowercaseKind(GraphWorkflowChangeKind kind, string expected)
    {
        var runId = Guid.NewGuid();
        var proxy = Substitute.For<IClientProxy>();
        var clients = Substitute.For<IHubClients>();
        clients.Group($"graph-workflow-run-{runId:N}").Returns(proxy);
        var hubContext = Substitute.For<IHubContext<GraphWorkflowRunHub>>();
        hubContext.Clients.Returns(clients);
        var publisher = new GraphWorkflowEventPublisher(hubContext);

        await publisher.PublishAsync(runId, sequence: 42, kind).ConfigureAwait(false);

        // Asserted against the literal, not against kind.ToString(): the client switches on these strings, so a
        // capitalised name would match no arm and silently stop updating the view — and nothing else would catch it.
        await proxy.Received(1)
                   .SendCoreAsync("graphWorkflowChanged",
                       Arg.Is<object?[]>(arguments => arguments.Length == 1
                                                      && arguments[0] is GraphWorkflowChanged
                                                      && ((GraphWorkflowChanged)arguments[0]!).RunId == runId
                                                      && ((GraphWorkflowChanged)arguments[0]!).Seq == 42
                                                      && ((GraphWorkflowChanged)arguments[0]!).Kind == expected),
                       Arg.Any<CancellationToken>());
    }

    /// <summary>Renaming or adding an enum member must not be able to change the wire contract silently.</summary>
    [Test]
    public async Task PublishAsync_WithAKindTheWireDoesNotKnow_Throws()
    {
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(Substitute.For<IClientProxy>());
        var hubContext = Substitute.For<IHubContext<GraphWorkflowRunHub>>();
        hubContext.Clients.Returns(clients);
        var publisher = new GraphWorkflowEventPublisher(hubContext);

        _ = await AssertEx.ThrowsAsync<ArgumentOutOfRangeException>(() => publisher.PublishAsync(Guid.NewGuid(), sequence: 1, (GraphWorkflowChangeKind)99))
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task PublishAsync_NeverSendsToAnotherRunsGroup()
    {
        var runId = Guid.NewGuid();
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(Substitute.For<IClientProxy>());
        var hubContext = Substitute.For<IHubContext<GraphWorkflowRunHub>>();
        hubContext.Clients.Returns(clients);
        var publisher = new GraphWorkflowEventPublisher(hubContext);

        await publisher.PublishAsync(runId, sequence: 1, GraphWorkflowChangeKind.Run).ConfigureAwait(false);

        _ = clients.Received(1).Group($"graph-workflow-run-{runId:N}");
        AssertEx.Equal(expected: 1, clients.ReceivedCalls().Count());
    }
}
