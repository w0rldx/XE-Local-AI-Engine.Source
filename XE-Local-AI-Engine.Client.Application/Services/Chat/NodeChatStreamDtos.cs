namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Represents chat stream event types.
/// </summary>
public static class ChatStreamEventTypes
{
    public const string UserMessagePersisted = "user-message-persisted";
    public const string AssistantPending = "assistant-pending";
    public const string AssistantQueued = "assistant-queued";
    public const string AssistantStreaming = "assistant-streaming";

    /// <summary>
    ///     A pre-first-token runtime-phase transition (preparing the runtime / loading the model). Carries no content —
    ///     only <see cref="ChatStreamEvent.RuntimePhase" /> — so the client can show "Loading model…" during a cold load
    ///     instead of the generic typing indicator. Emitted only while a local model warms; absent for cloud/Ollama.
    /// </summary>
    public const string AssistantPhase = "assistant-phase";

    /// <summary>
    ///     One live increment of the assistant's content and/or reasoning. Carries ONLY
    ///     <see cref="ChatStreamEvent.Delta" />/<see cref="ChatStreamEvent.ReasoningDelta" /> plus the character offset
    ///     each begins at — never the accumulated text. Sending the full snapshot on every frame is what made the wire
    ///     cost of a turn quadratic in its output length, so <see cref="ChatStreamEvent.Content" /> and
    ///     <see cref="ChatStreamEvent.Reasoning" /> are never populated on this type and the client must append rather
    ///     than replace. The client repairs a discontinuity by re-subscribing through <c>ResumeMessage</c>, whose first
    ///     frame is an <see cref="AssistantSnapshot" />.
    /// </summary>
    public const string AssistantDelta = "assistant-delta";

    /// <summary>
    ///     An authoritative REPLACEMENT of the accumulated content/reasoning, carrying the full text and the offsets
    ///     the next delta will continue from. Emitted on resume replay, on gap repair, and on queue overflow — never
    ///     mid-stream on the happy path. Deliberately NOT terminal: its status stays <c>streaming</c> and the turn
    ///     continues after it.
    /// </summary>
    public const string AssistantSnapshot = "assistant-snapshot";

    /// <summary>
    ///     A "resynchronize" marker: the server could not deliver a contiguous stream (the consumer's queue overflowed,
    ///     or a replay snapshot was too large to send). Carries no payload beyond the correlation and a sequence; the
    ///     client re-subscribes through <c>ResumeMessage</c> and consumes it without surfacing anything to the user.
    /// </summary>
    public const string AssistantReconcile = "assistant-reconcile";

    public const string AssistantCompleted = "assistant-completed";
    public const string AssistantCancelled = "assistant-cancelled";
    public const string AssistantFailed = "assistant-failed";
    public const string AssistantInterrupted = "assistant-interrupted";
    public const string ToolCallRequested = "tool-call-requested";
    public const string ToolCallCompleted = "tool-call-completed";

    /// <summary>
    ///     A non-fatal turn notice (model substitution, tool disabled, history truncated) surfaced alongside the
    ///     content stream. See <see cref="XE_Local_AI_Engine.Client.Services.Events.TurnNoticePayload" />.
    /// </summary>
    public const string AssistantNotice = "assistant-notice";

    /// <summary>
    ///     A tool-approval request the in-flight turn is paused on. Carries the tool-call id (correlating it to
    ///     the waiting tool-call card), the tool name, and the <see cref="ChatStreamEvent.ApprovalRequestId" /> the
    ///     browser echoes back to the loopback resolve endpoint. Status stays <c>streaming</c>; no content rides it.
    /// </summary>
    public const string ApprovalRequested = "approval-requested";

    /// <summary>
    ///     An <c>ask_user</c> question the in-flight turn is paused on. Carries the tool-call id (correlating it to the
    ///     waiting tool-call card), the tool name, the <see cref="ChatStreamEvent.QuestionRequestId" /> the browser echoes
    ///     back to the loopback resolve endpoint, and — unlike an approval — the
    ///     <see cref="ChatStreamEvent.Questions" /> themselves, because a client cannot render an answerable prompt from a
    ///     correlation id alone. Status stays <c>streaming</c>; no content rides it.
    /// </summary>
    public const string QuestionRequested = "question-requested";
}

