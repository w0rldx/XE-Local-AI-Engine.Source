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
///     <c>int</c> column rather than <c>.HasConversion&lt;string&gt;()</c>: a combined flags value has no stable,
///     length-bounded string form — the text would depend on member declaration order and would grow past
///     <c>HasMaxLength(32)</c> as soon as a third kind is added. <c>McpServerApiKey.Scope</c> is an <c>int</c> for the
///     same reason.
/// </summary>
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
///     Lifecycle of an <c>integration_executions</c> row. The legal moves are exactly these, and nothing else
///     (ruling R3-2, reproduced verbatim in ADR 0008):
///     <list type="table">
///         <listheader><term>From</term><description>To — when</description></listheader>
///         <item><term><see cref="Accepted" /></term><description><see cref="Queued" /> — waits for the invocation lease</description></item>
///         <item><term><see cref="Accepted" />, <see cref="Queued" /></term><description><see cref="Running" /> — lease held, runner about to be called</description></item>
///         <item><term><see cref="Running" /></term><description><see cref="Completed" />, <see cref="Failed" />, <see cref="Cancelled" /> — the run reported a terminal state</description></item>
///         <item><term><see cref="Accepted" />, <see cref="Queued" /></term><description><see cref="Cancelled" /> — cancelled before the run started</description></item>
///         <item><term><see cref="Accepted" />, <see cref="Queued" /></term><description><see cref="Failed" /> — rejected before the run started</description></item>
///     </list>
///     <para>
///         <see cref="Running" /> is never re-entered, and there is no move out of a terminal status.
///         <see cref="Queued" /> is written <b>only</b> when an execution actually waits for the lease —
///         <see cref="Accepted" /> → <see cref="Running" /> is legal, and so is <see cref="Accepted" /> or
///         <see cref="Queued" /> → <see cref="Cancelled" />/<see cref="Failed" /> without ever running. That is
///         written down here rather than only in the coordinator because cross-review found <see cref="Queued" />
///         defined, counted, swept, streamed and rendered with no visible producer.
///     </para>
///     <para>
///         Every move into a terminal status is made by <c>IIntegrationExecutionStore.TryTerminalizeAsync</c>, which
///         writes the status and the matching terminal event in one transaction (ruling R5-4);
///         <c>UpdateStatusAsync</c> makes the non-terminal moves and nothing else.
///     </para>
///     <para>
///         <c>FailureCategory</c> is a <b>closed</b> vocabulary of exactly ten values — <c>trigger-unavailable</c>,
///         <c>cloud-model-rejected</c>, <c>capacity-rejected</c>, <c>restart</c>, <c>queue-full</c>, <c>shutdown</c>,
///         <c>internal-failure</c>, plus <c>approval-required</c> (an unattended run invoked an approval-gated tool),
///         <c>queue-timeout</c> (a still-queued execution outlived <c>MaxQueueAgeSeconds</c>) and
///         <c>session-policy</c> (a caller-managed trigger resolved to an agent offering a tool outside
///         <c>ToolCategory.ReadLocal</c>). An eleventh value is a bug, not an extension point.
///     </para>
/// </summary>
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
