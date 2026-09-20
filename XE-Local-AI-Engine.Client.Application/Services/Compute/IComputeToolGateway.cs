namespace XE_Local_AI_Engine.Client.Services.Compute;

/// <summary>
///     Executes one <c>run_python</c> request inside the node's compute sandbox.
/// </summary>
/// <remarks>
///     This interface is THE boundary: the kill-switch (<c>Compute:Enabled</c>), the request validation and every
///     fail-closed sandbox refusal live behind <see cref="ExecuteDetailedAsync" />, so a caller cannot reach the
///     sandbox without passing all of them. They live here rather than in a handler because a handler's copy is a
///     property of one CALLER, and a second caller — a benchmark verifier, say — reaching the gateway directly would
///     get neither. One definition of each makes a bypass unrepresentable rather than merely discouraged.
/// </remarks>
internal interface IComputeToolGateway
{
    /// <summary>
    ///     Runs the request and renders the outcome as the model-facing string (exit code, stdout, stderr — or the
    ///     refusal sentence). Equivalent to <see cref="ExecuteDetailedAsync" /> with
    ///     <c>requireResourceLimits: false</c> followed by the formatter.
    /// </summary>
    Task<string> ExecuteAsync(ComputeRunToolRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Runs the request and returns the structured outcome, for callers that must distinguish "the sandbox refused"
    ///     from "the script ran and failed" — a distinction the rendered string deliberately blurs, because a model
    ///     reads both as text.
    /// </summary>
    /// <param name="requireResourceLimits">
    ///     When <see langword="true" />, a backend that cannot enforce CPU / memory / process ceilings is refused with
    ///     <see cref="ComputeRefusalCodes.NoResourceLimits" /> instead of running the script unbounded.
    /// </param>
    /// <remarks>
    ///     <c>run_python</c> passes <see langword="false" />; unattended callers that execute operator-authored code on
    ///     a schedule pass <see langword="true" />. A single unconditional check would refuse the shipped tool on every
    ///     host without systemd user scopes, which is why the ceilings are capability-gated in the first place.
    /// </remarks>
    Task<ComputeExecutionOutcome> ExecuteDetailedAsync(ComputeRunToolRequest request,
        bool requireResourceLimits,
        CancellationToken cancellationToken = default);
}