public sealed record NodeChatStreamRequest(
    Guid ConversationId,
    string Content,
    Guid? UserMessageId = null,
    Guid? MessageId = null,
    Guid? RequestId = null,
    string? Model = null,
    bool UseLocalTools = false,
    string? ReasoningEffort = null,
    IReadOnlyDictionary<Guid, Guid>? SelectedPath = null,
    // The per-send selected agent (composer agent mode). Takes precedence over the legacy conversation binding; null
    // falls back to the conversation binding, then to the seeded Default Assistant (mode-off persona). Trailing
    // optional so the SignalR hub forwards the record unchanged.
    Guid? AgentDefinitionId = null,
    // Developer-gated per-send sampling overrides. Null (the default) keeps the no-override path byte-identical to
    // today; the SignalR hub forwards the record unchanged.
    SamplingOptions? SamplingOptions = null,
    // The conversation's uploaded-file attachments to ground this turn on. In plain chat (no tools) the extracted text
    // of these files is inlined (capped) into the context; in agent mode they are read via the file tools, so this is
    // ignored. The client re-sends the conversation's current attachment ids each turn. Trailing optional so the
    // SignalR hub forwards the record unchanged.
    IReadOnlyList<Guid>? AttachmentFileIds = null,
    // Opt-in knowledge-base grounding for a PLAIN-chat turn (default OFF). When true and the effective model is
    // node-local (or the operator opted cloud data-access in), the send path retrieves the top-k fused knowledge-base
    // hits for the user's latest message and inlines them as ONE fenced untrusted context region, and records their
    // provenance as the turn's sources. Ignored in agent mode (the agent uses the search_knowledge_base tool instead).
    // Trailing optional so the SignalR hub forwards the record unchanged.
    bool UseKnowledgeBase = false,
    // Whether ReasoningEffort above is a PIN rather than a preference. A bound agent's own pinned effort normally wins
    // over the one that arrives with a send — the composer's selection is what the operator would like, the pin is what
    // the agent is configured to do. A work session driven by a development-workflow node is the other way round: the
    // node's authored effort IS that session's pin and there is no composer behind it. False everywhere else, so every
    // ordinary send keeps the precedence it has today. Trailing optional so the SignalR hub forwards the record
    // unchanged.
    bool ReasoningEffortOverridesAgentPin = false,
    // Whether this turn is a step of a supervised work session rather than a send someone typed. Set UNCONDITIONALLY by
    // WorkSessionExecutionSupervisor, so it is true on every step of every session — including every development-
    // workflow node step, whatever that node authored — and false on every ordinary chat send. The runtime package
    // refuses the adaptive-effort model swap on it: a session step is autonomous, and a step served by a model neither
    // the graph's author nor the operator chose is not their decision. Trailing optional so the SignalR hub forwards the
    // record unchanged.
    bool IsWorkSessionTurn = false,
    // GRAPH-C4-2's runtime half, carried on the turn it judges. Set by a work session a development-workflow Agent node
    // drives when that node declares no WriteExecute capability and its template waives nothing: the send resolves the
    // agent definition ONCE and refuses before it sends if that resolution's own tool offer carries a tool which writes
    // files or runs commands. It rides the request rather than being asked ahead of the send because the definition is
    // mutable — a check that resolves it separately is answering about a projection the turn may no longer use. False
    // everywhere else, so every ordinary send is unchanged. Trailing optional so the SignalR hub forwards the record
    // unchanged.
    bool RefuseUndeclaredWrites = false);

