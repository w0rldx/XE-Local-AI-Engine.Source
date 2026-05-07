namespace XE_Local_AI_Engine.Client.Services.Events;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;

public interface IWorkerEventDispatcher
{
    InvocationState? CurrentInvocation { get; }
    event EventHandler<InvocationStateChangedEventArgs>? InvocationStateChanged;

    Task DispatchInvocationAssignedAsync(EncryptedRuntimePackageDto package);

    Task DispatchInvocationAssignedV2Async(InvocationAssignedEnvelope envelope);

    Task DispatchToolCallResultAsync(ToolCallResultEvent evt);

    Task DispatchDisconnectRequestedAsync(DisconnectRequestedEvent evt);

    Task DispatchApprovalResolvedAsync(ApprovalResolvedEvent evt);

    Task DispatchInvocationCancelledAsync(InvocationCancelledEvent evt);

    Task ReportInvocationAssignedAsync(RuntimePackage package);

    Task ReportInvocationStreamChunkAsync(Guid invocationId, string chunk);

    Task ReportInvocationThinkingChunkAsync(Guid invocationId, string chunk);

    Task ReportInvocationCompletedAsync(Guid invocationId);

    Task ReportInvocationFailedAsync(Guid invocationId, string failureMessage, FailureCategory failureCategory);

    Task ReportToolCallRequestedAsync(ToolCallRequestPayload payload);

    Task ReportApprovalRequestedAsync(ApprovalRequestPayload payload);
}
