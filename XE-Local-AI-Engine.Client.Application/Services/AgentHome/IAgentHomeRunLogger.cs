namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     run logger run-scoped logger. Appends structured JSONL records to the host-side
///     <c>runs/&lt;run-id&gt;/logs/</c> directory (the host-side root, NOT the in-sandbox
///     <c>/agent-home</c> — see AgentHome plan two-roots split). Every record is correlated with the
///     run-id, NodeId, and OwnerUserId. Raw host paths and secrets are NEVER written; argument summaries
///     are caller-supplied model-safe objects (run-relative paths only, §11).
/// </summary>
/// <remarks>
///     OTel meters/counters and the list-runs endpoint are explicitly deferred (see run logger item 6/8).
///     Tool-correlation hooks (item 3/4) are wired by the run gateway (AgentHome gateway); this service owns only the
///     file-write primitives and the redaction contract so the run gateway can inject it without modifying this type.
/// </remarks>
internal interface IAgentHomeRunLogger
{
    /// <summary>
    ///     Opens (or re-opens) the log directory for <paramref name="context" /> and writes the initial
    ///     <c>started</c> event to <c>events.jsonl</c>. Must be called once before any other method.
    /// </summary>
    Task OpenAsync(AgentHomeRunLogContext context, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Appends a generic event record to <c>events.jsonl</c> (e.g. <c>prepare_completed</c>,
    ///     <c>run_completed</c>, <c>cancelled</c>).
    /// </summary>
    Task AppendEventAsync(string eventName, string? detail = null, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Appends a command execution record to <c>commands.jsonl</c>. The executable and any
    ///     arguments are already model-safe strings supplied by the caller (no raw host paths).
    /// </summary>
    Task AppendCommandAsync(AgentHomeCommandLogRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Appends a tool-call lifecycle record to <c>tool-calls.jsonl</c>. The argument summary
    ///     is a caller-supplied model-safe object; secrets and host paths are never written (§11).
    /// </summary>
    Task AppendToolCallAsync(AgentHomeToolCallLogRecord record, CancellationToken cancellationToken = default);
}

/// <summary>
///     Correlation envelope shared by every log record in a run. Supplied once to
///     <see cref="IAgentHomeRunLogger.OpenAsync" /> and carried on all subsequent appends.
/// </summary>
internal sealed record AgentHomeRunLogContext
{
    /// <summary>The run id; log files live under <c>&lt;RootPath&gt;/runs/&lt;RunId&gt;/logs/</c>.</summary>
    public required string RunId { get; init; }

    /// <summary>
    ///     The worker-local host log directory (<c>&lt;RootPath&gt;/runs/&lt;RunId&gt;/logs</c>);
    ///     caller ensures the directory is already created before calling <see cref="IAgentHomeRunLogger.OpenAsync" />.
    /// </summary>
    public required string HostLogDirectory { get; init; }

    /// <summary>The node id from worker identity; correlated on every record.</summary>
    public required string NodeId { get; init; }

    /// <summary>The owner user id from worker identity; correlated on every record.</summary>
    public required string OwnerUserId { get; init; }

    /// <summary>The sandbox provider name (e.g. <c>fake</c>, <c>local-container</c>).</summary>
    public required string ProviderName { get; init; }
}

/// <summary>Record appended to <c>commands.jsonl</c> for each in-sandbox command execution.</summary>
internal sealed record AgentHomeCommandLogRecord
{
    /// <summary>UTC timestamp of the command start.</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>The execution id passed to the sandbox provider (matches the run-scoped id in <c>SandboxCommandRequest.ExecutionId</c>).</summary>
    public required string ExecutionId { get; init; }

    /// <summary>The command executable (model-safe; no host paths).</summary>
    public required string Executable { get; init; }

    /// <summary>The command arguments (model-safe strings only; caller strips host paths before passing).</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Whether the command ran to completion.</summary>
    public required bool Completed { get; init; }

    /// <summary>The command exit code.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Elapsed wall-clock milliseconds from command start to result.</summary>
    public required long DurationMs { get; init; }

    /// <summary>Error class name when the command raised an exception; <see langword="null" /> on success.</summary>
    public string? ErrorClass { get; init; }
}

/// <summary>
///     Record appended to <c>tool-calls.jsonl</c> for each tool-call lifecycle event.
///     Shape matches the draft in AgentHome plan § run logger.
/// </summary>
internal sealed record AgentHomeToolCallLogRecord
{
    /// <summary>UTC timestamp of the event.</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>The run id this event belongs to.</summary>
    public required string RunId { get; init; }

    /// <summary>The tool name (e.g. <c>run_in_agent_home</c>).</summary>
    public required string ToolName { get; init; }

    /// <summary>Tool location: <c>ClientLocal</c> or <c>ApiSide</c>.</summary>
    public required string Location { get; init; }

    /// <summary>The approval id, or <see langword="null" /> when not yet assigned.</summary>
    public string? ApprovalId { get; init; }

    /// <summary>Lifecycle status: <c>started</c>, <c>approved</c>, <c>rejected</c>, <c>succeeded</c>, <c>failed</c>, or <c>cancelled</c>.</summary>
    public required string Status { get; init; }

    /// <summary>
    ///     Caller-supplied model-safe argument summary. Host paths and secrets are redacted by the caller
    ///     before this object is constructed (§11). May be <see langword="null" /> when not applicable.
    /// </summary>
    public object? ArgumentSummary { get; init; }

    /// <summary><see langword="true" /> when the caller applied redaction to the argument summary.</summary>
    public required bool RedactionApplied { get; init; }

    /// <summary>Elapsed milliseconds; <see langword="null" /> for events recorded before completion.</summary>
    public long? DurationMs { get; init; }

    /// <summary>Error class name on failure; <see langword="null" /> on success.</summary>
    public string? ErrorClass { get; init; }
}
