namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     Why a node run failed. PascalCase, closed, and written to the node run's <c>failure_class</c> column and to its
///     output document verbatim.
///     <para>
///         Deliberately a different vocabulary from <see cref="DevWorkflowOutcomes" />: these answer "why did it fail",
///         those answer "how did it end". Collapsing the two is what once produced tokens that belonged to neither.
///     </para>
/// </summary>
internal static class DevWorkflowFailureClasses
{
    /// <summary>The agent's work session failed. Retryable — a fresh session, never the poisoned one.</summary>
    public const string ProviderError = "ProviderError";

    /// <summary>The node's absolute deadline expired. Retryable.</summary>
    public const string Timeout = "Timeout";

    /// <summary>The host died under the node run. Retryable.</summary>
    public const string Interrupted = "Interrupted";

    /// <summary>A validation command reported failure. Retryable — this is the fix loop's fuel, not an error.</summary>
    public const string ToolCommandFailed = "ToolCommandFailed";

    /// <summary>An executor threw something nobody predicted. Retryable once.</summary>
    public const string Internal = "Internal";

    /// <summary>
    ///     The node cannot run as configured: an agent that is missing or cannot call tools, a repo-bound node on a work
    ///     item with no project, a node type this build has no executor for. NOT retryable — a retry produces the same
    ///     answer, so it goes straight to a human.
    /// </summary>
    public const string Configuration = "Configuration";

    /// <summary>
    ///     A policy refused the work: a dependency-manifest touch, a protected-path violation, an unacknowledged
    ///     repository. NOT retryable, and on evidence — the manifest check hard-fails with zero commands run and the
    ///     sandbox has no egress to re-resolve, so a second attempt produces the byte-identical answer.
    /// </summary>
    public const string Policy = "Policy";

    /// <summary>A budget ran out: session resumes, total attempts, node runs per run. NOT retryable.</summary>
    public const string BudgetExhausted = "BudgetExhausted";

    /// <summary>An operator cancelled it.</summary>
    public const string Cancelled = "Cancelled";

    /// <summary>A human gate was rejected and no out-edge matched, so the run ends.</summary>
    public const string GateRejected = "GateRejected";
}

/// <summary>
///     How an event ended. Lowercase verbs, closed, at most 64 characters.
///     <para>
///         There is deliberately no token for a refused admission: a lane that will not take a node run yet is
///         queueing, not failing, and it produces a <c>Queued</c> row with a reason rather than an event.
///     </para>
/// </summary>
internal static class DevWorkflowOutcomes
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Rejected = "rejected";
    public const string ChangesRequested = "changes-requested";
    public const string Timeout = "timeout";
    public const string Interrupted = "interrupted";
}

/// <summary>
///     Why a node run is <c>Queued</c> rather than <c>Running</c>. Lowercase-hyphenated, closed, and displayed verbatim:
///     the whole point of separating the two states is that the UI can say which of these it is.
/// </summary>
internal static class DevWorkflowQueueReasons
{
    /// <summary>The work-session admission cap is full — the honest name for "one node, one invocation slot".</summary>
    public const string AwaitingAgentSlot = "awaiting-agent-slot";

    /// <summary>The bounded sandbox lane is saturated.</summary>
    public const string AwaitingSandboxSlot = "awaiting-sandbox-slot";

    /// <summary>Admitted by its lane, but an inbound edge has not settled.</summary>
    public const string AwaitingDependency = "awaiting-dependency";
}

/// <summary>
///     The <c>status</c> of a node run's output document — the two values a condition on an out-edge may compare it
///     against.
///     <para>
///         Deliberately not <see cref="DevWorkflowOutcomes" />, which two of these strings happen to match: an event's
///         outcome describes how one event ended, while this describes what the node produced, and a definition author
///         writing <c>status eq "succeeded"</c> is reading THIS. Sharing the constants would make a later change to
///         either vocabulary silently reach into the other.
///     </para>
/// </summary>
internal static class DevWorkflowNodeOutputStatuses
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

/// <summary>
///     The <c>verdict</c> of a node run's output document, for the answers a status alone cannot carry.
///     <para>
///         One value today. A zero-task decomposition writes an already-succeeded row at each of its template's
///         validation nodes — so an apply downstream reads a validation that really did run for this run — and that
///         row has to say it validated NOTHING rather than let a reader take it for a check that passed. The
///         <c>status</c> stays <c>succeeded</c> because routing reads it and a conditional out-edge on the template's
///         validation node must fire exactly as a real pass would.
///     </para>
/// </summary>
internal static class DevWorkflowNodeOutputVerdicts
{
    public const string ValidationNotApplicable = "validation-not-applicable";
}
