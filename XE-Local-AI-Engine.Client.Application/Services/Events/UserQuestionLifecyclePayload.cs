namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     A pending <c>ask_user</c> question surfaced for the in-flight invocation. Deliberately shaped like
///     <see cref="ApprovalLifecyclePayload" /> — same fan-out, same correlation keys — so the local
///     send/regenerate/resume paths cannot drift apart. The <see cref="RequestId" /> is the key the browser echoes back
///     to the loopback resolve endpoint to release the waiting turn; <see cref="CallId" /> attaches the question card to
///     the tool-call card the model is waiting on.
///     <para>
///         Unlike an approval — which the client can render from the request id alone — a question is only answerable if
///         the client also has the QUESTIONS. They therefore ride this payload in full, which is what lets a reconnecting
///         browser be handed a still-pending question rather than losing the prompt (see the reconnect replay).
///     </para>
/// </summary>
public sealed record UserQuestionLifecyclePayload
{
    public required Guid InvocationId { get; init; }

    /// <summary>The question request id — the durable key the resolve endpoint echoes back to release the waiting run.</summary>
    public required string RequestId { get; init; }

    /// <summary>
    ///     The tool-call id the question belongs to, so the chat stream attaches the question card to the matching
    ///     tool-call card. Matches the id the <c>tool-call-requested</c> lifecycle event used for the same call.
    /// </summary>
    public required string CallId { get; init; }

    /// <summary>The tool asking the question (always <c>ask_user</c> today; carried so the card labels itself).</summary>
    public required string ToolName { get; init; }

    /// <summary>
    ///     The questions to render, already parsed and validated from the tool call's arguments. Never raw model output:
    ///     parsing rejects a malformed call before it reaches the operator.
    /// </summary>
    public required IReadOnlyList<UserQuestionSpec> Questions { get; init; }
}
