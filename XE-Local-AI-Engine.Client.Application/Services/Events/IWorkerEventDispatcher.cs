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

    /// <summary>
    ///     Raised once per <c>ask_user</c> question the in-flight invocation is paused on. The local chat stream
    ///     subscribes to surface these as <c>question-requested</c> stream events so the browser can render the
    ///     question card. Unlike an approval, the payload carries the QUESTIONS themselves — a client cannot render an
    ///     answerable prompt from a correlation id alone, which is also what makes the reconnect replay possible.
    /// </summary>
    event EventHandler<UserQuestionRequestedChangedEventArgs>? UserQuestionRequestedChanged;

    void StopAcceptingRemoteInvocations();

    Task DispatchInvocationAssignedAsync(EncryptedRuntimePackageDto package);

    Task DispatchInvocationAssignedV2Async(InvocationAssignedEnvelope envelope);

    Task DispatchToolCallResultAsync(ToolCallResultEvent evt);

    Task DispatchDisconnectRequestedAsync(DisconnectRequestedEvent evt);

    /// <summary>
    ///     Carries an operator's approval decision to the runner. <paramref name="scope" /> is Application-internal and
    ///     defaults to <see cref="ApprovalScope.Once" />: the platform hub path passes the wire
    ///     <see cref="ApprovalResolvedEvent" /> unchanged (session scope is a loopback-only concept and deliberately
    ///     absent from that cross-repo contract), while the loopback resolve endpoint can ask for a decision that lasts
    ///     the rest of the conversation.
    /// </summary>
    Task DispatchApprovalResolvedAsync(ApprovalResolvedEvent evt, ApprovalScope scope = ApprovalScope.Once);

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

    /// <param name="finishReason">
    ///     Why the model stopped generating (<c>ChatFinishReason.Value</c>), or null when the provider reported none.
    ///     Recorded on the terminal state as <see cref="Events.InvocationState.FinishReason" />; it never changes the
    ///     status, so a turn cut off at the token budget still completes.
    /// </param>
    /// <param name="throughput">
    ///     The turn's separated throughput facts (TTFT, pp/tg tokens and milliseconds), or null when the provider
    ///     reported none. Recorded on the terminal state as <see cref="Events.InvocationState.Throughput" />.
    /// </param>
    Task ReportInvocationCompletedAsync(Guid invocationId,
        int? inputTokens = null,
        int? outputTokens = null,
        int? totalTokens = null,
        int? reasoningTokens = null,
        long? generationDurationMs = null,
        string? finishReason = null,
        InvocationThroughput? throughput = null);

    Task ReportInvocationFailedAsync(Guid invocationId, string failureMessage, FailureCategory failureCategory);

    /// <summary>
    ///     Records the turn's tool-schema token estimate on the invocation state so the terminalize write can persist it
    ///     onto the run-envelope row. Reported by the runner immediately BEFORE each terminal report, on the completed,
    ///     cancelled and failed paths alike, because the estimate is as interesting on a turn that ran out of context as
    ///     on one that finished. Counts only — no tool name ever reaches this seam.
    /// </summary>
    /// <param name="toolSchemaTokens">Cumulative estimate across the turn's provider rounds.</param>
    /// <param name="maxToolSchemaTokens">The largest single round's estimate.</param>
    Task ReportToolSchemaTokensAsync(Guid invocationId, long? toolSchemaTokens, int? maxToolSchemaTokens);

    /// <summary>
    ///     Records the turn-scoped facts that belong on the run-envelope row rather than on the message: how long the
    ///     turn spent making a LOCAL runtime ready (launching <c>llama-server</c> and loading the model), and the token
    ///     usage SUMMED over the turn's provider rounds. Reported by the runner on the completed, cancelled and failed
    ///     paths alike, for the same reason as <see cref="ReportToolSchemaTokensAsync" />: a cold start and the tokens
    ///     already paid for are most interesting on the turn that paid for them, however that turn ended. Durations and
    ///     counts only — no model identity or prompt text reaches this seam.
    /// </summary>
    /// <param name="modelReadinessMs">Milliseconds spent in the warm phase, or null when no local warm happened.</param>
    /// <param name="usage">
    ///     The turn's summed usage, or null when the provider reported none. Deliberately separate from the counts on
    ///     <see cref="ReportInvocationCompletedAsync" />, which stay the LAST round's — see <see cref="TurnUsageTotals" />.
    /// </param>
    Task ReportTurnTelemetryAsync(Guid invocationId, long? modelReadinessMs, TurnUsageTotals? usage);

    /// <summary>
    ///     Records what reasoning effort <c>auto</c> resolved to on the invocation state, so the terminalize write can
    ///     persist it onto the run-envelope row. Reported by the runner immediately after the dispatch, and only on a
    ///     turn that was authored <c>auto</c> — every other turn leaves both members null. Category labels only: the
    ///     tier and the authored effort, never a reason code, a signal value or any message text.
    /// </summary>
    /// <param name="dispatchedTier">The resolved tier's name.</param>
    /// <param name="authoredEffort">The effort the turn was authored with (<c>auto</c>).</param>
    Task ReportEffortDispatchAsync(Guid invocationId, string dispatchedTier, string authoredEffort);

    /// <summary>
    ///     Records the model that ACTUALLY served the turn, when it is not the one the runtime package named. Reported
    ///     by the runner only once an admitted <c>auto</c> model swap has run its send, because
    ///     <see cref="Events.InvocationState.ModelUsed" /> is seeded from the package and is what BOTH the persisted
    ///     message row and the run envelope's provider attribution are read from — without this a swapped turn is
    ///     recorded against a model that never saw it, and the measurement queries attribute the fast model's tokens
    ///     and latency to the big one. A turn that falls back to the original model never reports, so its seeded value
    ///     stands.
    /// </summary>
    /// <param name="modelUsed">The served model id.</param>
    Task ReportServedModelAsync(Guid invocationId, string modelUsed);

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

    /// <summary>
    ///     Reports a pending <c>ask_user</c> question for the in-flight invocation: records it on the invocation state
    ///     (so a reconnecting browser can be replayed the still-unanswered prompt) and raises
    ///     <see cref="UserQuestionRequestedChanged" /> for the local chat stream.
    /// </summary>
    Task ReportUserQuestionAsync(UserQuestionLifecyclePayload payload);

    /// <summary>
    ///     Feeds the operator's answers into the waiting turn and clears the pending-question slot. Idempotent: an
    ///     unknown or already-resolved request id logs and no-ops, so a duplicate or stale post never faults the turn.
    /// </summary>
    Task DispatchUserQuestionAnsweredAsync(UserQuestionAnsweredEvent evt);
}

/// <summary>
///     A turn's token usage SUMMED over its provider rounds — what the turn COST. A tool-calling turn is several
///     provider requests inside one run, each reporting its own usage, so the totals add up.
///     Deliberately NOT the same numbers as <see cref="InvocationState.InputTokens" /> and friends, which stay the LAST
///     round's: a round's prompt is the whole conversation so far, so the final round's input count is what the model's
///     context actually HELD, and that is the occupancy the chat meter reads off the assistant message. Cost sums;
///     occupancy does not. These totals are persisted onto the run-envelope row instead.
/// </summary>
public sealed record TurnUsageTotals(int? InputTokens, int? OutputTokens, int? TotalTokens, int? ReasoningTokens);
