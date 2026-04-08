namespace XE_Local_AI_Engine.Services.Events
{
    using System;
    using System.Threading.Tasks;
    using XE_Local_AI_Engine.Models;

    public interface IWorkerEventDispatcher
    {
        event EventHandler<InvocationStateChangedEventArgs>? InvocationStateChanged;

        InvocationState? CurrentInvocation { get; }

        Task DispatchInvocationAssignedAsync(RuntimePackage package);

        Task DispatchToolCallResultAsync(ToolCallResultEvent evt);

        Task DispatchDisconnectRequestedAsync(DisconnectRequestedEvent evt);

        Task DispatchApprovalResolvedAsync(ApprovalResolvedEvent evt);

        Task DispatchInvocationCancelledAsync(InvocationCancelledEvent evt);
    }

    public sealed class InvocationStateChangedEventArgs : EventArgs
    {
        public InvocationStateChangedEventArgs(InvocationState state)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
        }

        public InvocationState State { get; }
    }
}
