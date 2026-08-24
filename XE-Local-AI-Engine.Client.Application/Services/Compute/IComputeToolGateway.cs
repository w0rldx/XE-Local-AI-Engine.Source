namespace XE_Local_AI_Engine.Client.Services.Compute;

/// <summary>
///     Executes one validated <c>run_python</c> request inside the node's compute sandbox and renders the outcome as the
///     model-facing string. Sits between the JSON-in / JSON-out handler and the sandbox provider for the same reason
///     <c>IAgentHomeToolGateway</c> does: the handler owns flag-gating and argument validation, the gateway owns the
///     execution and its failure vocabulary.
/// </summary>
internal interface IComputeToolGateway
{
    Task<string> ExecuteAsync(ComputeRunToolRequest request, CancellationToken cancellationToken = default);
}
