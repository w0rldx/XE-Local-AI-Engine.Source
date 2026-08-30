namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     What one tool node-run's pass through the sandbox produced: a verdict, the counts a conditional edge routes on,
///     and the sanitized report bytes the run keeps as evidence.
///     <para>
///         A pure VALUE, deliberately. The sandbox work is detached, and the invariant the whole runtime rests on is
///         that only the dispatcher's serialized tick writes a node-run status — so what runs out of band answers with
///         a result and the tick decides what it means.
///     </para>
///     <para>
///         <see cref="FailureClass" /> is null exactly when <see cref="Passed" /> is true. A failing verdict from the
///         commands themselves is <c>ToolCommandFailed</c>, which is the fix loop's fuel rather than an error; the
///         other classes mean the pass never got as far as a verdict.
///     </para>
/// </summary>
internal sealed record DevWorkflowToolRun(
    bool Passed,
    string? FailureClass,
    string? FailureCode,
    string? SanitizedReason,
    int CommandsRun,
    int CommandsFailed,
    int? TestsPassed,
    int? TestsFailed,
    ReadOnlyMemory<byte> Report,
    /// <summary>
    ///     The committed credential paths the prepared workspace carried, which the tick records as a run event. Carried
    ///     back rather than written where they are found, so the detached pass writes nothing at all.
    /// </summary>
    IReadOnlyList<string> SecretPaths);

/// <summary>
///     The sandbox half of the tool lane: prepare a workspace for a node-run that has no Dev Mode task behind it, run
///     the validation commands in it, and answer with a sanitized verdict.
///     <para>
///         The second and last interface-for-one-implementation in this runtime, for the same reason as the agent
///         session seam: it is the only way to exercise the graph without provisioning a real repository and running a
///         real build, and the harness that makes every other test fast depends on being able to script it. Everything
///         around it — the lane, the rows, the report artifact — is a concrete type.
///     </para>
/// </summary>
internal interface IDevWorkflowToolCommands
{
    Task<DevWorkflowToolRun> RunAsync(DevWorkflowRunSnapshot run,
        DevWorkflowGraphNode node,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken);
}
