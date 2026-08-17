namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     A single tool-approval request surfaced for the in-flight invocation. Mirrors
///     <see cref="ToolCallLifecyclePayload" />'s shape and fan-out so the local send/regenerate/resume paths cannot
///     drift. Distinct from <see cref="XE_Local_AI_Engine.Client.Models.ApprovalRequestPayload" /> (the platform-hub /
///     invocation-monitor contract, which carries only the invocation id, request id, and description): this payload
///     additionally carries the tool-call <see cref="CallId" /> and <see cref="ToolName" /> so the local chat stream can
///     correlate the pending approval to the exact tool-call card the model is waiting on. The <see cref="RequestId" />
///     is the approval request id the browser echoes back to the loopback resolve endpoint to release the run.
/// </summary>
public sealed record ApprovalLifecyclePayload
{
    public required Guid InvocationId { get; init; }

    /// <summary>The approval request id — the durable key the resolve endpoint echoes back to release the waiting run.</summary>
    public required string RequestId { get; init; }

    /// <summary>
    ///     The tool-call id the approval belongs to, so the chat stream attaches the pending-approval state to the
    ///     matching tool-call card. Matches the id the <c>tool-call-requested</c> lifecycle event used for the same call.
    /// </summary>
    public required string CallId { get; init; }

    /// <summary>The tool awaiting approval.</summary>
    public required string ToolName { get; init; }

    /// <summary>Sanitized, user-facing description of what is being requested. Never carries a stack trace or file path.</summary>
    public required string Description { get; init; }

    /// <summary>
    ///     Whether the node can actually REMEMBER an "approve for this session" decision for THIS request — the runner's
    ///     own answer, taken from the same memo-key resolution that would honor the decision, so the card offers the
    ///     button only where the click means something. Strictly better than the tool-catalog boolean the client falls
    ///     back to: the catalog answers at tool-identity level and cannot see the per-CALL narrowings (an imported skill,
    ///     a resource-less <c>read_skill_resource</c>) — nor does it carry the MAF skill tools at all, which is how
    ///     <c>run_skill_script</c> kept offering a session scope the node never honors.
    ///     <see langword="null" /> means "not resolved here" (the platform API-tool path, and a reconnect replay
    ///     rebuilt from <c>InvocationApprovalState</c>), in which case the client keeps its catalog fallback.
    /// </summary>
    public bool? SessionScopeEligible { get; init; }
}
