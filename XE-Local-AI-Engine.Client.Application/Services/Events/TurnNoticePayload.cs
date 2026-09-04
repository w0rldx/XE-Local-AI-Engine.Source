namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     A single non-fatal, in-turn notice for an invocation: a behavior the runner would otherwise only log
///     server-side (a model substitution, a tool disabled after repeated invalid calls, or a history truncation) is
///     instead surfaced to the chat client as a sanitized, structured notice. Mirrors
///     <see cref="ToolCallLifecyclePayload" />'s shape and fan-out so the local send/regenerate/resume paths cannot
///     drift. <see cref="Message" /> is always a fixed, path-free, user-facing string; nothing here ever carries a raw
///     exception, stack trace, or file path.
/// </summary>
public sealed record TurnNoticePayload
{
    public required Guid InvocationId { get; init; }

    public required TurnNoticeKind Kind { get; init; }

    /// <summary>Sanitized, user-facing description of what happened.</summary>
    public required string Message { get; init; }

    /// <summary>Optional sanitized detail (e.g. the substituted model name, or the disabled tool's name).</summary>
    public string? Detail { get; init; }
}

/// <summary>
///     Enumerates the silent-behavior classes surfaced as a <see cref="TurnNoticePayload" />.
/// </summary>
public enum TurnNoticeKind
{
    /// <summary>The requested model could not be verified; the turn ran on the node's fallback default instead.</summary>
    ModelSubstituted = 0,

    /// <summary>A tool was disabled for the rest of this turn after repeated invalid-argument calls.</summary>
    ToolDisabled = 1,

    /// <summary>Conversation history was trimmed (messages dropped and/or tool results truncated) to fit the context budget.</summary>
    HistoryTruncated = 2,

    /// <summary>
    ///     Conversation attachments (and node-local file tools) were withheld from a CLOUD-hosted effective model because
    ///     the operator has not opted in to exposing node-local private data to cloud providers
    ///     (<c>KnowledgeBase:AllowCloudModelAccess</c>). <see cref="TurnNoticePayload.Detail" /> names the effective model.
    /// </summary>
    AttachmentsWithheld = 3,

    /// <summary>
    ///     Knowledge-base grounding was requested for this plain-chat turn but withheld from a CLOUD-hosted
    ///     effective model because the operator has not opted in to exposing node-local private data to cloud providers
    ///     (<c>KnowledgeBase:AllowCloudModelAccess</c>) — the same egress gate as attachments. The turn still runs, just
    ///     without knowledge-base context. <see cref="TurnNoticePayload.Detail" /> names the effective model.
    /// </summary>
    KnowledgeWithheld = 4,

    /// <summary>
    ///     The bound agent is an Orchestrator but its orchestration did not compile for this turn (invalid topology, a
    ///     model that cannot call tools, a missing triage, or too few capable participants), so the turn ran as a single
    ///     agent. Without this the degrade was visible only in a server log.
    ///     <see cref="TurnNoticePayload.Detail" /> carries the
    ///     <c>OrchestrationDegradationReason</c> name.
    /// </summary>
    OrchestrationDegraded = 5,

    /// <summary>
    ///     Some of the agent's tools were held back from the model this turn to save context, and the model can list
    ///     and use them by calling <c>list_tools</c>. Counts only — the notice never names a tool. Hiding a tool is a
    ///     context-budget optimisation and never an authorisation change: a held-back tool the model names still
    ///     executes under exactly the same approval rules.
    /// </summary>
    ToolsFiltered = 6,

    /// <summary>
    ///     The turn was authored with reasoning effort <c>auto</c> and the node resolved it into a concrete tier for
    ///     this turn — a different reasoning depth, and possibly a different (node-local, smaller) model.
    ///     Deliberately silent on the common NORMAL, no-swap case: a notice on every ordinary turn is noise.
    ///     <see cref="TurnNoticePayload.Detail" /> carries the stable kebab-case dispatch reason code, which names a
    ///     RULE and never a signal value — no message length, no conversation depth, no score, and never any message
    ///     text.
    /// </summary>
    EffortDispatched = 7
}
