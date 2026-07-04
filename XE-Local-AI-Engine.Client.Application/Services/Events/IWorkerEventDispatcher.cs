namespace XE_Local_AI_Engine.Client.Services.Events;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;

/// <summary>
///     Abstraction for worker event dispatcher behavior.
/// </summary>
public interface IWorkerEventDispatcher
{
    /// <summary>
    ///     The live in-flight invocation, or null. This exposes the dispatcher's mutable instance, which the streaming
    ///     path keeps updating under its internal lock — its <see cref="InvocationState.StreamedContent" /> and
    ///     <see cref="InvocationState.StreamedThinkingContent" /> getters lazily materialize a StringBuilder that is NOT
    ///     safe to read while a concurrent append runs. A consumer reading those two members off the dispatcher's lock
    ///     must first take an immutable clone (subscribe to <see cref="InvocationStateChanged" />, whose args are already
    ///     cloned). The lock-safe scalar members (status, counts, timestamps, token totals) are fine to read directly.
    /// </summary>
    InvocationState? CurrentInvocation { get; }

    bool IsAcceptingRemoteInvocations { get; }

    event EventHandler<InvocationStateChangedEventArgs>? InvocationStateChanged;

    /// <summary>
    ///     Raised once per tool-call lifecycle transition (requested/completed). The local chat stream subscribes
    ///     to surface these as <c>tool-call-requested</c>/<c>tool-call-completed</c> stream events alongside the
    ///     content deltas; the platform-served path does not consume it.
    /// </summary>
    event EventHandler<ToolCallLifecycleChangedEventArgs>? ToolCallLifecycleChanged;

    void StopAcceptingRemoteInvocations();

    Task DispatchInvocationAssignedAsync(EncryptedRuntimePackageDto package);

    Task DispatchInvocationAssignedV2Async(InvocationAssignedEnvelope envelope);

    Task DispatchToolCallResultAsync(ToolCallResultEvent evt);

    Task DispatchDisconnectRequestedAsync(DisconnectRequestedEvent evt);

    Task DispatchApprovalResolvedAsync(ApprovalResolvedEvent evt);

    Task DispatchInvocationCancelledAsync(InvocationCancelledEvent evt);

    /// <summary>
    ///     Reports a local invocation assignment, queueing behind any in-flight invocation (local or platform)
    ///     instead of throwing when busy. The returned lease holds the shared invocation slot until disposed,
    ///     which the caller must do when the local run terminates. Cancelling <paramref name="cancellationToken" />
    ///     while the turn is still queued aborts the wait.
    /// </summary>
    Task<IAsyncDisposable> ReportInvocationAssignedAsync(RuntimePackage package, CancellationToken cancellationToken = default);

    Task ReportInvocationStreamChunkAsync(Guid invocationId, string chunk);

    Task ReportInvocationThinkingChunkAsync(Guid invocationId, string chunk);

    Task ReportInvocationCompletedAsync(Guid invocationId, int? inputTokens = null, int? outputTokens = null, int? totalTokens = null, int? reasoningTokens = null, long? generationDurationMs = null);

    Task ReportInvocationFailedAsync(Guid invocationId, string failureMessage, FailureCategory failureCategory);

    Task ReportToolCallRequestedAsync(ToolCallRequestPayload payload);

    Task ReportApprovalRequestedAsync(ApprovalRequestPayload payload);

    /// <summary>
    ///     Reports a tool-call lifecycle transition (requested or completed) for the in-flight invocation, raising
    ///     <see cref="ToolCallLifecycleChanged" /> so a subscribed local chat stream can fan it out.
    /// </summary>
    Task ReportToolCallLifecycleAsync(ToolCallLifecyclePayload payload);
}