public sealed record ChatStreamEvent(
    string Type,
    Guid ConversationId,
    Guid MessageId,
    Guid RequestId,
    string Status,
    long Sequence,
    long OccurredAtUtc,
    string? Delta = null,
    string? ReasoningDelta = null,
    string? Content = null,
    string? Reasoning = null,
    string? Error = null,
    string? Model = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    int? TotalTokens = null,
    int? ReasoningTokens = null,
    string? ToolCallId = null,
    string? ToolName = null,
    string? Arguments = null,
    bool? RequiresApproval = null,
    string? Result = null,
    bool? IsError = null,
    // Non-fatal turn notice fields (AssistantNotice events only). NoticeKind is the TurnNoticeKind enum name (e.g.
    // "ModelSubstituted", "ToolDisabled", "HistoryTruncated"); NoticeMessage is the sanitized, user-facing text.
    // Trailing optional so every existing event type's wire shape is unchanged.
    string? NoticeKind = null,
    string? NoticeMessage = null,
    // Runtime phase (AssistantPhase events only): the wire form of InvocationRuntimePhase — "preparing_runtime",
    // "loading_model", or "generating" — so the client can show a distinct model-loading indicator before the first
    // token. Trailing optional so every existing event type's wire shape is unchanged.
    string? RuntimePhase = null,
    // Approval request id (ApprovalRequested events only): the durable key the browser echoes back to the
    // loopback resolve endpoint to release the waiting run. Distinct from the top-level RequestId (the turn
    // correlation guid). The tool-call id rides ToolCallId and the tool name rides ToolName. Trailing optional so
    // every existing event type's wire shape is unchanged.
    string? ApprovalRequestId = null,
    // Question request id (QuestionRequested events only): the durable key the browser echoes back to the loopback
    // resolve endpoint to release the waiting run. Distinct from the top-level RequestId (the turn correlation guid).
    // The tool-call id rides ToolCallId and the tool name rides ToolName. Trailing optional so every existing event
    // type's wire shape is unchanged.
    string? QuestionRequestId = null,
    // The ask_user questions (QuestionRequested events only), as a JSON array of
    // {header, question, multiSelect, options:[{label, description, recommended}]} — the serialized
    // UserQuestionSpec[]. Rides as a JSON STRING for the same reason Arguments does: the event record stays a flat
    // wire shape and the client parses the payload it renders. Trailing optional so every existing event type's wire
    // shape is unchanged.
    string? Questions = null,
    // The character index in the accumulated content at which Delta begins (AssistantDelta), or the length of the
    // carried Content (AssistantSnapshot). Null on every other event type. The client uses it to detect a gap: a delta
    // whose ContentOffset is not where the previous one ended means the stream is discontinuous, and it repairs by
    // re-subscribing through ResumeMessage.
    //
    // These are .NET string indices, i.e. UTF-16 code units — deliberately the same index space as JavaScript's
    // String.length, so client and server agree on the index without any conversion. A delta may therefore split a
    // surrogate pair; it already could, and rendering concatenates before display, so nothing changes.
    //
    // Trailing optional so every existing event type's wire shape is unchanged.
    long? ContentOffset = null,
    long? ReasoningOffset = null,
    // The effective whole-turn ceiling for THIS turn in seconds — the operator's node "Maximum message request
    // timeout" as it was resolved into the runtime package's TimeoutSettings.InvocationTimeoutSeconds. Stamped on
    // AssistantQueued and AssistantStreaming only (null everywhere else) so the browser's own stream watchdog can
    // derive its deadline from the node's ceiling instead of a fixed constant that pre-empts it. Trailing optional so
    // every existing event type's wire shape is unchanged.
    int? InvocationTimeoutSeconds = null,
    // Whether the node can REMEMBER an "approve for this session" decision for this exact request (ApprovalRequested
    // events only; null everywhere else, and null on a reconnect replay that cannot resolve it). The browser prefers
    // this per-request answer over the tool catalog's tool-identity flag when deciding whether to offer the session
    // button. Trailing optional so every existing event type's wire shape is unchanged.
    bool? SessionScopeEligible = null,
    // The notice's optional structured detail (AssistantNotice events only), carried verbatim from
    // TurnNoticePayload.Detail: a stable machine code or short identifier that names WHY the notice fired, next to
    // NoticeMessage's prose. EffortDispatched carries the kebab-case dispatch reason code, ModelSubstituted /
    // AttachmentsWithheld / KnowledgeWithheld the effective model, OrchestrationDegraded the degradation reason.
    // Sanitized at the source like every other notice field. Trailing optional so every existing event type's wire
    // shape is unchanged.
    string? NoticeDetail = null);
