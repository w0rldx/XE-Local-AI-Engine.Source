namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One row per <c>(run, node key)</c>. <see cref="Attempt" /> increments in place; there is no per-attempt row.
///     Per-attempt history — prior session ids, prior failures — lives in the run event log, which is what makes the
///     <c>(run_id, node_key)</c> unique index the node-run's identity rather than a secondary constraint.
/// </summary>
internal sealed class DevWorkflowNodeRun
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }

    /// <summary>The graph node id; a materialized child is <c>"{template}#{taskId}"</c>. Structural, so plaintext — labels and instructions stay in the encrypted graph.</summary>
    public string NodeKey { get; set; } = string.Empty;

    public DevWorkflowNodeType NodeType { get; set; }
    public int Attempt { get; set; }

    /// <summary>Copied from the graph node at materialization so the runtime never re-reads the graph to answer "may I retry?".</summary>
    public int MaxAttempts { get; set; }

    /// <summary>How many times the owned work session was resumed after an interruption.</summary>
    public int SessionResumes { get; set; }

    public DevWorkflowNodeRunStatus Status { get; set; }

    /// <summary>Which queue this node-run is actually waiting in. Plaintext — it is what makes queued-vs-running honest instead of a spinner.</summary>
    public string? QueueReason { get; set; }

    /// <summary>What human input this node-run is blocked on, so run detail can count pending decisions without a run-level column.</summary>
    public DevWorkflowDecisionKind? PendingDecisionKind { get; set; }

    /// <summary>
    ///     Allocated from the run watermark at insert only. This is a stable creation order, not a change watermark: a
    ///     node-run that changes status eight times keeps its original sequence, so node-runs are deliberately not a
    ///     <c>sinceSeq</c> feed. Status changes are observed through the event log and a run-detail refetch.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>The work session this agent node-run owns. Loose reference, no foreign key: a purged session must read back as recoverable state.</summary>
    public Guid? WorkSessionId { get; set; }

    public Guid? AgentDefinitionId { get; set; }

    /// <summary>The Dev Mode project and task a <c>DevTask</c> node drives. Together they are the workspace identity, which is why no separate workspace reference exists.</summary>
    public Guid? DevelopmentProjectId { get; set; }

    public Guid? DevelopmentTaskId { get; set; }

    /// <summary>
    ///     The resolved input handed to the node. For an entry node — one with no inbound edges — this is where the
    ///     operator's request reaches the first agent: run start seeds it with the work-item request and the run inputs.
    /// </summary>
    public byte[]? InputJson { get; set; }

    /// <summary>The node's structured output. Gate nodes decide over this, which is why it carries its own AAD column name.</summary>
    public byte[]? OutputJson { get; set; }

    /// <summary>One <c>{id, name, contentSha256}</c> per applied rule set, captured at materialization.</summary>
    public byte[]? PolicyResolutionJson { get; set; }

    public Guid? MaterializedFromNodeRunId { get; set; }
    public int? MaterializationIndex { get; set; }
    public string? FailureClass { get; set; }
    public string? TerminalReason { get; set; }

    // Cost telemetry: fifteen nullable, plaintext, metadata-only columns saying what this node run SPENT and where it
    // routed — counts, a served model name, structural node keys and tool NAMES, never a prompt, an argument, a result
    // or a transcript. Written once, at the terminal-or-blocked transition, through the one store decorator every call
    // site crosses; nothing reads them to decide anything. Attempt increments in place, so they describe the LAST
    // attempt only — the earlier attempts ride on their own node.retry.scheduled events, which is exactly why the
    // Pending reset below clears every one of them.

    /// <summary>Provider-reported prompt tokens, summed over the attempt's chat-run envelopes; over its attempts on a DevTask node.</summary>
    public long? InputTokens { get; set; }

    /// <summary>Provider-reported completion tokens, summed the same way.</summary>
    public long? OutputTokens { get; set; }

    /// <summary>Provider-reported reasoning tokens, summed the same way.</summary>
    public long? ReasoningTokens { get; set; }

    /// <summary>The character-profile estimate summed over the work session's steps — the lower bound that exists when no envelope does.</summary>
    public long? EstimatedInputTokens { get; set; }

    /// <summary>Raw provider rounds the attempt admitted, summed over the work session's steps.</summary>
    public int? ProviderCalls { get; set; }

    /// <summary>Tool invocations that returned, successfully or not, summed over the work session's steps.</summary>
    public int? ToolCalls { get; set; }

    /// <summary>Tool-schema tokens SHIPPED ACROSS ROUNDS, not schema size — the largest single round is a different number.</summary>
    public long? ToolSchemaTokens { get; set; }

    /// <summary>
    ///     Up to sixteen distinct tool NAMES this attempt called, as an ordinal-sorted JSON string array. Names only —
    ///     never arguments, never results. Agent path only: null on a DevTask node run and on every structural row,
    ///     where it means "there were no step rows to read", never "this node called no tools".
    /// </summary>
    public string? ToolNamesJson { get; set; }

    /// <summary>
    ///     WHOLE agent turns, summed: every chat-run envelope's own duration, which spans the provider rounds AND the
    ///     tool loop between them. It is deliberately not called provider time — nothing this attempt persists
    ///     separates the two, so subtracting this from the node's runtime leaves what happened OUTSIDE the turns
    ///     (queueing before the session started, the node's own settle work), not the tool loop.
    /// </summary>
    public long? AgentTurnMs { get; set; }

    /// <summary>
    ///     How much of <see cref="AgentTurnMs" /> was a LOCAL runtime warming — <c>llama-server</c> launching and the
    ///     model loading — summed over the same envelopes, so <c>AgentTurnMs - ModelReadinessMs</c> is the
    ///     warm-equivalent turn time. Null means unmeasured: no turn of this attempt went through the local-runtime
    ///     warmer at all, or the row predates the column. Non-null is the warmer's measured wall time — it times EVERY
    ///     call, cache reuse included, and the sum truncates to whole milliseconds — so an already-resident model
    ///     measures near zero (live: 0) and zero itself proves only "under 1 ms", never residency on its own.
    /// </summary>
    public long? ModelReadinessMs { get; set; }

    /// <summary>
    ///     Machine-global free VRAM in bytes as the capacity gate measured it just before the most recent SUCCESSFUL
    ///     load of the model that served this run THAT CARRIED A CAPACITY ADMISSION — not necessarily a load this run
    ///     caused, and an unadmitted reload since (a direct, profiling or variant-moved spawn) clears the reading
    ///     rather than letting it describe the process that reload replaced.
    ///     <para>
    ///         <b>A warm run reports the EARLIER load's figures.</b> <see cref="ModelReadinessMs" /> is what separates
    ///         the two: a SMALL readiness there means the warmer waited for nothing, so the load these bytes describe
    ///         predates the run and the box may have looked different by the time it started; null there is
    ///         unmeasured, which settles nothing either way. Null here means
    ///         nobody measured — a remote or Ollama model, a model the node never loaded itself, a host with no
    ///         readable global-free figure (non-NVIDIA or CPU-only), or a row written before this column existed.
    ///     </para>
    /// </summary>
    public long? VramFreeAtLoadBytes { get; set; }

    /// <summary>
    ///     The GPU bytes the capacity gate RESERVED for that same load's process. NOT llama.cpp's own
    ///     <c>--list-devices</c> process budget, which is a different axis and is not read on this path. Zero is a real
    ///     answer for a CPU-placed allocation; null carries the same "nobody measured" meaning as
    ///     <see cref="VramFreeAtLoadBytes" />, and the same warm-run caveat applies.
    /// </summary>
    public long? VramAdmittedBytes { get; set; }

    /// <summary>What the provider actually SERVED, off the envelope. Beside the authored pin, never instead of it: a pin is a request, not a receipt.</summary>
    public string? ServedModelName { get; set; }

    /// <summary>
    ///     Which of this node's out-edges its settle satisfied and which it killed, as the state machine itself judged
    ///     them — <c>{"satisfied":[…],"dead":[…],"gateAnswer":…,"truncated":…}</c> over plaintext structural node keys.
    ///     Null on a row that is not terminal: a node that has not finished has routed nowhere yet.
    /// </summary>
    public string? RouteJson { get; set; }

    /// <summary>How many steps the owned work session had taken when this node run settled.</summary>
    public int? WorkSessionSteps { get; set; }

    public long? QueuedAtUtc { get; set; }
    public long? StartedAtUtc { get; set; }
    public long? EndedAtUtc { get; set; }
    public long CreatedAtUtc { get; set; }
}
