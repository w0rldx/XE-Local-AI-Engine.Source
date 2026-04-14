namespace XE_Local_AI_Engine.Client.Services.Events;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Events;

public interface IWorkerEventDispatcher
{
    InvocationState? CurrentInvocation { get; }
    event EventHandler<InvocationStateChangedEventArgs>? InvocationStateChanged;

    Task DispatchInvocationAssignedAsync(RuntimePackage package);

    Task DispatchToolCallResultAsync(ToolCallResultEvent evt);

    Task DispatchDisconnectRequestedAsync(DisconnectRequestedEvent evt);

    Task DispatchApprovalResolvedAsync(ApprovalResolvedEvent evt);

    Task DispatchInvocationCancelledAsync(InvocationCancelledEvent evt);
}
