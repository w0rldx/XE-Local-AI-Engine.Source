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
    ///     path keeps updating under its internal lock. Its <see cref="InvocationState.StreamedContent" /> and
    ///     <see cref="InvocationState.StreamedThinkingContent" /> getters materialize from an IMMUTABLE append-only
    ///     accumulator, so reading them off the lock is memory-safe (no torn buffer) — but it can still observe a
    ///     transient value while an append is in flight. A consumer that needs a consistent point-in-time view should
    ///     subscribe to <see cref="InvocationStateChanged" /> (whose args are an immutable clone). The scalar members
    ///     (status, counts, timestamps, token totals) are fine to read directly.
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

    /// <summary>
    ///     Raised once per non-fatal turn notice (model substitution, tool disabled, history truncated). The local
    ///     chat stream subscribes to surface these as <c>assistant-notice</c> stream events alongside the content
    ///     deltas and tool-call lifecycle; the platform-served path does not consume it.
    /// </summary>
    event EventHandler<TurnNoticeChangedEventArgs>? TurnNoticeChanged;

    /// <summary>
    ///     Raised once per tool-approval request the in-flight invocation is paused on. The local chat stream
    ///     subscribes to surface these as <c>approval-requested</c> stream events so the browser can render
    ///     Approve/Deny controls on the waiting tool-call card; the platform-served path (which resolves approvals over
    ///     the worker hub) does not consume it.
    /// </summary>
    event EventHandler<ApprovalRequestedChangedEventArgs>? ApprovalRequestedChanged;

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

    /// <summary>
    ///     Reports the runtime phase of the in-flight turn (preparing runtime → loading model → generating). The
    ///     cold-load phases fire BEFORE the stream-idle watchdog is armed, so the UI can render a legitimate load rather
    ///     than an apparent hang while a large local model warms. A no-op when the id is not the current invocation.
    /// </summary>
    Task ReportInvocationPhaseAsync(Guid invocationId, InvocationRuntimePhase phase);

    Task ReportInvocationCompletedAsync(Guid invocationId, int? inputTokens = null, int? outputTokens = null, int? totalTokens = null, int? reasoningTokens = null, long? generationDurationMs = null);

    Task ReportInvocationFailedAsync(Guid invocationId, string failureMessage, FailureCategory failureCategory);

    Task ReportToolCallRequestedAsync(ToolCallRequestPayload payload);

    Task ReportApprovalRequestedAsync(ApprovalRequestPayload payload);

    /// <summary>
    ///     Reports a tool-call lifecycle transition (requested or completed) for the in-flight invocation, raising
    ///     <see cref="ToolCallLifecycleChanged" /> so a subscribed local chat stream can fan it out.
    /// </summary>
    Task ReportToolCallLifecycleAsync(ToolCallLifecyclePayload payload);

    /// <summary>
    ///     Reports a non-fatal turn notice for the in-flight invocation, raising <see cref="TurnNoticeChanged" /> so a
    ///     subscribed local chat stream can fan it out.
    /// </summary>
    Task ReportTurnNoticeAsync(TurnNoticePayload payload);

    /// <summary>
    ///     Reports a tool-approval request for the in-flight invocation, raising <see cref="ApprovalRequestedChanged" />
    ///     so a subscribed local chat stream can surface it as an <c>approval-requested</c> stream event. Distinct from
    ///     <see cref="ReportApprovalRequestedAsync" />, which updates the invocation-monitor state; this fans the
    ///     request out to the local browser so the operator can resolve it.
    /// </summary>
    Task ReportApprovalLifecycleAsync(ApprovalLifecyclePayload payload);
}
