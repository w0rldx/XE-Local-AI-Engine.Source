namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Orchestrates a node-scoped AgentHome run (AgentHome plan §6.1). The phase split is deliberate: preparation
///     (attach/create the sandbox, recover the layout, validate selected folders) is timed separately from command
///     execution. Marker I-pre ships a minimal body — it resolves selected folders without copying them (Marker F),
///     runs one liveness-probe command on the configured provider (the fake by default), and returns run-scoped
///     metadata. Markers F/G/H/I/K extend these two method bodies (workspace copy, patch export, memory proposals,
///     run-scoped log content) rather than re-plumbing the gateway.
/// </summary>
internal interface IAgentHomeService
{
    /// <summary>
    ///     Resolves owner/node identity, recovers the worker-local layout, attaches/creates the sandbox, and validates
    ///     the requested selected-folder ids (resolve-only; no copy in Marker I-pre). Applies the preparation timeout
    ///     separately from the command timeout (§6.1).
    /// </summary>
    Task<AgentHomePrepareResult> PrepareAsync(AgentHomePrepareRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Assumes preparation completed; creates a run-scoped output directory, executes one command on the provider
    ///     under the command timeout, and returns the run id, completion status, and log path.
    /// </summary>
    Task<AgentHomeRunResult> RunAsync(AgentHomeRunRequest request, CancellationToken cancellationToken = default);
}
