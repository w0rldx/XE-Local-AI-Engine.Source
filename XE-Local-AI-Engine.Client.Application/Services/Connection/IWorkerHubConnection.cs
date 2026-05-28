namespace XE_Local_AI_Engine.Client.Services.Connection;

using XE_Local_AI_Engine.Client.Models;

public interface IWorkerHubConnection : IHubMessageSender, IAsyncDisposable
{
    WorkerConnectionState State { get; }

    event EventHandler<WorkerConnectionStateChangedEventArgs>? StateChanged;

    event EventHandler<InvocationAssignedReceivedEventArgs>? InvocationAssignedReceived;

    event EventHandler<ToolCallResultReceivedEventArgs>? ToolCallResultReceived;

    event EventHandler<DisconnectRequestedReceivedEventArgs>? DisconnectRequestedReceived;

    event EventHandler<ApprovalResolvedReceivedEventArgs>? ApprovalResolvedReceived;

    event EventHandler<InvocationCancelledReceivedEventArgs>? InvocationCancelledReceived;

    event EventHandler<ConversationPurgedReceivedEventArgs>? ConversationPurgedReceived;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task SendWorkerHelloAsync(Guid clientNodeId, CancellationToken cancellationToken = default);

    Task SendWorkerKeyRegisteredAsync(Guid keyId, string publicKey, string popSignature, string popChallenge, CancellationToken cancellationToken = default);

    Task SendCapabilitiesAsync(ClientCapabilities capabilities, CancellationToken cancellationToken = default);

    Task SendHeartbeatAsync(Guid clientNodeId, CancellationToken cancellationToken = default);
}
