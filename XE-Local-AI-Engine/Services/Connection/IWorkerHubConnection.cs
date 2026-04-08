namespace XE_Local_AI_Engine.Services.Connection
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using XE_Local_AI_Engine.Models;

    public interface IWorkerHubConnection : IHubMessageSender, IAsyncDisposable
    {
        WorkerConnectionState State { get; }

        event EventHandler<WorkerConnectionStateChangedEventArgs>? StateChanged;

        event EventHandler<InvocationAssignedReceivedEventArgs>? InvocationAssignedReceived;

        event EventHandler<ToolCallResultReceivedEventArgs>? ToolCallResultReceived;

        event EventHandler<DisconnectRequestedReceivedEventArgs>? DisconnectRequestedReceived;

        event EventHandler<ApprovalResolvedReceivedEventArgs>? ApprovalResolvedReceived;

        event EventHandler<InvocationCancelledReceivedEventArgs>? InvocationCancelledReceived;

        Task ConnectAsync(CancellationToken cancellationToken = default);

        Task DisconnectAsync(CancellationToken cancellationToken = default);

        Task SendWorkerHelloAsync(Guid clientNodeId, CancellationToken cancellationToken = default);

        Task SendCapabilitiesAsync(ClientCapabilities capabilities, CancellationToken cancellationToken = default);

        Task SendHeartbeatAsync(Guid clientNodeId, CancellationToken cancellationToken = default);
    }

    public sealed class InvocationAssignedReceivedEventArgs : EventArgs
    {
        public InvocationAssignedReceivedEventArgs(RuntimePackage runtimePackage)
        {
            RuntimePackage = runtimePackage ?? throw new ArgumentNullException(nameof(runtimePackage));
        }

        public RuntimePackage RuntimePackage { get; }
    }

    public sealed class ToolCallResultReceivedEventArgs : EventArgs
    {
        public ToolCallResultReceivedEventArgs(ToolCallResultEvent toolCallResult)
        {
            ToolCallResult = toolCallResult ?? throw new ArgumentNullException(nameof(toolCallResult));
        }

        public ToolCallResultEvent ToolCallResult { get; }
    }

    public sealed class DisconnectRequestedReceivedEventArgs : EventArgs
    {
        public DisconnectRequestedReceivedEventArgs(DisconnectRequestedEvent disconnectRequest)
        {
            DisconnectRequest = disconnectRequest ?? throw new ArgumentNullException(nameof(disconnectRequest));
        }

        public DisconnectRequestedEvent DisconnectRequest { get; }
    }

    public sealed class ApprovalResolvedReceivedEventArgs : EventArgs
    {
        public ApprovalResolvedReceivedEventArgs(ApprovalResolvedEvent approvalResolution)
        {
            ApprovalResolution = approvalResolution ?? throw new ArgumentNullException(nameof(approvalResolution));
        }

        public ApprovalResolvedEvent ApprovalResolution { get; }
    }

    public sealed class InvocationCancelledReceivedEventArgs : EventArgs
    {
        public InvocationCancelledReceivedEventArgs(InvocationCancelledEvent cancellation)
        {
            Cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));
        }

        public InvocationCancelledEvent Cancellation { get; }
    }
}
