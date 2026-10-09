namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     A single non-fatal, in-turn notice for an invocation: a behaviour the runner would otherwise only log
///     server-side is instead surfaced to the chat client as a sanitized, structured notice.
/// </summary>
/// <remarks>
///     A model substitution, a tool disabled after repeated invalid calls, a history truncation. Mirrors
///     <see cref="ToolCallLifecyclePayload" />'s shape and fan-out so the local send/regenerate/resume paths cannot
///     drift. <see cref="Message" /> is always a fixed, path-free, user-facing string; nothing here ever carries a raw
///     exception, stack trace, or file path.
/// </remarks>
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
    ///     Conversation attachments (and node-local file tools) were withheld from a CLOUD-hosted effective model.
    /// </summary>
    /// <remarks>
    ///     The operator has not opted in to exposing node-local private data to cloud providers
    ///     (the <c>AllowCloudModelAccess</c> node setting). <see cref="TurnNoticePayload.Detail" /> names the effective model.
    /// </remarks>
    AttachmentsWithheld = 3,

    /// <summary>
    ///     Knowledge-base grounding was requested for this plain-chat turn but withheld from a CLOUD-hosted effective
    ///     model.
    /// </summary>
    /// <remarks>
    ///     The operator has not opted in to exposing node-local private data to cloud providers
    ///     (the <c>AllowCloudModelAccess</c> node setting) — the same egress gate as attachments. The turn still runs, just
    ///     without knowledge-base context. <see cref="TurnNoticePayload.Detail" /> names the effective model.
    /// </remarks>
    KnowledgeWithheld = 4,

    /// <summary>
    ///     The bound agent is an Orchestrator but its orchestration did not compile for this turn, so the turn ran as a
    ///     single agent.
    /// </summary>
    /// <remarks>
    ///     Invalid topology, a model that cannot call tools, a missing triage, or too few capable participants. Without
    ///     this the degrade is visible only in a server log. <see cref="TurnNoticePayload.Detail" /> carries the
    ///     <c>OrchestrationDegradationReason</c> name.
    /// </remarks>
    OrchestrationDegraded = 5,

    /// <summary>
    ///     Some of the agent's tools were held back from the model this turn to save context, and the model can list
    ///     and use them by calling <c>list_tools</c>.
    /// </summary>
    /// <remarks>
    ///     Counts only — the notice never names a tool. Hiding a tool is a context-budget optimisation and never an
    ///     authorisation change: a held-back tool the model names still executes under exactly the same approval rules.
    /// </remarks>
    ToolsFiltered = 6,

    /// <summary>
    ///     The turn was authored with reasoning effort <c>auto</c> and the node resolved it into a concrete tier for
    ///     this turn — a different reasoning depth, and possibly a different (node-local, smaller) model.
    /// </summary>
    /// <remarks>
    ///     Deliberately silent on the common NORMAL, no-swap case: a notice on every ordinary turn is noise.
    ///     <see cref="TurnNoticePayload.Detail" /> carries the stable kebab-case dispatch reason code, which names a
    ///     RULE and never a signal value — no message length, no conversation depth, no score, and never any message
    ///     text.
    /// </remarks>
    EffortDispatched = 7,

    /// <summary>
    ///     The agent's enabled playbook memory was withheld from a CLOUD-hosted effective model.
    /// </summary>
    /// <remarks>
    ///     The same <c>AllowCloudModelAccess</c> egress gate as knowledge and attachments; silent when the
    ///     playbook is off or empty. <see cref="TurnNoticePayload.Detail" /> names the effective model, or for an
    ///     orchestration the affected participants' names — never memory content.
    /// </remarks>
    PlaybookWithheld = 8,

    /// <summary>
    ///     The model's last round produced neither text nor a tool call, so the turn ended without an answer.
    /// </summary>
    /// <remarks><see cref="TurnNoticePayload.Detail" /> carries the provider's finish reason when it reported one.</remarks>
    EmptyAnswer = 9,

    /// <summary>
    ///     Earlier tool calls and their results were not replayed to a CLOUD-hosted effective model.
    /// </summary>
    /// <remarks>
    ///     Plain chat replays excerpts of earlier tool exchanges, which are node-local data, so they ride the same
    ///     <c>AllowCloudModelAccess</c> egress gate as attachments. <see cref="TurnNoticePayload.Detail" />
    ///     names the model.
    /// </remarks>
    ToolHistoryWithheld = 10,

    /// <summary>
    ///     The client asked for tools and the node tool engine is on, but the effective model does not declare tool
    ///     support, so the turn runs without tools.
    /// </summary>
    /// <remarks>
    ///     Unlike <see cref="ToolsFiltered" /> nothing is callable through <c>list_tools</c>: no tool is offered at all.
    ///     An external model whose tool support is Unknown reads as unsupported here. <see cref="TurnNoticePayload.Detail" />
    ///     names the effective model.
    /// </remarks>
    ToolsWithheld = 11,

    /// <summary>The answer ended on the output cap or the context window instead of where the model chose to stop.</summary>
    /// <remarks>Gated by the node's chat output-cap variant (off silences it). <see cref="TurnNoticePayload.Detail" /> carries the finish reason.</remarks>
    OutputLimitReached = 12,

    /// <summary>
    ///     The turn asked for knowledge-base grounding, but no embedding model is installed, so no document is indexed and
    ///     the answer is not grounded in them.
    /// </summary>
    KnowledgeUnavailable = 13,

    /// <summary>The inlined attachment text was shortened to fit the model's launched context window; the model is told too.</summary>
    AttachmentShortened = 14,

    /// <summary>
    ///     Some attached files did not reach a node-local model: no text was extracted, the file was not readable, or it is
    ///     an image and the model cannot see images.
    /// </summary>
    /// <remarks>
    ///     Not a privacy withhold (that is <see cref="AttachmentsWithheld" />). <see cref="TurnNoticePayload.Detail" />
    ///     lists the affected file names.
    /// </remarks>
    AttachmentsNotSent = 15,

    /// <summary>
    ///     Some tools were not offered to a model that leaves the node because their cloud-model switch is off.
    /// </summary>
    /// <remarks>
    ///     Covers <c>AllowCloudModelMcpTools</c>, <c>AllowCloudModelWebTools</c> and <c>AllowCloudModelSubAgents</c>.
    ///     <see cref="TurnNoticePayload.Detail" /> carries the switch codes (<c>mcp-tools</c>, <c>web-tools</c>,
    ///     <c>sub-agents</c>), never a tool name.
    /// </remarks>
    CloudToolsWithheld = 16,

    /// <summary>
    ///     The inlined attachment text exceeded the node's attachment budget (<c>MaxInlinedAttachmentChars</c>), so the
    ///     model received only its start.
    /// </summary>
    /// <remarks>
    ///     The fixed node cap; <see cref="AttachmentShortened" /> is the further cut to the launched context window.
    ///     <see cref="TurnNoticePayload.Detail" /> lists the affected file names.
    /// </remarks>
    AttachmentsTruncated = 17
}
