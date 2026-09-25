namespace XE_Local_AI_Engine.Tests.Hubs;

using Microsoft.AspNetCore.SignalR;
using NSubstitute;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Services.Transcription;

[Category(TestCategories.Unit)]
public sealed class TranscriptionEventPublisherTests
{
    [Test]
    [Arguments(LiveEndReason.Completed, "Completed", null)]
    [Arguments(LiveEndReason.Cancelled, "Cancelled", null)]
    [Arguments(LiveEndReason.Abandoned, "Abandoned", null)]
    [Arguments(LiveEndReason.NeverAttached, "Abandoned", "live-never-attached")]
    [Arguments(LiveEndReason.Failed, "Failed", "live-failed")]
    public async Task PublishStatusAsync_SendsTheWireStatusAndTheErrorCodeTheBrowserNames(LiveEndReason reason, string status, string? errorCode)
    {
        var sessionId = Guid.NewGuid();
        var proxy = Substitute.For<IClientProxy>();
        var clients = Substitute.For<IHubClients>();
        clients.Group($"transcription-session-{sessionId:N}").Returns(proxy);
        var hubContext = Substitute.For<IHubContext<TranscriptionHub>>();
        hubContext.Clients.Returns(clients);
        var publisher = new TranscriptionEventPublisher(hubContext);

        await publisher.PublishStatusAsync(sessionId, reason, CancellationToken.None);

        // Literals, not enum names: the SPA switches on these exact strings to tell "no audio arrived" from "you left".
        await proxy.Received(1)
                   .SendCoreAsync("transcriptionSessionStatusChanged",
                       Arg.Is<object?[]>(arguments => arguments.Length == 1
                                                      && arguments[0] is TranscriptionSessionStatusPush
                                                      && ((TranscriptionSessionStatusPush)arguments[0]!).SessionId == sessionId
                                                      && ((TranscriptionSessionStatusPush)arguments[0]!).Status == status
                                                      && ((TranscriptionSessionStatusPush)arguments[0]!).ErrorCode == errorCode),
                       Arg.Any<CancellationToken>());
    }
}
