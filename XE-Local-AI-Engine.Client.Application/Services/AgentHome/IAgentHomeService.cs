namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Orchestrates a node-scoped AgentHome run. The phase split is deliberate: preparation
///     (attach/create the sandbox, recover the layout, validate selected folders) is timed separately from command
///     execution. AgentHome gateway ships a minimal body — it resolves selected folders without copying them (workspace copy),
///     runs one liveness-probe command on the configured provider (the fake by default), and returns run-scoped
///     metadata. The workspace-copy, patch-export, memory-proposal, gateway, and run-logging invariants extend these two method bodies (
///     run-scoped log content) rather than re-plumbing the gateway.
/// </summary>
internal interface IAgentHomeService
{
    /// <summary>
    ///     Resolves owner/node identity, recovers the worker-local layout, attaches/creates the sandbox, and validates
    ///     the requested selected-folder ids (resolve-only; no copy in AgentHome gateway). Applies the preparation timeout
    ///     separately from the command timeout.
    /// </summary>
    Task<AgentHomePrepareResult> PrepareAsync(AgentHomePrepareRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Assumes preparation completed; creates a run-scoped output directory, executes one command on the provider
    ///     under the command timeout, and returns the run id, completion status, and log path.
    /// </summary>
    Task<AgentHomeRunResult> RunAsync(AgentHomeRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The single lifecycle entry the gateway calls (AgentHome gateway): resolves owner/node identity once, acquires the
    ///     run-level single-flight guard keyed by that owner-node (a second concurrent run for the same owner-node is
    ///     rejected with <see cref="AgentHomeBusyException" />, not queued), then runs Prepare followed
    ///     by Run and releases the guard in a finally. <see cref="PrepareAsync" />/<see cref="RunAsync" /> remain public
    ///     so the phases stay individually testable, but the guard wraps both at this one lock site.
    /// </summary>
    Task<AgentHomeRunResult> RunLifecycleAsync(AgentHomeRunLifecycleRequest request, CancellationToken cancellationToken = default);
}
