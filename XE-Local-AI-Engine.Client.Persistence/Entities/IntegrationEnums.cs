namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>What an integration trigger invokes. V1 has exactly one target kind (ADR 0008 Decision §2).</summary>
public enum IntegrationTargetKind
{
    /// <summary>A saved agent definition, run headless through the scheduler's <c>run-agent</c> shape.</summary>
    Agent
}

/// <summary>How a trigger maps incoming invocations onto <c>integration_sessions</c> rows.</summary>
public enum IntegrationSessionPolicy
{
    /// <summary>Every invocation gets a fresh session and a fresh owned conversation.</summary>
    PerInvocation,

    /// <summary>The caller supplies a session id and continues its transcript across invocations.</summary>
    CallerManaged
}

/// <summary>
///     Which input kinds a trigger accepts in an invoke body. A <c>[Flags]</c> combination, mapped as a plain
///     <c>int</c> column rather than <c>.HasConversion&lt;string&gt;()</c>.
/// </summary>
/// <remarks>
///     A combined flags value has no stable, length-bounded string form — the text would depend on member
///     declaration order and would grow past <c>HasMaxLength(32)</c> as soon as a third kind is added.
///     <c>McpServerApiKey.Scope</c> is an <c>int</c> for the same reason.
/// </remarks>
[Flags]
public enum IntegrationInputKinds
{
    /// <summary>A plain text block.</summary>
    Text = 1,

    /// <summary>A labelled JSON document, framed as untrusted content before it reaches the model.</summary>
    Json = 2
}

/// <summary>Lifecycle of an <c>integration_sessions</c> row.</summary>
public enum IntegrationSessionStatus
{
    /// <summary>Accepting further executions.</summary>
    Active,

    /// <summary>Closed by the operator or the caller; no further execution may join it.</summary>
    Closed
}

/// <summary>
///     Lifecycle of an <c>integration_executions</c> row. The legal moves are exactly these and nothing else (ruling
///     R3-2, reproduced verbatim in ADR 0008); <see cref="Running" /> is never re-entered, and there is no move out
///     of a terminal status.
/// </summary>
/// <remarks>
///     <see cref="Accepted" /> → <see cref="Queued" /> (waits for the invocation lease); <see cref="Accepted" /> or
///     <see cref="Queued" /> → <see cref="Running" /> (lease held, runner about to be called); <see cref="Running" />
///     → <see cref="Completed" />/<see cref="Failed" />/<see cref="Cancelled" /> (the run reported a terminal state);
///     <see cref="Accepted" /> or <see cref="Queued" /> → <see cref="Cancelled" />/<see cref="Failed" /> (cancelled
///     or rejected before the run started). See docs/wiki/08-data-and-persistence.md ("The integration execution lifecycle").
/// </remarks>
public enum IntegrationExecutionStatus
{
    /// <summary>Admitted and durable; the accept transaction has committed.</summary>
    Accepted,

    /// <summary>Waiting for the node's single invocation lease.</summary>
    Queued,

    /// <summary>The lease is held and the runner is driving the invocation.</summary>
    Running,

    /// <summary>The run finished normally.</summary>
    Completed,

    /// <summary>The run ended with a <c>FailureCategory</c> from the closed vocabulary above.</summary>
    Failed,

    /// <summary>The run was cancelled, before or during execution.</summary>
    Cancelled
}
