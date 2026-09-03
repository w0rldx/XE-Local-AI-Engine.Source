namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One external invocation of a trigger, from admission to a terminal state. Content-free: the seed and the
///     assistant turns live in the owned conversation, and the only column here that could carry content —
///     <see cref="FailureSummary" /> — is a category label by contract, exactly as <c>AgentExecutionLog.ErrorClass</c>
///     is.
/// </summary>
internal sealed record class IntegrationExecution
{
    public Guid Id { get; set; }

    /// <summary>The trigger that was invoked. Loose reference with no FK. Plaintext (structural).</summary>
    public Guid TriggerId { get; set; }

    /// <summary>The session this execution belongs to. Plaintext (structural).</summary>
    public Guid SessionId { get; set; }

    /// <summary>The integrator identity that owns this execution (ruling R4-6). Plaintext (structural).</summary>
    public Guid PrincipalId { get; set; }

    /// <summary>
    ///     The caller-supplied idempotency key, unique <b>per principal</b> rather than globally, so one integrator
    ///     cannot preclaim another's request id and force it a permanent 409. Plaintext (structural).
    /// </summary>
    public Guid RequestId { get; set; }

    /// <summary>
    ///     SHA-256 over the principal, trigger name, session id and raw request body. A replay of the same
    ///     <see cref="RequestId" /> with a different fingerprint is answered 409. A digest with no plaintext to
    ///     protect, so it is not encrypted. Plaintext (structural).
    /// </summary>
    public byte[] RequestFingerprint { get; set; } = [];

    /// <summary>
    ///     Which of the principal's credentials sent the request. <b>Audit metadata only</b> (ruling R4-6): nothing is
    ///     looked up by it and it answers no ownership question. Plaintext (structural).
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>The runner invocation id, correlating this row with the audit ledger and the cancel path. Plaintext (structural).</summary>
    public Guid InvocationId { get; set; }

    /// <summary>Where the execution is in <see cref="IntegrationExecutionStatus" />'s transition table. Plaintext (structural).</summary>
    public IntegrationExecutionStatus Status { get; set; }

    /// <summary>Unix-ms instant the accept transaction committed. Plaintext (structural).</summary>
    public long ReceivedAtUtc { get; set; }

    /// <summary>Unix-ms instant the runner was entered, or null while the execution has not started. Plaintext (structural).</summary>
    public long? StartedAtUtc { get; set; }

    /// <summary>Unix-ms instant a terminal state was written, or null while non-terminal. Plaintext (structural).</summary>
    public long? EndedAtUtc { get; set; }

    /// <summary>
    ///     Durable cancel marker (ruling R2-3): stamped when a cancel is requested so a restart cannot resurrect a
    ///     stopped run. Shaped on <see cref="McpAgentRun.StopRequestedAtUtc" />. Plaintext (structural).
    /// </summary>
    public long? StopRequestedAtUtc { get; set; }

    /// <summary>
    ///     One of the ten categories <see cref="IntegrationExecutionStatus" /> lists, or null. Content-free by
    ///     contract — a category label, never a message. Plaintext (structural).
    /// </summary>
    public string? FailureCategory { get; set; }

    /// <summary>A short, content-free elaboration of <see cref="FailureCategory" />, or null. Plaintext (structural).</summary>
    public string? FailureSummary { get; set; }

    /// <summary>How many <c>external.output</c> events the execution has committed. Plaintext (structural).</summary>
    public int OutputCount { get; set; }

    /// <summary>
    ///     Running total of <b>plaintext UTF-8 bytes</b> of the persisted <c>external.output</c> payloads (ruling
    ///     R3-5) — never the encrypted column length. The stored BLOB is <c>nonce ‖ ciphertext ‖ tag</c>, so a SQL
    ///     <c>SUM(length(detail_json))</c> would count the fixed AES-GCM envelope on every row on top of the payload;
    ///     this column exists precisely so no such sum is ever needed. Maintained by <c>AppendOutputEventAsync</c> in
    ///     the same save as the event insert. Plaintext (structural).
    /// </summary>
    public long OutputBytes { get; set; }

    /// <summary>The highest event sequence committed for this execution, maintained as a running maximum. Plaintext (structural).</summary>
    public long LastSequence { get; set; }

    /// <summary>Optimistic concurrency token; every status compare-and-swap contends on it. Plaintext (structural).</summary>
    public long Version { get; set; }
}
