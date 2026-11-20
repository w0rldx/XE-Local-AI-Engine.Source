namespace XE_Local_AI_Engine.Tests.Hubs;

using Microsoft.AspNetCore.SignalR;
using NSubstitute;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class WorkSessionEventPublisherTests
{
    [Test]
    [Arguments(WorkSessionChangeKind.Status, "status")]
    [Arguments(WorkSessionChangeKind.Step, "step")]
    [Arguments(WorkSessionChangeKind.Task, "task")]
    [Arguments(WorkSessionChangeKind.Finding, "finding")]
    [Arguments(WorkSessionChangeKind.Artifact, "artifact")]
    [Arguments(WorkSessionChangeKind.Checkpoint, "checkpoint")]
    public async Task PublishAsync_SendsTheChangeToTheSessionGroupWithALowercaseKind(WorkSessionChangeKind kind, string expected)
    {
        var sessionId = Guid.NewGuid();
        var proxy = Substitute.For<IClientProxy>();
        var clients = Substitute.For<IHubClients>();
        clients.Group($"work-session-{sessionId:N}").Returns(proxy);
        var hubContext = Substitute.For<IHubContext<WorkSessionHub>>();
        hubContext.Clients.Returns(clients);
        var publisher = new WorkSessionEventPublisher(hubContext);

        await publisher.PublishAsync(sessionId, sequence: 42, kind);

        // Asserted against the literal, not against kind.ToString(): the client switches on these strings, so a
        // capitalized name would match no arm and silently stop updating the pane — and nothing else would catch it.
        await proxy.Received(1)
                   .SendCoreAsync("workSessionChanged",
                       Arg.Is<object?[]>(arguments => arguments.Length == 1
                                                      && arguments[0] is WorkSessionChanged
                                                      && ((WorkSessionChanged)arguments[0]!).SessionId == sessionId
                                                      && ((WorkSessionChanged)arguments[0]!).Seq == 42
                                                      && ((WorkSessionChanged)arguments[0]!).Kind == expected),
                       Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PublishAsync_NeverSendsToAnotherSessionsGroup()
    {
        var sessionId = Guid.NewGuid();
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(Substitute.For<IClientProxy>());
        var hubContext = Substitute.For<IHubContext<WorkSessionHub>>();
        hubContext.Clients.Returns(clients);
        var publisher = new WorkSessionEventPublisher(hubContext);

        await publisher.PublishAsync(sessionId, sequence: 1, WorkSessionChangeKind.Status);

        var own = $"work-session-{sessionId:N}";
        _ = clients.Received(1).Group(own);
        _ = clients.DidNotReceive().Group(Arg.Is<string>(group => group != own));
    }

    /// <summary>The operations feed watches no session, so a status change, and only a status change, also goes to everyone.</summary>
    [Test]
    [Arguments(WorkSessionChangeKind.Status, true)]
    [Arguments(WorkSessionChangeKind.Step, false)]
    [Arguments(WorkSessionChangeKind.Task, false)]
    [Arguments(WorkSessionChangeKind.Finding, false)]
    [Arguments(WorkSessionChangeKind.Artifact, false)]
    [Arguments(WorkSessionChangeKind.Checkpoint, false)]
    public async Task PublishAsync_BroadcastsTheSessionIdOnAStatusChangeOnly(WorkSessionChangeKind kind, bool broadcast)
    {
        var sessionId = Guid.NewGuid();
        var all = Substitute.For<IClientProxy>();
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(Substitute.For<IClientProxy>());
        clients.All.Returns(all);
        var hubContext = Substitute.For<IHubContext<WorkSessionHub>>();
        hubContext.Clients.Returns(clients);
        var publisher = new WorkSessionEventPublisher(hubContext);

        await publisher.PublishAsync(sessionId, sequence: 7, kind);

        await all.Received(broadcast ? 1 : 0)
                 .SendCoreAsync("workSessionStatusChanged",
                     Arg.Is<object?[]>(arguments => arguments.Length == 1
                                                    && arguments[0] is WorkSessionStatusChanged
                                                    && ((WorkSessionStatusChanged)arguments[0]!).SessionId == sessionId),
                     Arg.Any<CancellationToken>());
        AssertEx.Equal(broadcast ? 1 : 0, all.ReceivedCalls().Count());
    }
}
